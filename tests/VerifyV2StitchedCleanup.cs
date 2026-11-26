using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Key = BetterMake2D.V2PlanarArrangement.Key;

namespace BetterMake2D;

public static class VerifyV2StitchedCleanup
{
    public static string Run()
    {
        string path = Environment.GetEnvironmentVariable("BMA2D_STITCHED_BOUNDARY") ??
            throw new ArgumentException("Set the stitched boundary checkpoint.");
        double tolerance = double.Parse(Environment.GetEnvironmentVariable("BMA2D_STITCHED_TOLERANCE") ?? ".1",
            CultureInfo.InvariantCulture);
        var plan = Read(path);
        using var process = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        void Report(string line)
        {
            process.Refresh();
            Console.WriteLine($"{clock.Elapsed.TotalSeconds:F2}s; {process.PrivateMemorySize64 / 1048576d:F1} MiB private; {line}");
        }
        var result = V2Slivers.Clean(plan, tolerance, 1, null, Report);
        int before = plan.EdgeCount;
        plan = null;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        var regions = result.Plan.Regions().ToArray();
        Report($"Prepared {regions.Length} closed regions.");
        var chainIds = new int[result.Plan.EdgeCount * 2];
        var chains = result.Plan.BoundaryChains(chainIds);
        try
        {
            Report($"Prepared {chains.Count} shared chains.");
            return $"PASS saved seam cleanup: {before} → {result.Plan.EdgeCount} edges; " +
                $"{regions.Length} closed regions, {chains.Count} shared chains, " +
                $"{result.Absorbed} bands absorbed, {result.Flattened} steps flattened.";
        }
        finally { foreach (var chain in chains) chain.Dispose(); }
    }

    private static V2PlanarArrangement Read(string path)
    {
        using var input = new BinaryReader(new BufferedStream(File.OpenRead(path), 1024 * 1024));
        double grid = input.ReadDouble();
        var edges = new List<V2OwnedEdge>();
        while (input.BaseStream.Position < input.BaseStream.Length)
        {
            var a = new Key(input.ReadInt64(), input.ReadInt64());
            var b = new Key(input.ReadInt64(), input.ReadInt64());
            int left = input.ReadInt32(), right = input.ReadInt32();
            var tags = new V2SourceSpan[input.ReadInt32()];
            for (int i = 0; i < tags.Length; i++)
                tags[i] = new V2SourceSpan(input.ReadInt32(), input.ReadInt32(), string.Intern(input.ReadString()),
                    input.ReadDouble(), input.ReadDouble());
            edges.Add(new V2OwnedEdge(a, b, left, right, tags));
        }
        return V2PlanarArrangement.FromBoundary(edges, grid);
    }
}
