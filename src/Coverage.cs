using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

internal readonly record struct Rect(double Left, double Bottom, double Right, double Top)
{
    internal static Rect From(BoundingBox b) => new(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y);
    internal bool HasArea => Right > Left && Top > Bottom;
    internal double Area => (Right - Left) * (Top - Bottom);
    internal Rect Grow(double t) => new(Left - t, Bottom - t, Right + t, Top + t);
    internal Rect Intersection(Rect b) =>
        new(Math.Max(Left, b.Left), Math.Max(Bottom, b.Bottom), Math.Min(Right, b.Right), Math.Min(Top, b.Top));
    internal BoundingBox Box => new(new Point3d(Left, Bottom, 0), new Point3d(Right, Top, 0));
    internal Curve ToCurve() => new Rectangle3d(Plane.WorldXY, new Interval(Left, Right), new Interval(Bottom, Top)).ToNurbsCurve();

    internal Rect[] Quarters()
    {
        double x = (Left + Right) * .5;
        double y = (Bottom + Top) * .5;
        return new[] { new Rect(Left, Bottom, x, y), new Rect(x, Bottom, Right, y),
            new Rect(Left, y, x, Top), new Rect(x, y, Right, Top) };
    }
}

internal sealed class PlanarCover : IDisposable
{
    private readonly Curve[] loops;
    private readonly Plane plane;
    private readonly double tolerance;
    internal Rect Bounds { get; }
    internal Rect? Rectangle { get; }
    internal double MaxZ { get; }
    internal IReadOnlyList<Curve> Loops => loops;

    private PlanarCover(Curve[] loops, Plane plane, BoundingBox box, double tolerance)
    {
        this.loops = loops;
        this.plane = plane;
        this.tolerance = tolerance;
        Bounds = Rect.From(box);
        MaxZ = box.Max.Z;
        Rectangle = loops.Length == 1 ? ExactRectangle(loops[0]) : null;
    }

    internal static PlanarCover Create(BrepFace face, BoundingBox box, double tolerance, IProjectionCamera camera = null)
    {
        if (!face.TryGetPlane(out var plane, tolerance * .01) ||
            camera?.Perspective != true && Math.Abs(plane.Normal.Z) < .01)
            return null;
        double screenTolerance = camera?.ScreenMargin(box, tolerance) ?? tolerance;
        if (!double.IsFinite(screenTolerance)) return null;
        var loops = new List<Curve>();
        foreach (var loop in face.Loops.OrderBy(loop => loop.LoopType != BrepLoopType.Outer))
        {
            using var source = camera == null ? null : loop.To3dCurve();
            var curve = camera == null ? loop.To3dCurve() : source == null ? null : camera.ProjectCurve(source);
            if (curve == null || camera == null && !curve.Transform(Transform.PlanarProjection(Plane.WorldXY)) ||
                !curve.IsValid || !curve.IsClosed ||
                (loop.LoopType != BrepLoopType.Outer && loop.LoopType != BrepLoopType.Inner))
            {
                curve?.Dispose();
                foreach (var previous in loops)
                    previous.Dispose();
                return null;
            }
            loops.Add(curve);
        }
        if (loops.Count == 0)
            return null;
        return new PlanarCover(loops.ToArray(), plane, box, screenTolerance);
    }

    internal bool Covers(Rect rect)
    {
        if (Rectangle is Rect box)
            return box.Left <= rect.Left && box.Right >= rect.Right &&
                box.Bottom <= rect.Bottom && box.Top >= rect.Top;

        using var outline = rect.Grow(tolerance).ToCurve();
        if (Curve.PlanarClosedCurveRelationship(outline, loops[0], Plane.WorldXY, tolerance) != RegionContainment.AInsideB)
            return false;
        for (int i = 1; i < loops.Length; i++)
            if (Curve.PlanarClosedCurveRelationship(outline, loops[i], Plane.WorldXY, tolerance) != RegionContainment.Disjoint)
                return false;
        return true;
    }

    internal double FarDepth(Rect box)
    {
        var normal = plane.Normal;
        var origin = plane.Origin;
        double Z(double x, double y) => origin.Z - (normal.X * (x - origin.X) + normal.Y * (y - origin.Y)) / normal.Z;
        return Math.Min(Math.Min(Z(box.Left, box.Bottom), Z(box.Right, box.Bottom)),
            Math.Min(Z(box.Left, box.Top), Z(box.Right, box.Top))) - tolerance / Math.Abs(normal.Z);
    }

    internal bool StrictlyAheadOf(Plane rear, Rect overlap)
    {
        if (Math.Abs(rear.Normal.Z) < .01)
            return false;
        double separation = tolerance * (2 + 1 / Math.Abs(plane.Normal.Z) + 1 / Math.Abs(rear.Normal.Z));
        return Ahead(overlap.Left, overlap.Bottom) && Ahead(overlap.Left, overlap.Top) &&
            Ahead(overlap.Right, overlap.Bottom) && Ahead(overlap.Right, overlap.Top);

        bool Ahead(double x, double y) => Depth(plane, x, y) - Depth(rear, x, y) > separation;
        static double Depth(Plane surface, double x, double y) => surface.Origin.Z -
            (surface.Normal.X * (x - surface.Origin.X) + surface.Normal.Y * (y - surface.Origin.Y)) / surface.Normal.Z;
    }

    internal static Rect? ExactRectangle(Curve curve)
    {
        if (!curve.TryGetPolyline(out var polygon) || polygon.Count != 5 || polygon[0] != polygon[4])
            return null;
        for (int i = 0; i < 4; i++)
        {
            if ((polygon[i].X == polygon[i + 1].X) == (polygon[i].Y == polygon[i + 1].Y))
                return null;
        }
        var box = Rect.From(polygon.BoundingBox);
        var corners = new HashSet<(double X, double Y)>();
        for (int i = 0; i < 4; i++)
        {
            var p = polygon[i];
            if ((p.X != box.Left && p.X != box.Right) || (p.Y != box.Bottom && p.Y != box.Top))
                return null;
            corners.Add((p.X, p.Y));
        }
        return corners.Count == 4 && box.HasArea ? box : null;
    }

    public void Dispose()
    {
        foreach (var loop in loops)
            loop.Dispose();
    }
}

// A tile stores a lower bound on the front surface's Z across its entire area.
// Positive Z points toward the viewer. Unknown coverage always keeps its target.
internal sealed class CoverageIndex : IDisposable
{
    private const int MaximumDepth = 5;
    private readonly Tile root;
    private readonly PlanarCover[] covers;
    private readonly double tolerance;
    private readonly RTree coverIndex = new();

    internal CoverageIndex(Rect extent, IEnumerable<PlanarCover> covers, double tolerance)
    {
        this.covers = covers.OrderByDescending(cover => cover.Bounds.Area).ToArray();
        this.tolerance = tolerance;
        root = new Tile(extent, this.covers, 0);
        for (int i = 0; i < this.covers.Length; i++)
            coverIndex.Insert(this.covers[i].Bounds.Box, i);
    }

    internal bool Hides(BoundingBox box, Plane? targetPlane = null)
    {
        if (covers.Length == 0)
            return false;
        var target = Rect.From(box).Grow(tolerance);
        double depth = box.Max.Z + tolerance;
        bool directlyCovered = false;
        var candidates = new List<PlanarCover>();
        coverIndex.Search(target.Box, (_, hit) =>
        {
            var cover = covers[hit.Id];
            if (cover.Bounds.Intersection(target) == target &&
                (cover.MaxZ > depth && cover.FarDepth(target) > depth ||
                 targetPlane.HasValue && cover.StrictlyAheadOf(targetPlane.Value, target)) && cover.Covers(target))
            {
                directlyCovered = true;
                hit.Cancel = true;
            }
            else if (cover.MaxZ > depth && cover.Rectangle.HasValue)
                candidates.Add(cover);
        });
        if (directlyCovered)
            return true;
        return RectanglesCover(target, depth, candidates) || Test(root, target, depth);
    }

    private bool Test(Tile tile, Rect target, double depth)
    {
        if (!tile.Rect.Intersection(target).HasArea)
            return true;
        if (tile.Covers.Length == 0 || tile.Covers.All(cover => cover.MaxZ <= depth))
            return false;
        if (tile.Front > depth)
            return true;
        if (tile.Level == MaximumDepth)
            return false;
        tile.Children ??= tile.Rect.Quarters().Select(rect => new Tile(rect,
            tile.Covers.Where(cover => cover.Bounds.Intersection(rect).HasArea).ToArray(), tile.Level + 1)).ToArray();
        return tile.Children.All(child => Test(child, target, depth));
    }

    // Exact interval coverage for axis-aligned rectangular planar footprints.
    // This also certifies abutting rectangle seams without rounding a gap shut.
    private static bool RectanglesCover(Rect target, double depth, List<PlanarCover> covers)
    {
        var rectangles = new List<Rect>();
        foreach (var cover in covers)
        {
            if (cover.Rectangle is not Rect box)
                continue;
            var overlap = box.Intersection(target);
            if (overlap.HasArea && cover.FarDepth(overlap) > depth)
                rectangles.Add(overlap);
        }
        if (rectangles.Count < 2)
            return false;
        var xValues = rectangles.SelectMany(box => new[] { box.Left, box.Right })
            .Append(target.Left).Append(target.Right).Distinct().OrderBy(x => x).ToArray();
        for (int i = 0; i < xValues.Length - 1; i++)
        {
            double y = target.Bottom;
            foreach (var box in rectangles.Where(box => box.Left <= xValues[i] && box.Right >= xValues[i + 1])
                .OrderBy(box => box.Bottom))
            {
                if (box.Bottom > y)
                    break;
                y = Math.Max(y, box.Top);
            }
            if (y < target.Top)
                return false;
        }
        return true;
    }

    public void Dispose() => coverIndex.Dispose();

    private sealed class Tile
    {
        internal Rect Rect { get; }
        internal PlanarCover[] Covers { get; }
        internal int Level { get; }
        internal Tile[] Children { get; set; }
        private double? front;
        internal double Front => front ??= Covers.Where(cover => cover.Covers(Rect))
            .Select(cover => cover.FarDepth(Rect)).DefaultIfEmpty(double.NegativeInfinity).Max();

        internal Tile(Rect rect, PlanarCover[] covers, int level) =>
            (Rect, Covers, Level) = (rect, covers, level);
    }
}



