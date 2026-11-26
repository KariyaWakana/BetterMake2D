using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal sealed class V2ProjectedCurves : Dictionary<int, Curve>, IDisposable
{
    private const long ByteBudget = 96L * 1024 * 1024;
    private long bytes;
    internal int Skipped { get; private set; }

    internal void Capture(int id, Curve curve)
    {
        if (ContainsKey(id)) return;
        long size = (long)curve.MemoryEstimate();
        if (size > ByteBudget - bytes)
        {
            Skipped++;
            return;
        }
        Add(id, curve.DuplicateCurve());
        bytes += size;
    }

    public void Dispose()
    {
        foreach (var curve in Values) curve.Dispose();
        Clear();
    }
}

internal sealed class V2EdgeChain(int left, int right, V2PlanarArrangement plan, int[] sides, int offset,
    int count, V2SourceSpan source) : IDisposable
{
    internal readonly int Left = left, Right = right;
    internal ReadOnlySpan<int> Sides => sides.AsSpan(offset, count);
    internal V2SourceSpan Source => source;
    private Curve restored;
    internal bool Upgraded => restored != null;
    internal Curve RestoredGeometry => restored;
    internal Curve CreateOriginalGeometry() => count == 1
        ? new LineCurve(Start, End) : new PolylineCurve(Points());
    internal Curve DuplicateGeometry() => restored?.DuplicateCurve() ?? CreateOriginalGeometry();
    internal bool IsStraight => restored is LineCurve || restored == null && count == 1;
    internal BoundingBox Bounds => new(Points());
    private Point3d Start => plan.Point(plan.Start(sides[offset]));
    private Point3d End => plan.Point(plan.Start(sides[offset + count - 1] ^ 1));

    private IEnumerable<Point3d> Points()
    {
        for (int i = offset; i < offset + count; i++) yield return plan.Point(plan.Start(sides[i]));
        yield return End;
    }

    internal void Restore(IReadOnlyDictionary<int, Curve> originals, double fit,
        Func<Curve, bool> compatible)
    {
        if (count <= 1) return;
        using var candidate = Candidate(originals, fit);
        if (candidate == null || !candidate.IsValid ||
            candidate.PointAtStart != Start || candidate.PointAtEnd != End ||
            candidate.IsClosed != (Start == End))
            return;
        if (candidate is not LineCurve)
        {
            using var self = Intersection.CurveSelf(candidate, fit * .01);
            if (self is { Count: > 0 }) return;
            using var baseline = DuplicateGeometry();
            if (!Curve.GetDistancesBetweenCurves(candidate, baseline, fit * .05, out double error,
                    out _, out _, out _, out _, out _) || !(error <= fit))
                return;
        }
        if (compatible(candidate)) restored = candidate.DuplicateCurve();
    }

    private Curve Candidate(IReadOnlyDictionary<int, Curve> originals, double fit)
    {
        var chord = new Line(Start, End);
        if (chord.IsValid && Points().All(point => chord.DistanceTo(point, true) <= fit * .25))
            return new LineCurve(chord);
        if (!originals.TryGetValue(Source.CurveId, out var original)) return null;
        double from = Source.From, to = plan.PrimarySpan(sides[offset + count - 1]).To;
        double sign = Math.Sign(to - from);
        double parameterTolerance = original.Domain.Length * 1e-8;
        if (sign == 0) return null;
        double end = from;
        foreach (int side in Sides)
        {
            var span = plan.PrimarySpan(side);
            if (span.CurveId != Source.CurveId || sign * (span.To - span.From) < -parameterTolerance ||
                Math.Abs(end - span.From) > parameterTolerance)
                return null;
            end = span.To;
        }
        using var piece = original.Trim(Math.Min(from, to), Math.Max(from, to));
        if (piece == null) return null;
        using var nurbs = piece.ToNurbsCurve();
        if (nurbs == null || sign < 0 && !nurbs.Reverse() ||
            nurbs.PointAtStart.DistanceTo(Start) > fit || nurbs.PointAtEnd.DistanceTo(End) > fit ||
            !nurbs.SetStartPoint(Start) || !nurbs.SetEndPoint(End))
            return null;
        return nurbs.DuplicateCurve();
    }

    internal void Revert()
    {
        restored?.Dispose();
        restored = null;
    }

    public void Dispose() => Revert();

    internal static bool Compatible(Curve a, Curve b, double epsilon)
    {
        using var hits = Intersection.CurveCurve(a, b, epsilon, epsilon);
        foreach (var hit in hits ?? Enumerable.Empty<IntersectionEvent>())
        {
            if (hit.IsOverlap) return false;
            if (!CommonEndpoint(a.PointAtStart) && !CommonEndpoint(a.PointAtEnd)) return false;

            bool CommonEndpoint(Point3d endpoint) => hit.PointA.DistanceTo(endpoint) <= epsilon &&
                hit.PointB.DistanceTo(endpoint) <= epsilon &&
                (b.PointAtStart == endpoint || b.PointAtEnd == endpoint);
        }
        return hits != null;
    }
}
