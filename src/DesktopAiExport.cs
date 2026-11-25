using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BetterMake2D;

public static class DesktopAiExport
{
    public static int Run(string folder)
    {
        string source = Path.Combine(folder, "drawing.svg");
        string target = Path.Combine(folder, "drawing.ai");
        string staging = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".ai");
        string importSvg = Path.ChangeExtension(staging, ".svg");
        XNamespace svg = "http://www.w3.org/2000/svg";
        XNamespace inkscape = "http://www.inkscape.org/namespaces/inkscape";
        var root = XDocument.Load(source).Root;
        if (root.Name != svg + "svg" || !Number(root, "width", "mm", out double width) ||
            !Number(root, "height", "mm", out double height) || !Number(root, "data-model-scale", "", out double scale) ||
            scale <= 0 || width < .15 || height < .15) throw new InvalidDataException("The drawing has an invalid paper size.");
        string paperSettings = Path.Combine(folder, "paper.json");
        bool allowLarge = File.Exists(paperSettings) &&
            JsonSerializer.Deserialize<PaperExportSettings>(File.ReadAllText(paperSettings)).AllowLargeCanvas;
        PaperSizing.Check(new PaperExtent(width, height), scale, allowLarge, Console.Error.WriteLine);
        bool largeCanvas = Math.Max(width, height) > PaperSizing.StandardLimit;
        var categories = root.Elements(svg + "g").Select(category => new
        {
            id = Attr(category, "id"), label = Attr(category, inkscape + "label"),
            layers = category.Elements(svg + "g").Select(layer =>
            {
                var paths = layer.Descendants(svg + "path").ToArray();
                var fills = paths.Where(item => Attr(item, "fill-rule") == "evenodd").ToArray();
                var lines = paths.Where(item => Attr(item, "fill-rule") != "evenodd").ToArray();
                return new
                {
                    id = Attr(layer, "id"), label = Attr(layer, inkscape + "label"),
                    regions = fills.Length, lines = lines.Length,
                    loops = fills.Sum(item => Regex.Matches(Attr(item, "d"), @"\bZ\b").Count),
                    closedLines = lines.Count(item => Regex.IsMatch(Attr(item, "d"), @"\bZ\b")),
                    roundCaps = lines.Count(item => Attr(item, "stroke-linecap") == "round"),
                    dashed = lines.Count(item => Attr(item, "stroke-dasharray") == "0.7 0.4")
                };
            }).ToArray()
        }).ToArray();
        var layers = categories.SelectMany(category => category.layers).ToArray();
        if (layers.Length == 0 || layers.Any(layer => string.IsNullOrEmpty(layer.id) || string.IsNullOrEmpty(layer.label)) ||
            layers.Sum(layer => layer.regions + layer.lines) != root.Descendants(svg + "path").Count())
            throw new InvalidDataException("The SVG contains paths outside the original layers.");
        string payload = JsonSerializer.Serialize(new
        {
            source = largeCanvas ? importSvg : source, staging, width, height, largeCanvas,
            drawingTolerance = Math.Max(.002, .008 / scale), categories
        });
        using var script = new StreamReader(typeof(DesktopAiExport).Assembly.GetManifestResourceStream("ExportElevationAi.jsx"));
        var type = Type.GetTypeFromProgID("Illustrator.Application") ??
            throw new InvalidOperationException("Adobe Illustrator is required for AI export.");
        var illustrator = Activator.CreateInstance(type);
        try
        {
            if (largeCanvas)
            {
                // Illustrator uses one-tenth coordinates in its native large canvas.
                root.SetAttributeValue("width", FormattableString.Invariant($"{width / 10:G17}mm"));
                root.SetAttributeValue("height", FormattableString.Invariant($"{height / 10:G17}mm"));
                root.Document.Save(importSvg);
            }
            var report = type.InvokeMember("DoJavaScript", BindingFlags.InvokeMethod, null, illustrator,
                new object[] { script.ReadToEnd().Replace("BETTERMAKE2D_INPUT", payload) });
            File.Move(staging, target);
            Console.WriteLine(report);
            return 0;
        }
        finally
        {
            Marshal.FinalReleaseComObject(illustrator);
            File.Delete(staging);
            File.Delete(importSvg);
        }
    }

    private static string Attr(XElement item, XName name) => (string)item.Attribute(name) ?? "";

    private static bool Number(XElement item, XName name, string suffix, out double value)
    {
        string text = Attr(item, name);
        value = 0;
        return text.EndsWith(suffix, StringComparison.Ordinal) &&
            double.TryParse(text.AsSpan(0, text.Length - suffix.Length), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}
