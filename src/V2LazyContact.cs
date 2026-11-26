
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Rhino.Geometry;

namespace BetterMake2D;

internal readonly record struct V2ContactPair(int First, int Second);

internal readonly record struct V2LazyContactPass(int Round, int Candidates, int Native,
    int Empty, int Points, int CurveHits, double NativeSeconds, int RebuiltTiles, int Tiles, double WallSeconds)
{
    public override string ToString() => $"Lazy contact round {Round}: " +
        $"{Candidates} candidates, {Native} BrepBrep ({Empty} empty, {Points} point-only, {CurveHits} curve hits), " +
        $"{NativeSeconds:F2} s native, {RebuiltTiles}/{Tiles} tiles rearranged, {WallSeconds:F2} s wall.";
}

internal sealed class V2ContactFront
{
    // The ninth cell replaces the eight separate boxes with their union.
    // A merged box can only increase the native candidate set.
    private const int Limit = 8;
    private sealed class Windows(Rect first)
    {
        internal readonly List<Rect> Rectangles = new() { first };
        internal bool Merged;
    }

    private readonly Dictionary<int, Windows> owners = new();
    internal int Count => owners.Count;
    internal IEnumerable<int> Owners => owners.Keys;

    internal void Read(V2PlanarArrangement plan, double guard)
    {
        foreach (var cell in plan.Cells)
        {
            if (cell.Owner < 0 || cell.Outer.Length == 0) continue;
            long left = long.MaxValue, bottom = long.MaxValue, right = long.MinValue, top = long.MinValue;
            foreach (int side in cell.Outer)
            {
                var vertex = plan.Start(side);
                left = Math.Min(left, vertex.X);
                bottom = Math.Min(bottom, vertex.Y);
                right = Math.Max(right, vertex.X);
                top = Math.Max(top, vertex.Y);
            }
            var min = plan.Point(new V2PlanarArrangement.Key(left, bottom));
            var max = plan.Point(new V2PlanarArrangement.Key(right, top));
            Add(cell.Owner, new Rect(min.X, min.Y, max.X, max.Y).Grow(guard));
        }
    }

    internal void Merge(V2ContactFront front)
    {
        foreach (var (owner, windows) in front.owners)
            foreach (var window in windows.Rectangles) Add(owner, window);
    }

    internal bool TryGet(int face, out List<Rect> windows)
    {
        windows = owners.TryGetValue(face, out var value) ? value.Rectangles : null;
        return windows != null;
    }

    private void Add(int owner, Rect window)
    {
        if (!owners.TryGetValue(owner, out var current))
        {
            owners.Add(owner, new Windows(window));
            return;
        }
        var boxes = current.Rectangles;
        if (!current.Merged && boxes.Count < Limit)
        {
            boxes.Add(window);
            return;
        }
        var union = window;
        foreach (var box in boxes)
            union = new Rect(Math.Min(union.Left, box.Left), Math.Min(union.Bottom, box.Bottom),
                Math.Max(union.Right, box.Right), Math.Max(union.Top, box.Top));
        boxes.Clear();
        boxes.Add(union);
        current.Merged = true;
    }
}

internal sealed class V2LazyContact(V2Face[] faces, IProjectionCamera camera, V2SurfaceBounds bounds,
    V2Drawing drawing, double tolerance, Action cancel, Action<string> progress) : IDisposable
{
    private readonly List<V2ContactPair> unresolved = new();
    private bool recordedInitial;
    private readonly V2PlanarDepth?[] near = new V2PlanarDepth?[faces.Length];
    private readonly V2PlanarDepth?[] far = new V2PlanarDepth?[faces.Length];
    private readonly bool[] prepared = new bool[faces.Length];

    public void Dispose()
    {
        unresolved.Clear();
        unresolved.TrimExcess();
    }

    internal bool HasPending => unresolved.Count != 0;
    internal int PendingCount => unresolved.Count;
    internal void Defer(int first, int second) => unresolved.Add(new V2ContactPair(first, second));

    internal List<V2ContactPair> Select(V2ContactFront front)
    {
        long began = Stopwatch.GetTimestamp();
        int retained = 0, neighbors = 0, outside = 0, behind = 0;
        var chosen = new List<V2ContactPair>();
        for (int k = 0; k < unresolved.Count; k++)
        {
            if ((k & 8191) == 0) cancel?.Invoke();
            var pair = unresolved[k];
            var a = faces[pair.First];
            var b = faces[pair.Second];
            bool seenA = front.TryGet(pair.First, out var left);
            bool seenB = front.TryGet(pair.Second, out var right);
            if (seenA || seenB) neighbors++;
            if (!seenA && !seenB)
            {
                unresolved[retained++] = pair;
                continue;
            }
            var common = BoundingBox.Intersection(a.Box, b.Box);
            double pad = tolerance + Math.Max(1, Math.Max(common.Min.MaximumCoordinate, common.Max.MaximumCoordinate)) * 1e-13;
            common.Inflate(pad);
            var overlap = camera.Bounds(common);
            bool touches = false;
            if (seenA && Demands(left, pair.First, pair.Second) ||
                seenB && Demands(right, pair.Second, pair.First))
            {
                chosen.Add(pair);
                continue;
            }
            if (touches) behind++;
            else outside++;
            unresolved[retained++] = pair;

            bool Demands(List<Rect> windows, int owner, int other)
            {
                foreach (var window in windows)
                {
                    var part = overlap.Intersection(window);
                    if (!V2TileWindow.Touches(overlap, window)) continue;
                    touches = true;
                    if (Depth(other, part).Upper >= Depth(owner, part).Lower - pad) return true;
                }
                return false;
            }
        }
        unresolved.RemoveRange(retained, unresolved.Count - retained);
        if (!recordedInitial)
        {
            drawing.LazyInitialNeighbors += neighbors;
            recordedInitial = true;
        }
        drawing.LazyDeferred2D += outside;
        drawing.LazyDeferredDepth += behind;
        progress?.Invoke($"Lazy contact front: {neighbors} owner neighbors, " +
            $"{outside} local 2D misses and {behind} local depth separations deferred; " +
            $"{chosen.Count} selected, {retained} still pending in {Stopwatch.GetElapsedTime(began).TotalSeconds:F2} s.");
        return chosen;
    }

    private (double Lower, double Upper) Depth(int id, Rect window)
    {
        var face = faces[id];
        double lo = face.Box.Min.Z, hi = face.Box.Max.Z;
        if (face.Plane is Plane plane)
        {
            if (!prepared[id])
            {
                if (V2PlanarDepth.TryCreate(plane, camera, -tolerance, out var closest)) near[id] = closest;
                if (V2PlanarDepth.TryCreate(plane, camera, tolerance, out var farthest)) far[id] = farthest;
                prepared[id] = true;
            }
            if (near[id] is V2PlanarDepth n && n.TryExtrema(window, out _, out double upper))
                hi = Math.Min(hi, upper);
            if (far[id] is V2PlanarDepth f && f.TryExtrema(window, out double lower, out _))
                lo = Math.Max(lo, lower);
            return lo <= hi ? (lo, hi) : (face.Box.Min.Z, face.Box.Max.Z);
        }
        if (face.Triangles != null)
        {
            var range = face.Triangles.DepthRange(window, camera);
            return (Math.Max(lo, range.Lower), Math.Min(hi, range.Upper));
        }
        return bounds.DepthRange(id, window, camera, lo, hi);
    }
}
