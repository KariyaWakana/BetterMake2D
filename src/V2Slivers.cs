using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Key = BetterMake2D.V2PlanarArrangement.Key;

namespace BetterMake2D;

internal static class V2Slivers
{
    internal static (V2PlanarArrangement Plan, int Absorbed, int Flattened) Clean(
        V2PlanarArrangement plan, double tolerance, double millimetersPerUnit, Action cancel,
        Action<string> progress = null, int rebuildTileLimit = 80000)
    {
        double clearance = Math.Min(Math.Max(plan.Grid * 2.5, tolerance * .04), .005 / millimetersPerUnit);
        progress?.Invoke("Post-stitch cleanup: absorbing sub-tolerance bands.");
        Compact(plan.EdgeCount, rebuildTileLimit);
        int absorbed = Absorb(plan, clearance / plan.Grid, cancel, out var owners);
        if (absorbed > 0)
        {
            Compact(plan.EdgeCount, rebuildTileLimit);
            plan = V2PlanarArrangement.FromBoundary(plan.VisibleEdges(owners), plan.Grid);
        }
        int flattened = 0;
        int steps = (int)Math.Min(2, Math.Floor(clearance / plan.Grid));
        if (steps > 0)
        {
            Compact(plan.EdgeCount, rebuildTileLimit);
            Rewrite("horizontal steps", Flatten(plan, true, steps, cancel));
            Compact(plan.EdgeCount, rebuildTileLimit);
            Rewrite("vertical steps", Flatten(plan, false, steps, cancel));
            Compact(plan.EdgeCount, rebuildTileLimit);
            Rewrite("short splices", CollapseSplices(plan, steps, cancel));
        }
        return (plan, absorbed, flattened);

        void Rewrite(string label, Dictionary<Key, Key> moves)
        {
            if (moves == null || moves.Count == 0) return;
            long changed = 0;
            var original = plan;
            try
            {
                Compact(original.EdgeCount, rebuildTileLimit);
                progress?.Invoke($"Post-stitch cleanup: rewriting {label} at {moves.Count} vertices.");
                plan = Reclassify(original, Revised(), tolerance, rebuildTileLimit, cancel, progress);
                flattened += checked((int)changed);
            }
            catch (InvalidOperationException)
            {
                // A rewrite is atomic. Keep the original closed boundary if
                // the proposed vertex moves cannot be arranged consistently.
                progress?.Invoke($"Post-stitch cleanup: {label} kept its original boundary.");
            }

            IEnumerable<V2OwnedEdge> Revised()
            {
                foreach (var edge in original.EnumerateVisibleEdges())
                {
                    var a = moves.GetValueOrDefault(edge.A, edge.A);
                    var b = moves.GetValueOrDefault(edge.B, edge.B);
                    if (a == b) { changed++; continue; }
                    yield return edge with { A = a, B = b };
                }
            }
        }
    }

    private static void Compact(int edges, int limit)
    {
        if (edges > limit * 3L)
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static int Absorb(V2PlanarArrangement plan, double clearance, Action cancel, out int[] owners)
    {
        owners = null;
        var regions = plan.Regions(includeBackground: true).ToArray();
        var regionOf = new int[plan.EdgeCount * 2];
        Array.Fill(regionOf, -1);
        var directions = new Vector2d[regions.Length];
        var lengths = new double[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            if ((i & 511) == 0) cancel?.Invoke();
            var cell = regions[i].Region;
            foreach (int side in cell.Outer) regionOf[side] = i;
            foreach (var hole in cell.Holes)
                foreach (int side in hole) regionOf[side] = i;
            if (cell.Holes.Count != 0) continue;
            var longest = Vector2d.Zero;
            foreach (int side in cell.Outer)
            {
                var v = Direction(plan.Start(side), plan.Start(side ^ 1));
                if (v.SquareLength > longest.SquareLength) longest = v;
            }
            double length = longest.Length;
            if (length < clearance * 16 || !longest.Unitize()) continue;
            var head = plan.Start(cell.Outer[0]);
            double low = 0, high = 0;
            foreach (int side in cell.Outer)
            {
                var point = Direction(head, plan.Start(side));
                double across = longest.X * point.Y - longest.Y * point.X;
                low = Math.Min(low, across);
                high = Math.Max(high, across);
            }
            // This is a width bound for the whole component, including long tapering cells.
            if (high - low > 2 * clearance || high - low > length / 16) continue;
            directions[i] = longest;
            lengths[i] = length;
        }

        int changed = 0;
        var neighborLengths = new Dictionary<int, double>();
        for (int i = 0; i < regions.Length; i++)
        {
            // A valid original may legitimately contribute a very narrow visible
            // fragment. Width alone certifies a cleanup candidate only in a void.
            if (regions[i].Owner >= 0 || !(lengths[i] > 0)) continue;
            double forward = 0, reverse = 0;
            neighborLengths.Clear();
            foreach (int side in regions[i].Region.Outer)
            {
                int neighbor = regionOf[side ^ 1];
                if (neighbor < 0 || lengths[neighbor] > 0 || regions[neighbor].Owner < 0) continue;
                double tangent = directions[i] * Direction(plan.Start(side), plan.Start(side ^ 1));
                forward += Math.Max(0, tangent);
                reverse += Math.Max(0, -tangent);
                int owner = regions[neighbor].Owner;
                neighborLengths[owner] = neighborLengths.GetValueOrDefault(owner) + Math.Abs(tangent);
            }
            // A supported band has substantial real material on both flanks.
            if (Math.Min(forward, reverse) < lengths[i] * .6) continue;
            int replacement = neighborLengths.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
            owners ??= Enumerable.Range(0, regionOf.Length).Select(plan.Owner).ToArray();
            foreach (int side in regions[i].Region.Outer) owners[side] = replacement;
            changed++;
        }
        return changed;
    }

    private static Dictionary<Key, Key> Flatten(V2PlanarArrangement plan, bool horizontal, int limit, Action cancel)
    {
        var edges = plan.VisibleEdges();
        var incident = new Dictionary<Key, int>(edges.Count);
        var fan = new int[edges.Count * 2];
        int[] roots = new int[edges.Count];
        long[] low = new long[edges.Count], high = new long[edges.Count];
        long[] start = new long[edges.Count], end = new long[edges.Count];
        var connected = new HashSet<int>();
        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            roots[i] = i;
            low[i] = Math.Min(Row(e.A), Row(e.B));
            high[i] = Math.Max(Row(e.A), Row(e.B));
            start[i] = Math.Min(Along(e.A), Along(e.B));
            end[i] = Math.Max(Along(e.A), Along(e.B));
            incident.TryAdd(e.A, -1);
            incident.TryAdd(e.B, -1);
        }
        for (int i = edges.Count - 1; i >= 0; i--)
        {
            Add(edges[i].A, i * 2);
            Add(edges[i].B, i * 2 + 1);
        }
        foreach (var entry in incident.Values)
        {
            int first = -1;
            for (int link = entry; link >= 0; link = fan[link])
            {
                int id = link / 2;
                if (!IsRail(id)) continue;
                if (first < 0) first = id;
                else Unite(first, id);
            }
        }
        for (int i = 0; i < edges.Count; i++)
        {
            if ((i & 4095) == 0) cancel?.Invoke();
            var e = edges[i];
            long gap = high[i] - low[i];
            if (gap == 0 || gap > limit || Along(e.A) != Along(e.B)) continue;
            for (int left = incident[e.A]; left >= 0; left = fan[left])
                for (int right = incident[e.B]; right >= 0; right = fan[right])
                {
                    int a = left / 2, b = right / 2;
                    if (!IsRail(a) || !IsRail(b)) continue;
                    long da = Extent(a, e.A), db = Extent(b, e.B);
                    if (Math.Sign(da) == Math.Sign(db) ||
                        Math.Min(RailSupport(a, e.A, da), RailSupport(b, e.B, db)) < limit * 12)
                        continue;
                    var first = Oriented(edges[a]);
                    var second = Oriented(edges[b]);
                    if (!((first.Left >= 0 && first.Left == second.Left) ||
                        (first.Right >= 0 && first.Right == second.Right))) continue;
                    if (Unite(a, b)) { connected.Add(a); connected.Add(b); }
                }
        }
        if (connected.Count == 0) return null;
        var activeRoots = connected.Select(Root).ToHashSet();
        var groups = Enumerable.Range(0, edges.Count).Where(IsRail)
            .Where(id => activeRoots.Contains(Root(id))).GroupBy(Root).ToArray();
        var neededRows = new HashSet<long>();
        foreach (int id in activeRoots)
            for (long row = low[id]; row <= high[id]; row++) neededRows.Add(row);
        var rows = incident.Keys.Where(vertex => neededRows.Contains(Row(vertex)))
            .GroupBy(Row).ToDictionary(group => group.Key, group => group.ToArray());
        var positions = new Dictionary<Key, Key>();
        foreach (var group in groups)
        {
            cancel?.Invoke();
            long row = group.GroupBy(id => Row(edges[id].A))
                .OrderByDescending(layer => layer.Sum(id => Math.Abs(Along(edges[id].A) - Along(edges[id].B))))
                .ThenBy(layer => layer.Key).First().Key;
            int id = group.Key;
            for (long other = low[id]; other <= high[id]; other++)
            {
                if (other == row || !rows.TryGetValue(other, out var vertices)) continue;
                foreach (var v in vertices)
                    if (Along(v) >= start[id] && Along(v) <= end[id])
                        positions.TryAdd(v, horizontal ? new Key(v.X, row) : new Key(row, v.Y));
            }
        }
        if (!edges.Any(edge => positions.GetValueOrDefault(edge.A, edge.A) ==
            positions.GetValueOrDefault(edge.B, edge.B))) return null;
        return positions;

        void Add(Key point, int side)
        {
            fan[side] = incident.GetValueOrDefault(point, -1);
            incident[point] = side;
        }
        long Along(Key p) => horizontal ? p.X : p.Y;
        long Row(Key p) => horizontal ? p.Y : p.X;
        bool IsRail(int i) => Row(edges[i].A) == Row(edges[i].B);
        long Extent(int i, Key at) => Along(edges[i].A == at ? edges[i].B : edges[i].A) - Along(at);
        long RailSupport(int i, Key at, long direction) =>
            direction < 0 ? Along(at) - start[Root(i)] : end[Root(i)] - Along(at);
        (int Left, int Right) Oriented(V2OwnedEdge e) =>
            Along(e.A) < Along(e.B) ? (e.Left, e.Right) : (e.Right, e.Left);
        int Root(int i)
        {
            while (i != roots[i]) { roots[i] = roots[roots[i]]; i = roots[i]; }
            return i;
        }
        bool Unite(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            long min = Math.Min(low[a], low[b]), max = Math.Max(high[a], high[b]);
            if (max - min > limit) return false;
            roots[b] = a;
            (low[a], high[a]) = (min, max);
            start[a] = Math.Min(start[a], start[b]);
            end[a] = Math.Max(end[a], end[b]);
            return true;
        }
    }

    private static Vector2d Direction(Key a, Key b) => new(b.X - a.X, b.Y - a.Y);

    private static Dictionary<Key, Key> CollapseSplices(V2PlanarArrangement plan, int limit, Action cancel)
    {
        var edges = plan.VisibleEdges();
        var incident = new Dictionary<Key, (int Count, int First, int Second)>();
        for (int i = 0; i < edges.Count; i++)
        {
            Add(edges[i].A, i);
            Add(edges[i].B, i);
        }
        var moves = new Dictionary<Key, Key>();
        var fixedVertices = new HashSet<Key>();
        for (int i = 0; i < edges.Count; i++)
        {
            if ((i & 4095) == 0) cancel?.Invoke();
            var edge = edges[i];
            if (Direction(edge.A, edge.B).SquareLength > limit * limit ||
                fixedVertices.Contains(edge.A) || fixedVertices.Contains(edge.B))
                continue;
            var atA = incident[edge.A];
            var atB = incident[edge.B];
            if (atA.Count != 2 || atB.Count != 2) continue;
            var prior = edges[atA.First == i ? atA.Second : atA.First];
            var next = edges[atB.First == i ? atB.Second : atB.First];
            if (Owners(prior, prior.B == edge.A) != (edge.Left, edge.Right) ||
                Owners(next, next.A == edge.B) != (edge.Left, edge.Right))
                continue;
            var a = Direction(prior.A == edge.A ? prior.B : prior.A, edge.A);
            var b = Direction(edge.B, next.A == edge.B ? next.B : next.A);
            double lengthA = a.Length, lengthB = b.Length;
            if (Math.Min(lengthA, lengthB) < limit * 12 || (a * b) < .999 * lengthA * lengthB) continue;
            if (lengthA >= lengthB) moves.Add(edge.B, edge.A);
            else moves.Add(edge.A, edge.B);
            fixedVertices.Add(edge.A);
            fixedVertices.Add(edge.B);
        }
        return moves;

        void Add(Key point, int id)
        {
            var fan = incident.GetValueOrDefault(point);
            incident[point] = (fan.Count + 1, id, fan.First);
        }
        static (int Left, int Right) Owners(V2OwnedEdge edge, bool forward) =>
            forward ? (edge.Left, edge.Right) : (edge.Right, edge.Left);
    }

    private static V2PlanarArrangement Reclassify(V2PlanarArrangement original, IEnumerable<V2OwnedEdge> revised,
        double tolerance, int tileLimit, Action cancel, Action<string> progress)
    {
        var previous = original.Regions().ToArray();
        using var index = new RTree();
        for (int i = 0; i < previous.Length; i++) index.Insert(original.RegionBounds(previous[i].Region), i);

        if (original.EdgeCount <= tileLimit * 3L)
        {
            var rebuilt = V2PlanarArrangement.RebuildBoundary(revised.ToArray(), original.Grid, cancel);
            Classify(rebuilt);
            return V2PlanarArrangement.FromBoundary(rebuilt.VisibleEdges(), original.Grid);
        }

        // The geometry already has exact global integer coordinates. Stream
        // every source span, then node the moved boundaries in bounded tiles
        // and reconcile their owners using the pre-rewrite region topology.
        using var buffer = new V2SegmentBuffer(fileBacked: true);
        foreach (var edge in revised)
        {
            if ((buffer.Count & 8191) == 0) cancel?.Invoke();
            var a = original.Point(edge.A);
            var b = original.Point(edge.B);
            foreach (var source in edge.Sources)
                buffer.Add(new V2Segment(new Point2d(a.X, a.Y), new Point2d(b.X, b.Y), source));
        }
        using var file = buffer.Seal();
        Compact(original.EdgeCount, tileLimit);
        return V2TiledArrangement.Build(file, original.Grid, tolerance, tileLimit, 0, Classify, cancel,
            line => progress?.Invoke($"Cleanup {line}"), new List<V2TileReport>(),
            manageMemory: true, splitOnConvergence: true);

        void Classify(V2PlanarArrangement rebuilt)
        {
            foreach (var cell in rebuilt.Cells)
            {
                cancel?.Invoke();
                if (!rebuilt.TryInterior(cell, out var point)) continue;
                var p = new Point3d(point.X, point.Y, 0);
                index.Search(new BoundingBox(p, p), (_, hit) =>
                {
                    var old = previous[hit.Id];
                    if (!original.Contains(old.Region, point)) return;
                    cell.Owner = old.Owner;
                    hit.Cancel = true;
                });
            }
        }
    }
}
