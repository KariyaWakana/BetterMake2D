using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

// Only successful empty results leave the workers. Native curve fingerprints
// can differ, so nonempty results are reconstructed by the serial caller.
internal sealed class V2ContactBatch(int lanes, Func<int, Brep> duplicate, double tolerance) : IDisposable
{
    internal const int Capacity = 4096;
    private readonly Process process = Process.GetCurrentProcess();
    private readonly object sourceGate = new();
    private readonly ThreadLocal<SupportCache> supports = new(() => new SupportCache());
    private BlockingCollection<Action>[] inboxes;
    private Thread[] threads;

    internal bool TryRun(IReadOnlyList<(int First, int Second)> pairs, Action cancel, out bool[] empty, out long ticks)
    {
        empty = null;
        ticks = 0;
        if (pairs.Count < 256 || lanes < 2) return false;
        process.Refresh();
        if (Math.Max(process.WorkingSet64, process.PrivateMemorySize64) >= 3420L * 1024 * 1024)
        {
            ReleaseCopies();
            return false;
        }
        long start = Stopwatch.GetTimestamp();
        Start();
        var order = new int[pairs.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int compare = pairs[a].First.CompareTo(pairs[b].First);
            return compare != 0 ? compare : pairs[a].Second.CompareTo(pairs[b].Second);
        });
        var answers = new bool[pairs.Count];
        using var stop = new CancellationTokenSource();
        using var remaining = new CountdownEvent(lanes);
        var errors = new ExceptionDispatchInfo[lanes];
        for (int i = 0; i < lanes; i++)
        {
            int lane = i;
            inboxes[lane].Add(() =>
            {
                try { Work(lane); }
                catch (Exception error) { errors[lane] = ExceptionDispatchInfo.Capture(error); }
                finally { remaining.Signal(); }
            });
        }
        try { while (!remaining.Wait(40)) cancel?.Invoke(); }
        finally
        {
            stop.Cancel();
            remaining.Wait();
        }
        foreach (var error in errors) error?.Throw();
        ticks = Stopwatch.GetTimestamp() - start;
        empty = answers;
        return true;

        void Work(int lane)
        {
            var cache = supports.Value;
            int end = (lane + 1) * order.Length / lanes;
            for (int i = lane * order.Length / lanes; i < end; i++)
            {
                stop.Token.ThrowIfCancellationRequested();
                int index = order[i];
                var pair = pairs[index];
                var a = cache.Get(pair.First, Copy);
                var b = cache.Get(pair.Second, Copy);
                bool ok = Intersection.BrepBrep(a, b, tolerance, out var curves, out var points);
                answers[index] = ok && (curves?.Length ?? 0) == 0 && (points?.Length ?? 0) == 0;
                foreach (var curve in curves ?? Array.Empty<Curve>()) curve?.Dispose();
            }
        }
        Brep Copy(int id)
        {
            lock (sourceGate) return duplicate(id);
        }
    }

    private void Start()
    {
        if (threads != null) return;
        inboxes = new BlockingCollection<Action>[lanes];
        threads = new Thread[lanes];
        for (int i = 0; i < lanes; i++)
        {
            var queue = inboxes[i] = new BlockingCollection<Action>();
            threads[i] = new Thread(() =>
            {
                try { foreach (var work in queue.GetConsumingEnumerable()) work(); }
                finally { if (supports.IsValueCreated) supports.Value.Clear(); }
            }) { IsBackground = true, Name = "BetterMake2D contact" };
            threads[i].Start();
        }
    }

    internal void ReleaseCopies()
    {
        if (threads == null) return;
        foreach (var inbox in inboxes) inbox.CompleteAdding();
        foreach (var thread in threads) thread.Join();
        foreach (var inbox in inboxes) inbox.Dispose();
        inboxes = null;
        threads = null;
    }

    public void Dispose()
    {
        ReleaseCopies();
        supports.Dispose();
        process.Dispose();
    }

    private sealed class SupportCache
    {
        private readonly Dictionary<int, LinkedListNode<(int Id, Brep Shape, long Cost)>> shapes = new();
        private readonly LinkedList<(int Id, Brep Shape, long Cost)> recent = new();
        private long bytes;

        internal Brep Get(int id, Func<int, Brep> duplicate)
        {
            if (shapes.TryGetValue(id, out var hit))
            {
                recent.Remove(hit);
                recent.AddFirst(hit);
                return hit.Value.Shape;
            }
            var shape = duplicate(id) ?? throw new InvalidOperationException("Could not copy a contact face.");
            long cost = (long)shape.MemoryEstimate();
            while (recent.Count >= 2 && (recent.Count >= 512 || bytes + cost > 12L * 1024 * 1024))
            {
                var entry = recent.Last.Value;
                recent.RemoveLast();
                shapes.Remove(entry.Id);
                bytes -= entry.Cost;
                entry.Shape.Dispose();
            }
            shapes.Add(id, recent.AddFirst((id, shape, cost)));
            bytes += cost;
            return shape;
        }

        internal void Clear()
        {
            foreach (var entry in recent) entry.Shape.Dispose();
            recent.Clear();
            shapes.Clear();
            bytes = 0;
        }
    }
}
