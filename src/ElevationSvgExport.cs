using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class ElevationSvgExport
{
    private const string Svg = "http://www.w3.org/2000/svg";
    private const string Inkscape = "http://www.inkscape.org/namespaces/inkscape";
    private const double StrokeMillimeters = PaperSizing.StrokeWidth;
    private sealed record Item(GeometryBase Geometry, string Layer, string Source, int Face, string Color,
        bool Hidden, string Boundary)
    {
        internal bool Section => Boundary == "SectionCut";
    }

    internal static (int Lines, int Regions, int Loops, double MaximumSampleError, IReadOnlyList<Guid> RoundoffRegions) Write(
        string drawingFile, string outputFile, double modelScale, bool allowLargeCanvas = false, Action<string> paperWarning = null)
    {
        if (!double.IsFinite(modelScale) || modelScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(modelScale));
        string destination = Path.GetFullPath(outputFile);
        if (!Path.GetExtension(destination).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An SVG output path is required.", nameof(outputFile));
        using var model = File3dm.Read(drawingFile) ?? throw new IOException($"Could not read {drawingFile}.");
        var units = model.Settings.ModelUnitSystem;
        if (units is UnitSystem.None or UnitSystem.Unset or UnitSystem.CustomUnits)
            throw new InvalidDataException("A known model unit is required to export at paper scale.");
        double modelMillimeters = RhinoMath.UnitScale(units, UnitSystem.Millimeters);
        double paperScale = modelMillimeters / modelScale;
        if (!double.IsFinite(paperScale) || paperScale <= 0)
            throw new InvalidDataException("The model-to-paper scale is invalid.");
        var layers = model.AllLayers.ToDictionary(layer => layer.Index);
        var bounds = BoundingBox.Empty;
        var items = new List<Item>();
        var roundoffRegions = new List<Guid>();
        foreach (var obj in model.Objects)
        {
            var attr = obj.Attributes;
            string source = attr.GetUserString("SourceObject");
            string layer = attr.GetUserString("OriginalLayer");
            var geometry = obj.Geometry;
            if (!int.TryParse(attr.GetUserString("SourceFace"), NumberStyles.None, CultureInfo.InvariantCulture, out int face) ||
                string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(layer) ||
                !layers.TryGetValue(attr.LayerIndex, out var style) ||
                geometry is not (Curve or Brep) || !geometry.IsValid)
                throw new InvalidDataException($"Invalid drawing geometry or missing source information for {attr.ObjectId}.");
            var box = geometry.GetBoundingBox(true);
            if (!box.IsValid || Math.Max(Math.Abs(box.Min.Z), Math.Abs(box.Max.Z)) * modelMillimeters > .01)
                throw new InvalidDataException($"Drawing geometry from {source}/{face} is outside the XY plane.");
            if (geometry is Brep && IsRoundoffRegion(box, paperScale))
            {
                roundoffRegions.Add(attr.ObjectId);
                continue;
            }
            bounds.Union(box);
            var color = style.Color;
            items.Add(new Item(geometry, layer, source, face, $"#{color.R:X2}{color.G:X2}{color.B:X2}",
                attr.GetUserString("Visibility") == "Hidden", attr.GetUserString("BoundaryType")));
        }
        if (items.Count == 0)
            throw new InvalidDataException("The Rhino drawing has no geometry to export.");

        const double padding = StrokeMillimeters * .5;
        double width = bounds.Diagonal.X * paperScale + 2 * padding;
        double height = bounds.Diagonal.Y * paperScale + 2 * padding;
        PaperSizing.Check(new PaperExtent(width, height), modelScale, allowLargeCanvas, paperWarning);
        int lines = 0, regions = 0, loops = 0, groups = 0;
        double maximumSampleError = 0;
        string folder = Path.GetDirectoryName(destination);
        Directory.CreateDirectory(folder);
        string staging = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".svg");
        try
        {
            using (var writer = XmlWriter.Create(staging, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            {
                writer.WriteStartElement("svg", Svg);
                writer.WriteAttributeString("xmlns", "inkscape", null, Inkscape);
                writer.WriteAttributeString("version", "1.1");
                writer.WriteAttributeString("width", Number(width) + "mm");
                writer.WriteAttributeString("height", Number(height) + "mm");
                writer.WriteAttributeString("viewBox", $"0 0 {Number(width)} {Number(height)}");
                writer.WriteAttributeString("data-model-scale", Number(modelScale));
                writer.WriteAttributeString("data-model-min-x", Number(bounds.Min.X));
                writer.WriteAttributeString("data-model-max-y", Number(bounds.Max.Y));
                WriteLayerGroups(writer, items.Where(item => item.Geometry is Brep), "闭合区", fill: true);
                WriteLayerGroups(writer, items.Where(item => item.Geometry is Curve && !item.Hidden && !item.Section), "线条", fill: false);
                WriteLayerGroups(writer, items.Where(item => item.Geometry is Curve && !item.Hidden && item.Section), "SectionCuts", fill: false);
                WriteLayerGroups(writer, items.Where(item => item.Geometry is Curve && item.Hidden), "隐藏线", fill: false);
                writer.WriteEndElement();
            }
            File.Move(staging, destination);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
        return (lines, regions, loops, maximumSampleError, roundoffRegions);

        void WriteLayerGroups(XmlWriter writer, IEnumerable<Item> shapes, string title, bool fill)
        {
            var byLayer = shapes.GroupBy(item => item.Layer, StringComparer.Ordinal).ToArray();
            if (byLayer.Length == 0)
                return;
            BeginLayer(writer, title);
            foreach (var layer in byLayer)
            {
                BeginLayer(writer, layer.Key);
                foreach (var item in layer)
                {
                    if (item.Geometry is Curve line)
                    {
                        var path = new StringBuilder();
                        WriteCurve(path, line);
                        WritePath(writer, item, path, fill);
                        lines++;
                        continue;
                    }
                    var brep = (Brep)item.Geometry;
                    var region = new DraftRegion(brep, item.Source, item.Face);
                    foreach (var face in brep.Faces)
                    {
                        var path = new StringBuilder();
                        foreach (var loop in face.Loops)
                        {
                            using var curve = DrawingPath.FilledLoop(loop, region, modelMillimeters);
                            var size = curve.GetBoundingBox(true).Diagonal;
                            double fit = Math.Clamp(Math.Min(size.X, size.Y) * modelMillimeters * .05,
                                1e-8, DrawingPath.FitMillimeters);
                            WriteCurve(path, curve, fit);
                            loops++;
                        }
                        WritePath(writer, item, path, fill);
                        regions++;
                    }
                }
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
        }

        void BeginLayer(XmlWriter writer, string label)
        {
            writer.WriteStartElement("g", Svg);
            writer.WriteAttributeString("id", $"layer-{++groups}");
            writer.WriteAttributeString("inkscape", "groupmode", Inkscape, "layer");
            writer.WriteAttributeString("inkscape", "label", Inkscape, label);
        }

        void WriteCurve(StringBuilder path, Curve curve, double fit = DrawingPath.FitMillimeters)
        {
            var measure = DrawingPath.Write(curve, modelMillimeters,
                point => path.Append("M ").Append(Coordinates(point)).Append(' '),
                point => path.Append("L ").Append(Coordinates(point)).Append(' '),
                (first, second, end) => path.Append("C ").Append(Coordinates(first)).Append(' ')
                    .Append(Coordinates(second)).Append(' ').Append(Coordinates(end)).Append(' '), fit);
            if (measure.Segments == 0)
                throw new InvalidDataException("A vector path has no drawable segments.");
            maximumSampleError = Math.Max(maximumSampleError, measure.SampleError);
            if (curve.IsClosed) path.Append("Z ");
        }

        string Coordinates(Point3d point) =>
            $"{Number((point.X - bounds.Min.X) * paperScale + padding)} " +
            $"{Number((bounds.Max.Y - point.Y) * paperScale + padding)}";
    }

    internal static bool IsRoundoffRegion(BoundingBox box, double paperScale)
    {
        // Only exclude fills whose thin axis is below both model arithmetic and
        // the coordinate precision available on paper. The saved Rhino geometry is retained.
        const double roundoff = 128 * 2.220446049250313e-16;
        const double paperResolution = 1e-12;
        var size = box.Diagonal;
        double x = Math.Max(1, Math.Max(Math.Abs(box.Min.X), Math.Abs(box.Max.X)));
        double y = Math.Max(1, Math.Max(Math.Abs(box.Min.Y), Math.Abs(box.Max.Y)));
        return size.X <= Math.Min(roundoff * x, paperResolution / paperScale) ||
            size.Y <= Math.Min(roundoff * y, paperResolution / paperScale);
    }

    private static void WritePath(XmlWriter writer, Item item, StringBuilder path, bool fill)
    {
        writer.WriteStartElement("path", Svg);
        writer.WriteAttributeString("d", path.ToString());
        writer.WriteAttributeString("data-source", item.Source);
        writer.WriteAttributeString("data-face", item.Face.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("fill", fill ? item.Color : "none");
        if (fill)
            writer.WriteAttributeString("fill-rule", "evenodd");
        else
        {
            if (item.Boundary == "Tangent") writer.WriteAttributeString("data-boundary-type", item.Boundary);
            writer.WriteAttributeString("stroke", item.Color);
            writer.WriteAttributeString("stroke-width", Number(StrokeMillimeters));
            writer.WriteAttributeString("stroke-linejoin", "round");
            writer.WriteAttributeString("stroke-linecap", "round");
            if (item.Hidden) writer.WriteAttributeString("stroke-dasharray", "0.7 0.4");
        }
        writer.WriteEndElement();
    }

    private static string Number(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);
}
