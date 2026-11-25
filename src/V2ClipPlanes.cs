using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

// Plane normals point toward the discarded half-space.
internal sealed class V2ClipPlanes(IReadOnlyList<Plane> planes)
{
    internal static readonly V2ClipPlanes Empty = new(Array.Empty<Plane>());
    internal const int ViewPolygonCapacity = 3 + 6;
    internal readonly IReadOnlyList<Plane> Planes = planes;
    internal bool Active => Planes.Count != 0;
    internal bool Includes(Point3d p) => Planes.All(plane => plane.DistanceTo(p) <= Roundoff(p));
    internal bool Discards(BoundingBox box) => Planes.Any(plane => Range(box, plane).Min > Roundoff(box.Center));
    internal bool Keeps(BoundingBox box) => Planes.All(plane => Range(box, plane).Max <= 0);

    internal byte ViewOutcode(Point3d point)
    {
        int mask = 0;
        double slack = Roundoff(point);
        for (int i = 0; i < Planes.Count; i++)
            if (Planes[i].DistanceTo(point) > slack) mask |= 1 << i;
        return (byte)mask;
    }

    internal int ClipViewTriangle(Span<Point3d> polygon, int outcode)
    {
        Span<Point3d> next = stackalloc Point3d[ViewPolygonCapacity];
        int count = 3;
        for (int i = 0; i < Planes.Count; i++)
        {
            if ((outcode & (1 << i)) == 0) continue;
            var plane = Planes[i];
            int written = 0;
            var a = polygon[count - 1];
            double da = Distance(a);
            for (int j = 0; j < count; j++)
            {
                var b = polygon[j];
                double db = Distance(b);
                if (da < 0 && db > 0 || da > 0 && db < 0)
                {
                    // Shared triangle edges compute identical cut vertices in either winding.
                    next[written++] = a.CompareTo(b) <= 0
                        ? a + (b - a) * (da / (da - db))
                        : b + (a - b) * (db / (db - da));
                }
                if (db <= 0) next[written++] = b;
                (a, da) = (b, db);
            }
            if (written < 3) return 0;
            next[..written].CopyTo(polygon);
            count = written;

            double Distance(Point3d point)
            {
                double value = plane.DistanceTo(point);
                return Math.Abs(value) <= Roundoff(point) ? 0 : value;
            }
        }
        return count;
    }

    internal bool OnViewBoundary(Point3d a, Point3d b)
    {
        double slack = Math.Max(Roundoff(a), Roundoff(b));
        foreach (var plane in Planes)
            if (Math.Abs(plane.DistanceTo(a)) <= slack && Math.Abs(plane.DistanceTo(b)) <= slack)
                return true;
        return false;
    }

    internal IEnumerable<Plane> Crossing(BoundingBox box) => Planes.Where(plane =>
    {
        var range = Range(box, plane);
        return range.Min <= Roundoff(box.Center) && range.Max >= -Roundoff(box.Center);
    });

    internal bool Trim(ref Line line)
    {
        double from = 0, to = 1;
        foreach (var plane in Planes)
        {
            double a = plane.DistanceTo(line.From), b = plane.DistanceTo(line.To);
            if (Math.Abs(a) <= Roundoff(line.From)) a = 0;
            if (Math.Abs(b) <= Roundoff(line.To)) b = 0;
            if (a > 0 && b > 0) return false;
            if (a <= 0 && b <= 0) continue;
            double at = a / (a - b);
            if (a > 0) from = Math.Max(from, at);
            else to = Math.Min(to, at);
            if (from >= to) return false;
        }
        line = new Line(line.PointAt(from), line.PointAt(to));
        return true;
    }

    // Only boundary parameters are split. Original trimmed surfaces remain in the depth index.
    internal IEnumerable<Curve> Pieces(Curve curve, double fit)
    {
        if (!Active || Keeps(curve.GetBoundingBox(true)))
        {
            yield return curve;
            yield break;
        }
        if (Discards(curve.GetBoundingBox(true))) yield break;
        var stops = new SortedSet<double> { curve.Domain.Min, curve.Domain.Max };
        foreach (var plane in Crossing(curve.GetBoundingBox(true)))
        {
            using var events = Intersection.CurvePlane(curve, plane, fit);
            if (events == null) continue;
            foreach (var contact in events)
            {
                if (curve.Domain.IncludesParameter(contact.ParameterA)) stops.Add(contact.ParameterA);
                if (contact.IsOverlap && curve.Domain.IncludesParameter(contact.OverlapA.T1))
                    stops.Add(contact.OverlapA.T1);
            }
        }
        double start = curve.Domain.Min;
        foreach (double end in stops)
        {
            if (end <= start) continue;
            var interval = new Interval(start, end);
            start = end;
            if (!Includes(curve.PointAt(interval.Mid))) continue;
            if (interval == curve.Domain) yield return curve;
            else
            {
                var piece = curve.Trim(interval);
                if (piece != null) yield return piece;
            }
        }
    }

    private static (double Min, double Max) Range(BoundingBox box, Plane plane)
    {
        var n = plane.Normal;
        var extent = box.Diagonal;
        double radius = .5 * (Math.Abs(n.X) * extent.X + Math.Abs(n.Y) * extent.Y + Math.Abs(n.Z) * extent.Z);
        double center = plane.DistanceTo(box.Center);
        return (center - radius, center + radius);
    }

    private static double Roundoff(Point3d point) => Math.Max(1, point.MaximumCoordinate) * 1e-12;
}
