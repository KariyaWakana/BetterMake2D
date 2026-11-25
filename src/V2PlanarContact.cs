using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal sealed class V2PlanarContact(V2Face[] faces, V2ClipPlanes clips, double tolerance) : IDisposable
{
    internal enum Result { Empty, Segments, Fallback }
    private sealed record Footprint((Point2d[] Points, bool Outer)[] Loops)
    {
        internal static Footprint Read(BrepFace face, Plane plane)
        {
            var loops = new List<(Point2d[], bool)>();
            int outer = 0;
            foreach (var loop in face.Loops)
            {
                if (loop.LoopType is not (BrepLoopType.Outer or BrepLoopType.Inner)) return null;
                using var rim = loop.To3dCurve();
                if (rim == null || !rim.TryGetPolyline(out var polygon) || !polygon.IsClosed || polygon.Count < 4)
                    return null;
                bool isOuter = loop.LoopType == BrepLoopType.Outer;
                if (isOuter) outer++;
                var points = new Point2d[polygon.Count - 1];
                for (int i = 0; i < points.Length; i++) points[i] = Local(plane, polygon[i]);
                loops.Add((points, isOuter));
            }
            return outer == 1 ? new Footprint(loops.ToArray()) : null;
        }

        internal void Stations(Point2d origin, Vector2d direction, double length, double slack, List<double> stops)
        {
            double norm = direction.SquareLength;
            foreach (var (loop, _) in Loops)
            {
                var a = loop[^1] - origin;
                double da = Cross(direction, a);
                foreach (var point in loop)
                {
                    var b = point - origin;
                    double db = Cross(direction, b);
                    if (Math.Abs(db) <= slack) Add(b);
                    if (da < 0 && db > 0 || da > 0 && db < 0)
                        Add(a + (b - a) * (da / (da - db)));
                    (a, da) = (b, db);
                }
            }
            void Add(Vector2d p)
            {
                double at = (direction.X * p.X + direction.Y * p.Y) / norm;
                if (at > 0 && at < length) stops.Add(at);
            }
        }

        internal bool? Contains(Point2d point, double slack)
        {
            bool insideOuter = false;
            foreach (var (loop, outer) in Loops)
            {
                bool inside = false;
                var a = loop[^1];
                foreach (var b in loop)
                {
                    var ab = b - a;
                    var offset = point - a;
                    if (offset.X >= Math.Min(0, ab.X) - slack && offset.X <= Math.Max(0, ab.X) + slack &&
                        offset.Y >= Math.Min(0, ab.Y) - slack && offset.Y <= Math.Max(0, ab.Y) + slack &&
                        Math.Abs(Cross(ab, offset)) <= slack * ab.Length) return null;
                    if ((a.Y > point.Y) != (b.Y > point.Y) && offset.X < offset.Y * (ab.X / ab.Y))
                        inside = !inside;
                    a = b;
                }
                if (!outer && inside) return false;
                if (outer) insideOuter = inside;
            }
            return insideOuter;
        }
    }

    private readonly Footprint[] footprints = new Footprint[faces.Length];
    private readonly bool[] read = new bool[faces.Length];
    private readonly List<double> stations = new();
    private readonly Dictionary<int, (Curve Rim, BoundingBox Box)[]> curvedRims = new();
    internal readonly List<Line> Lines = new();

    internal void TrimLine(int id, Plane plane, Line line, Action<Line> add)
    {
        var axis = line.Direction;
        double length = axis.Length;
        if (length <= tolerance * .001 || !clips.Trim(ref line)) return;
        axis = line.Direction;
        length = axis.Length;
        if (!axis.Unitize()) return;
        double numeric = 1e-12 * Math.Max(1, line.From.MaximumCoordinate);
        var footprint = Read(id, plane);
        var origin = Local(plane, line.From);
        var direction = new Vector2d(plane.XAxis * axis, plane.YAxis * axis);
        stations.Clear();
        stations.Add(0);
        stations.Add(length);
        if (footprint != null)
            footprint.Stations(origin, direction, length, numeric, stations);
        else
        {
            if (!curvedRims.TryGetValue(id, out var rims))
            {
                if (curvedRims.Count == 1024) ClearRims();
                var collected = new List<(Curve, BoundingBox)>();
                foreach (var loop in faces[id].Face.Loops)
                {
                    var rim = loop.To3dCurve();
                    if (rim != null) collected.Add((rim, rim.GetBoundingBox(true)));
                }
                curvedRims[id] = rims = collected.ToArray();
            }
            var bounds = new BoundingBox(line.From, line.To);
            bounds.Inflate(tolerance * .1);
            using var segment = new LineCurve(line) { Domain = new Interval(0, length) };
            foreach (var (rim, box) in rims)
            {
                if (!BoundingBox.Intersection(bounds, box).IsValid) continue;
                using var hits = Intersection.CurveCurve(segment, rim, tolerance * .1, tolerance * .1);
                if (hits == null) continue;
                foreach (var hit in hits)
                {
                    stations.Add(Math.Clamp(hit.ParameterA, 0, length));
                    if (hit.IsOverlap) stations.Add(Math.Clamp(hit.OverlapA.T1, 0, length));
                }
            }
        }
        stations.Sort();
        double start = 0;
        for (int i = 1; i < stations.Count; i++)
        {
            double end = stations[i];
            if (end - start <= numeric) continue;
            double middle = (start + end) * .5;
            bool inside = footprint?.Contains(origin + direction * middle, tolerance * .01 + numeric) ??
                NativeContains(id, line.From + axis * middle);
            if (inside) add(new Line(line.From + axis * start, line.From + axis * end));
            start = end;
        }
    }

    internal void ClearRims()
    {
        foreach (var rims in curvedRims.Values)
            foreach (var (curve, _) in rims) curve.Dispose();
        curvedRims.Clear();
    }

    public void Dispose() => ClearRims();

    internal Result Intersect(int ia, int ib, Plane a, Plane b, BoundingBox common)
    {
        Lines.Clear();
        if (!common.IsValid) return Result.Fallback;
        var axis = Vector3d.CrossProduct(a.Normal, b.Normal);
        double sine = axis.Length;
        // Keep the solver's existing nearly-parallel contact band.
        if (Math.Abs(a.Normal * b.Normal) > 1 - 1e-12) return Result.Empty;
        double scale = Math.Max(1, Math.Max(common.Min.MaximumCoordinate, common.Max.MaximumCoordinate));
        double numeric = scale * 1e-12;
        var center = common.Center;
        double da = a.Normal * (a.Origin - center), db = b.Normal * (b.Origin - center);
        var origin = center + (Vector3d.CrossProduct(b.Normal, axis) * da +
            Vector3d.CrossProduct(axis, a.Normal) * db) / (sine * sine);
        axis /= sine;
        // The fit envelope is inflated by the intersection's conditioning.
        double pad = (tolerance * .1 + numeric) / sine;
        common.Inflate(pad);
        double from = double.NegativeInfinity, to = double.PositiveInfinity;
        for (int k = 0; k < 3; k++)
        {
            double x = origin[k], d = axis[k], low = common.Min[k], high = common.Max[k];
            if (d == 0)
            {
                if (x < low || x > high) return Result.Empty;
                continue;
            }
            double enter = (low - x) / d, exit = (high - x) / d;
            from = Math.Max(from, Math.Min(enter, exit));
            to = Math.Min(to, Math.Max(enter, exit));
            if (from > to) return Result.Empty;
        }
        var line = new Line(origin + axis * from, origin + axis * to);
        if (line.Length <= numeric || !clips.Trim(ref line)) return Result.Empty;
        var first = Read(ia, a);
        var second = Read(ib, b);
        if (first == null || second == null) return Result.Fallback;
        var xa = Local(a, line.From);
        var xb = Local(b, line.From);
        var va = new Vector2d(a.XAxis * axis, a.YAxis * axis);
        var vb = new Vector2d(b.XAxis * axis, b.YAxis * axis);
        double length = line.Length;
        double slack = tolerance * .01 + numeric;
        stations.Clear();
        stations.Add(0);
        stations.Add(length);
        first.Stations(xa, va, length, numeric, stations);
        second.Stations(xb, vb, length, numeric, stations);
        stations.Sort();
        double left = stations[0], start = double.NaN, end = double.NaN;
        for (int i = 1; i < stations.Count; i++)
        {
            double right = stations[i];
            if (right - left <= numeric) continue;
            double middle = (left + right) * .5;
            var point = new Point2d(xa.X + va.X * middle, xa.Y + va.Y * middle);
            bool? inside = first.Contains(point, slack);
            if (inside != false)
            {
                var other = new Point2d(xb.X + vb.X * middle, xb.Y + vb.Y * middle);
                bool? within = second.Contains(other, slack);
                if (within != false)
                {
                    var world = line.From + axis * middle;
                    inside = (inside ?? NativeContains(ia, world)) && (within ?? NativeContains(ib, world));
                }
                else inside = false;
            }
            if (inside == true)
            {
                if (double.IsNaN(start)) start = left;
                end = right;
            }
            else Flush();
            left = right;
        }
        Flush();
        return Lines.Count == 0 ? Result.Empty : Result.Segments;

        void Flush()
        {
            if (double.IsNaN(start)) return;
            Lines.Add(new Line(line.From + axis * start, line.From + axis * end));
            start = double.NaN;
        }
    }

    private Footprint Read(int id, Plane plane)
    {
        if (read[id]) return footprints[id];
        read[id] = true;
        return footprints[id] = Footprint.Read(faces[id].Face, plane);
    }

    private bool NativeContains(int id, Point3d p)
    {
        var face = faces[id].Face;
        return face.ClosestPoint(p, out double u, out double v) &&
            face.IsPointOnFace(u, v, tolerance * .01) != PointFaceRelation.Exterior;
    }

    private static Point2d Local(Plane plane, Point3d point)
    {
        var v = point - plane.Origin;
        return new Point2d(plane.XAxis * v, plane.YAxis * v);
    }
    private static double Cross(Vector2d a, Vector2d b) => a.X * b.Y - a.Y * b.X;
}
