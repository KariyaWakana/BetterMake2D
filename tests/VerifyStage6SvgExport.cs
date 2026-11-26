using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Environment = System.Environment;

namespace BetterMake2D;

public static class VerifyStage6SvgExport
{
    public static string Run()
    {
        string drawing = Environment.GetEnvironmentVariable("BMA2D_SVG_DRAWING");
        string output = Environment.GetEnvironmentVariable("BMA2D_SVG_OUTPUT") ?? throw new ArgumentException("SVG output directory");
        Directory.CreateDirectory(output);
        if (!string.IsNullOrEmpty(drawing))
        {
            string requestedScale = Environment.GetEnvironmentVariable("BMA2D_SVG_SCALE");
            double scale = string.IsNullOrEmpty(requestedScale) ? 1000 :
                double.Parse(requestedScale, CultureInfo.InvariantCulture);
            return VerifyFile(drawing, Path.Combine(output, "立面_分图层.svg"), scale);
        }

        string fixture = Path.Combine(output, "孔洞与原图层.3dm");
        const string owner = "fixture-face";
        var corner = new Point3d(300000, 15000, 0);
        var box = new Rectangle3d(new Plane(corner, Vector3d.XAxis, Vector3d.YAxis), 2000, 1000);
        using var outer = box.ToNurbsCurve();
        using var hole = new Circle(corner + new Vector3d(1000, 500, 0), 150).ToNurbsCurve();
        using var diagonal = new LineCurve(corner, corner + new Vector3d(2000, 1000, 0));
        using var plate = Brep.CreatePlanarBreps(new Curve[] {outer, hole}, 1e-6).Single();
        using (var model = new File3dm())
        {
            model.Settings.ModelUnitSystem = UnitSystem.Millimeters;
            int fills = model.AllLayers.AddLayer("测试填色", System.Drawing.Color.CornflowerBlue);
            int strokes = model.AllLayers.AddLayer("测试线条", System.Drawing.Color.DarkGray);
            using var attributes = new ObjectAttributes { LayerIndex = fills };
            attributes.SetUserString("OriginalLayer", "结构&接头::轴套");
            attributes.SetUserString("SourceObject", owner);
            attributes.SetUserString("SourceFace", "3");
            if (model.Objects.AddBrep(plate, attributes) == Guid.Empty)
                throw new IOException("Could not write the synthetic filling region.");
            attributes.LayerIndex = strokes;
            attributes.SetUserString("OriginalLayer", "线稿::辅助线");
            if (model.Objects.AddCurve(diagonal, attributes) == Guid.Empty || !model.Write(fixture, 8))
                throw new IOException("Could not write the synthetic drawing.");
        }
        string svgFile = Path.Combine(output, "孔洞与原图层.svg");
        string report = VerifyFile(fixture, svgFile, 1000);
        XNamespace svg = "http://www.w3.org/2000/svg";
        XNamespace inkscape = "http://www.inkscape.org/namespaces/inkscape";
        var tree = XDocument.Load(svgFile);
        if ((string)tree.Root.Attribute("width") != "2.15mm" || (string)tree.Root.Attribute("height") != "1.15mm" ||
            tree.Descendants(svg + "g").All(element => (string)element.Attribute(inkscape + "label") != "结构&接头::轴套"))
            throw new InvalidDataException("The SVG changed the physical drawing size or original layer label.");
        var fill = tree.Descendants(svg + "path").Single(path => (string)path.Attribute("fill-rule") == "evenodd");
        var stroke = tree.Descendants(svg + "path").Single(path => path.Attribute("stroke") != null);
        if (Regex.Matches((string)fill.Attribute("d"), @"\bZ\b").Count != 2 ||
            !((string)fill.Attribute("d")).Contains("C ", StringComparison.Ordinal) ||
            (string)stroke.Attribute("d") != "M 0.075 1.075 L 2.075 0.075 ")
            throw new InvalidDataException("The SVG lost the compound hole, Bezier curves, or upright Y direction.");
        return report + "\nPASS compound hole, vector arc, special layer label, Y direction and physical scale.";
    }

    private static string VerifyFile(string drawing, string path, double scale)
    {
        string paperNotice = null;
        var result = ElevationSvgExport.Write(drawing, path, scale,
            Environment.GetEnvironmentVariable("BMA2D_SVG_ALLOW_LARGE") == "1", message => paperNotice = message);
        bool large = new FileInfo(drawing).Length >= 256L * 1024 * 1024;
        if (large) ReleaseReadbackMemory();
        var roundoffIds = result.RoundoffRegions.ToHashSet();
        var expected = new Dictionary<(string Layer, string Source, string Face, bool Fill), int>();
        var roundoffDescriptions = new List<string>();
        int expectedLoops = 0, expectedClosedLines = 0;
        using (var model = File3dm.Read(drawing) ?? throw new IOException(drawing))
        {
            double paperScale = RhinoMath.UnitScale(model.Settings.ModelUnitSystem, UnitSystem.Millimeters) / scale;
            foreach (var item in model.Objects)
            {
                var attributes = item.Attributes;
                var geometry = item.Geometry;
                if (roundoffIds.Contains(attributes.ObjectId))
                {
                    if (geometry is not Brep region ||
                        !ElevationSvgExport.IsRoundoffRegion(region.GetBoundingBox(true), paperScale))
                        throw new InvalidDataException("The SVG would drop an original region with representable paper width.");
                    roundoffDescriptions.Add($"{attributes.GetUserString("SourceObject")}/{attributes.GetUserString("SourceFace")} ({attributes.ObjectId})");
                    continue;
                }
                var key = (attributes.GetUserString("OriginalLayer"), attributes.GetUserString("SourceObject"),
                    attributes.GetUserString("SourceFace"), geometry is Brep);
                expected[key] = expected.GetValueOrDefault(key) + (geometry is Brep brep ? brep.Faces.Count : 1);
                if (geometry is Brep fill) expectedLoops += fill.Loops.Count;
                else if (geometry is Curve { IsClosed: true }) expectedClosedLines++;
            }
        }
        if (large) ReleaseReadbackMemory();
        var actual = new Dictionary<(string Layer, string Source, string Face, bool Fill), int>();
        int paths = 0, closed = 0, closedLines = 0;
        using (var reader = XmlReader.Create(path))
        {
            const string svg = "http://www.w3.org/2000/svg";
            const string inkscape = "http://www.inkscape.org/namespaces/inkscape";
            string layer = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != svg) continue;
                if (reader.LocalName == "g" && reader.Depth == 2) layer = reader.GetAttribute("label", inkscape);
                if (reader.LocalName != "path") continue;
                bool fill = reader.GetAttribute("fill-rule") == "evenodd";
                var key = (layer, reader.GetAttribute("data-source"), reader.GetAttribute("data-face"), fill);
                actual[key] = actual.GetValueOrDefault(key) + 1;
                paths++;
                string data = reader.GetAttribute("d") ?? throw new InvalidDataException("Missing SVG path geometry.");
                if (fill)
                {
                    int loops = Regex.Matches(data, @"\bZ\b").Count;
                    if (Regex.Matches(data, @"\bM\b").Count != loops)
                        throw new InvalidDataException("An SVG fill has an open subpath.");
                    closed += loops;
                }
                else
                {
                    if (reader.GetAttribute("stroke-linecap") != "round" || reader.GetAttribute("stroke-linejoin") != "round")
                        throw new InvalidDataException("An SVG stroke lost its round cap or join.");
                    if (Regex.IsMatch(data, @"\bZ\b")) closedLines++;
                }
            }
        }
        if (expected.Count != actual.Count || expected.Any(row => !actual.TryGetValue(row.Key, out int number) || row.Value != number) ||
            paths != result.Lines + result.Regions || closed != result.Loops || result.Loops != expectedLoops ||
            closedLines != expectedClosedLines ||
            result.MaximumSampleError > DrawingPath.FitMillimeters)
            throw new InvalidDataException("The SVG reopened with different layer attribution, topology or fitted path counts.");
        string report = FormattableString.Invariant(
            $"PASS {result.Lines} line paths ({closedLines} closed), round caps and joins, {result.Regions} fill paths, {closed} closed loops, max sampled error {result.MaximumSampleError:G6} model mm. {path}");
        if (paperNotice != null) report = paperNotice + Environment.NewLine + report;
        return roundoffDescriptions.Count == 0 ? report : report + Environment.NewLine +
            $"Excluded {roundoffDescriptions.Count} projected fills below model roundoff and SVG paper resolution:" +
            Environment.NewLine + string.Join(Environment.NewLine, roundoffDescriptions);

        static void ReleaseReadbackMemory()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
