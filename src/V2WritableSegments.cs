using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Rhino.Geometry;
using Row = BetterMake2D.V2SegmentFile.Row;

namespace BetterMake2D;

// Collection is serial. Keep the append page and one edited historical page;
// after contact anchoring, the ordinary read-only tile store owns the file.
internal sealed class V2WritableSegments : IDisposable
{
    private const int PageSize = V2SegmentFile.PageSize;
    private FileStream stream = V2SegmentFile.Create();
    private readonly Row[] append = new Row[PageSize];
    private readonly Row[] edit = new Row[PageSize];
    private readonly Dictionary<string, int> codes = new(StringComparer.Ordinal);
    private readonly List<string> kinds = new();
    private int count, tail, editStart = -1;
    private bool dirty;

    internal V2Segment this[int index]
    {
        get => Decode(index >= tail ? append[index - tail] : Previous(index)[index - editStart]);
        set
        {
            if (index >= tail) append[index - tail] = Encode(value);
            else
            {
                Previous(index)[index - editStart] = Encode(value);
                dirty = true;
            }
        }
    }

    internal void Add(V2Segment segment)
    {
        if (count - tail == PageSize)
        {
            Write(append, tail, PageSize);
            tail = count;
        }
        append[count++ - tail] = Encode(segment);
    }

    internal V2SegmentFile Seal()
    {
        FlushEdit();
        Write(append, tail, count - tail);
        var sealedFile = new V2SegmentFile(stream, count, kinds.ToArray());
        stream = null;
        return sealedFile;
    }

    private Row[] Previous(int index)
    {
        int start = index / PageSize * PageSize;
        if (start == editStart) return edit;
        FlushEdit();
        long offset = (long)start * V2SegmentFile.RecordSize;
        var rows = MemoryMarshal.AsBytes(edit.AsSpan());
        while (!rows.IsEmpty)
        {
            int read = RandomAccess.Read(stream.SafeFileHandle, rows, offset);
            if (read == 0) throw new EndOfStreamException("Missing projected segment page.");
            offset += read;
            rows = rows[read..];
        }
        editStart = start;
        return edit;
    }

    private void FlushEdit()
    {
        if (!dirty) return;
        Write(edit, editStart, PageSize);
        dirty = false;
    }

    private void Write(Row[] rows, int start, int length)
    {
        if (length != 0)
            RandomAccess.Write(stream.SafeFileHandle, MemoryMarshal.AsBytes(rows.AsSpan(0, length)),
                (long)start * V2SegmentFile.RecordSize);
    }

    private Row Encode(V2Segment segment)
    {
        var tag = segment.Source;
        int kind = -1;
        if (tag.Kind != null && !codes.TryGetValue(tag.Kind, out kind))
        {
            kind = kinds.Count;
            codes.Add(tag.Kind, kind);
            kinds.Add(tag.Kind);
        }
        return new Row(segment.A.X, segment.A.Y, segment.B.X, segment.B.Y, tag.From, tag.To, tag.CurveId, tag.Face, kind);
    }

    private V2Segment Decode(Row row) => new(new Point2d(row.Ax, row.Ay), new Point2d(row.Bx, row.By),
        new V2SourceSpan(row.Curve, row.Face, row.Kind < 0 ? null : kinds[row.Kind], row.From, row.To));

    public void Dispose() => stream?.Dispose();
}
