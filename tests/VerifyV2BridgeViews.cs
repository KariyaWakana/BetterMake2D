using System;
using System.IO;

namespace BetterMake2D;

public static class VerifyV2BridgeViews
{
    public static string Run()
    {
        string model = Environment.GetEnvironmentVariable("BMA2D_V2_BRIDGE_MODEL") ??
            throw new ArgumentException("Bridge fixture 3dm");
        string folder = Environment.GetEnvironmentVariable("BMA2D_V2_BRIDGE_OUTPUT") ??
            throw new ArgumentException("Bridge output");
        var names = new[] { "Top", "验证_侧转35度立面", "验证_侧转35度俯视25度轴测" };
        var reports = new string[names.Length];
        Environment.SetEnvironmentVariable("BMA2D_V2_SOURCE", model);
        Environment.SetEnvironmentVariable("BMA2D_V2_STAGE", null);
        Environment.SetEnvironmentVariable("BMA2D_V2_TILE_SEGMENTS", "20000");
        Environment.SetEnvironmentVariable("BMA2D_V2_MIN_TILE_DEPTH", "2");
        for (int i = 0; i < names.Length; i++)
        {
            Environment.SetEnvironmentVariable("BMA2D_V2_VIEW", names[i]);
            Environment.SetEnvironmentVariable("BMA2D_V2_OUTPUT", Path.Combine(folder, names[i]));
            reports[i] = names[i] + Environment.NewLine + VerifyV2DrawingArchive.Run();
        }
        return string.Join(Environment.NewLine, reports);
    }
}
