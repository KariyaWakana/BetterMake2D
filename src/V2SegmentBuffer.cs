using System;
using System.Collections;
using System.Collections.Generic;

namespace BetterMake2D;

// Boundary and contact collection can exceed ten million segments. Keep its
// integer indexes stable without allocating a second, geometrically larger array.
internal sealed class V2SegmentBuffer : IReadOnlyList<V2Segment>, IDisposable
{
    private const int PageBits = 14;
    private const int PageSize = 1 << PageBits;
    private readonly List<V2Segment[]> pages = new();

    private V2SegmentFile file;
    private Dictionary<int, V2Segment> changed;
    private Dictionary<int, V2Segment> oldSegments;
    private int nextPassStart = -1;

    public int Count { get; private set; }
    internal int PassStart => nextPassStart;
    internal int ResidentCount => Count - Offset;
    private int Offset => file?.Count ?? 0;

    internal void TrackChanges()
    {
        nextPassStart = Count;
        oldSegments = new Dictionary<int, V2Segment>();
    }

    internal IEnumerable<V2Segment> ChangedSegments()
    {
        foreach (var (index, old) in oldSegments)
        {
            yield return old;
            yield return this[index];
        }
        for (int i = nextPassStart; i < Count; i++) yield return this[i];
    }

    internal IEnumerable<int> ChangedIndexes()
    {
        foreach (int index in oldSegments.Keys) yield return index;
        for (int i = nextPassStart; i < Count; i++) yield return i;
    }

    internal void Spill(Action cancel)
    {
        var previous = file;
        file = V2SegmentFile.Write(this, cancel);
        previous?.Dispose();
        changed = new Dictionary<int, V2Segment>();
        pages.Clear();
    }

    public void Dispose()
    {
        file?.Dispose();
        file = null;
        pages.Clear();
        changed?.Clear();
        oldSegments?.Clear();
        Count = 0;
    }

    public V2Segment this[int index]
    {
        get
        {
            int local = index - Offset;
            return local < 0 ? changed.TryGetValue(index, out var segment) ? segment : file[index] :
                pages[local >> PageBits][local & (PageSize - 1)];
        }
        set
        {
            int local = index - Offset;
            if (oldSegments != null && index < nextPassStart)
            {
                var old = this[index];
                if (old == value) return;
                oldSegments.TryAdd(index, old);
            }
            if (local < 0) changed[index] = value;
            else pages[local >> PageBits][local & (PageSize - 1)] = value;
        }
    }

    internal void Add(V2Segment segment)
    {
        int local = Count - Offset;
        if (local == 0) pages.Add(new V2Segment[128]);
        else if ((local & (PageSize - 1)) == 0) pages.Add(new V2Segment[PageSize]);
        else if (local < PageSize && local == pages[0].Length)
        {
            var first = pages[0];
            Array.Resize(ref first, first.Length * 2);
            pages[0] = first;
        }
        this[Count++] = segment;
    }

    internal void AddRange(IEnumerable<V2Segment> segments)
    {
        foreach (var segment in segments) Add(segment);
    }

    public IEnumerator<V2Segment> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
