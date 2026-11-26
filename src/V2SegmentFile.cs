using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Rhino.Geometry;

namespace BetterMake2D;

// Read-only, lossless storage for clusters whose segment table would otherwise
// compete with the local DCEL. Each reading thread caches one sequential page.
internal sealed class V2SegmentFile : IReadOnlyList<V2Segment>, IDisposable
{
    internal const int PageSize = 4096;
    internal static readonly int RecordSize = Unsafe.SizeOf<Row>();

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal readonly record struct Row(double Ax, double Ay, double Bx, double By, double From, double To,
        int Curve, int Face, int Kind);

    private sealed class Page
    {
        internal int Start = -1;
        internal readonly Row[] Rows = new Row[PageSize];
    }

    private readonly FileStream file;
    private readonly string[] kinds;
    private readonly ThreadLocal<Page> pages = new(() => new Page());
    public int Count { get; }

    internal V2SegmentFile(FileStream file, int count, string[] kinds)
    {
        this.file = file;
        this.kinds = kinds;
        Count = count;
    }

    internal static V2SegmentFile Write(IReadOnlyList<V2Segment> segments, Action cancel)
    {
        var stream = Create();
        try
        {
            var codes = new Dictionary<string, int>(StringComparer.Ordinal);
            var kinds = new List<string>();
            var rows = new Row[PageSize];
            for (int start = 0; start < segments.Count; start += PageSize)
            {
                cancel?.Invoke();
                int count = Math.Min(PageSize, segments.Count - start);
                for (int i = 0; i < count; i++)
                {
                    var segment = segments[start + i];
                    var source = segment.Source;
                    int kind = -1;
                    if (source.Kind != null && !codes.TryGetValue(source.Kind, out kind))
                    {
                        kind = kinds.Count;
                        codes.Add(source.Kind, kind);
                        kinds.Add(source.Kind);
                    }
                    rows[i] = new Row(segment.A.X, segment.A.Y, segment.B.X, segment.B.Y,
                        source.From, source.To, source.CurveId, source.Face, kind);
                }
                stream.Write(MemoryMarshal.AsBytes(rows.AsSpan(0, count)));
            }
            stream.Flush();
            return new V2SegmentFile(stream, segments.Count, kinds.ToArray());
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static FileStream Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"BetterMake2D-{Guid.NewGuid():N}.segments");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1,
            FileOptions.DeleteOnClose | FileOptions.RandomAccess);
    }

    public V2Segment this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            var page = pages.Value;
            int start = index / PageSize * PageSize;
            if (start != page.Start)
            {
                long offset = (long)start * RecordSize;
                var data = MemoryMarshal.AsBytes(page.Rows.AsSpan(0, Math.Min(PageSize, Count - start)));
                while (!data.IsEmpty)
                {
                    int read = RandomAccess.Read(file.SafeFileHandle, data, offset);
                    if (read == 0) throw new EndOfStreamException("Could not read the projected segment table.");
                    offset += read;
                    data = data[read..];
                }
                page.Start = start;
            }
            var row = page.Rows[index - start];
            return new V2Segment(new Point2d(row.Ax, row.Ay), new Point2d(row.Bx, row.By),
                new V2SourceSpan(row.Curve, row.Face, row.Kind < 0 ? null : kinds[row.Kind], row.From, row.To));
        }
    }

    public IEnumerator<V2Segment> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        pages.Dispose();
        file.Dispose();
    }
}
