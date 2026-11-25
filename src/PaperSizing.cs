using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino;

namespace BetterMake2D;

internal enum PaperBand { Normal, Small, Large, OverLimit }

internal readonly record struct PaperExtent(double Width, double Height)
{
    internal double LongEdge => Math.Max(Width, Height);
    internal bool IsValid => double.IsFinite(Width) && double.IsFinite(Height) && Width >= 0 && Height >= 0 && LongEdge > 0;

    internal static PaperExtent FromModel(double width, double height, UnitSystem units)
    {
        if (units is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
            throw new InvalidDataException("Known model units are required for paper scale.");
        double millimeters = RhinoMath.UnitScale(units, UnitSystem.Millimeters);
        return new(width * millimeters, height * millimeters);
    }

    internal PaperExtent OnPaper(double denominator) =>
        new(Width / denominator + PaperSizing.StrokeWidth, Height / denominator + PaperSizing.StrokeWidth);
}

internal static class PaperSizing
{
    internal const double StrokeWidth = .15;
    internal const double SmallLimit = 100;
    internal const double WarningLimit = 5500;
    internal const double StandardLimit = 5779.5;
    internal const double LargeLimit = 57795;
    internal static readonly double[] StandardScales =
        { 1, 2, 5, 10, 20, 25, 50, 75, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000,
          20000, 25000, 50000, 100000, 200000, 250000, 500000, 1000000 };

    internal static PaperBand Band(PaperExtent paper) => paper.LongEdge > LargeLimit ? PaperBand.OverLimit :
        paper.LongEdge > WarningLimit ? PaperBand.Large : paper.LongEdge < SmallLimit ? PaperBand.Small : PaperBand.Normal;

    internal static double Recommend(PaperExtent model, double requested)
    {
        bool tooSmall = model.OnPaper(requested).LongEdge < SmallLimit;
        var sequence = tooSmall ? StandardScales.Reverse() : StandardScales.AsEnumerable();
        foreach (double candidate in sequence)
        {
            double length = model.OnPaper(candidate).LongEdge;
            if (length >= 300 && length <= 1200) return candidate;
        }
        return model.LongEdge / (600 - StrokeWidth);
    }

    internal static void Check(PaperExtent paper, double scale, bool allowLarge, Action<string> warning = null)
    {
        if (!paper.IsValid || !double.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("Invalid paper dimensions or scale.");
        var band = Band(paper);
        if (band == PaperBand.Normal) return;
        var model = new PaperExtent(Math.Max(0, (paper.Width - StrokeWidth) * scale),
            Math.Max(0, (paper.Height - StrokeWidth) * scale));
        string suggestion = model.IsValid ? " Recommended -PaperScale " + ScaleText(Recommend(model, scale)) + "." : "";
        string size = FormattableString.Invariant($"{paper.Width:F1} × {paper.Height:F1} mm");
        if (band == PaperBand.OverLimit)
            throw new InvalidDataException($"Paper size {size} exceeds the 57,795 mm Illustrator large-canvas limit." + suggestion);
        if (paper.LongEdge > StandardLimit && !allowLarge)
            throw new InvalidDataException($"Paper size {size} exceeds the 5,779.5 mm standard canvas. Use -AllowLargeCanvas to keep its scale." + suggestion);
        if (band == PaperBand.Small)
            warning?.Invoke($"Paper size {size} has a long edge under 100 mm. A 0.15 mm stroke may look heavy." + suggestion);
        else
            warning?.Invoke($"Paper size {size} is near or above the standard canvas limit. Check the imported artboard and scale." + suggestion);
    }

    internal static string ScaleText(double scale) => scale.ToString("G8", CultureInfo.InvariantCulture);
}
