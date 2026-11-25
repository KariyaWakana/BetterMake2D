using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2ClipPlanes
{
    public static string Run() => Check(V2Accuracy.Exact);

    internal static string Check(V2Accuracy accuracy)
    {
        const double fit = .005;
        using var terrain = new Mesh();
        const int n = 13;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                terrain.Vertices.Add(x - 6, y - 6, -3 + .7 * Math.Sin(x * .6) + .15 * y);
                if (x > 0 && y > 0)
                {
                    int at = y * n + x;
                    terrain.Faces.AddFace(at - n - 1, at - n, at, at - 1);
                }
            }
        using var wall = Brep.CreateFromBox(new BoundingBox(new Point3d(-4, -4, -5), new Point3d(.4, 4, 2)));
        using var pile = new Cylinder(new Circle(new Plane(new Point3d(1.6, -5, -2.5), Vector3d.YAxis), .9), 10)
            .ToBrep(true, true);
        using var hidden = Brep.CreateFromBox(new BoundingBox(new Point3d(-3.7, -2.5, -6), new Point3d(-3.2, 2.5, -5.5)));
        var inputs = new[] { new Input(null, "terrain") { NativeMesh = terrain }, new Input(wall, "wall"),
            new Input(pile, "pile"), new Input(hidden, "hidden") };
        var first = new Plane(new Point3d(2.1, 0, -1), new Vector3d(1, .14, .12));
        var second = new Plane(new Point3d(0, 3.7, 0), new Vector3d(-.14, 1, .18));
        using var doc = RhinoDoc.CreateHeadless(null);
        var viewId = Guid.NewGuid();
        doc.Objects.AddClippingPlane(first, 1, 1, viewId);
        doc.Objects.AddClippingPlane(second, 1, 1, viewId);
        doc.Objects.AddClippingPlane(Plane.WorldXY, 1, 1, Guid.NewGuid());
        var planes = BetterMake2DCommand.ReadActiveClippingPlanes(doc, viewId, Transform.Identity);
        if (planes.Length != 2) throw new InvalidOperationException($"Expected two active viewport planes, got {planes.Length}.");
        long start = Stopwatch.GetTimestamp();
        using var output = V2DrawingSolver.Solve(inputs, fit, .02, clipPlanes: planes,
            hiddenMode: V2HiddenMode.Occluded, hiddenLayer: "hidden", layerOf: source => source, accuracy: accuracy);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        int sections = output.Curves.Count(edge => !edge.IsHidden && edge.Boundary == "SectionCut");
        int hiddenCount = output.Curves.Count(edge => edge.IsHidden);
        if (sections < 2 || hiddenCount == 0 || output.Regions.Any(region => !region.Brep.IsValid) ||
            output.Issues.Any(issue => issue.StartsWith("Invalid projected region", StringComparison.Ordinal)))
            throw new InvalidOperationException($"Section output incomplete: {sections} cuts, {hiddenCount} hidden. " +
                string.Join(" | ", output.Issues.Take(6)));
        int checkedRays = 0, differences = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                var p = new Point3d(x * .385 - 5.91, y * .382 - 5.84, 0);
                var line = new Line(p + Vector3d.ZAxis * 10, p - Vector3d.ZAxis * 10);
                using var ray = new LineCurve(line);
                var hits = new List<(string Source, double Z)>();
                foreach (var input in inputs)
                {
                    if (input.NativeMesh != null)
                        Collect(Intersection.MeshLine(input.NativeMesh, line), input.Source);
                    else
                    {
                        Intersection.CurveBrep(ray, input.Brep, fit * .01, out var overlaps, out var points);
                        foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                        Collect(points, input.Source);
                    }
                }
                string expected = hits.OrderByDescending(hit => hit.Z).FirstOrDefault().Source;
                var actual = output.Regions.Where(region =>
                {
                    var face = region.Brep.Faces[0];
                    return face.ClosestPoint(p, out double u, out double v) &&
                        face.IsPointOnFace(u, v, 1e-8) == PointFaceRelation.Interior;
                }).Select(region => region.Source).ToArray();
                if (actual.Length > 1 || actual.SingleOrDefault() != expected)
                {
                    if (accuracy == V2Accuracy.Exact || actual.Length > 1)
                        throw new InvalidOperationException($"Section mismatch at {p}: expected {expected}, got {string.Join(",", actual)}. " +
                            $"Regions {string.Join(", ", output.Regions.GroupBy(region => region.Source).Select(group => group.Key + "=" + group.Count()))}. " +
                            string.Join(" | ", output.Issues.Take(8)));
                    differences++;
                }
                checkedRays++;

                void Collect(Point3d[] points, string source)
                {
                    foreach (var hit in points ?? Array.Empty<Point3d>())
                        if (first.DistanceTo(hit) <= 0 && second.DistanceTo(hit) <= 0) hits.Add((source, hit.Z));
                }
            }
        if (differences * 200 > checkedRays)
            throw new InvalidOperationException($"Fast sections have {differences}/{checkedRays} independent ray differences.");
        if (System.Environment.GetEnvironmentVariable("BMA2D_V2_SECTION_TEST_OUTPUT") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            var styles = inputs.ToDictionary(input => input.Source, input => (input.Source, System.Drawing.Color.Black));
            bool release = accuracy == V2Accuracy.Fast;
            string stem = release ? "Section-fast" : "Section";
            string drawing = Path.Combine(folder, stem + ".3dm");
            string svg = Path.Combine(folder, stem + ".svg");
            int count = output.Regions.Count + output.Curves.Count;
            V2DrawingOutput.Write(output, drawing, UnitSystem.Millimeters, fit, styles, releaseDrawing: release);
            if (release)
            {
                using var reopened = File3dm.Read(drawing);
                if (output.Regions.Count != 0 || output.Curves.Count != 0 || reopened == null ||
                    reopened.Objects.Count != count || reopened.Objects.Any(item => !item.Geometry.IsValid))
                    throw new InvalidOperationException("The consuming writer lost geometry during copy and release.");
            }
            ElevationSvgExport.Write(drawing, svg, 1);
            XNamespace tag = "http://www.w3.org/2000/svg", label = "http://www.inkscape.org/namespaces/inkscape";
            var section = XDocument.Load(svg).Descendants(tag + "g")
                .Single(item => (string)item.Attribute(label + "label") == "SectionCuts");
            if (section.Descendants(tag + "path").Count() != sections)
                throw new InvalidOperationException("SVG did not preserve the SectionCuts layer and paths.");
        }
        return $"PASS {accuracy} two active oblique clipping planes, {sections} section paths, " +
            $"{hiddenCount} hidden paths, {checkedRays} independent rays, {differences} differences; {elapsed:F1} ms solve.";
    }
}
