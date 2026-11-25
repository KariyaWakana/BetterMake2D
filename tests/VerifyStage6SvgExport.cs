using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        using var model = File3dm.Read(drawing) ?? throw new IOException(drawing);
        var roundoffIds = result.RoundoffRegions.ToHashSet();
        double paperScale = RhinoMath.UnitScale(model.Settings.ModelUnitSystem, UnitSystem.Millimeters) / scale;
        var roundoffDescriptions = model.Objects.Where(item => roundoffIds.Contains(item.Attributes.ObjectId)).Select(item =>
        {
            if (item.Geometry is not Brep brep || !ElevationSvgExport.IsRoundoffRegion(brep.GetBoundingBox(true), paperScale))
                throw new InvalidDataException("The SVG would drop an original region with representable paper width.");
            return $"{item.Attributes.GetUserString("SourceObject")}/{item.Attributes.GetUserString("SourceFace")} ({item.Attributes.ObjectId})";
        }).ToArray();
        var retained = model.Objects.Where(item => !roundoffIds.Contains(item.Attributes.ObjectId)).ToArray();
        var expected = retained.SelectMany(item =>
        {
            var attributes = item.Attributes;
            var key = (Layer: attributes.GetUserString("OriginalLayer"), Source: attributes.GetUserString("SourceObject"),
                Face: attributes.GetUserString("SourceFace"), Fill: item.Geometry is Brep);
            return Enumerable.Repeat(key, item.Geometry is Brep brep ? brep.Faces.Count : 1);
        }).GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count());
        XNamespace svg = "http://www.w3.org/2000/svg";
        XNamespace inkscape = "http://www.inkscape.org/namespaces/inkscape";
        var paths = XDocument.Load(path).Descendants(svg + "path").ToArray();
        var actual = paths.GroupBy(element =>
            (Layer: (string)element.Parent.Attribute(inkscape + "label"), Source: (string)element.Attribute("data-source"),
                Face: (string)element.Attribute("data-face"), Fill: (string)element.Attribute("fill-rule") == "evenodd"))
            .ToDictionary(group => group.Key, group => group.Count());
        int closed = paths.Where(element => (string)element.Attribute("fill-rule") == "evenodd")
            .Sum(element => Regex.Matches((string)element.Attribute("d"), @"\bZ\b").Count);
        int expectedLoops = retained.Select(item => item.Geometry).OfType<Brep>().Sum(brep => brep.Loops.Count);
        var lines = paths.Where(element => element.Attribute("stroke") != null).ToArray();
        int closedLines = lines.Count(element => Regex.IsMatch((string)element.Attribute("d"), @"\bZ\b"));
        if (expected.Count != actual.Count || expected.Any(row => !actual.TryGetValue(row.Key, out int number) || row.Value != number) ||
            paths.Length != result.Lines + result.Regions || closed != result.Loops || result.Loops != expectedLoops ||
            closedLines != retained.Count(item => item.Geometry is Curve { IsClosed: true }) ||
            lines.Any(element => (string)element.Attribute("stroke-linecap") != "round" ||
                (string)element.Attribute("stroke-linejoin") != "round") ||
            result.MaximumSampleError > DrawingPath.FitMillimeters)
            throw new InvalidDataException("The SVG reopened with different layer attribution, topology or fitted path counts.");
        string report = FormattableString.Invariant(
            $"PASS {result.Lines} line paths ({closedLines} closed), round caps and joins, {result.Regions} fill paths, {closed} closed loops, max sampled error {result.MaximumSampleError:G6} model mm. {path}");
        if (paperNotice != null) report = paperNotice + Environment.NewLine + report;
        return roundoffDescriptions.Length == 0 ? report : report + Environment.NewLine +
            $"Excluded {roundoffDescriptions.Length} projected fills below model roundoff and SVG paper resolution:" +
            Environment.NewLine + string.Join(Environment.NewLine, roundoffDescriptions);
    }
}
