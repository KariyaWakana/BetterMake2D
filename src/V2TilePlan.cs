using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Key = BetterMake2D.V2PlanarArrangement.Key;

namespace BetterMake2D;

internal readonly record struct V2TileWindow(long X0, long Y0, long X1, long Y1)
{
    internal Rect Bounds(double grid) => new(X0 * grid, Y0 * grid, X1 * grid, Y1 * grid);
    internal V2TileWindow Scale(int scale) => new(X0 * scale, Y0 * scale, X1 * scale, Y1 * scale);
    internal bool Contains(Key point) => X0 <= point.X && Y0 <= point.Y && point.X <= X1 && point.Y <= Y1;
    internal bool Contains(V2TileWindow child) =>
        X0 <= child.X0 && Y0 <= child.Y0 && X1 >= child.X1 && Y1 >= child.Y1;

    internal static bool Touches(Rect a, Rect b) =>
        a.Left <= b.Right && a.Right >= b.Left && a.Bottom <= b.Top && a.Top >= b.Bottom;
}

internal sealed record V2TilePlan(V2TileWindow Window, int[] VisibleCandidates, int[] HiddenCandidates, int ProvenOwner);

// Windows own candidate demand. Canonical face and pair traversal owns the one
// global copy of each discretization; windows only route those shared segments.
internal sealed class V2TileSchedule
{
    private sealed record Node(V2TileWindow Window, int Tile, Node Left = null, Node Right = null);
    private readonly Node root;
    private readonly Dictionary<int, List<int>> visibleWindows = new();
    private readonly HashSet<int> boundaryOwners = new();
    private readonly Dictionary<int, (int First, int Second)> contactPartners = new();
    private readonly double grid, imageGuard, fit;
    private readonly V2HiZ hiZ;
    private readonly IProjectionCamera camera;
    internal V2TilePlan[] Tiles { get; }
    internal long FaceCandidates { get; }
    internal long HiddenFaces { get; }
    // Additional geometric seams cost clipping, probes and stitching. Retain
    // the density-based arrangement when a draft plan proves little work away.
    internal bool Relevant => HiddenFaces * 100 >= Math.Max(1, FaceCandidates) ||
        Tiles.Any(tile => tile.ProvenOwner >= 0);

    private V2TileSchedule(Node root, V2TilePlan[] tiles, long candidates, long hidden, double grid,
        double imageGuard, double fit, V2HiZ hiZ, IProjectionCamera camera)
    {
        this.root = root;
        this.grid = grid;
        this.imageGuard = imageGuard;
        this.fit = fit;
        this.hiZ = hiZ;
        this.camera = camera;
        (Tiles, FaceCandidates, HiddenFaces) = (tiles, candidates, hidden);
        for (int i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            if (tile.ProvenOwner >= 0) continue;
            foreach (int face in tile.VisibleCandidates)
            {
                if (!visibleWindows.TryGetValue(face, out var slots))
                    visibleWindows.Add(face, slots = new List<int>());
                slots.Add(i);
                boundaryOwners.Add(face);
            }
            boundaryOwners.UnionWith(tile.HiddenCandidates);
        }
    }

    internal static V2TileSchedule Create(int[] members, V2Face[] faces, IProjectionCamera camera,
        V2HiZ hiZ, V2SurfaceBounds surfaceBounds, double fit, double grid, double imageTolerance,
        int limit, int minimumDepth, Func<int, bool> hiddenLayerMatches, Action cancel)
    {
        if (limit <= 0 || hiZ?.Available != true || members.Length == 0) return null;
        long estimate = members.Sum(id => Weight(faces[id]));
        if (minimumDepth == 0 && estimate <= limit) return null;
        int depth = Math.Max(minimumDepth, Math.Clamp((int)Math.Ceiling(Math.Log2((double)estimate / limit)), 2, 5));
        double guard = Math.Max(grid * 16, imageTolerance * 2);
        var extent = new Rect(members.Min(id => faces[id].Screen.Left), members.Min(id => faces[id].Screen.Bottom),
            members.Max(id => faces[id].Screen.Right), members.Max(id => faces[id].Screen.Top)).Grow(guard * 4);
        if (!extent.HasArea) return null;
        var a = V2PlanarArrangement.Snap(new Point2d(extent.Left, extent.Bottom), grid);
        var b = V2PlanarArrangement.Snap(new Point2d(extent.Right, extent.Top), grid);
        var tiles = new List<V2TilePlan>();
        var covers = new Dictionary<int, PlanarCover>();
        long candidates = 0, hidden = 0;
        try
        {
            var root = Visit(new V2TileWindow(a.X, a.Y, b.X, b.Y), members, 0);
            return new V2TileSchedule(root, tiles.ToArray(), candidates, hidden, grid, guard, fit, hiZ, camera);
        }
        finally { foreach (var cover in covers.Values) cover?.Dispose(); }

        Node Visit(V2TileWindow window, int[] possible, int level)
        {
            cancel?.Invoke();
            long width = window.X1 - window.X0, height = window.Y1 - window.Y0;
            if (level < depth && Math.Max(width, height) >= 8)
            {
                bool x = width >= height;
                long mid = x ? window.X0 + width / 2 : window.Y0 + height / 2;
                var left = x ? window with { X1 = mid } : window with { Y1 = mid };
                var right = x ? window with { X0 = mid } : window with { Y0 = mid };
                return new Node(window, -1, Child(left), Child(right));

                Node Child(V2TileWindow child)
                {
                    var screen = child.Bounds(grid).Grow(guard * 2);
                    return Visit(child, possible.Where(id => V2TileWindow.Touches(faces[id].Screen, screen)).ToArray(), level + 1);
                }
            }

            var area = window.Bounds(grid).Grow(guard);
            var front = new List<int>();
            var rear = new List<int>();
            var upper = new double[possible.Length];
            for (int i = 0; i < possible.Length; i++)
            {
                int id = possible[i];
                var face = faces[id];
                var clipped = area.Intersection(face.Screen.Grow(guard));
                double pad = fit + Math.Max(face.Box.Min.MaximumCoordinate, face.Box.Max.MaximumCoordinate) * 1e-12;
                upper[i] = UpperDepth(id, clipped, pad);
                // A face barely meeting a guarded window may still supply a
                // seam vertex. Keep it when the projected rectangle degenerates.
                if (hiZ.Hides(clipped, upper[i], pad))
                {
                    hidden++;
                    if (hiddenLayerMatches?.Invoke(id) == true) rear.Add(id);
                }
                else front.Add(id);
            }
            candidates += possible.Length;
            int proven = rear.Count == 0 ? Prove(area, possible, upper, front) : -1;
            front.Sort();
            rear.Sort();
            int index = tiles.Count;
            tiles.Add(new V2TilePlan(window, front.ToArray(), rear.ToArray(), proven));
            return new Node(window, index);
        }

        int Prove(Rect area, int[] possible, double[] upper, List<int> front)
        {
            foreach (int id in front)
            {
                var face = faces[id];
                if (!face.Opaque || face.Triangles != null || face.Plane is not Plane plane ||
                    !camera.Clips.Keeps(face.Box) || face.Screen.Intersection(area) != area)
                    continue;
                if (!V2PlanarDepth.TryCreate(plane, camera, 2 * fit, out var field) ||
                    !field.TryExtrema(area, out double minimum, out _)) continue;
                bool clear = true;
                for (int i = 0; i < possible.Length && clear; i++)
                    clear = possible[i] == id || upper[i] < minimum - 2 * fit;
                if (!clear) continue;
                if (!covers.TryGetValue(id, out var cover))
                    covers.Add(id, cover = PlanarCover.Create(face.Face, face.Box, fit, camera.Perspective ? camera : null));
                if (cover?.Covers(area) == true) return id;
            }
            return -1;
        }

        double UpperDepth(int id, Rect window, double pad)
        {
            var face = faces[id];
            double boxMax = face.Box.Max.Z;
            if (!window.HasArea) return boxMax;
            if (face.Plane is Plane plane && V2PlanarDepth.TryCreate(plane, camera, -pad, out var field) &&
                field.TryExtrema(window, out _, out double front)) return Math.Min(front, boxMax);
            if (face.Triangles != null) return Math.Min(face.Triangles.UpperDepth(window, camera), boxMax);
            return surfaceBounds.UpperDepth(id, window, camera, boxMax);
        }

        static long Weight(V2Face face) => face.Plane.HasValue ? 20 :
            face.Triangles != null && face.Face == null ? Math.Max(512, face.Triangles.TriangleCount / 8) : 320;
    }

    internal bool NeedsBoundary(int id) => boundaryOwners.Contains(id);
    internal V2TileWindow Extent(int scale) => root.Window.Scale(scale);
    internal void RememberContact(int curveId, int first, int second) => contactPartners.Add(curveId, (first, second));

    internal bool NeedsContact(int first, int second, Rect overlap, BoundingBox common, out bool covered)
    {
        covered = false;
        if (!visibleWindows.TryGetValue(first, out var a) || !visibleWindows.TryGetValue(second, out var b))
            return false;
        var commonScreen = common;
        commonScreen.Inflate(fit);
        var image = camera.Bounds(commonScreen).Grow(imageGuard).Intersection(overlap.Grow(imageGuard));
        int j = 0, k = 0;
        while (j < a.Count && k < b.Count)
        {
            int left = a[j], right = b[k];
            if (left < right) { j++; continue; }
            if (left > right) { k++; continue; }
            j++;
            k++;
            var area = Tiles[left].Window.Bounds(grid).Grow(imageGuard);
            if (!overlap.Intersection(area).HasArea) continue;
            var clipped = image.Intersection(area);
            if (!clipped.HasArea || !hiZ.Hides(clipped, common.Max.Z, fit * 2))
                return true;
            covered = true;
        }
        return false;
    }

    internal void Overlapping(Key start, Key end, int scale, List<int> result)
    {
        result.Clear();
        Visit(root);
        void Visit(Node node)
        {
            var w = node.Window.Scale(scale);
            if (Math.Max(start.X, end.X) < w.X0 || Math.Min(start.X, end.X) > w.X1 ||
                Math.Max(start.Y, end.Y) < w.Y0 || Math.Min(start.Y, end.Y) > w.Y1) return;
            if (node.Tile >= 0) result.Add(node.Tile);
            else { Visit(node.Left); Visit(node.Right); }
        }
    }

    internal bool Allows(V2TilePlan tile, V2SourceSpan span)
    {
        if (tile.ProvenOwner >= 0) return false;
        if (span.Face < 0) return true;
        if (span.Kind == "Contact" && contactPartners.TryGetValue(span.CurveId, out var pair))
            return Array.BinarySearch(tile.VisibleCandidates, pair.First) >= 0 &&
                Array.BinarySearch(tile.VisibleCandidates, pair.Second) >= 0;
        return Array.BinarySearch(tile.VisibleCandidates, span.Face) >= 0 ||
            Array.BinarySearch(tile.HiddenCandidates, span.Face) >= 0;
    }
}
