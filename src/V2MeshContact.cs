using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Triangle = BetterMake2D.V2NativeMesh.Triangle;

namespace BetterMake2D;

internal static class V2MeshContact
{
    internal static void IntersectProxy(V2NativeMesh mesh, V2Face other, int otherId,
        V2PlanarContact planar, double tolerance, Action cancel, Action<Line> add)
    {
        if (other.Triangles is V2NativeMesh second)
        {
            mesh.VisitPairs(second, tolerance, cancel, (first, candidate) =>
            {
                var plane = new Plane(first.A, first.Normal);
                if (Section(candidate, plane, tolerance, out var line) &&
                    ClipTriangleLine(first, ref line, tolerance)) add(line);
            });
        }
        else if (other.Plane is Plane plane)
        {
            mesh.VisitPlane(other.Box, plane, tolerance, cancel, triangle =>
            {
                if (Section(triangle, plane, tolerance, out var line))
                    planar.TrimLine(otherId, plane, line, add);
            });
        }
    }

    internal static void Intersect(V2NativeMesh terrain, V2Face other, Brep support,
        double tolerance, Action cancel, Action<Curve> add)
    {
        int count = 0;
        terrain.Visit(other.Box, tolerance, triangle =>
        {
            if ((count++ & 511) == 0) cancel?.Invoke();
            if (other.Triangles is V2NativeMesh mesh)
            {
                var plane = new Plane(triangle.A, triangle.Normal);
                mesh.Visit(triangle.Bounds, tolerance, candidate =>
                {
                    if (!Section(candidate, plane, tolerance, out var segment) ||
                        !ClipLine(triangle, ref segment, tolerance)) return;
                    using var line = new LineCurve(segment);
                    add(line);
                });
                return;
            }
            if (other.Plane is Plane planar)
            {
                if (!Section(triangle, planar, tolerance, out var line)) return;
                using var section = new LineCurve(line);
                // A planar face can have arbitrarily shaped trim islands or holes.
                var stations = new List<double> { section.Domain.T0, section.Domain.T1 };
                foreach (var loop in other.Face.Loops)
                {
                    using var rim = loop.To3dCurve();
                    if (rim == null) continue;
                    using var contacts = Intersection.CurveCurve(section, rim, tolerance, tolerance);
                    if (contacts != null)
                        foreach (var contact in contacts) AddStation(stations, contact);
                }
                Split(section, stations, tolerance, point =>
                    other.Face.ClosestPoint(point, out double u, out double v) &&
                    other.Face.IsPointOnFace(u, v, tolerance) != PointFaceRelation.Exterior, add);
                return;
            }
            var sectionPlane = new Plane(triangle.A, triangle.Normal);
            if (V2DrawingSolver.Separates(sectionPlane, other.Box, tolerance)) return;
            Intersection.BrepPlane(support, sectionPlane, tolerance, out var sections, out _);
            foreach (var curve in sections ?? Array.Empty<Curve>())
                using (curve) ClipCurve(triangle, curve, tolerance, add);
        });
    }

    internal static bool Section(Triangle t, Plane plane, double tolerance, out Line line)
    {
        double a = plane.DistanceTo(t.A), b = plane.DistanceTo(t.B), c = plane.DistanceTo(t.C);
        line = Line.Unset;
        double scale = Math.Max(t.A.MaximumCoordinate, Math.Max(t.B.MaximumCoordinate, t.C.MaximumCoordinate));
        double coincidence = Math.Max(tolerance * 1e-7, scale * 3e-14);
        if (Math.Min(a, Math.Min(b, c)) > coincidence || Math.Max(a, Math.Max(b, c)) < -coincidence ||
            Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c))) <= coincidence)
            return false;
        Point3d start = Point3d.Unset, end = Point3d.Unset;
        Edge(t.A, a, t.B, b);
        Edge(t.B, b, t.C, c);
        Edge(t.C, c, t.A, a);
        if (!start.IsValid || !end.IsValid || start.DistanceTo(end) <= tolerance * .01) return false;
        line = new Line(start, end);
        return true;

        void Edge(Point3d p, double dp, Point3d q, double dq)
        {
            if (Math.Abs(dp) <= coincidence) Add(p);
            if (dp < 0 && dq > 0 || dp > 0 && dq < 0) Add(p + (q - p) * (dp / (dp - dq)));
        }

        void Add(Point3d point)
        {
            if (!start.IsValid) start = point;
            else if (start.DistanceTo(point) > tolerance * .001) end = point;
        }
    }

    private static bool ClipLine(Triangle t, ref Line line, double tolerance)
    {
        double low = 0, high = 1;
        foreach (var plane in Sides(t))
        {
            double a = plane.DistanceTo(line.From), b = plane.DistanceTo(line.To);
            if (a < -tolerance && b < -tolerance) return false;
            if (a < 0 && b > 0) low = Math.Max(low, a / (a - b));
            if (a > 0 && b < 0) high = Math.Min(high, a / (a - b));
        }
        if (low >= high) return false;
        line = new Line(line.PointAt(low), line.PointAt(high));
        return line.Length > tolerance * .01;
    }

    private static bool ClipTriangleLine(Triangle t, ref Line line, double tolerance)
    {
        double low = 0, high = 1;
        var from = line.From;
        var to = line.To;
        var normal = t.Normal;
        if (!Side(t.A, t.B) || !Side(t.B, t.C) || !Side(t.C, t.A) || low >= high) return false;
        line = new Line(line.PointAt(low), line.PointAt(high));
        return line.Length > tolerance * .01;

        bool Side(Point3d first, Point3d second)
        {
            var n = Vector3d.CrossProduct(normal, second - first);
            double a = n * (from - first), b = n * (to - first);
            if (a < 0 && b < 0 && Math.Max(a, b) < -tolerance * n.Length) return false;
            if (a < 0 && b >= 0) low = Math.Max(low, a / (a - b));
            if (a >= 0 && b < 0) high = Math.Min(high, a / (a - b));
            return true;
        }
    }

    private static void ClipCurve(Triangle t, Curve curve, double tolerance, Action<Curve> add)
    {
        if (curve.IsLinear(tolerance * .001))
        {
            var segment = new Line(curve.PointAtStart, curve.PointAtEnd);
            if (!ClipLine(t, ref segment, tolerance)) return;
            using var clipped = new LineCurve(segment);
            add(clipped);
            return;
        }
        var stations = new List<double> { curve.Domain.T0, curve.Domain.T1 };
        var planes = Sides(t);
        foreach (var plane in planes)
        {
            using var contacts = Intersection.CurvePlane(curve, plane, tolerance);
            if (contacts != null)
                foreach (var contact in contacts) AddStation(stations, contact);
        }
        Split(curve, stations, tolerance, point => Array.TrueForAll(planes, plane =>
            plane.DistanceTo(point) >= -tolerance), add);
    }

    private static Plane[] Sides(Triangle t)
    {
        var n = t.Normal;
        return new[] { new Plane(t.A, Vector3d.CrossProduct(n, t.B - t.A)),
            new Plane(t.B, Vector3d.CrossProduct(n, t.C - t.B)),
            new Plane(t.C, Vector3d.CrossProduct(n, t.A - t.C)) };
    }

    private static void AddStation(List<double> stations, IntersectionEvent hit)
    {
        stations.Add(hit.ParameterA);
        if (hit.IsOverlap) stations.Add(hit.OverlapA.T1);
    }

    private static void Split(Curve curve, List<double> stations, double tolerance, Func<Point3d, bool> inside, Action<Curve> add)
    {
        stations.Sort();
        double start = curve.Domain.T0;
        foreach (double end in stations)
        {
            if (end <= start || end > curve.Domain.T1) continue;
            if (inside(curve.PointAt(start + (end - start) * .5)))
            {
                if (start == curve.Domain.T0 && end == curve.Domain.T1)
                {
                    if (curve.GetLength() > tolerance * .01) add(curve);
                }
                else
                {
                    using var part = curve.Trim(start, end);
                    if (part is { IsValid: true } && part.GetLength() > tolerance * .01) add(part);
                }
            }
            start = end;
        }
    }
}
