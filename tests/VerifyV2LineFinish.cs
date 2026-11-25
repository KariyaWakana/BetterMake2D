using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2LineFinish
{
    public static string Run()
    {
        const double fit = .055;
        double grid = fit * .01;
        foreach (int grids in new[] {1, 2})
        {
            double lower = Math.Round(9 / grid) * grid, upper = lower + grids * grid;
            var bands = new List<V2Segment>();
            AddRectangle(0, 0, 20, lower);
            AddRectangle(0, upper, 20, 20);
            AddRectangle(2, 15, 18, 15.05);
            double rimY = Math.Round(17 / grid) * grid;
            AddRectangle(2, rimY, 18, rimY + 2 * grid);
            AddRectangle(0, lower, 1, upper);
            AddRectangle(19, lower, 20, upper);
            foreach (int depth in new[] {0, 2})
            {
                var plan = depth == 0 ? V2PlanarArrangement.Build(bands, grid, null) :
                    V2TiledArrangement.Build(bands, grid, fit, 1000, depth, Classify, null, null, new List<V2TileReport>());
                if (depth == 0) Classify(plan);
                var result = V2Slivers.Clean(plan, fit, 1, null);
                Require(result.Absorbed == 1, $"The narrow void between two supported rails was retained ({grids} grids, depth {depth}).");
                var regions = result.Plan.Regions().ToArray();
                Require(regions.Length == 4, "Absorption changed a valid fill around the shared border.");
                var thin = regions.Single(region => region.Owner == 2).Region;
                var rim = thin.Outer.Select(side => result.Plan.Point(result.Plan.Start(side))).ToArray();
                Require(Math.Abs(rim.Max(p => p.Y) - rim.Min(p => p.Y) - .05) < grid &&
                    result.Plan.Contains(thin, new Point2d(10, 15.025)), "The modeled 0.05 mm strip was absorbed.");
                Require(result.Plan.Contains(regions.Single(region => region.Owner == 3).Region,
                    new Point2d(10, rimY + grid)), "An owned two-grid visible fragment was absorbed.");

                void Classify(V2PlanarArrangement arrangement)
                {
                    foreach (var cell in arrangement.Cells)
                    {
                        if (!arrangement.TryInterior(cell, out var p) || !(p.X > 0 && p.X < 20 && p.Y > 0 && p.Y < 20)) continue;
                        if (p.Y < lower) cell.Owner = 0;
                        else if (p.Y > upper)
                        {
                            bool withinStrip = p.X > 2 && p.X < 18;
                            cell.Owner = withinStrip && p.Y > 15 && p.Y < 15.05 ? 2 :
                                withinStrip && p.Y > rimY && p.Y < rimY + 2 * grid ? 3 : 1;
                        }
                        else if (p.X < 1 || p.X > 19) cell.Owner = 0;
                        cell.Uncertain = false;
                    }
                }
            }

            void AddRectangle(double left, double bottom, double right, double top)
            {
                var points = new[] { new Point2d(left, bottom), new Point2d(right, bottom),
                    new Point2d(right, top), new Point2d(left, top), new Point2d(left, bottom) };
                for (int i = 1; i < points.Length; i++)
                    bands.Add(new V2Segment(points[i - 1], points[i],
                        new V2SourceSpan(bands.Count, 0, "Original", 0, 1)));
            }
        }
        var zigzag = new[] { new Point2d(0, 10), new Point2d(7, 10), new Point2d(7, 10 + grid),
            new Point2d(15, 10 + grid), new Point2d(15, 10), new Point2d(20, 10) };
        var contour = new[] { new Point2d(0, 0), new Point2d(20, 0), new Point2d(20, 20),
            new Point2d(0, 20), new Point2d(0, 0) };
        var segments = new List<V2Segment>();
        foreach (var points in new[] {zigzag, contour})
            for (int i = 1; i < points.Length; i++)
                segments.Add(new V2Segment(points[i - 1], points[i], new V2SourceSpan(i, 0, "Original", 0, 1)));
        var arrangement = V2PlanarArrangement.Build(segments, grid, null);
        foreach (var cell in arrangement.Cells)
        {
            if (!arrangement.TryInterior(cell, out var point)) throw new InvalidDataException("No fixture cell probe.");
            cell.Owner = point.Y < 10 ? 0 : 1;
        }
        var clean = V2Slivers.Clean(arrangement, fit, 1, null);
        var common = clean.Plan.VisibleEdges().Where(edge => edge.Left >= 0 && edge.Right >= 0).ToArray();
        Require(clean.Flattened == 2 && common.All(edge => edge.A.Y == edge.B.Y) &&
            common.Select(edge => edge.A.Y).Distinct().Count() == 1,
            "A one-grid staircase survived or the two sides changed different borders.");

        string folder = Environment.GetEnvironmentVariable("BMA2D_LINE_FINISH_OUTPUT") ?? throw new ArgumentException("Output directory");
        Directory.CreateDirectory(folder);
        using var rectangle = Panel(0, 0, 20, 10);
        using var perforated = Panel(30, 0, 60, 20, new Rect(35, 4, 55, 16));
        using var first = Panel(70, 0, 80, 10);
        using var second = Panel(80, 0, 90, 10);
        var inputs = new[] { new Input(rectangle, "rectangle"), new Input(perforated, "with-hole"),
            new Input(first, "neighbor-a"), new Input(second, "neighbor-b") };
        using var frames = V2DrawingSolver.Solve(inputs, fit, .04, layerOf: _ => "测试图层");
        Require(frames.Curves.Count == 5 && frames.Curves.Count(edge => edge.Curve.IsClosed) == 4,
            "The rectangular frames, hole and T-node did not join into four closed paths plus one open path.");
        Require(Math.Abs(frames.Curves.Sum(edge => edge.Curve.GetLength()) - (60 + 100 + 64 + 70)) < .005,
            "The shared edge was omitted or written more than once.");
        var styles = inputs.ToDictionary(input => input.Source, _ => ("测试图层", Color.DarkSlateGray));
        string model = Path.Combine(folder, "闭环与T节点.3dm"), svg = Path.Combine(folder, "闭环与T节点.svg");
        V2DrawingOutput.Write(frames, model, UnitSystem.Millimeters, fit, styles);
        ElevationSvgExport.Write(model, svg, 1);
        XNamespace ns = "http://www.w3.org/2000/svg";
        var paths = XDocument.Load(svg).Descendants(ns + "path").Where(element => element.Attribute("stroke") != null).ToArray();
        Require(paths.Length == 5 && paths.Count(path => ((string)path.Attribute("d")).TrimEnd().EndsWith("Z")) == 4 &&
            paths.All(path => (string)path.Attribute("stroke-linecap") == "round" &&
                (string)path.Attribute("stroke-linejoin") == "round"), "SVG changed closure or stroke style.");
        return $"PASS one- and two-grid bands (untiled and tiled), staircase, protected 0.05 mm strip, " +
            $"four closed line frames, unique T-node edge and round SVG caps. {svg}";
    }

    private static Brep Panel(double left, double bottom, double right, double top, Rect? hole = null)
    {
        using var outer = new Rect(left, bottom, right, top).ToCurve();
        using var inner = hole?.ToCurve();
        return Brep.CreatePlanarBreps(inner == null ? new[] {outer} : new[] { outer, inner }, 1e-8).Single();
    }

    private static void Require(bool passed, string reason)
    {
        if (!passed) throw new InvalidDataException(reason);
    }
}

public static class VerifyV2SavedSeam
{
    public static string Run()
    {
        string file = Environment.GetEnvironmentVariable("BMA2D_LINE_FINISH_ARCHIVE") ?? throw new ArgumentException("Drawing");
        double row = double.Parse(Environment.GetEnvironmentVariable("BMA2D_LINE_FINISH_ROW") ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        using var drawing = File3dm.Read(file) ?? throw new IOException(file);
        double fit = drawing.Settings.ModelAbsoluteTolerance, grid = fit * .01;
        var segments = new List<V2Segment>();
        var fills = new List<(Brep Geometry, int Owner)>();
        var owners = new Dictionary<(string Source, string Face), int>();
        using var prior = new RTree();
        foreach (var item in drawing.Objects)
        {
            if (item.Geometry is not Brep brep) continue;
            var box = brep.GetBoundingBox(false);
            if (Math.Max(box.Min.Y - row, row - box.Max.Y) > grid * 4) continue;
            var key = (item.Attributes.GetUserString("SourceObject"), item.Attributes.GetUserString("SourceFace"));
            if (!owners.TryGetValue(key, out int owner)) owners.Add(key, owner = owners.Count);
            prior.Insert(box, fills.Count);
            fills.Add((brep, owner));
            foreach (var loop in brep.Loops)
            {
                using var rim = loop.To3dCurve();
                if (!rim.TryGetPolyline(out var points))
                {
                    using var approximated = rim.ToPolyline(grid / 4, .05, 0, 0);
                    if (approximated == null || !approximated.TryGetPolyline(out points))
                        throw new InvalidDataException("A nearby archived rim could not be read as a polyline.");
                }
                int id = segments.Count;
                for (int i = 1; i < points.Count; i++)
                    segments.Add(new V2Segment(new Point2d(points[i - 1].X, points[i - 1].Y),
                        new Point2d(points[i].X, points[i].Y), new V2SourceSpan(id, owner, "Original", i - 1, i)));
            }
        }
        var plan = V2PlanarArrangement.Build(segments, grid, null);
        foreach (var cell in plan.Cells)
        {
            if (!plan.TryInterior(cell, out var interior)) continue;
            var point = new Point3d(interior.X, interior.Y, 0);
            prior.Search(new BoundingBox(point, point), (_, hit) =>
            {
                var fill = fills[hit.Id];
                if (!VerifyV2Drawing.Contains(fill.Geometry, point, grid * .001)) return;
                cell.Owner = fill.Owner;
                hit.Cancel = true;
            });
        }
        var patched = V2Slivers.Clean(plan, fit, 1, null);
        var remaining = patched.Plan.VisibleEdges();
        var details = remaining.Where(edge => IsStep(patched.Plan, edge)).Take(12).Select(edge =>
            $"{patched.Plan.Point(edge.A)} to {patched.Plan.Point(edge.B)}: " +
            string.Join(" | ", remaining.Where(rim => rim.A == edge.A || rim.B == edge.A || rim.A == edge.B || rim.B == edge.B)
                .Select(rim => $"{patched.Plan.Point(rim.A)}->{patched.Plan.Point(rim.B)} [{rim.Left},{rim.Right}]")));
        return $"Saved 2D rims on row {row}. {fills.Count} regions, {segments.Count} input segments. " +
            $"One-grid steps {Steps(plan)} to {Steps(patched.Plan)}. " +
            $"{patched.Absorbed} absorbed, {patched.Flattened} flattened.\n" + string.Join("\n", details);

        int Steps(V2PlanarArrangement current) => current.VisibleEdges().Count(edge => IsStep(current, edge));
        bool IsStep(V2PlanarArrangement current, V2OwnedEdge edge) =>
            edge.A.X == edge.B.X && Math.Abs(edge.A.Y - edge.B.Y) == 1 &&
            Math.Abs(current.Point(edge.A).Y - row) <= grid * 4 &&
            Math.Abs(current.Point(edge.B).Y - row) <= grid * 4;
    }
}
