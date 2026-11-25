using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Rhino.Geometry;

namespace BetterMake2D;

internal sealed class V2DepthPool(V2DepthIndex original, int workers) : IDisposable
{
    private readonly V2DepthIndex[] lanes = new V2DepthIndex[workers];
    private readonly object cloneGate = new();
    private readonly Process process = Process.GetCurrentProcess();
    internal int MaxLanes { get; private set; }
    internal int ParallelTiles { get; private set; }

    internal bool TryClassify(V2PlanarArrangement plan, IReadOnlySet<int> output, List<string> issues, Action cancel,
        V2DepthMemo memo = null)
    {
        if (workers < 2 || plan.Cells.Length < 4096) return false;
        process.Refresh();
        long memory = Math.Max(process.WorkingSet64, process.PrivateMemorySize64) / (1024 * 1024);
        int use = Math.Min(workers, memory < 3000 ? 4 : memory < 3320 ? 3 : memory < 3640 ? 2 : 1);
        for (int i = use; i < lanes.Length; i++) Release(i);
        if (use < 2)
        {
            Release(0);
            return false;
        }
        cancel?.Invoke();
        for (int i = 0; i < use; i++) lanes[i] ??= original.Fork(cloneGate);
        MaxLanes = Math.Max(MaxLanes, use);
        ParallelTiles++;
        var notes = new string[plan.Cells.Length];
        using var stop = new CancellationTokenSource();
        var task = Task.Run(() => Parallel.For(0, use, new ParallelOptions
            { MaxDegreeOfParallelism = use, CancellationToken = stop.Token }, Classify));
        try
        {
            while (!task.Wait(40)) cancel?.Invoke();
            task.GetAwaiter().GetResult();
        }
        finally
        {
            if (!task.IsCompleted)
            {
                stop.Cancel();
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
        }
        foreach (string item in notes)
            if (item != null) issues.Add(item);
        return true;

        void Classify(int lane)
        {
            var index = lanes[lane];
            const int rows = 256;
            for (int first = lane * rows; first < plan.Cells.Length; first += use * rows)
                for (int i = first; i < Math.Min(first + rows, plan.Cells.Length); i++)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var cell = plan.Cells[i];
                    if (!plan.TryInterior(cell, out Point2d witness))
                    {
                        notes[i] = "No interior probe for a bounded cell.";
                        continue;
                    }
                    var (owner, uncertain) = memo == null ? index.Classify(witness) : memo.Classify(index, witness);
                    cell.Uncertain = uncertain;
                    if (uncertain) notes[i] = $"Uncertain owner at {witness}.";
                    if (output.Contains(owner)) cell.Owner = owner;
                }
        }
    }

    internal void Finish()
    {
        for (int i = 0; i < lanes.Length; i++) Release(i);
    }

    private void Release(int id)
    {
        if (lanes[id] is not V2DepthIndex lane) return;
        original.AddMeasurements(lane);
        lane.Dispose();
        lanes[id] = null;
    }

    public void Dispose()
    {
        Finish();
        process.Dispose();
    }
}
