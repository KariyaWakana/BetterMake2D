using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2HiddenLines
{
    public static string Run()
    {
        const double fit = .01;
        using var cover = Panel(0, 0, 10, 10, 3);
        using var rear = Panel(2, 2, 4, 4, 1);
        using var duplicate = Panel(2, 2, 4, 4, .5);
        using var crosses = Panel(8, 6, 14, 8, 1);
        var inputs = new[] { new Input(cover, "cover"), new Input(rear, "rear"),
            new Input(duplicate, "duplicate"), new Input(crosses, "crosses") };
        using var baseline = V2DrawingSolver.Solve(inputs, fit, .04, minimumTileDepth: 2, layerOf: Layer);
        using var untiled = V2DrawingSolver.Solve(inputs, fit, .04, layerOf: Layer,
            hiddenMode: V2HiddenMode.Occluded);
        using var drawing = V2DrawingSolver.Solve(inputs, fit, .04, minimumTileDepth: 2, layerOf: Layer,
            hiddenMode: V2HiddenMode.Occluded);
        using var filtered = V2DrawingSolver.Solve(inputs, fit, .04, minimumTileDepth: 2, layerOf: Layer,
            hiddenMode: V2HiddenMode.Occluded, hiddenLayer: "Structure::Piles");
        var hidden = drawing.Curves.Where(edge => edge.IsHidden).ToArray();
        Require(hidden.Length == 2 && hidden.Count(edge => edge.Curve.IsClosed && edge.Source == "rear") == 1,
            $"Expected one deduplicated enclosed frame and one partial frame, got {string.Join(", ",
                hidden.Select(edge => $"{edge.Source}/{edge.Curve.IsClosed}"))}.");
        var coveredSection = hidden.Single(edge => edge.Source == "crosses").Curve;
        var box = coveredSection.GetBoundingBox(true);
        Require(Math.Abs(box.Min.X - 8) < fit * .01 && Math.Abs(box.Max.X - 10) < fit * .01 &&
            !coveredSection.IsClosed && hidden.All(edge => edge.Boundary != V2TiledArrangement.SeamKind),
            "The dashed section did not stop exactly at the foreground boundary.");
        Require(untiled.Curves.Count(edge => edge.IsHidden) == 2 && drawing.Tiles.Count >= 4 &&
            filtered.Curves.Count(edge => edge.IsHidden) == 1 &&
            filtered.Curves.Single(edge => edge.IsHidden).Source == "rear",
            "Tile seams or the layer filter changed hidden-line membership.");
        Require(baseline.Regions.Count == drawing.Regions.Count &&
            baseline.Curves.Count == drawing.Curves.Count - hidden.Length &&
            drawing.Regions.All(region => region.Brep.IsValid), "Hidden strokes changed visible output.");

        using var slope = new LineCurve(new Point3d(1, 0, -2), new Point3d(3, 0, 2));
        using var revolve = RevSurface.Create(slope, new Line(Point3d.Origin, Vector3d.ZAxis, 1), 0, 2 * Math.PI);
        using var truncated = Brep.CreateFromRevSurface(revolve, true, true);
        using var noBack = V2DrawingSolver.Solve(new[] { new Input(truncated, "solid") }, fit, .04,
            hiddenMode: V2HiddenMode.Occluded);
        using var withBack = V2DrawingSolver.Solve(new[] { new Input(truncated, "solid") }, fit, .04,
            hiddenMode: V2HiddenMode.IncludeBackFaces);
        Require(noBack.Curves.All(edge => !edge.IsHidden) && withBack.Curves.Count(edge => edge.IsHidden) == 1 &&
            withBack.Curves.Single(edge => edge.IsHidden).Curve.IsClosed, "The optional closed-shell back rim did not round-trip.");
        using var rearSolid = new Cylinder(new Circle(new Plane(new Point3d(0, 0, -2), Vector3d.ZAxis), 1), 4)
            .ToBrep(true, true);
        using var terrain = new Mesh();
        foreach (var point in new[] { new Point3d(-3, -3, 3), new Point3d(3, -3, 3),
            new Point3d(3, 3, 3), new Point3d(-3, 3, 3) }) terrain.Vertices.Add(point);
        terrain.Faces.AddFace(0, 1, 2, 3);
        using var seabed = V2DrawingSolver.Solve(new[] { new Input(rearSolid, "pile"),
            new Input(null, "seabed") { NativeMesh = terrain } }, fit, .04, hiddenMode: V2HiddenMode.Occluded);
        Require(seabed.Regions.Count == 1 && seabed.Regions[0].Source == "seabed" &&
            seabed.Curves.Count(edge => edge.IsHidden) == 1 &&
            seabed.Curves.Single(edge => edge.IsHidden).Curve.IsClosed,
            $"The native mesh lost the entirely covered pile. Regions: {string.Join(", ", seabed.Regions.Select(region => region.Source))}. " +
            $"Hidden lines: {string.Join(", ", seabed.Curves.Where(edge => edge.IsHidden).Select(edge =>
                $"{edge.Source}/{edge.FaceIndex}/{edge.Boundary}/{edge.Curve.IsClosed}"))}. Issues {string.Join(", ", seabed.Issues)}.");

        string folder = System.Environment.GetEnvironmentVariable("BMA2D_HIDDEN_TEST_OUTPUT") ??
            throw new ArgumentException("Hidden test directory");
        Directory.CreateDirectory(folder);
        var style = inputs.ToDictionary(input => input.Source, input => (Layer(input.Source), Color.Black), StringComparer.Ordinal);
        string file = Path.Combine(folder, "隐藏线.3dm");
        string svg = Path.Combine(folder, "隐藏线.svg");
        V2DrawingOutput.Write(drawing, file, UnitSystem.Millimeters, fit, style);
        using (var reopened = File3dm.Read(file))
        {
            var lines = reopened.Objects.Where(item => item.Attributes.GetUserString("Visibility") == "Hidden").ToArray();
            Require(lines.Length == hidden.Length && lines.All(item =>
                reopened.AllLayers.FindIndex(item.Attributes.LayerIndex).FullPath.StartsWith("Hidden::", StringComparison.Ordinal) &&
                item.Attributes.LinetypeSource == ObjectLinetypeSource.LinetypeFromObject &&
                reopened.AllLinetypes.FindIndex(item.Attributes.LinetypeIndex).SegmentCount == 2),
                "Rhino reopen lost the original hidden layer tree or the dash pattern.");
        }
        ElevationSvgExport.Write(file, svg, 1);
        var ns = XNamespace.Get("http://www.w3.org/2000/svg");
        var inkscape = XNamespace.Get("http://www.inkscape.org/namespaces/inkscape");
        var doc = XDocument.Load(svg);
        var hiddenPaths = doc.Root.Elements(ns + "g").Single(group => (string)group.Attribute(inkscape + "label") == "隐藏线")
            .Descendants(ns + "path").ToArray();
        Require(hiddenPaths.Length == 2 && hiddenPaths.All(path => (string)path.Attribute("stroke-dasharray") == "0.7 0.4" &&
            (string)path.Attribute("stroke-linecap") == "round" && (string)path.Attribute("stroke-linejoin") == "round") &&
            hiddenPaths.Count(path => ((string)path.Attribute("d")).TrimEnd().EndsWith("Z", StringComparison.Ordinal)) == 1,
            "SVG reopen lost the hidden ring or its round dashed style.");
        return $"PASS: {hidden.Length} hidden strokes in a separately layered Rhino and SVG output. " +
            "Full/partial occlusion, duplicate collapse, a covered cylindrical pile behind native terrain, and exact tile endpoints.";
    }

    private static string Layer(string source) => source switch
    {
        "rear" => "Structure::Piles", "duplicate" => "Structure::Test", "crosses" => "Frame::Crossing", _ => "Cover"
    };

    private static Brep Panel(double x0, double y0, double x1, double y1, double z)
    {
        using var surface = new PlaneSurface(new Plane(new Point3d(0, 0, z), Vector3d.ZAxis),
            new Interval(x0, x1), new Interval(y0, y1));
        return surface.ToBrep();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
