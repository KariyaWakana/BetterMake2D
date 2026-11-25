using System;
using System.IO;
using Rhino;

namespace BetterMake2D;

public static class VerifyPaperSizing
{
    public static string Run()
    {
        var house = PaperExtent.FromModel(20, 8, UnitSystem.Meters);
        var site = PaperExtent.FromModel(19, 6, UnitSystem.Kilometers);
        var small = house.OnPaper(1000);
        var large = site.OnPaper(1000);
        Require(Math.Abs(small.Width - 20.15) < 1e-10 && Math.Abs(small.Height - 8.15) < 1e-10 &&
            PaperSizing.Band(small) == PaperBand.Small && PaperSizing.Recommend(house, 1000) == 50,
            "20 m width, scale 1:1000 and recommendation 1:50");
        Require(Math.Abs(large.Width - 19000.15) < 1e-9 && PaperSizing.Band(large) == PaperBand.Large &&
            PaperSizing.Recommend(site, 1000) == 20000, "19 km width and recommendation 1:20000");
        Require(PaperSizing.Band(new PaperExtent(95, 100)) == PaperBand.Normal &&
            PaperSizing.Band(new PaperExtent(5500, 100)) == PaperBand.Normal &&
            PaperSizing.Band(new PaperExtent(100, 5500.01)) == PaperBand.Large,
            "Small and near-limit thresholds");
        Require(PaperSizing.Band(new PaperExtent(57795, 200)) == PaperBand.Large &&
            PaperSizing.Band(new PaperExtent(80, 57795.01)) == PaperBand.OverLimit, "Absolute limit checks both axes");
        ExpectRejection(large, 1000, false, "-PaperScale 20000");
        PaperSizing.Check(large, 1000, true);
        PaperSizing.Check(new PaperExtent(100, 5779.5), 1000, false);
        ExpectRejection(new PaperExtent(100, 5779.51), 1000, false, "standard canvas");
        ExpectRejection(new PaperExtent(100, 57795.01), 1000, true, "large-canvas limit");
        string warning = "";
        PaperSizing.Check(small, 1000, false, text => warning = text);
        Require(warning.Contains("-PaperScale 50", StringComparison.Ordinal), "Small paper warning includes its scale");
        return "PASS paper units and dimensions, 20 m → 1:50, 19 km → 1:20000, soft/standard/absolute canvas limits.";
    }

    private static void ExpectRejection(PaperExtent paper, double scale, bool allow, string message)
    {
        try { PaperSizing.Check(paper, scale, allow); }
        catch (InvalidDataException error) when (error.Message.Contains(message, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException("Expected paper guard: " + message);
    }

    private static void Require(bool success, string cause)
    {
        if (!success) throw new InvalidOperationException(cause);
    }
}
