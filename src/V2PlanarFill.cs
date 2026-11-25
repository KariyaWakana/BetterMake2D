using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class V2PlanarFill
{
    internal static bool Valid(Brep piece, int rings) =>
        piece is { IsValid: true } && piece.Faces.Count == 1 && piece.Faces[0].Loops.Count == rings;

    internal static List<Brep> Polygon(Point3d[][] rings, double tolerance)
    {
        if (rings.Length == 0 || rings.Any(ring => ring.Length < 4)) return null;
        if (rings.Sum(ring => ring.Length) <= 1024) return Recover(rings, tolerance);
        var shift = rings[0][0] - Point3d.Origin;
        var local = rings.Select(ring => ring.Select(point => point - shift).ToArray()).ToArray();
        return SplitAtPinches(local, shift, tolerance, 256);
    }

    internal static List<Brep> Recover(Point3d[][] rings, double tolerance)
    {
        var origin = rings[0][0];
        var shift = origin - Point3d.Origin;
        var local = rings.Select(ring => ring.Select(p => p - shift).ToArray()).ToArray();
        var loops = local.Select(ring => new PolylineCurve(ring)).ToArray();
        try
        {
            var direct = Brep.CreateTrimmedPlane(Plane.WorldXY, loops);
            if (Valid(direct, rings.Length) && SameBoundary(direct, local) &&
                direct.Transform(Transform.Translation(shift)) && Valid(direct, rings.Length))
                return new List<Brep> { direct };
            direct?.Dispose();
            return SplitAtPinches(local, shift, tolerance);
        }
        finally { foreach (var loop in loops) loop.Dispose(); }
    }

    private static bool SameBoundary(Brep brep, Point3d[][] rings)
    {
        var remaining = new HashSet<(Point3d, Point3d)>();
        foreach (var ring in rings)
            for (int i = 1; i < ring.Length; i++)
                remaining.Add(Ordered(ring[i - 1], ring[i]));
        foreach (var loop in brep.Loops)
        {
            using var curve = loop.To3dCurve();
            if (curve == null || !curve.TryGetPolyline(out var polyline)) return false;
            for (int i = 1; i < polyline.Count; i++)
                if (!remaining.Remove(Ordered(polyline[i - 1], polyline[i]))) return false;
        }
        return remaining.Count == 0;

        static (Point3d, Point3d) Ordered(Point3d a, Point3d b) => a.CompareTo(b) <= 0 ? (a, b) : (b, a);
    }

    private readonly record struct Ramp(Point3d Low, Point3d High)
    {
        internal double At(double y) => y == Low.Y ? Low.X : y == High.Y ? High.X :
            Low.X + (High.X - Low.X) * ((y - Low.Y) / (High.Y - Low.Y));
    }

    private sealed class Band(Ramp left, Ramp right, double bottom, double top)
    {
        private readonly List<Point3d> leftRim = new() { new(left.At(bottom), bottom, 0), new(left.At(top), top, 0) };
        private readonly List<Point3d> rightRim = new() { new(right.At(bottom), bottom, 0), new(right.At(top), top, 0) };
        private Ramp lastLeft = left, lastRight = right;

        internal int PointCount => leftRim.Count + rightRim.Count;

        internal void Extend(Ramp nextLeft, Ramp nextRight, double top)
        {
            ExtendRim(leftRim, lastLeft, nextLeft, top);
            ExtendRim(rightRim, lastRight, nextRight, top);
            (lastLeft, lastRight) = (nextLeft, nextRight);
        }

        internal Point3d[] Close()
        {
            var points = new List<Point3d>(leftRim.Count + rightRim.Count + 1);
            foreach (var point in rightRim.AsEnumerable().Concat(leftRim.AsEnumerable().Reverse()))
                if (points.Count == 0 || point != points[^1]) points.Add(point);
            if (points[0] != points[^1]) points.Add(points[0]);
            return points.ToArray();
        }

        private static void ExtendRim(List<Point3d> rim, Ramp previous, Ramp next, double top)
        {
            var point = new Point3d(next.At(top), top, 0);
            if (previous == next) rim[^1] = point;
            else rim.Add(point);
        }
    }

    // Sweep the exact polygon domain. Each positive-width strip alternates between filled
    // and empty intervals, so pinches can split the fill without moving the hole or the rim.
    // Strips continue as one monotone piece only across a full, positive-width shared edge.
    private static List<Brep> SplitAtPinches(Point3d[][] rings, Vector3d shift, double tolerance,
        int bandLimit = int.MaxValue)
    {
        var levels = rings.SelectMany(ring => ring).Select(point => point.Y).Distinct().Order().ToArray();
        var ramps = new List<Ramp>();
        foreach (var ring in rings)
            for (int i = 1; i < ring.Length; i++)
            {
                var a = ring[i - 1];
                var b = ring[i];
                if (a.Y != b.Y) ramps.Add(a.Y < b.Y ? new Ramp(a, b) : new Ramp(b, a));
            }
        ramps.Sort((a, b) => a.Low.Y.CompareTo(b.Low.Y));
        var active = new List<Ramp>();
        var bands = new List<Band>();
        var below = new Dictionary<(double Left, double Right), Band>();
        var above = new Dictionary<(double Left, double Right), Band>();
        int next = 0;
        for (int i = 1; i < levels.Length; i++)
        {
            double low = levels[i - 1], high = levels[i], mid = low + (high - low) * .5;
            active.RemoveAll(ramp => ramp.High.Y <= low);
            while (next < ramps.Count && ramps[next].Low.Y <= low)
                active.Add(ramps[next++]);
            if ((active.Count & 1) != 0) return null;
            active.Sort((a, b) => a.At(mid).CompareTo(b.At(mid)));
            above.Clear();
            for (int j = 0; j < active.Count; j += 2)
            {
                var left = active[j];
                var right = active[j + 1];
                var start = (Left: left.At(low), Right: right.At(low));
                var finish = (Left: left.At(high), Right: right.At(high));
                if (start.Left > start.Right || finish.Left > finish.Right ||
                    left.At(mid) >= right.At(mid)) return null;
                if (start.Left < start.Right && below.TryGetValue(start, out var existing) &&
                    existing.PointCount < bandLimit)
                {
                    existing.Extend(left, right, high);
                }
                else
                {
                    existing = new Band(left, right, low, high);
                    bands.Add(existing);
                }
                if (finish.Left < finish.Right) above.Add(finish, existing);
            }
            (below, above) = (above, below);
        }

        double expected = rings.Sum(Area);
        var paths = bands.Select(band => band.Close()).ToArray();
        double actual = paths.Sum(Area);
        if (!(expected > 0) || Math.Abs(expected - actual) > Math.Max(tolerance * tolerance, expected * 1e-9))
            return null;

        var pieces = new List<Brep>();
        var restore = Transform.Translation(shift);
        foreach (var path in paths)
        {
            using var rim = new PolylineCurve(path);
            var piece = Brep.CreateTrimmedPlane(Plane.WorldXY, rim);
            if (Valid(piece, 1) && piece.Transform(restore) && Valid(piece, 1))
            {
                pieces.Add(piece);
                continue;
            }
            piece?.Dispose();
            foreach (var valid in pieces) valid.Dispose();
            return null;
        }
        return pieces.Count == 0 ? null : pieces;
    }

    private static double Area(Point3d[] points)
    {
        var origin = points[0];
        double twice = 0;
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1] - origin;
            var b = points[i] - origin;
            twice += a.X * b.Y - a.Y * b.X;
        }
        return twice * .5;
    }
}
