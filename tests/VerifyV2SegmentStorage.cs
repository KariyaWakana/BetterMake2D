using System;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2SegmentStorage
{
    public static string Run()
    {
        var expected = Enumerable.Range(0, 17_000).Select(i => new V2Segment(
            new Point2d(-.125 * i, i * .03125), new Point2d(i + .05, -i),
            new V2SourceSpan(i, i % 497, i % 3 == 0 ? "Contact" : i % 3 == 1 ? "Silhouette" : null,
                i * .001, i * .001 + .004))).ToArray();
        using var inMemory = new V2SegmentBuffer();
        inMemory.AddRange(expected);
        expected[16384] = expected[16384] with { B = Point2d.Origin };
        inMemory[16384] = expected[16384];
        using var onDisk = V2SegmentFile.Write(inMemory, null);
        if (!onDisk.SequenceEqual(expected) || onDisk[0] != expected[0] || onDisk[^1] != expected[^1])
            throw new InvalidOperationException("Paged or disk segment storage changed endpoint or source provenance.");
        inMemory.Spill(null);
        inMemory.TrackChanges();
        var old = expected[16384];
        var edited = old with { B = new Point2d(-1, 4), Source = old.Source with { To = .3 } };
        var tail = old with { A = edited.B, Source = old.Source with { From = .3 } };
        inMemory[16384] = edited;
        inMemory.Add(tail);
        if (inMemory[16384] != edited || inMemory[^1] != tail ||
            !inMemory.ChangedIndexes().SequenceEqual(new[] { 16384, expected.Length }) ||
            !inMemory.ChangedSegments().SequenceEqual(new[] { old, edited, tail }) ||
            onDisk[16384] != old)
            throw new InvalidOperationException("An anchored contact must retain both sides of an edited disk segment without rewriting the original.");
        return "PASS segment storage: 17,000 ordered segments, preserved provenance, and a deferred weld spanning disk and append storage.";
    }
}
