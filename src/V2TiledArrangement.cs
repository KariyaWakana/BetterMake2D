using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rhino.Geometry;
using Key = BetterMake2D.V2PlanarArrangement.Key;
using Window = BetterMake2D.V2TileWindow;

namespace BetterMake2D;

internal readonly record struct V2TileReport(long X0, long Y0, long X1, long Y1,
    int Segments, int Cells, int VisibleEdges, int Edges, int Rounds, double ArrangementSeconds, double DepthSeconds);

internal static class V2TiledArrangement
{
    internal const int DefaultSegmentLimit = 250_000;
    internal const string SeamKind = "TileSeam";
    internal readonly record struct SeamLine(bool Vertical, long Axis);
    internal readonly record struct SeamPart(long From, long To, bool Left, int Cell, V2SourceSpan[] Sources);
    internal sealed record Tile(Window Window, int[] Owners, List<V2OwnedEdge> Visible, List<V2OwnedEdge> Interior);
    internal readonly record struct Witness(int Local, int Owner, bool Uncertain, double Clearance, bool Proven);
    private sealed class SeamCell(int tile, Witness witness, int id)
    {
        internal readonly int Tile = tile, Local = witness.Local, Owner = witness.Owner;
        internal readonly bool Uncertain = witness.Uncertain, Proven = witness.Proven;
        internal readonly double Clearance = witness.Clearance;
        internal int Parent = id, Best = id, Size = 1;
    }

    internal sealed record Snapshot(Tile State, int[] Candidates, List<Witness> Witnesses,
        List<(SeamLine Axis, SeamPart Part)> Seams, V2TileReport Report, V2ContactFront Front);

    internal sealed class Replay
    {
        internal readonly List<Snapshot> Tiles = new();
        internal int Rebuilt;
        internal long RebuiltSegments;

        internal V2ContactFront Frontier = new();
    }
    private readonly record struct Fraction(long Numerator, long Denominator) : IComparable<Fraction>
    {
        public int CompareTo(Fraction other) => ((Int128)Numerator * other.Denominator)
            .CompareTo((Int128)other.Numerator * Denominator);
        internal double Value => (double)Numerator / Denominator;
    }

    internal static V2PlanarArrangement Build(IReadOnlyList<V2Segment> sources, double grid, double modelTolerance,
        int limit, int minimumDepth,
        Action<V2PlanarArrangement> classify, Action cancel, Action<string> progress, List<V2TileReport> tiles,
        bool overlapArrangement = false, Func<int, IReadOnlyList<V2SourceSpan>, bool> retainInterior = null,
        List<V2OwnedEdge> interior = null, bool manageMemory = false, bool reportTotal = false,
        bool splitOnConvergence = false, V2TileSchedule schedule = null, int windowScale = 1,
        Replay replay = null, V2SegmentBuffer changed = null, Rect? fixedExtent = null, bool frontierOnly = false)
    {
        var seams = new Dictionary<SeamLine, List<SeamPart>>();
        var states = new List<Tile>();
        var cells = new List<SeamCell> { null };
        var realSeams = new List<(Key A, Key B, int Left, int Right, V2SourceSpan[] Sources)>();
        var bounds = new Window(long.MaxValue, long.MaxValue, long.MinValue, long.MinValue);
        var previousTiles = replay?.Tiles.ToArray();
        replay?.Tiles.Clear();
        if (replay != null)
        {
            replay.Rebuilt = 0;
            replay.RebuiltSegments = 0;
            replay.Frontier = new V2ContactFront();
        }
        if (fixedExtent is Rect extent)
        {
            var a = V2PlanarArrangement.Snap(new Point2d(extent.Left, extent.Bottom), grid);
            var b = V2PlanarArrangement.Snap(new Point2d(extent.Right, extent.Top), grid);
            bounds = new Window(a.X, a.Y, b.X, b.Y);
        }
        if (previousTiles is not { Length: > 0 })
            for (int i = 0; i < sources.Count; i++)
            {
                if ((i & 65535) == 0) cancel?.Invoke();
                var (a, b) = Ends(i);
                bounds = new Window(Math.Min(bounds.X0, Math.Min(a.X, b.X)), Math.Min(bounds.Y0, Math.Min(a.Y, b.Y)),
                    Math.Max(bounds.X1, Math.Max(a.X, b.X)), Math.Max(bounds.Y1, Math.Max(a.Y, b.Y)));
            }
        // The padded outer frame has background on both sides.
        long pad = Math.Max(2, (long)Math.Ceiling(modelTolerance * 8 / grid));
        bounds = new Window(bounds.X0 - pad, bounds.Y0 - pad, bounds.X1 + pad, bounds.Y1 + pad);
        using var stop = new CancellationTokenSource();
        using var process = overlapArrangement || manageMemory ? System.Diagnostics.Process.GetCurrentProcess() : null;
        Task<(V2PlanarArrangement Plan, int Segments, double Seconds)> pending = null;
        Window pendingWindow = default;
        int[] pendingCandidates = null;
        var work = reportTotal ? new Queue<(Window Window, int[] Candidates)>() : null;
        try
        {
            if (manageMemory)
            {
                process.Refresh();
                if (Math.Max(process.WorkingSet64, process.PrivateMemorySize64) >= 4000L * 1024 * 1024)
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            if (previousTiles is { Length: > 0 })
            {
                if (changed == null)
                    foreach (var tile in previousTiles) Append(tile, reused: true);
                else
                    VisitChanged(previousTiles);
            }
            else if (schedule == null)
                Visit(bounds, Enumerable.Range(0, sources.Count).ToArray(), 0);
            else
                VisitPlanned();
            if (work != null)
            {
                progress?.Invoke($"Tile plan {work.Count}");
                while (work.TryDequeue(out var tile)) Schedule(tile.Window, tile.Candidates);
            }
            if (pending != null) FinishOrSplit(pendingWindow, pendingCandidates, pending.GetAwaiter().GetResult());
        }
        finally
        {
            stop.Cancel();
            if (pending is { IsCompleted: false })
                try { pending.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
        if (frontierOnly) return null;
        int disagreements = 0;
        foreach (var (line, intervals) in seams)
        {
            cancel?.Invoke();
            Stitch(line, intervals, (a, b, left, right, spans) =>
            {
                if (spans.Length > 0)
                    realSeams.Add((a, b, left, right, spans));
                else if (left > 0 && right > 0)
                {
                    if (cells[left].Owner != cells[right].Owner) disagreements++;
                    Join(left, right);
                }
                else if (Owner(left) != Owner(right))
                    throw new InvalidOperationException($"A visible region reaches the artificial outer frame at {a}.");
            });
        }
        var dirty = new HashSet<int>();
        for (int i = 1; i < cells.Count; i++)
        {
            var cell = cells[i];
            int owner = Owner(i);
            if (owner == cell.Owner) continue;
            if (cell.Proven) throw new InvalidOperationException("Conflicting certified owners across a tile seam.");
            states[cell.Tile].Owners[cell.Local] = owner;
            dirty.Add(cell.Tile);
        }
        if (dirty.Count > 0)
            progress?.Invoke($"Global cells cross {disagreements} seam intervals with different local witnesses; rebuilding {dirty.Count} affected tiles.");
        foreach (int id in dirty.OrderBy(value => value))
        {
            cancel?.Invoke();
            long mark = Stopwatch.GetTimestamp();
            var state = states[id];
            if (replay != null)
            {
                state = state with { Visible = new List<V2OwnedEdge>(),
                    Interior = retainInterior == null ? null : new List<V2OwnedEdge>() };
                states[id] = state;
            }
            else
            {
                state.Visible.Clear();
                state.Interior?.Clear();
            }
            var candidates = replay?.Tiles[id].Candidates;
            var plan = BuildLocal(state.Window, candidates ?? Enumerable.Range(0, sources.Count), out _, cancel, filter: true);
            if (state.Owners.Length != plan.Cells.Length)
                throw new InvalidOperationException($"Rebuilding tile {id + 1} changed its local cell indexing.");
            for (int i = 0; i < state.Owners.Length; i++) plan.Cells[i].Owner = state.Owners[i];
            CollectEdges(state.Window, plan, state);
            tiles[id] = tiles[id] with { VisibleEdges = state.Visible.Count,
                ArrangementSeconds = tiles[id].ArrangementSeconds + Stopwatch.GetElapsedTime(mark).TotalSeconds };
        }
        var boundary = new List<V2OwnedEdge>();
        foreach (var state in states)
        {
            boundary.AddRange(state.Visible);
            if (replay == null) state.Visible.Clear();
            if (state.Interior != null)
            {
                interior.AddRange(state.Interior);
                if (replay == null) state.Interior.Clear();
            }
        }
        foreach (var edge in realSeams)
        {
            int left = Owner(edge.Left), right = Owner(edge.Right);
            if (left != right) boundary.Add(new V2OwnedEdge(edge.A, edge.B, left, right, edge.Sources));
            else if (left >= 0 && retainInterior?.Invoke(left, edge.Sources) == true)
                interior.Add(new V2OwnedEdge(edge.A, edge.B, left, right, edge.Sources));
        }
        progress?.Invoke($"Stitched {tiles.Count} tiles on {seams.Count} grid lines; {boundary.Count} real edges; " +
            $"{disagreements} seam intervals reconciled, {dirty.Count} tiles rebuilt.");
        return V2PlanarArrangement.FromBoundary(boundary, grid);

        int Root(int id)
        {
            while (cells[id].Parent != id)
            {
                cells[id].Parent = cells[cells[id].Parent].Parent;
                id = cells[id].Parent;
            }
            return id;
        }

        int Owner(int id) => id == 0 ? -1 : cells[cells[Root(id)].Best].Owner;

        void Join(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            if (a == b) return;
            if (cells[a].Size < cells[b].Size) (a, b) = (b, a);
            var first = cells[a];
            var second = cells[b];
            first.Size += second.Size;
            second.Parent = a;
            if (Quality(second.Best).CompareTo(Quality(first.Best)) > 0) first.Best = second.Best;

            (bool Proven, bool Certain, double Clearance, int Order) Quality(int id) =>
                (cells[id].Proven, !cells[id].Uncertain, cells[id].Clearance, -id);
        }

        (Key A, Key B) Ends(int i) => (V2PlanarArrangement.Snap(sources[i].A, grid),
            V2PlanarArrangement.Snap(sources[i].B, grid));

        void VisitChanged(Snapshot[] old)
        {
            var moving = changed.ChangedSegments().Select(segment =>
                (V2PlanarArrangement.Snap(segment.A, grid), V2PlanarArrangement.Snap(segment.B, grid))).ToArray();
            for (int tileIndex = 0; tileIndex < old.Length; tileIndex++)
            {
                var tile = old[tileIndex];
                old[tileIndex] = null;
                cancel?.Invoke();
                var window = tile.State.Window;
                if (tile.Candidates != null &&
                    moving.Any(pair => Clip(pair.Item1, pair.Item2, window, out _, out _, out _, out _)))
                {
                    var route = schedule?.Tiles.FirstOrDefault(tile => tile.Window.Scale(windowScale).Contains(window));
                    var ids = new List<int>(tile.Candidates.Length);
                    foreach (int id in tile.Candidates)
                        if (id < changed.PassStart) ids.Add(id);
                    foreach (int id in changed.ChangedIndexes())
                    {
                        var (a, b) = Ends(id);
                        if (Clip(a, b, window, out _, out _, out _, out _) &&
                            (route == null || schedule.Allows(route, sources[id].Source)))
                            ids.Add(id);
                    }
                    ids.Sort();
                    int count = 0;
                    for (int i = 0; i < ids.Count; i++)
                        if (count == 0 || ids[count - 1] != ids[i]) ids[count++] = ids[i];
                    ids.RemoveRange(count, ids.Count - count);
                    replay.Rebuilt++;
                    var candidates = ids.ToArray();
                    FinishOrSplit(window, candidates, ArrangeChanged(window, candidates));
                }
                else
                    Append(tile, reused: true);
            }
        }

        void Append(Snapshot snapshot, bool reused = false)
        {
            int index = states.Count, first = cells.Count;
            var raw = snapshot.State;
            states.Add(raw with { Owners = (int[])raw.Owners.Clone() });
            foreach (var witness in snapshot.Witnesses)
                cells.Add(new SeamCell(index, witness, cells.Count));
            foreach (var (axis, part) in snapshot.Seams)
                AddSeam(axis, part.Cell < 0 ? part with { Cell = 0 } : part with { Cell = first + part.Cell });
            tiles.Add(reused ? snapshot.Report with { ArrangementSeconds = 0, DepthSeconds = 0 } : snapshot.Report);
            if (!reused && replay != null) replay.RebuiltSegments += snapshot.Report.Segments;
            if (replay != null)
            {
                if (!reused) replay.Frontier.Merge(snapshot.Front);
                replay.Tiles.Add(snapshot with { Front = null });
            }
        }

        void AddSeam(SeamLine axis, SeamPart part)
        {
            if (!seams.TryGetValue(axis, out var parts))
                seams.Add(axis, parts = new List<SeamPart>());
            parts.Add(part);
        }

        void VisitPlanned()
        {
            var planned = schedule.Tiles;
            var batches = new List<int>[planned.Length];
            var windows = planned.Select(tile => tile.Window.Scale(windowScale)).ToArray();
            var extent = schedule.Extent(windowScale);
            var slots = new List<int>();
            for (int id = 0; id < sources.Count; id++)
            {
                if ((id & 8191) == 0) cancel?.Invoke();
                var (a, b) = Ends(id);
                if (!extent.Contains(a) || !extent.Contains(b))
                    throw new InvalidOperationException("A projected source falls outside the planned tile extent.");
                schedule.Overlapping(a, b, windowScale, slots);
                var span = sources[id].Source;
                foreach (int index in slots)
                {
                    if (!schedule.Allows(planned[index], span) ||
                        !Clip(a, b, windows[index], out _, out _, out _, out _)) continue;
                    (batches[index] ??= new List<int>()).Add(id);
                }
            }
            if (!reportTotal) progress?.Invoke($"Tile plan {planned.Length}");
            for (int i = 0; i < planned.Length; i++)
            {
                cancel?.Invoke();
                if (planned[i].ProvenOwner is int owner && owner >= 0) FinishProven(windows[i], owner);
                else Visit(windows[i], batches[i]?.ToArray() ?? Array.Empty<int>(), minimumDepth);
                batches[i] = null;
            }
        }

        void FinishProven(Window window, int owner)
        {
            var state = new Tile(window, new[] {owner}, new List<V2OwnedEdge>(), null);
            var parts = new List<(SeamLine, SeamPart)>
            {
                (new SeamLine(true, window.X0), new SeamPart(window.Y0, window.Y1, false, 0, Array.Empty<V2SourceSpan>())),
                (new SeamLine(true, window.X1), new SeamPart(window.Y0, window.Y1, true, 0, Array.Empty<V2SourceSpan>())),
                (new SeamLine(false, window.Y0), new SeamPart(window.X0, window.X1, true, 0, Array.Empty<V2SourceSpan>())),
                (new SeamLine(false, window.Y1), new SeamPart(window.X0, window.X1, false, 0, Array.Empty<V2SourceSpan>()))
            };
            var report = new V2TileReport(window.X0, window.Y0, window.X1, window.Y1, 0, 1, 0, 4, 0, 0, 0);
            Append(new Snapshot(state, null, new List<Witness> {new(0, owner, false, 0, true)}, parts, report,
                new V2ContactFront()));
        }

        void Visit(Window window, int[] candidates, int depth)
        {
            cancel?.Invoke();
            if (depth >= minimumDepth && candidates.Length <= limit)
            {
                if (work == null) Schedule(window, candidates);
                else work.Enqueue((window, candidates));
                return;
            }
            if (depth >= 32)
                throw new InvalidOperationException($"A tile still has {candidates.Length} segments after 32 subdivisions.");

            var choices = new List<(bool X, long Cut, int Largest, long Total)>(2);
            Choose(x: true, window.X0, window.X1);
            Choose(x: false, window.Y0, window.Y1);
            if (choices.Count == 0)
                throw new InvalidOperationException($"Cannot subdivide a {candidates.Length}-segment tile on the global grid.");
            var split = choices.MinBy(choice => (choice.Largest, choice.Total));
            var lower = split.X ? window with { X1 = split.Cut } : window with { Y1 = split.Cut };
            var upper = split.X ? window with { X0 = split.Cut } : window with { Y0 = split.Cut };
            var (left, right) = SplitCandidates(candidates, lower, upper);
            candidates = null;
            Visit(lower, left, depth + 1);
            left = null;
            Visit(upper, right, depth + 1);

            (int[] Left, int[] Right) SplitCandidates(int[] ids, Window lowerWindow, Window upperWindow)
            {
                // Retain the existing clip predicate and candidate order, with
                // one byte per decision and exact-sized child index arrays.
                var sides = new byte[ids.Length];
                int firstCount = 0, secondCount = 0;
                for (int j = 0; j < ids.Length; j++)
                {
                    if ((j & 65535) == 0) cancel?.Invoke();
                    var (a, b) = Ends(ids[j]);
                    if (Clip(a, b, lowerWindow, out _, out _, out _, out _)) { sides[j] = 1; firstCount++; }
                    if (Clip(a, b, upperWindow, out _, out _, out _, out _)) { sides[j] |= 2; secondCount++; }
                }
                var first = new int[firstCount];
                var second = new int[secondCount];
                int x = 0, y = 0;
                for (int j = 0; j < ids.Length; j++)
                {
                    if ((sides[j] & 1) != 0) first[x++] = ids[j];
                    if ((sides[j] & 2) != 0) second[y++] = ids[j];
                }
                return (first, second);
            }

            void Choose(bool x, long lo, long hi)
            {
                if (hi - lo < 4) return;
                int stride = Math.Max(1, candidates.Length / 2048);
                var intervals = new List<(long Min, long Max)>();
                for (int i = 0; i < candidates.Length; i += stride)
                {
                    var (a, b) = Ends(candidates[i]);
                    long u = x ? a.X : a.Y, v = x ? b.X : b.Y;
                    intervals.Add((Math.Max(lo, Math.Min(u, v)), Math.Min(hi, Math.Max(u, v))));
                }
                var centers = intervals.Select(interval => interval.Min + (interval.Max - interval.Min) / 2).ToArray();
                Array.Sort(centers);
                long margin = Math.Max(1, (hi - lo) / 8);
                long cut = centers.Length == 0 ? lo + (hi - lo) / 2 :
                    Math.Clamp(centers[centers.Length / 2], lo + margin, hi - margin);
                int first = intervals.Count(interval => interval.Min <= cut);
                int second = intervals.Count(interval => interval.Max >= cut);
                choices.Add((x, cut, Math.Max(first, second), (long)first + second));
            }
        }

        void Schedule(Window window, int[] candidates)
        {
            var previous = pending;
            var previousWindow = pendingWindow;
            var previousCandidates = pendingCandidates;
            var ready = previous == null ? default : previous.GetAwaiter().GetResult();
            pending = null;
            process?.Refresh();
            long inUse = process == null ? 0 : Math.Max(process.WorkingSet64, process.PrivateMemorySize64);
            // Reserve room for two live DCELs and the serial native depth queries.
            bool canOverlap = overlapArrangement && inUse < (long)2_850 * 1024 * 1024 && candidates.Length <= 250_000;
            if (canOverlap)
            {
                pendingWindow = window;
                pendingCandidates = candidates;
                pending = Task.Run(() => Arrange(window, candidates, stop.Token.ThrowIfCancellationRequested), stop.Token);
            }
            if (previous != null) FinishOrSplit(previousWindow, previousCandidates, ready);
            if (!canOverlap)
            {
                if (manageMemory && inUse >= 4_000L * 1024 * 1024)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                }
                FinishOrSplit(window, candidates, Arrange(window, candidates, cancel));
            }
        }

        (V2PlanarArrangement Plan, int Segments, double Seconds) Arrange(Window window, int[] candidates, Action check)
        {
            long mark = Stopwatch.GetTimestamp();
            try
            {
                var plan = BuildLocal(window, candidates, out int realCount, check);
                return (plan, realCount, Stopwatch.GetElapsedTime(mark).TotalSeconds);
            }
            catch (V2SnapConvergenceException) when (splitOnConvergence)
            {
                return (null, candidates.Length, Stopwatch.GetElapsedTime(mark).TotalSeconds);
            }
        }

        (V2PlanarArrangement Plan, int Segments, double Seconds) ArrangeChanged(Window window, int[] candidates)
        {
            int allowed = limit + limit / 3;
            if (manageMemory)
            {
                process.Refresh();
                if (Math.Max(process.WorkingSet64, process.PrivateMemorySize64) >= 3650L * 1024 * 1024)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    process.Refresh();
                    if (Math.Max(process.WorkingSet64, process.PrivateMemorySize64) >= 3850L * 1024 * 1024)
                        allowed = Math.Min(allowed, 195_000);
                }
            }
            return candidates.Length > allowed ? (null, candidates.Length, 0) : Arrange(window, candidates, cancel);
        }

        void FinishOrSplit(Window window, int[] candidates,
            (V2PlanarArrangement Plan, int Segments, double Seconds) arranged, int depth = 0)
        {
            if (arranged.Plan != null)
            {
                Finish(window, candidates, arranged);
                return;
            }
            long width = window.X1 - window.X0, height = window.Y1 - window.Y0;
            if (depth >= 12 || Math.Max(width, height) < 4) throw new V2SnapConvergenceException();
            bool x = width >= height;
            long middle = x ? window.X0 + width / 2 : window.Y0 + height / 2;
            var lower = x ? window with { X1 = middle } : window with { Y1 = middle };
            var upper = x ? window with { X0 = middle } : window with { Y0 = middle };
            progress?.Invoke($"Subdividing a {arranged.Segments}-segment tile on the same grid after {arranged.Seconds:F2} s of snap rounding.");
            Split(lower);
            Split(upper);

            void Split(Window child)
            {
                var ids = new List<int>();
                int visited = 0;
                foreach (int id in candidates)
                {
                    if ((visited++ & 8191) == 0) cancel?.Invoke();
                    var (a, b) = Ends(id);
                    if (Clip(a, b, child, out _, out _, out _, out _)) ids.Add(id);
                }
                var list = ids.ToArray();
                var childPlan = previousTiles is { Length: > 0 }
                    ? ArrangeChanged(child, list) : Arrange(child, list, cancel);
                FinishOrSplit(child, list, childPlan, depth + 1);
            }
        }

        void Finish(Window window, int[] candidates, (V2PlanarArrangement Plan, int Segments, double Seconds) arranged)
        {
            var (plan, realCount, arrange) = arranged;
            long mark = Stopwatch.GetTimestamp();
            classify(plan);
            double depth = Stopwatch.GetElapsedTime(mark).TotalSeconds;
            var state = new Tile(window, plan.Cells.Select(cell => cell.Owner).ToArray(), new List<V2OwnedEdge>(),
                retainInterior == null ? null : new List<V2OwnedEdge>());
            var tokens = new Dictionary<int, int>();
            var witnesses = new List<Witness>();
            var parts = new List<(SeamLine, SeamPart)>();
            for (int side = 0; side < plan.EdgeCount * 2; side += 2)
            {
                var a = plan.Start(side);
                var b = plan.Start(side ^ 1);
                if (OnSeam(window, a, b) is not SeamLine axis) continue;
                bool interiorLeft = axis.Vertical ? axis.Axis == window.X1 : axis.Axis == window.Y0;
                int localId = plan.CellIndex(interiorLeft ? side : side ^ 1);
                int token = -1;
                if (localId >= 0 && !tokens.TryGetValue(localId, out token))
                {
                    token = witnesses.Count;
                    tokens.Add(localId, token);
                    var cell = plan.Cells[localId];
                    witnesses.Add(new Witness(localId, cell.Owner, cell.Uncertain, cell.Clearance, false));
                }
                parts.Add((axis, new SeamPart(axis.Vertical ? a.Y : a.X, axis.Vertical ? b.Y : b.X,
                    interiorLeft, token, plan.Sources(side).Where(span => span.Kind != SeamKind).ToArray())));
            }
            CollectEdges(window, plan, state);
            var tile = new V2TileReport(window.X0, window.Y0, window.X1, window.Y1, realCount,
                plan.Cells.Length, state.Visible.Count, plan.EdgeCount, plan.Rounds, arrange, depth);
            var front = replay == null ? null : new V2ContactFront();
            front?.Read(plan, Math.Max(grid * 4, modelTolerance));
            Append(new Snapshot(state, replay == null ? null : candidates, witnesses, parts, tile, front));
            progress?.Invoke($"Tile {tiles.Count}: {tile.Segments} segments, {tile.Cells} cells, {tile.VisibleEdges} real boundary edges.");
        }

        V2PlanarArrangement BuildLocal(Window window, IEnumerable<int> candidates, out int realCount, Action check,
            bool filter = false)
        {
            var batch = new List<V2Segment>();
            var route = filter ? schedule?.Tiles.FirstOrDefault(tile => tile.Window.Scale(windowScale).Contains(window)) : null;
            int visited = 0;
            foreach (int i in candidates)
            {
                if ((visited++ & 8191) == 0) check?.Invoke();
                var (a, b) = Ends(i);
                if (Clip(a, b, window, out var start, out var end, out double from, out double to) &&
                    (route == null || schedule.Allows(route, sources[i].Source)))
                    batch.Add(new V2Segment(Point(start), Point(end), Slice(sources[i].Source, from, to)));
            }
            realCount = batch.Count;
            var corners = new[] { new Key(window.X0, window.Y0), new Key(window.X1, window.Y0),
                new Key(window.X1, window.Y1), new Key(window.X0, window.Y1) };
            for (int i = 0; i < corners.Length; i++)
                batch.Add(new V2Segment(Point(corners[i]), Point(corners[(i + 1) % corners.Length]),
                    new V2SourceSpan(-1, -1, SeamKind, 0, 1)));
            return V2PlanarArrangement.Build(batch, grid, check, fast: splitOnConvergence);
        }

        void CollectEdges(Window window, V2PlanarArrangement plan, Tile state)
        {
            for (int side = 0; side < plan.EdgeCount * 2; side += 2)
            {
                var a = plan.Start(side);
                var b = plan.Start(side ^ 1);
                int left = plan.Owner(side), right = plan.Owner(side ^ 1);
                if (left == right && (left < 0 || retainInterior?.Invoke(left, plan.Sources(side)) != true)) continue;
                if (OnSeam(window, a, b) != null) continue;
                var edge = new V2OwnedEdge(a, b, left, right, plan.Sources(side).ToArray());
                if (left != right) state.Visible.Add(edge);
                else state.Interior.Add(edge);
            }
        }

        Point2d Point(Key key) => new(key.X * grid, key.Y * grid);
    }

    private static SeamLine? OnSeam(Window window, Key a, Key b) =>
        a.X == b.X && (a.X == window.X0 || a.X == window.X1) ? new SeamLine(true, a.X) :
        a.Y == b.Y && (a.Y == window.Y0 || a.Y == window.Y1) ? new SeamLine(false, a.Y) : null;

    private static void Stitch(SeamLine line, List<SeamPart> parts, Action<Key, Key, int, int, V2SourceSpan[]> visit)
    {
        var left = parts.Where(part => part.Left).OrderBy(part => part.From).ToArray();
        var right = parts.Where(part => !part.Left).OrderBy(part => part.From).ToArray();
        var stations = parts.SelectMany(part => new[] { part.From, part.To }).Distinct().OrderBy(value => value).ToArray();
        int l = 0, r = 0;
        for (int i = 1; i < stations.Length; i++)
        {
            long start = stations[i - 1], end = stations[i];
            while (l < left.Length && left[l].To <= start) l++;
            while (r < right.Length && right[r].To <= start) r++;
            SeamPart? a = l < left.Length && left[l].From <= start ? left[l] : null;
            SeamPart? b = r < right.Length && right[r].From <= start ? right[r] : null;
            var sources = Sources(a, start, end).Concat(Sources(b, start, end)).Distinct().ToArray();
            var p = line.Vertical ? new Key(line.Axis, start) : new Key(start, line.Axis);
            var q = line.Vertical ? new Key(line.Axis, end) : new Key(end, line.Axis);
            visit(p, q, a?.Cell ?? 0, b?.Cell ?? 0, sources);
        }

        static IEnumerable<V2SourceSpan> Sources(SeamPart? part, long from, long to)
        {
            if (part is not SeamPart span) yield break;
            double total = span.To - span.From;
            foreach (var source in span.Sources)
                yield return Slice(source, (from - span.From) / total, (to - span.From) / total);
        }
    }

    private static bool MayOverlap(Key a, Key b, Window window) =>
        Math.Max(a.X, b.X) >= window.X0 && Math.Min(a.X, b.X) <= window.X1 &&
        Math.Max(a.Y, b.Y) >= window.Y0 && Math.Min(a.Y, b.Y) <= window.Y1;

    private static bool Clip(Key a, Key b, Window window, out Key start, out Key end, out double from, out double to)
    {
        start = end = default;
        from = to = 0;
        if (!MayOverlap(a, b, window)) return false;
        if (Inside(a) && Inside(b))
        {
            start = a;
            end = b;
            to = 1;
            return a != b;
        }
        Fraction min = new(0, 1), max = new(1, 1);
        if (!ClipAxis(a.X, b.X, window.X0, window.X1) || !ClipAxis(a.Y, b.Y, window.Y0, window.Y1))
            return false;
        start = new Key(At(a.X, b.X, min), At(a.Y, b.Y, min));
        end = new Key(At(a.X, b.X, max), At(a.Y, b.Y, max));
        from = min.Value;
        to = max.Value;
        return start != end;

        bool Inside(Key key) => key.X >= window.X0 && key.X <= window.X1 &&
            key.Y >= window.Y0 && key.Y <= window.Y1;

        bool ClipAxis(long u, long v, long lo, long hi)
        {
            long delta = v - u;
            if (delta == 0) return u >= lo && u <= hi;
            Fraction enter = delta > 0 ? new(lo - u, delta) : new(u - hi, -delta);
            Fraction exit = delta > 0 ? new(hi - u, delta) : new(u - lo, -delta);
            if (enter.CompareTo(min) > 0) min = enter;
            if (exit.CompareTo(max) < 0) max = exit;
            return min.CompareTo(max) < 0;
        }

        static long At(long u, long v, Fraction t)
        {
            Int128 value = (Int128)u * t.Denominator + (Int128)(v - u) * t.Numerator;
            Int128 q = value / t.Denominator, r = value % t.Denominator;
            if (2 * r >= t.Denominator) q++;
            if (2 * r < -t.Denominator) q--;
            return (long)q;
        }
    }

    private static V2SourceSpan Slice(V2SourceSpan source, double from, double to) => source with
    {
        From = source.From + from * (source.To - source.From),
        To = source.From + to * (source.To - source.From)
    };
}
