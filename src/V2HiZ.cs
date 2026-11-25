using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

// A finite entry bounds the depth of an opaque occluder over the entire pixel.
internal sealed class V2HiZ
{
    private readonly record struct Level(int Width, int Height, double[] Depth);
    private readonly record struct Edge(double X0, double Y0, double X1, double Y1)
    {
        internal double At(double y) => X0 + (X1 - X0) * ((y - Y0) / (Y1 - Y0));
    }

    private readonly List<Level> levels = new();
    private readonly double x0, y0, step, fit;
    private readonly IProjectionCamera camera;
    private readonly Action cancel;
    private const int Resolution = 2048;
    private int work = 36_000_000;
    internal int Written { get; private set; }
    internal int Occluders { get; private set; }
    internal int CurvedOccluders { get; private set; }
    internal bool Available => levels.Count != 0;

    internal V2HiZ(V2Face[] faces, double fit, Action cancel, IProjectionCamera camera)
    {
        this.fit = fit;
        this.camera = camera;
        this.cancel = cancel;
        if (faces.Length < 8) return;
        var extent = new Rect(faces.Min(face => face.Screen.Left), faces.Min(face => face.Screen.Bottom),
            faces.Max(face => face.Screen.Right), faces.Max(face => face.Screen.Top)).Grow(fit * 2);
        x0 = extent.Left;
        y0 = extent.Bottom;
        step = Math.Max(extent.Right - x0, extent.Top - y0) / Resolution;
        if (!(step > 0) || !double.IsFinite(step)) return;
        var baseLevel = Allocate((int)Math.Ceiling((extent.Right - x0) / step),
            (int)Math.Ceiling((extent.Top - y0) / step));
        Array.Fill(baseLevel.Depth, double.NegativeInfinity);
        levels.Add(baseLevel);

        foreach (var face in Candidates(planar: true, 4096))
        {
            cancel?.Invoke();
            if (work <= 0) break;
            if (!TryFootprint(face, out var loops, out double error)) continue;
            RasterPlanar(loops, face.Plane.Value, fit + error);
        }
        foreach (var face in Candidates(planar: false, 128))
        {
            cancel?.Invoke();
            if (work <= 0) break;
            if (!camera.Clips.Keeps(face.Box) || !TryFootprint(face, out var loops, out double error))
                continue;
            var margin = camera.ScreenMargin(face.Box, fit + error);
            // A nonzero degree of the projected, oriented trim boundary proves
            // coverage even when the original surface folds inside its domain.
            // The original face box bounds the depth of any such preimage.
            if (Raster(loops, margin, null, face.Box.Min.Z - fit - error)) CurvedOccluders++;
        }
        foreach (var face in faces)
        {
            if (face.Triangles == null || work <= 0 || !face.Opaque) continue;
            foreach (var triangle in face.Triangles.ExactTriangles())
            {
                if (--work <= 0) break;
                if ((work & 8191) == 0) cancel?.Invoke();
                var projected = camera.Bounds(triangle.Bounds);
                if (projected.Right - projected.Left < step || projected.Top - projected.Bottom < step) continue;
                var plane = new Plane(triangle.A, triangle.Normal);
                RasterPlanar(new[] {new List<Point3d> {triangle.A, triangle.B, triangle.C}}, plane, fit);
            }
        }
        if (Written == 0) { levels.Clear(); return; }
        while (levels[^1].Width > 1 || levels[^1].Height > 1)
        {
            cancel?.Invoke();
            var child = levels[^1];
            var parent = Allocate((child.Width + 1) / 2, (child.Height + 1) / 2);
            for (int y = 0; y < child.Height; y++)
                for (int x = 0; x < child.Width; x++)
                {
                    int p = (y / 2) * parent.Width + x / 2;
                    parent.Depth[p] = Math.Min(parent.Depth[p], child.Depth[y * child.Width + x]);
                }
            levels.Add(parent);
        }

        IEnumerable<V2Face> Candidates(bool planar, int count) => faces
            .Where(face => face.Face != null && face.Opaque && face.Plane.HasValue == planar &&
                face.Screen.Area >= step * step * (planar ? 16 : 256))
            .OrderByDescending(face => face.Screen.Area).Take(count)
            .OrderByDescending(face => face.Box.Center.Z);
    }

    internal bool Hides(BoundingBox target)
    {
        if (levels.Count == 0 || !target.IsValid) return false;
        double pad = fit + Math.Max(target.Min.MaximumCoordinate, target.Max.MaximumCoordinate) * 1e-13;
        var enlarged = target;
        enlarged.Inflate(pad);
        return Hides(camera.Bounds(enlarged), target.Max.Z, pad);
    }

    // The caller supplies an outward-rounded screen window and a physical depth upper bound.
    internal bool Hides(Rect clippedScreen, double localMaxZ, double pad)
    {
        if (!Available || !clippedScreen.HasArea || !double.IsFinite(localMaxZ) ||
            !double.IsFinite(pad) || pad < 0) return false;
        int left = X(clippedScreen.Left), right = X(clippedScreen.Right);
        int bottom = Y(clippedScreen.Bottom), top = Y(clippedScreen.Top);
        var grid = levels[0];
        if (left < 0 || bottom < 0 || right >= grid.Width || top >= grid.Height) return false;
        return Covered(levels.Count - 1, 0, 0);

        bool Covered(int level, int x, int y)
        {
            int size = 1 << level;
            int edgeX = x * size, edgeY = y * size;
            if (edgeX > right || edgeY > top || edgeX + size <= left || edgeY + size <= bottom) return true;
            var gridAtLevel = levels[level];
            if (x >= gridAtLevel.Width || y >= gridAtLevel.Height) return true;
            if (gridAtLevel.Depth[y * gridAtLevel.Width + x] > localMaxZ + pad) return true;
            if (level == 0) return false;
            return Covered(level - 1, x * 2, y * 2) && Covered(level - 1, x * 2 + 1, y * 2) &&
                Covered(level - 1, x * 2, y * 2 + 1) && Covered(level - 1, x * 2 + 1, y * 2 + 1);
        }
    }

    private void RasterPlanar(List<Point3d>[] loops, Plane plane, double worldMargin)
    {
        if (!plane.IsValid) return;
        // Intersect each ring with the retained half spaces. A concave ring may
        // produce retraced clip edges, whose scanline crossings cancel in pairs.
        for (int i = 0; i < loops.Length; i++)
            loops[i] = Clip(loops[i], camera.Clips, worldMargin);
        if (loops[0].Count < 3) return;
        var modelBox = new BoundingBox(loops[0]);
        worldMargin += Math.Max(1, Math.Max(modelBox.Min.MaximumCoordinate, modelBox.Max.MaximumCoordinate)) * 1e-13;
        if (V2PlanarDepth.TryCreate(plane, camera, worldMargin, out var depth))
            Raster(loops, camera.ScreenMargin(modelBox, worldMargin), depth, 0);
    }

    private static List<Point3d> Clip(List<Point3d> loop, V2ClipPlanes clips, double retreat)
    {
        if (!clips.Active) return loop;
        foreach (var plane in clips.Planes)
        {
            if (loop.Count < 3) return loop;
            var next = new List<Point3d>(loop.Count + 2);
            var a = loop[^1];
            double da = plane.DistanceTo(a) + retreat;
            foreach (var b in loop)
            {
                double db = plane.DistanceTo(b) + retreat;
                if ((da > 0) != (db > 0)) next.Add(a + (b - a) * (da / (da - db)));
                if (db <= 0) next.Add(b);
                (a, da) = (b, db);
            }
            loop = next;
        }
        return loop;
    }

    private bool Raster(List<Point3d>[] loops, double margin, V2PlanarDepth? field, double constant)
    {
        if (!double.IsFinite(margin) || work <= 0) return false;
        var edges = new List<Edge>();
        double left = double.PositiveInfinity, right = double.NegativeInfinity;
        double bottom = double.PositiveInfinity, top = double.NegativeInfinity;
        foreach (var loop in loops)
        {
            if (loop.Count < 3) continue;
            var a = camera.Project(loop[^1]);
            foreach (var vertex in loop)
            {
                var b = camera.Project(vertex);
                if (!a.IsValid || !b.IsValid) return false;
                left = Math.Min(left, b.X);
                right = Math.Max(right, b.X);
                bottom = Math.Min(bottom, b.Y);
                top = Math.Max(top, b.Y);
                if (a != b) edges.Add(a.Y <= b.Y ? new Edge(a.X, a.Y, b.X, b.Y) : new Edge(b.X, b.Y, a.X, a.Y));
                a = b;
            }
        }
        if (edges.Count < 3 || right - left < step || top - bottom < step) return false;
        edges.Sort((a, b) => a.Y0.CompareTo(b.Y0));
        double scale = Math.Max(Math.Max(Math.Abs(left), Math.Abs(right)), Math.Max(Math.Abs(bottom), Math.Abs(top)));
        // L-infinity distance at the pixel center bounds the whole closed pixel.
        double band = margin + step + Math.Max(scale, 1) * 1e-12;
        var grid = levels[0];
        int firstX = Math.Max(0, X(left)), lastX = Math.Min(grid.Width - 1, X(right));
        int firstY = Math.Max(0, Y(bottom)), lastY = Math.Min(grid.Height - 1, Y(top));
        var depth = field.GetValueOrDefault();
        double fieldSlack = 1e-12 * (Math.Abs(depth.Origin) + (Math.Abs(depth.X) + Math.Abs(depth.Y)) * (scale + step));
        double worst = (Math.Min(depth.X, 0) + Math.Min(depth.Y, 0)) * step - fieldSlack;
        var active = new List<Edge>();
        var stations = new List<double>();
        var boundary = new List<(double Left, double Right)>();
        int pending = 0;
        bool changed = false;
        for (int y = firstY; y <= lastY && work > 0; y++)
        {
            if ((y & 63) == 0) cancel?.Invoke();
            double py = y0 + (y + .5) * step;
            double low = py - band, high = py + band;
            while (pending < edges.Count && edges[pending].Y0 <= high) active.Add(edges[pending++]);
            active.RemoveAll(edge => edge.Y1 < low);
            stations.Clear();
            boundary.Clear();
            foreach (var edge in active)
            {
                if (py >= edge.Y0 && py < edge.Y1) stations.Add(edge.At(py));
                double a = edge.Y0 == edge.Y1 ? edge.X0 : edge.At(Math.Max(low, edge.Y0));
                double b = edge.Y0 == edge.Y1 ? edge.X1 : edge.At(Math.Min(high, edge.Y1));
                boundary.Add((Math.Min(a, b) - band, Math.Max(a, b) + band));
            }
            if (stations.Count == 0 || (stations.Count & 1) != 0) continue;
            stations.Sort();
            boundary.Sort((a, b) => a.Left.CompareTo(b.Left));
            int blocker = 0;
            double rowDepth = depth.At(x0, y0 + y * step) + worst;
            for (int j = 1; j < stations.Count && work > 0; j += 2)
            {
                int start = Math.Max(firstX, X(stations[j - 1] + step * .5));
                int end = Math.Min(lastX, X(stations[j] + step * .5));
                for (int x = start; x <= end && work > 0; x++, work--)
                {
                    double px = x0 + (x + .5) * step;
                    if (px <= stations[j - 1] || px >= stations[j]) continue;
                    while (blocker < boundary.Count && boundary[blocker].Right < px) blocker++;
                    if (blocker < boundary.Count && boundary[blocker].Left <= px)
                    {
                        x = Math.Min(end, X(boundary[blocker].Right - step * .5));
                        continue;
                    }
                    double z = field.HasValue ? rowDepth + depth.X * (x * step) : constant;
                    if (depth.Reciprocal)
                    {
                        if (!(z > 0)) continue;
                        z = -1 / z;
                    }
                    int index = y * grid.Width + x;
                    if (z <= grid.Depth[index]) continue;
                    if (double.IsNegativeInfinity(grid.Depth[index])) Written++;
                    grid.Depth[index] = z;
                    changed = true;
                }
            }
        }
        if (changed) Occluders++;
        return changed;
    }

    private int X(double x) => (int)Math.Clamp(Math.Floor((x - x0) / step), int.MinValue, int.MaxValue);
    private int Y(double y) => (int)Math.Clamp(Math.Floor((y - y0) / step), int.MinValue, int.MaxValue);

    private static Level Allocate(int width, int height)
    {
        var depth = new double[checked(width * height)];
        Array.Fill(depth, double.PositiveInfinity);
        return new Level(width, height, depth);
    }

    private bool TryFootprint(V2Face entry, out List<Point3d>[] loops, out double error)
    {
        loops = null;
        error = 0;
        var rings = new List<List<Point3d>>();
        List<Point3d> outer = null;
        double chord = Math.Max(fit * .25, entry.Box.Diagonal.Length * 1e-5);
        var face = entry.Face;
        foreach (var loop in face.Loops)
        {
            if (loop.LoopType is not (BrepLoopType.Outer or BrepLoopType.Inner)) return false;
            foreach (var trim in loop.Trims)
            {
                if (trim.Edge is not BrepEdge edge || !double.IsFinite(edge.Tolerance) || edge.Tolerance < 0)
                    return false;
                error = Math.Max(error, edge.Tolerance);
            }
            using var curve = loop.To3dCurve();
            if (curve == null || !curve.IsClosed) return false;
            List<Point3d> polygon;
            if (curve.TryGetPolyline(out var linear)) polygon = linear.ToList();
            else
            {
                var bounded = V2BoundaryCollector.SubdivideProjected(curve, chord);
                if (bounded == null) return false;
                polygon = bounded.Select(point => point.Point).ToList();
                error += chord;
            }
            if (polygon.Count < 4 || polygon[0].DistanceTo(polygon[^1]) > fit * .01)
                return false;
            polygon.RemoveAt(polygon.Count - 1);
            if (loop.LoopType == BrepLoopType.Outer)
            {
                if (outer != null) return false;
                outer = polygon;
            }
            else rings.Add(polygon);
        }
        if (outer == null) return false;
        rings.Insert(0, outer);
        loops = rings.ToArray();
        return true;
    }
}
