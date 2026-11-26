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

        using var writable = new V2SegmentBuffer(fileBacked: true);
        writable.AddRange(expected.Take(16500));
        foreach (int index in new[] {8192, 0, 4095, 8191, 16384, 4096, 8250})
        {
            var part = writable[index];
            if (part != expected[index]) throw new InvalidOperationException($"Projected edit lost record {index}.");
            expected[index] = part with { A = Point2d.Origin };
            writable[index] = expected[index];
        }
        writable.AddRange(expected.Skip(16500));
        using var sealedFile = writable.Seal();
        if (!sealedFile.SequenceEqual(expected))
            throw new InvalidOperationException("Writable disk pages changed an edited or appended segment.");
        CompareDrawing();
        return "PASS segment storage: 17,000 ordered segments and edits round-tripped exactly; " +
            "forced streaming and memory drawings have identical tiled fills, curves and provenance.";
    }

    private static void CompareDrawing()
    {
        const double fit = .001;
        using var wall = Brep.CreateFromCornerPoints(new Point3d(-12, -6, 0), new Point3d(12, -6, 0),
            new Point3d(12, 6, 0), new Point3d(-12, 6, 0), fit);
        using var cylinder = new Cylinder(new Circle(new Plane(new Point3d(0, -4, 0),
            Vector3d.YAxis), 4), 8).ToBrep(true, true);
        var scene = new[] {new Input(wall, "wall"), new Input(cylinder, "cylinder")};
        var messages = new System.Collections.Generic.List<string>();
        using var memory = V2DrawingSolver.Solve(scene, fit, .04, minimumTileDepth: 2,
            spillDuringBoundaryCollection: false);
        using var disk = V2DrawingSolver.Solve(scene, fit, .04, minimumTileDepth: 2,
            progress: messages.Add, spillDuringBoundaryCollection: true);
        if (memory.Regions.Count < 2 || disk.Regions.Count != memory.Regions.Count ||
            !messages.Any(message => message.Contains("temporary file", StringComparison.Ordinal)) ||
            !disk.Regions.Zip(memory.Regions).All(pair => pair.First.Source == pair.Second.Source &&
                pair.First.FaceIndex == pair.Second.FaceIndex && GeometryBase.GeometryEquals(pair.First.Brep, pair.Second.Brep)) ||
            disk.Curves.Count != memory.Curves.Count ||
            !disk.Curves.Zip(memory.Curves).All(pair => pair.First.Source == pair.Second.Source &&
                pair.First.FaceIndex == pair.Second.FaceIndex && pair.First.Boundary == pair.Second.Boundary &&
                GeometryBase.GeometryEquals(pair.First.Curve, pair.Second.Curve)))
            throw new InvalidOperationException("Streaming altered the tiled drawing or did not exercise collection-time storage.");
    }
}
