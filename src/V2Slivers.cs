using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using Key = BetterMake2D.V2PlanarArrangement.Key;

namespace BetterMake2D;

internal static class V2Slivers
{
    internal static (V2PlanarArrangement Plan, int Absorbed, int Flattened) Clean(
        V2PlanarArrangement plan, double tolerance, double millimetersPerUnit, Action cancel)
    {
        double clearance = Math.Min(Math.Max(plan.Grid * 2.5, tolerance * .04), .005 / millimetersPerUnit);
        int absorbed = Absorb(plan, clearance / plan.Grid, cancel, out var owners);
        if (absorbed > 0)
            plan = V2PlanarArrangement.FromBoundary(plan.VisibleEdges(owners), plan.Grid);
        int flattened = 0;
        int steps = (int)Math.Min(2, Math.Floor(clearance / plan.Grid));
        if (steps > 0)
        {
            plan = Flatten(plan, true, steps, cancel, ref flattened);
            plan = Flatten(plan, false, steps, cancel, ref flattened);
            plan = CollapseSplices(plan, steps, cancel, ref flattened);
        }
        return (plan, absorbed, flattened);
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

    private static V2PlanarArrangement Flatten(V2PlanarArrangement plan, bool horizontal, int limit,
        Action cancel, ref int flattened)
    {
        var edges = plan.VisibleEdges();
        var incident = new Dictionary<Key, List<int>>();
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
            if (!incident.TryGetValue(e.A, out var a)) incident.Add(e.A, a = new List<int>(2));
            if (!incident.TryGetValue(e.B, out var b)) incident.Add(e.B, b = new List<int>(2));
            a.Add(i);
            b.Add(i);
        }
        foreach (var entry in incident.Values)
        {
            int first = -1;
            foreach (int id in entry)
            {
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
            foreach (int a in incident[e.A])
                foreach (int b in incident[e.B])
                {
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
        if (connected.Count == 0) return plan;
        var activeRoots = connected.Select(Root).ToHashSet();
        var groups = Enumerable.Range(0, edges.Count).Where(IsRail)
            .Where(id => activeRoots.Contains(Root(id))).GroupBy(Root).ToArray();
        var rows = incident.Keys.GroupBy(Row).ToDictionary(group => group.Key, group => group.ToArray());
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
        var revised = edges.Select(Applied).Where(edge => edge.A != edge.B).ToList();
        int count = edges.Count - revised.Count;
        if (count == 0) return plan;
        // An endpoint moved onto a rail must become a shared T vertex. The
        // arrangement also consolidates coincident intervals after the band collapses.
        try
        {
            var result = Reclassify(plan, V2PlanarArrangement.RebuildBoundary(revised, plan.Grid, cancel), cancel);
            flattened += count;
            return result;
        }
        catch (InvalidOperationException)
        {
            return plan;
        }

        V2OwnedEdge Applied(V2OwnedEdge edge) => edge with { A = Move(edge.A), B = Move(edge.B) };
        Key Move(Key point) => positions.GetValueOrDefault(point, point);
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

    private static V2PlanarArrangement CollapseSplices(V2PlanarArrangement plan, int limit, Action cancel, ref int count)
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
        if (moves.Count == 0) return plan;
        var revised = edges.Select(edge => edge with
        {
            A = moves.GetValueOrDefault(edge.A, edge.A),
            B = moves.GetValueOrDefault(edge.B, edge.B)
        }).Where(edge => edge.A != edge.B).ToList();
        try
        {
            var result = Reclassify(plan, V2PlanarArrangement.RebuildBoundary(revised, plan.Grid, cancel), cancel);
            count += edges.Count - revised.Count;
            return result;
        }
        catch (InvalidOperationException)
        {
            return plan;
        }

        void Add(Key point, int id)
        {
            var fan = incident.GetValueOrDefault(point);
            incident[point] = (fan.Count + 1, id, fan.First);
        }
        static (int Left, int Right) Owners(V2OwnedEdge edge, bool forward) =>
            forward ? (edge.Left, edge.Right) : (edge.Right, edge.Left);
    }

    private static V2PlanarArrangement Reclassify(V2PlanarArrangement original, V2PlanarArrangement rebuilt, Action cancel)
    {
        var previous = original.Regions().ToArray();
        using var index = new RTree();
        for (int i = 0; i < previous.Length; i++) index.Insert(original.RegionBounds(previous[i].Region), i);
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
        return V2PlanarArrangement.FromBoundary(rebuilt.VisibleEdges(), original.Grid);
    }
}
