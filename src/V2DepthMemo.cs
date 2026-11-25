
using System;
using System.Threading;
using Rhino.Geometry;

namespace BetterMake2D;

// The depth query depends on the original scene and the exact witness, not
// the arrangement that produced it. Collisions only evict a cached result.
internal sealed class V2DepthMemo(int segmentCount)
{
    private readonly record struct Entry(double X, double Y, int Owner, bool Uncertain, bool Set);

    private readonly Entry[] entries = new Entry[segmentCount >= 1_000_000 ? 1 << 19 : 1 << 15];
    private readonly object[] guards = Array.ConvertAll(new int[128], _ => new object());
    private int hits;
    internal int Hits => Volatile.Read(ref hits);

    internal (int Owner, bool Uncertain) Classify(V2DepthIndex index, Point2d point, int adjacent = -1)
    {
        int slot = point.GetHashCode() & (entries.Length - 1);
        var gate = guards[slot & (guards.Length - 1)];
        lock (gate)
        {
            var entry = entries[slot];
            if (entry.Set && entry.X == point.X && entry.Y == point.Y)
            {
                Interlocked.Increment(ref hits);
                return (entry.Owner, entry.Uncertain);
            }
        }
        var result = index.Classify(point, adjacent: adjacent);
        lock (gate)
            entries[slot] = new Entry(point.X, point.Y, result.Owner, result.Uncertain, true);
        return result;
    }
}
