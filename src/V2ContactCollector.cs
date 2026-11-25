using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal sealed class V2ContactCollector(V2Face[] faces, V2Drawing drawing, IProjectionCamera camera,
    V2ClipPlanes clips, V2HiZ hiZ, V2SurfaceBounds surfaceBounds, V2PlanarContact planarContact,
    double tolerance, Action cancel, Action<string> progress, Action<string, TimeSpan> addTime)
{
    internal void Read(int[] members, int[] contactMembers, int? background, V2TileSchedule tileSchedule,
        Action<Curve, int, int> emit, V2LazyContact lazy = null,
        IReadOnlyList<V2ContactPair> pending = null, bool pendingCoarseChecked = false)
    {
        long indexed = Stopwatch.GetTimestamp();
        var pairs = pending == null ? new V2ContactIndex(faces, contactMembers) : null;
        addTime("contact index construction", Stopwatch.GetElapsedTime(indexed));
        var patches = new ContactSupports(faces);
        // The tile schedule already establishes one canonical request
        // per pair. A native pilot or serial replay would evaluate it twice.
        int lanes = lazy == null && pending == null && tileSchedule == null && members.Length >= 2048 ? Math.Min(4, Environment.ProcessorCount) : 1;
        var queued = new List<(int First, int Second)>(V2ContactBatch.Capacity);
        var deferred = new List<(int After, int First, int Second, Line[] Lines)>();
        var neighborsByFace = new HashSet<int>[faces.Length];
        var candidates = new List<int>();
        long searchTicks = 0, broadTicks = 0, boundTicks = 0, cacheTicks = 0, nativeTicks = 0,
            emptyTicks = 0, coincidenceTicks = 0, resolveTicks = 0, planarTicks = 0, contactTreeTicks = 0;
        int batchTrials = 0, batchWins = 0;
        bool batchRejected = false;
        using var contactBatch = new V2ContactBatch(lanes, id =>
        {
            var face = faces[id].Face;
            return face.Brep.Faces.Count == 1 ? face.Brep.DuplicateBrep() : face.DuplicateFace(false);
        }, tolerance * .1);
        try
        {
            if (pending != null)
            {
                int previous = drawing.Contacts;
                for (int k = 0; k < pending.Count; k++)
                {
                    cancel?.Invoke();
                    if (k > 0 && (k & 32767) == 0)
                        progress?.Invoke($"Lazy contact batch: {k}/{pending.Count} pairs checked, {drawing.Contacts - previous} BrepBrep.");
                    var pair = pending[k];
                    Native(pair.First, pair.Second, pendingCoarseChecked);
                }
                return;
            }
            int queried = 0;
            foreach (int i in contactMembers)
            {
                cancel?.Invoke();
                if (members.Length >= 4096 && (++queried & 1023) == 0)
                    progress?.Invoke($"3D contact search: {queried}/{members.Length} owners, {drawing.Contacts} BrepBrep and " +
                        $"{drawing.MeshContactPairs} local mesh pairs; {drawing.AnalyticPlanarPairs} analytic planar pairs.");
                long search = Stopwatch.GetTimestamp();
                pairs.Search(i, candidates);
                searchTicks += Stopwatch.GetTimestamp() - search;
                foreach (int j in candidates) CheckPair(i, j);
            }
            FlushContacts();

            void CheckPair(int i, int j)
            {
                if (background.HasValue && i != background && j != background) return;
                drawing.ContactCandidates++;
                long broad = Stopwatch.GetTimestamp();
                var a = faces[i];
                var b = faces[j];
                var overlap = a.Screen.Intersection(b.Screen);
                var common = BoundingBox.Intersection(a.Box, b.Box);
                try
                {
                    if (!overlap.HasArea ||
                        a.Id.Source == b.Id.Source && a.Face != null && b.Face != null && Adjacent(i, j))
                        return;
                    if (V2DrawingSolver.Behind(a, b, overlap, tolerance, camera) || V2DrawingSolver.Behind(b, a, overlap, tolerance, camera))
                    {
                        if (camera.Perspective) drawing.PerspectiveDepthPrunes++;
                        return;
                    }
                    if (clips.Active && common.IsValid)
                    {
                        var guarded = common;
                        guarded.Inflate(tolerance);
                        if (clips.Discards(guarded)) return;
                    }
                    if (a.Plane is Plane planeA && V2DrawingSolver.Separates(planeA, common, tolerance) ||
                        b.Plane is Plane planeB && V2DrawingSolver.Separates(planeB, common, tolerance))
                    {
                        drawing.SeparatePlanes++;
                        return;
                    }
                    if (hiZ?.Hides(common) == true)
                    {
                        drawing.ContactBoxPrunes++;
                        return;
                    }
                    if (tileSchedule != null && !tileSchedule.NeedsContact(i, j, overlap, common, out bool covered))
                    {
                        drawing.TileContactPrunes++;
                        if (covered) drawing.TileContactBoxPrunes++;
                        return;
                    }
                }
                finally { broadTicks += Stopwatch.GetTimestamp() - broad; }
                if (a.Plane is Plane p && b.Plane is Plane q)
                {
                    long mark = Stopwatch.GetTimestamp();
                    var result = planarContact.Intersect(i, j, p, q, common);
                    planarTicks += Stopwatch.GetTimestamp() - mark;
                    if (result != V2PlanarContact.Result.Fallback)
                    {
                        drawing.AnalyticPlanarPairs++;
                        drawing.AnalyticContactSegments += planarContact.Lines.Count;
                        if (planarContact.Lines.Count > 0)
                            DelayOrResolve(i, j, planarContact.Lines.ToArray());
                        return;
                    }
                    drawing.PlanarContactFallbacks++;
                }
                if (lazy != null && a.Triangles == null && b.Triangles == null)
                {
                    lazy.Defer(i, j);
                    return;
                }
                Native(i, j);
            }

            void Native(int i, int j, bool coarseChecked = false)
            {
                var a = faces[i];
                var b = faces[j];
                long bounds = Stopwatch.GetTimestamp();
                bool separated = (!coarseChecked && surfaceBounds.Separated(i, j, faces, tolerance)) ||
                    surfaceBounds.SeparatedOnRenderAxis(i, j, faces, tolerance);
                boundTicks += Stopwatch.GetTimestamp() - bounds;
                if (separated)
                {
                    drawing.BoundContactPrunes++;
                    return;
                }
                long refinement = Stopwatch.GetTimestamp();
                bool clear = surfaceBounds.SeparatedByContactTree(i, j, faces, tolerance);
                contactTreeTicks += Stopwatch.GetTimestamp() - refinement;
                if (clear)
                {
                    drawing.ContactTreePrunes++;
                    return;
                }
                if (a.Triangles != null || b.Triangles != null)
                {
                    DelayOrResolve(i, j, null);
                    return;
                }
                if (a.Box.Min == b.Box.Min && a.Box.Max == b.Box.Max)
                {
                    var first = Support(i);
                    var second = Support(j);
                    long coincidence = Stopwatch.GetTimestamp();
                    bool equal = GeometryBase.GeometryEquals(first, second);
                    coincidenceTicks += Stopwatch.GetTimestamp() - coincidence;
                    if (equal) { drawing.ExactCoincidence++; return; }
                }
                if (lanes < 2 || batchRejected) SerialContact(i, j);
                else
                {
                    queued.Add((i, j));
                    if (queued.Count == V2ContactBatch.Capacity) FlushContacts();
                }
            }

            void DelayOrResolve(int i, int j, Line[] lines)
            {
                if (queued.Count == 0) Resolve(i, j, lines);
                else
                {
                    deferred.Add((queued.Count, i, j, lines));
                    if (deferred.Count >= 1024) FlushContacts();
                }
            }

            void Resolve(int i, int j, Line[] lines)
            {
                if (lines != null)
                {
                    long mark = Stopwatch.GetTimestamp();
                    foreach (var line in lines)
                    {
                        using var curve = new LineCurve(line);
                        emit(curve, i, j);
                    }
                    resolveTicks += Stopwatch.GetTimestamp() - mark;
                    return;
                }
                var a = faces[i];
                var b = faces[j];
                bool meshFirst = a.Triangles != null;
                var mesh = meshFirst ? a.Triangles : b.Triangles;
                var other = meshFirst ? b : a;
                var support = other.Triangles == null && !other.Plane.HasValue ? Support(meshFirst ? j : i) : null;
                long begin = Stopwatch.GetTimestamp();
                drawing.MeshContactPairs++;
                if (a.ProxyChord > 0 || b.ProxyChord > 0)
                    V2MeshContact.IntersectProxy(mesh, other, meshFirst ? j : i, planarContact, tolerance * .1, cancel, line =>
                    {
                        using var curve = new LineCurve(line);
                        emit(curve, i, j);
                    });
                else
                    V2MeshContact.Intersect(mesh, other, support, tolerance * .1, cancel,
                        curve => emit(curve, i, j));
                addTime("mesh-local 3D contact", Stopwatch.GetElapsedTime(begin));
            }

            void FlushContacts()
            {
                if (queued.Count == 0) return;
                if (queued.Count >= 1024 && batchTrials < 4)
                {
                    var serial = new bool[queued.Count];
                    int next = 0;
                    long began = Stopwatch.GetTimestamp();
                    long initialEmptyTicks = emptyTicks;
                    for (int k = 0; k < queued.Count; k++)
                    {
                        serial[k] = SerialContact(queued[k].First, queued[k].Second);
                        ResolveDeferred(k + 1, ref next);
                    }
                    long serialTicks = Stopwatch.GetTimestamp() - began;
                    bool ran = contactBatch.TryRun(queued, cancel, out var trial, out long trialTicks);
                    long started = ran ? trialTicks : 0;
                    bool agrees = ran && serial.SequenceEqual(trial);
                    batchTrials++;
                    long emptyCost = emptyTicks - initialEmptyTicks;
                    if (agrees && started < emptyCost * .9) batchWins++;
                    if (ran)
                    {
                        drawing.ContactPilotAttempts += queued.Count;
                        addTime("contact concurrency pilot", Stopwatch.GetElapsedTime(0, started));
                    }
                    progress?.Invoke($"Contact concurrency pilot {batchTrials}/4: " +
                        $"serial {Stopwatch.GetElapsedTime(0, serialTicks).TotalMilliseconds:F1} ms, " +
                        $"serial empty {Stopwatch.GetElapsedTime(0, emptyCost).TotalMilliseconds:F1} ms, " +
                        $"parallel {(ran ? Stopwatch.GetElapsedTime(0, started).TotalMilliseconds.ToString("F1") : "off")} ms, " +
                        $"empty results agree {agrees}.");
                    batchRejected = ran && !agrees || batchTrials == 4 && batchWins < 3;
                    if (batchRejected) contactBatch.ReleaseCopies();
                    queued.Clear();
                    deferred.Clear();
                    return;
                }
                bool[] empty = null;
                long elapsed = 0;
                bool parallel = batchTrials == 4 && contactBatch.TryRun(queued, cancel, out empty, out elapsed);
                if (parallel)
                {
                    drawing.ContactLanes = Math.Max(drawing.ContactLanes, lanes);
                    drawing.ParallelContactAttempts += queued.Count;
                    nativeTicks += elapsed;
                    addTime("parallel contact batch wall", Stopwatch.GetElapsedTime(0, elapsed));
                }
                int eventIndex = 0;
                for (int k = 0; k < queued.Count; k++)
                {
                    var (first, second) = queued[k];
                    if (parallel && empty[k])
                    {
                        drawing.Contacts++;
                        drawing.EmptyContacts++;
                        drawing.ParallelEmptyContacts++;
                    }
                    else SerialContact(first, second);
                    ResolveDeferred(k + 1, ref eventIndex);
                }
                queued.Clear();
                deferred.Clear();
            }

            void ResolveDeferred(int after, ref int next)
            {
                while (next < deferred.Count && deferred[next].After == after)
                {
                    var contact = deferred[next++];
                    Resolve(contact.First, contact.Second, contact.Lines);
                }
            }

            bool SerialContact(int i, int j)
            {
                    drawing.Contacts++;
                    var supportA = Support(i);
                    var supportB = Support(j);
                    long native = Stopwatch.GetTimestamp();
                    bool found = Intersection.BrepBrep(supportA, supportB, tolerance * .1, out var curves, out var points);
                    long elapsed = Stopwatch.GetTimestamp() - native;
                    nativeTicks += elapsed;
                    if (found && curves is not { Length: > 0 })
                    {
                        if (points is { Length: > 0 }) drawing.PointContacts++;
                        else
                        {
                            drawing.EmptyContacts++;
                            emptyTicks += elapsed;
                        }
                    }
                    if (!found) drawing.Issues.Add($"{faces[i].Id} / {faces[j].Id} contact could not be resolved.");
                    if (curves is not { Length: > 0 }) return found && points is not { Length: > 0 };
                    drawing.ContactCurveHits++;
                    long resolve = Stopwatch.GetTimestamp();
                    foreach (var curve in curves)
                    {
                        using (curve)
                            emit(curve, i, j);
                    }
                    resolveTicks += Stopwatch.GetTimestamp() - resolve;
                    return false;
            }

            bool Adjacent(int i, int j)
            {
                var a = neighborsByFace[i] ??= faces[i].Face.AdjacentEdges().ToHashSet();
                var b = neighborsByFace[j] ??= faces[j].Face.AdjacentEdges().ToHashSet();
                return a.Overlaps(b);
            }
        }
        finally
        {
            addTime("contact broad-phase queries", Stopwatch.GetElapsedTime(0, searchTicks));
            addTime("contact box plane and adjacency guards", Stopwatch.GetElapsedTime(0, broadTicks));
            addTime("contact surface hull separation", Stopwatch.GetElapsedTime(0, boundTicks));
            addTime("refined contact tree", Stopwatch.GetElapsedTime(0, contactTreeTicks));
            addTime("contact trimmed support cache", Stopwatch.GetElapsedTime(0, cacheTicks));
            addTime("contact exact coincidence", Stopwatch.GetElapsedTime(0, coincidenceTicks));
            addTime("analytic planar contact", Stopwatch.GetElapsedTime(0, planarTicks));
            addTime("native BrepBrep", Stopwatch.GetElapsedTime(0, nativeTicks));
            addTime("empty native BrepBrep", Stopwatch.GetElapsedTime(0, emptyTicks));
            addTime("contact curve anchoring and projection", Stopwatch.GetElapsedTime(0, resolveTicks));
            progress?.Invoke($"Contact support cache: {patches.Hits} hits, {patches.Misses} builds, {patches.Evictions} evictions.");
            patches.Dispose();
            planarContact.ClearRims();
            contactBatch.ReleaseCopies();
        }

        Brep Support(int i)
        {
            long started = Stopwatch.GetTimestamp();
            var shape = patches.Get(i);
            cacheTicks += Stopwatch.GetTimestamp() - started;
            return shape;
        }
    }

    private sealed class ContactSupports(V2Face[] faces) : IDisposable
    {
        private readonly LinkedList<(int Index, Brep Shape, long Bytes)> used = new();
        private readonly Dictionary<int, LinkedListNode<(int Index, Brep Shape, long Bytes)>> entries = new();
        private readonly Brep[] whole = new Brep[faces.Length];
        private long bytes;
        internal long Hits, Misses, Evictions;

        internal Brep Get(int index)
        {
            if (whole[index] is Brep complete) { Hits++; return complete; }
            if (entries.TryGetValue(index, out var hit))
            {
                Hits++;
                used.Remove(hit);
                used.AddFirst(hit);
                return hit.Value.Shape;
            }
            var face = faces[index].Face;
            var parent = face.Brep;
            if (parent.Faces.Count == 1) { Hits++; return whole[index] = parent; }
            Misses++;
            var shape = face.DuplicateFace(false);
            long cost = (long)shape.MemoryEstimate();
            entries.Add(index, used.AddFirst((index, shape, cost)));
            bytes += cost;
            // The two most recent supports are the two arguments of the current native query.
            while (used.Count > 2 && (used.Count > 4096 || bytes > 144L * 1024 * 1024))
            {
                Evictions++;
                var old = used.Last.Value;
                used.RemoveLast();
                entries.Remove(old.Index);
                old.Shape.Dispose();
                bytes -= old.Bytes;
            }
            return shape;
        }

        public void Dispose()
        {
            foreach (var entry in used) entry.Shape.Dispose();
            used.Clear();
            entries.Clear();
            bytes = 0;
        }
    }
}
