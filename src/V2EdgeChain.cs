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

internal sealed class V2EdgeChain(int left, int right, int[] sides, V2SourceSpan[] spans, Point3d[] points) : IDisposable
{
    internal readonly int Left = left, Right = right;
    internal readonly int[] Sides = sides;
    internal V2SourceSpan Source => spans[0];
    private Curve restored;
    private Curve polyline;
    internal bool Upgraded => restored != null;
    internal Curve OriginalGeometry => polyline ??= points.Length == 2
        ? new LineCurve(points[0], points[1]) : new PolylineCurve(points);
    internal Curve Geometry => restored ?? OriginalGeometry;
    internal bool IsStraight => Geometry is LineCurve;
    internal BoundingBox Bounds => new(points);

    internal void Restore(IReadOnlyDictionary<int, Curve> originals, double fit,
        Func<Curve, bool> compatible)
    {
        if (points.Length <= 2) return;
        using var candidate = Candidate(originals, fit);
        if (candidate == null || !candidate.IsValid ||
            candidate.PointAtStart != points[0] || candidate.PointAtEnd != points[^1] ||
            candidate.IsClosed != (points[0] == points[^1]))
            return;
        if (candidate is not LineCurve)
        {
            using var self = Intersection.CurveSelf(candidate, fit * .01);
            if (self is { Count: > 0 } ||
                !Curve.GetDistancesBetweenCurves(candidate, Geometry, fit * .05, out double error,
                    out _, out _, out _, out _, out _) || !(error <= fit))
                return;
        }
        if (compatible(candidate)) restored = candidate.DuplicateCurve();
    }

    private Curve Candidate(IReadOnlyDictionary<int, Curve> originals, double fit)
    {
        var chord = new Line(points[0], points[^1]);
        if (chord.IsValid && points.All(point => chord.DistanceTo(point, true) <= fit * .25))
            return new LineCurve(chord);
        if (spans.Any(span => span.CurveId != Source.CurveId)) return null;
        if (!originals.TryGetValue(Source.CurveId, out var original)) return null;
        double from = Source.From, to = spans[^1].To;
        double sign = Math.Sign(to - from);
        double parameterTolerance = original.Domain.Length * 1e-8;
        if (sign == 0 || spans.Any(span => sign * (span.To - span.From) < -parameterTolerance))
            return null;
        for (int i = 1; i < spans.Length; i++)
            if (Math.Abs(spans[i - 1].To - spans[i].From) > parameterTolerance)
                return null;
        using var piece = original.Trim(Math.Min(from, to), Math.Max(from, to));
        if (piece == null) return null;
        using var nurbs = piece.ToNurbsCurve();
        if (nurbs == null || sign < 0 && !nurbs.Reverse() ||
            nurbs.PointAtStart.DistanceTo(points[0]) > fit || nurbs.PointAtEnd.DistanceTo(points[^1]) > fit ||
            !nurbs.SetStartPoint(points[0]) || !nurbs.SetEndPoint(points[^1]))
            return null;
        return nurbs.DuplicateCurve();
    }

    internal void Revert()
    {
        restored?.Dispose();
        restored = null;
    }

    public void Dispose()
    {
        Revert();
        polyline?.Dispose();
    }

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
