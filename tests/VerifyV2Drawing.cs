using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Environment = System.Environment;

namespace BetterMake2D;

public static class VerifyV2CommandOutput
{
    public static string Run()
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        int parent = doc.Layers.Add("廊桥", Color.Black);
        using var layer = new Layer { Name = "支座", Color = Color.SteelBlue, ParentLayerId = doc.Layers[parent].Id };
        int layerIndex = doc.Layers.Add(layer);
        using var style = new ObjectAttributes { LayerIndex = layerIndex };
        using var panel = new PlaneSurface(Plane.WorldXY, new Interval(0, 5), new Interval(0, 4));
        int inner = doc.InstanceDefinitions.Add("内层块", "", Point3d.Origin, panel, style);
        using var insert = new InstanceReferenceGeometry(doc.InstanceDefinitions[inner].Id, Transform.Translation(12, 9, 0));
        int outer = doc.InstanceDefinitions.Add("外层块", "", Point3d.Origin, insert, style);
        var instance = doc.Objects.FindId(doc.Objects.AddInstanceObject(outer, Transform.Translation(21, 13, 0)));
        if (instance is not InstanceObject) throw new InvalidOperationException("Could not build nested input.");
        var sources = new List<ProjectionObject>();
        var layers = new Dictionary<string, int>();
        BetterMake2DCommand.Collect(instance, Transform.Identity, "", Transform.Identity, sources, layers);
        if (sources.Count != 1 || sources[0].Source.Count(c => c == '/') != 2 || layers[sources[0].Source] != layerIndex)
            throw new InvalidOperationException("The nested instance lost its source or layer.");
        var inputs = ProjectionPipeline.ReadObjects(sources);
        try
        {
            using var drawing = V2DrawingSolver.Solve(inputs, .01, .04);
            if (drawing.Regions.Count != 1 || drawing.Curves.Count != 1 ||
                drawing.Regions[0].Brep.GetBoundingBox(true).Min.DistanceTo(new Point3d(33, 22, 0)) > 1e-8)
                throw new InvalidOperationException("Nested input placement or planar ownership changed.");
            BetterMake2DCommand.WriteV2Output(doc, drawing, Transform.Translation(-33, -22, 0), layers);
            var added = doc.Objects.GetObjectList(ObjectType.AnyObject)
                .Where(obj => obj.Attributes.GetUserString("OriginalLayer") == "廊桥::支座").ToArray();
            if (added.Length != 2 || added.Any(obj => !obj.IsValid ||
                !doc.Layers[obj.Attributes.LayerIndex].FullPath.EndsWith("::廊桥::支座", StringComparison.Ordinal)) ||
                added.Count(obj => obj.Geometry is Curve { IsClosed: true }) != 1 ||
                added.Count(obj => obj.Geometry is Brep { IsValid: true }) != 1)
                throw new InvalidOperationException("Expected a closed curve and region on the original nested layer paths.");
            return "PASS: nested block transform, original layer tree, closed fill, and deduplicated line output.";
        }
        finally { foreach (var input in inputs) input.Brep.Dispose(); }
    }
}

public static class VerifyV2PeriodicArchive
{
    public static string Run()
    {
        var source = Environment.GetEnvironmentVariable("BMA2D_V2_SOURCE") ?? throw new ArgumentException("Source");
        var ids = (Environment.GetEnvironmentVariable("BMA2D_V2_DEFECT_SOURCES") ??
            throw new ArgumentException("Source object IDs")).Split(',').ToHashSet(StringComparer.Ordinal);
        return OriginalFaceStages.WithSavedFaces(source, (file, inputs, _, _, _) =>
        {
            var selected = inputs.Where(input => ids.Contains(input.Source)).ToArray();
            if (selected.Length != ids.Count) throw new InvalidOperationException("An archive source was not found.");
            double fit = file.Settings.ModelAbsoluteTolerance;
            double angle = file.Settings.ModelAngleToleranceRadians;
            var lines = new List<string>();
            using var drawing = V2DrawingSolver.Solve(selected, fit, angle);
            foreach (var input in selected)
            {
                var face = input.Brep.Faces[0];
                var box = face.GetBoundingBox(true);
                var camera = new V2ParallelCamera(box.Min.Z - fit * 4, box.Max.Z + fit * 4);
                bool cylinder = face.TryGetCylinder(out var round, fit * .01);
                var regions = drawing.Regions.Where(region => region.Source == input.Source && region.FaceIndex == 0).ToArray();
                var result = regions.Aggregate(BoundingBox.Empty, (bounds, region) => BoundingBox.Union(bounds,
                    region.Brep.GetBoundingBox(true)));
                lines.Add($"{input.Source}/0: {face.UnderlyingSurface().GetType().Name}; cylinder={cylinder} " +
                    $"(r={round.Radius:G8}, axis={round.Axis}); box {box}; output {regions.Length}, {result}.");
                foreach (var loop in face.Loops)
                    foreach (var trim in loop.Trims)
                        if (trim.Edge is BrepEdge edge)
                        {
                            using var projected = camera.ProjectCurve(edge);
                            using var polyline = projected?.ToPolyline(fit * .05, Math.Max(.35, angle), 0, 0);
                            lines.Add($"  {trim.TrimType} {edge.EdgeIndex}: {edge.PointAtStart} through {edge.PointAtEnd}; " +
                                $"{edge.GetBoundingBox(true)}. Projected {projected?.GetBoundingBox(true)} " +
                                $"closed={projected?.IsClosed}; discretized {polyline?.PointCount} {polyline?.GetBoundingBox(true)}.");
                        }
                foreach (double degrees in new[] { Math.Min(angle, .001), angle })
                {
                    var silhouettes = Silhouette.Compute(face, SilhouetteType.Tangent | SilhouetteType.Projecting,
                        camera.ViewRay(Point2d.Origin).Direction, fit * .05, degrees);
                    lines.Add($"  Rhino angle {degrees:G8}: {silhouettes?.Length ?? 0} contours.");
                    foreach (var rim in silhouettes ?? Array.Empty<Silhouette>())
                    {
                        using var curve = rim.Curve;
                        if (curve != null) lines.Add($"  {curve.PointAtStart} through {curve.PointAtEnd}; {curve.GetBoundingBox(true)}.");
                    }
                }
            }
            return string.Join(Environment.NewLine, lines.Concat(drawing.Issues));
        });
    }
}

public static class VerifyV2OverlapArchive
{
    public static string Run()
    {
        var drawingPath = Environment.GetEnvironmentVariable("BMA2D_V2_BASELINE") ?? throw new ArgumentException("Drawing");
        var rayPath = Environment.GetEnvironmentVariable("BMA2D_V2_REPLAY_RAYS") ?? throw new ArgumentException("Audit");
        bool traceHits = Environment.GetEnvironmentVariable("BMA2D_V2_TRACE_HITS") == "1";
        using var model = File3dm.Read(drawingPath) ?? throw new IOException(drawingPath);
        double grid = model.Settings.ModelAbsoluteTolerance * .01;
        var shapes = model.Objects.Where(item => item.Geometry is Brep)
            .Select(item => ((Brep)item.Geometry, $"{item.Attributes.GetUserString("SourceObject")}/{item.Attributes.GetUserString("SourceFace")}"))
            .ToArray();
        using var index = new RTree();
        for (int i = 0; i < shapes.Length; i++) index.Insert(shapes[i].Item1.GetBoundingBox(false), i);
        var rings = new Dictionary<int, Curve[]>();
        try
        {
            int dual = 0, strictDual = 0, deep = 0, near = 0, intervening = 0, rejected = 0, directMissing = 0, matches = 0;
            var examples = new List<string>();
            foreach (var row in File.ReadLines(rayPath).Skip(1))
            {
                var columns = row.Split('\t');
                if (columns[5] is not ("extra/wrong" or "overlap") || !columns[3].Contains(',')) continue;
                dual++;
                var point = new Point3d(double.Parse(columns[0], CultureInfo.InvariantCulture),
                    double.Parse(columns[1], CultureInfo.InvariantCulture), 0);
                var box = new BoundingBox(point, point);
                var hits = new List<(string Source, double Distance, bool Strict)>();
                index.Search(box, (_, hit) =>
                {
                    var (shape, id) = shapes[hit.Id];
                    if (!VerifyV2Drawing.Contains(shape, point, grid)) return;
                    if (!rings.TryGetValue(hit.Id, out var curves))
                        rings.Add(hit.Id, curves = shape.Faces[0].Loops.Select(loop => loop.To3dCurve()).ToArray());
                    double distance = double.PositiveInfinity;
                    bool inside = false, certain = true;
                    foreach (var rim in curves)
                    {
                        if (rim == null) { certain = false; continue; }
                        if (rim.ClosestPoint(point, out double t))
                            distance = Math.Min(distance, point.DistanceTo(rim.PointAt(t)));
                        var relation = rim.Contains(point, Plane.WorldXY, grid * 1e-3);
                        if (relation == PointContainment.Inside) inside = !inside;
                        else if (relation != PointContainment.Outside) certain = false;
                    }
                    hits.Add((id, distance, certain && inside));
                });
                if (hits.Count < 2) { rejected++; continue; }
                int strict = hits.Count(hit => hit.Strict);
                if (strict > 1) strictDual++;
                int interior = hits.Count(hit => hit.Strict && hit.Distance > grid * 2);
                if (interior > 1)
                {
                    deep++;
                }
                else if (hits.All(hit => hit.Distance <= grid * 2)) near++;
                else intervening++;
                if ((traceHits || interior > 1) && examples.Count < 25)
                    examples.Add($"{columns[0]},{columns[1]} expected {columns[2]}; " +
                        string.Join(", ", hits.Select(hit => $"{hit.Source} [{hit.Distance:G8}, {hit.Strict}]")));
                if (strict == 0) directMissing++;
                if (strict == 1 && columns[2].Split(',').Contains(hits.Single(hit => hit.Strict).Source)) matches++;
            }
            return $"Dual-hit rows {dual}; strict dual interiors {strictDual}; " +
                $"both strict and more than two snap grids from every output rim {deep}; " +
                $"all rims within two grids {near}; mixed {intervening}; could not replay {rejected}; " +
                $"zero strict interiors {directMissing}; single strict expected owner {matches}." +
                Environment.NewLine + string.Join(Environment.NewLine, examples);
        }
        finally { foreach (var curve in rings.Values.SelectMany(value => value)) curve?.Dispose(); }
    }
}

public static class VerifyV2Drawing
{
    public static string Run()
    {
        using (var column = new Cylinder(new Circle(new Plane(new Point3d(310000, -20000, 100),
            new Vector3d(.6, .8, 0)), 450), 10000).ToBrep(false, false))
        using (var projected = V2DrawingSolver.Solve(new[] { new Input(column, "Periodic side") }, .001, .04))
        {
            Require(projected.Regions.Count == 1 && Contains(projected, "Periodic side", 313000, -16000),
                "A periodic curved side must close on both edge-on end rings.");
            Require(projected.Curves.All(edge => edge.Boundary != V2TiledArrangement.SeamKind) &&
                projected.Regions[0].Brep.Faces[0].Loops.Count == 1 && projected.LinesBeforeJoin == 4,
                "The hidden periodic seam must not split the visible side.");
        }
        using var wall = Panel(0, 0, 20, 12, 0);
        using var blocker = Panel(3, 2, 17, 10, 3, new Rect(9.975, 3, 10.025, 9));
        using (var hole = V2DrawingSolver.Solve(new[] {new Input(wall, "wall"), new Input(blocker, "frame")}, .001, .04))
        {
            Require(Contains(hole, "frame", 5, 5) && Contains(hole, "wall", 10, 5) &&
                !Contains(hole, "frame", 10, 5) && Contains(hole, "wall", 1, 1), "Thin aperture ownership");
            Require(hole.Issues.Count == 0 && hole.Regions.Any(region => region.Brep.Faces[0].Loops.Count == 2),
                "Thin aperture did not preserve the inner loop");
        }

        using var outline = new Rect(3, 2, 17, 10).ToCurve();
        using var roundHole = new Circle(new Point3d(10, 6, 0), 2).ToNurbsCurve();
        using var roundFrame = Brep.CreatePlanarBreps(new Curve[] { outline, roundHole }, 1e-8).Single();
        Require(roundFrame.Transform(Transform.Translation(0, 0, 3)), "Round hole placement");
        using var thin = Panel(3, 5.975, 17, 6.025, 4);
        using (var circle = V2DrawingSolver.Solve(new[] { new Input(wall, "wall"),
            new Input(roundFrame, "round"), new Input(thin, "strip") }, .001, .04))
        {
            Require(circle.Issues.Count == 0 && Contains(circle, "wall", 10, 7) &&
                Contains(circle, "wall", 10, 5) && Contains(circle, "strip", 10, 6) &&
                Contains(circle, "round", 7, 7) && !Contains(circle, "wall", 10, 6),
                $"Round hole crossed by a 0.05 mm strip. {string.Join(", ", circle.Issues)}");
        }
        using (var tiledHole = V2DrawingSolver.Solve(new[] { new Input(wall, "wall"),
            new Input(roundFrame, "round"), new Input(thin, "strip") }, .001, .04, minimumTileDepth: 2))
        using (var overlappedHole = V2DrawingSolver.Solve(new[] { new Input(wall, "wall"),
            new Input(roundFrame, "round"), new Input(thin, "strip") }, .001, .04, minimumTileDepth: 2,
            overlapTileArrangement: true))
        {
            Require(tiledHole.Tiles.Count >= 4 && tiledHole.Issues.Count == 0 &&
                Contains(tiledHole, "wall", 10, 7) && Contains(tiledHole, "wall", 10, 5) &&
                Contains(tiledHole, "strip", 10, 6) && Contains(tiledHole, "round", 7, 7) &&
                tiledHole.Curves.All(curve => curve.Boundary != V2TiledArrangement.SeamKind),
                "Tile stitching changed the round aperture or left an artificial seam");
            Require(tiledHole.Regions.Count == overlappedHole.Regions.Count &&
                tiledHole.Curves.Count == overlappedHole.Curves.Count &&
                tiledHole.Regions.Zip(overlappedHole.Regions).All(pair =>
                    pair.First.Source == pair.Second.Source && GeometryBase.GeometryEquals(pair.First.Brep, pair.Second.Brep)) &&
                tiledHole.Curves.Zip(overlappedHole.Curves).All(pair =>
                    pair.First.Source == pair.Second.Source && GeometryBase.GeometryEquals(pair.First.Curve, pair.Second.Curve)),
                "Overlapping the managed arrangements changed the serial tile result.");
        }

        using var a = Panel(0, 0, 20, 12, 0);
        using var b = Panel(0, 0, 20, 12, 0);
        var skew = Transform.Identity;
        skew.M20 = .5;
        skew.M23 = -5;
        Require(b.Transform(skew), "Crossing test transformation");
        using (var crossed = V2DrawingSolver.Solve(new[] {new Input(a, "level"), new Input(b, "slope")}, .001, .04))
        {
            Require(crossed.Contacts == 0 && crossed.AnalyticContactSegments == 1 && Contains(crossed, "level", 4, 6) &&
                Contains(crossed, "slope", 16, 6) && !Contains(crossed, "level", 16, 6),
                $"Real depth switch. Contacts {crossed.Contacts}, cells {crossed.DistributionCells}, " +
                $"regions {string.Join(",", crossed.Regions.Select(region => region.Source))}, issues {string.Join(",", crossed.Issues)}.");
            Require(crossed.Regions.Count == 2 && crossed.Issues.Count == 0, "Crossing cells did not close");
        }
        using (var tiledCross = V2DrawingSolver.Solve(new[] {new Input(a, "level"), new Input(b, "slope")},
            .001, .04, minimumTileDepth: 2))
            Require(tiledCross.Tiles.Count == 4 && tiledCross.Regions.Count == 2 &&
                Contains(tiledCross, "level", 4, 6) && Contains(tiledCross, "slope", 16, 6) &&
                tiledCross.Curves.All(curve => curve.Boundary != V2TiledArrangement.SeamKind),
                "An intersection crossing a tile seam did not stitch");

        using var first = Panel(2, 2, 4, 4, 4);
        using var second = Panel(16, 2, 18, 4, 4);
        using var back = V2DrawingSolver.Solve(new[] {new Input(wall, "wall"),
            new Input(first, "first"), new Input(second, "second")}, .001, .04);
        Require(back.Backgrounds == 1 && back.Clusters == 2 && back.Issues.Count == 0 &&
            Contains(back, "wall", 10, 6) && Contains(back, "first", 3, 3) &&
            Contains(back, "second", 17, 3), "Background joined disjoint foreground clusters");
        Require(back.Regions.Single(region => region.Source == "wall").Brep.Faces[0].Loops.Count == 3,
            "Background did not receive the two foreground holes");

        long wide = 1L << 47;
        var origin = new Point2d(wide, wide);
        var rt = new Point2d(2 * wide, 2 * wide);
        var lt = new Point2d(wide, 2 * wide);
        var rb = new Point2d(2 * wide, wide);
        var grid = V2PlanarArrangement.Build(new[]
        {
            new V2Segment(origin, rt, new V2SourceSpan(1, 0, "Original", 0, 1)),
            new V2Segment(lt, rb, new V2SourceSpan(2, 1, "Original", 0, 1))
        }, 1, null);
        Require(grid.EdgeCount == 4 && grid.Rounds == 2, "Large grid crossing did not converge via wide arithmetic");
        VerifyTwinChain();
        VerifyCoplanarDetails();
        VerifyFoldedOpenRims();
        return "PASS tiled and untiled round hole with 0.05 mm strip and real intersection, thin aperture, shared Twin chain, separated backgrounds, wide integer ISR, coplanar trim priority, and edge-on open trim extrema.";
    }

    private static void VerifyFoldedOpenRims()
    {
        double r = 190;
        using var ruling = new LineCurve(new Point3d(r, 0, 0), new Point3d(r, 8, 0));
        using var sector = RevSurface.Create(ruling, new Line(Point3d.Origin, new Point3d(0, 1, 0)), .4, 4.5);
        using var shape = sector.ToBrep();
        using var drawing = V2DrawingSolver.Solve(new[] {new Input(shape, "folded open rims")}, .055, .04);
        Require(drawing.Regions.Count == 1 && Contains(drawing, "folded open rims", -r * .999, 4) &&
            drawing.Curves.Any(edge => edge.Curve.ClosestPoint(new Point3d(-r, 4, 0), out _, .001)),
            "The open trim with a turning point did not meet its silhouette.");
    }

    private static void VerifyCoplanarDetails()
    {
        const string wallId = "80000000-0000-0000-0000-000000000000";
        const string earlier = "10000000-0000-0000-0000-000000000000";
        const string later = "f0000000-0000-0000-0000-000000000000";
        using var wall = Panel(0, 0, 20, 12, 0);
        using var mullion = Panel(3, 2, 3.8, 9, 0);
        using var cap = Panel(8, 9, 17, 9.4, 0);
        foreach (bool reverse in new[] { false, true })
        {
            string mullionId = reverse ? later : earlier;
            string capId = reverse ? earlier : later;
            using var drawing = V2DrawingSolver.Solve(new[]
            {
                new Input(wall, wallId), new Input(mullion, mullionId), new Input(cap, capId)
            }, .055, .04, minimumTileDepth: reverse ? 2 : 0);
            Require(drawing.Regions.Count == 3 && drawing.Regions.All(region => region.Brep.IsValid) &&
                Contains(drawing, mullionId, 3.4, 6) && Contains(drawing, capId, 10, 9.2) &&
                !Contains(drawing, wallId, 3.4, 6) && !Contains(drawing, wallId, 10, 9.2) &&
                Contains(drawing, wallId, 2, 4) && drawing.Regions.Single(r => r.Source == wallId).Brep.Loops.Count == 3,
                "The face ID order changed coplanar mullion or cap fill ownership.");
            foreach (var point in new[] { new Point3d(3, 6, 0), new Point3d(3.8, 6, 0),
                new Point3d(3.4, 2, 0), new Point3d(3.4, 9, 0), new Point3d(8, 9.2, 0),
                new Point3d(17, 9.2, 0), new Point3d(10, 9, 0), new Point3d(10, 9.4, 0) })
                Require(drawing.Curves.Any(edge => edge.Curve.ClosestPoint(point, out _, .001)),
                    $"Missing shared line at {point}.");
        }

        using var shallowRear = Panel(3, 2, 3.8, 9, -.02);
        using var shallowFront = Panel(3, 2, 3.8, 9, .02);
        using (var inset = V2DrawingSolver.Solve(new[] { new Input(wall, later), new Input(shallowRear, earlier) }, .055, .04))
            Require(Contains(inset, later, 3.4, 6) && !Contains(inset, earlier, 3.4, 6),
                "A coplanar-area priority displaced a face that is 0.02 mm closer.");
        using (var raised = V2DrawingSolver.Solve(new[] { new Input(wall, earlier), new Input(shallowFront, later) }, .055, .04))
            Require(Contains(raised, later, 3.4, 6) && !Contains(raised, earlier, 3.4, 6),
                "A 0.02 mm projection was consumed by the modeling-tolerance tie.");

        using var frame = Panel(-2, -2, 22, 14, 0, new Rect(-1.9, -1.9, 21.9, 13.9));
        using var panel = Panel(-3, 2, 8, 7, 0);
        using var trimmed = V2DrawingSolver.Solve(new[] {new Input(panel, earlier), new Input(frame, later)}, .055, .04);
        Require(Contains(trimmed, later, -1.95, 4) && Contains(trimmed, earlier, 5, 4),
            "Coplanar priority must use the effective trim area after subtracting holes.");
    }

    private static void VerifyTwinChain()
    {
        var plan = V2PlanarArrangement.Build(new[]
        {
            Segment(0, 0, 20, 0, 0), Segment(20, 0, 20, 12, 1),
            Segment(20, 12, 0, 12, 2), Segment(0, 12, 0, 0, 3),
            Segment(10, 0, 10, 4, 4), Segment(10, 4, 10, 8, 4), Segment(10, 8, 10, 12, 4)
        }, .01, null);
        foreach (var cell in plan.Cells)
        {
            Require(plan.TryInterior(cell, out var point), "Missing Twin fixture interior");
            cell.Owner = point.X < 10 ? 0 : 1;
        }
        int[] chainBySide = new int[plan.EdgeCount * 2];
        var chains = plan.BoundaryChains(chainBySide);
        try
        {
            var common = chains.Single(chain => chain.Left >= 0 && chain.Right >= 0);
            common.Restore(new Dictionary<int, Curve>(), .01, _ => true);
            Require(common.Sides.Length == 3 && common.Upgraded && common.Geometry is LineCurve &&
                Math.Abs(common.Geometry.GetLength() - 12) < 1e-9, "The shared chain did not collapse to one line");
            foreach (int side in common.Sides)
                Require(chainBySide[side] == -chainBySide[side ^ 1] && chainBySide[side] > 0,
                    "A Twin does not reference the same geometry in reverse");
            common.Revert();
            Require(common.Geometry is PolylineCurve, "The shared chain did not restore its common fallback");
        }
        finally { foreach (var chain in chains) chain.Dispose(); }

        static V2Segment Segment(double x, double y, double u, double v, int id) =>
            new(new Point2d(x, y), new Point2d(u, v), new V2SourceSpan(id, 0, "Original", y, v));
    }

    private static Brep Panel(double l, double b, double r, double t, double z, Rect? hole = null)
    {
        using var outer = new Rect(l, b, r, t).ToCurve();
        using var inner = hole?.ToCurve();
        var result = Brep.CreatePlanarBreps(inner == null ? new[] {outer} : new[] {outer, inner}, 1e-8).Single();
        Require(result.Transform(Transform.Translation(0, 0, z)), "Panel placement");
        return result;
    }

    private static bool Contains(V2Drawing drawing, string source, double x, double y) =>
        drawing.Regions.Any(region => region.Source == source && Contains(region.Brep, new Point3d(x, y, 0), 1e-7));

    internal static bool Contains(Brep brep, Point3d point, double fit) =>
        brep.Faces[0].ClosestPoint(point, out double u, out double v) &&
        brep.Faces[0].PointAt(u, v).DistanceTo(point) <= fit &&
        brep.Faces[0].IsPointOnFace(u, v, fit) != PointFaceRelation.Exterior;

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }
}

public static class VerifyV2PinchedFill
{
    public static string Run()
    {
        Point3d At(double x, double y) => new(335000 + x, 7500 + y, 0);
        var rings = new[]
        {
            new[] {At(0, 0), At(8, 0), At(8, 4), At(8, 8), At(0, 8), At(0, 0)},
            new[] {At(8, 4), At(5, 2), At(3, 4), At(5, 6), At(8, 4)}
        };
        var patches = V2PlanarFill.Recover(rings, 1e-5) ??
            throw new InvalidOperationException("A hole touching the outer boundary at one vertex was rejected.");
        try
        {
            if (patches.Any(patch => !patch.IsValid) ||
                patches.Any(patch => VerifyV2Drawing.Contains(patch, At(5.5, 4), 1e-5)) ||
                !patches.Any(patch => VerifyV2Drawing.Contains(patch, At(2, 4), 1e-5)))
                throw new InvalidOperationException("Pinch repair changed the inside of the projected hole.");
            return $"PASS: exact point-touching hole, {patches.Count} valid closed fill piece(s).";
        }
        finally { foreach (var patch in patches) patch.Dispose(); }
    }
}

public static class VerifyV2SavedRings
{
    public static string Run()
    {
        string folder = Environment.GetEnvironmentVariable("BMA2D_V2_REJECTED_RINGS") ?? throw new ArgumentException("Ring folder");
        var results = new List<string>();
        int failed = 0;
        foreach (string file in Directory.GetFiles(folder, "拒收环_*.tsv").Order())
        {
            var rows = File.ReadAllLines(file);
            var header = rows[0].Split('\t');
            double tolerance = double.Parse(header[2], CultureInfo.InvariantCulture) * .0001;
            var rings = new List<Point3d[]>();
            var ring = new List<Point3d>();
            foreach (string row in rows.Skip(1))
            {
                if (row.Length == 0)
                {
                    if (ring.Count != 0) { rings.Add(ring.ToArray()); ring.Clear(); }
                    continue;
                }
                var c = row.Split('\t');
                ring.Add(new Point3d(double.Parse(c[0], CultureInfo.InvariantCulture),
                    double.Parse(c[1], CultureInfo.InvariantCulture), 0));
            }
            if (ring.Count != 0) rings.Add(ring.ToArray());
            var fill = V2PlanarFill.Recover(rings.ToArray(), tolerance);
            if (fill == null)
            {
                failed++;
                results.Add($"FAIL {header[0]} from {Path.GetFileName(file)}, {rings.Count} rings.");
                continue;
            }
            double area = 0;
            foreach (var piece in fill)
            {
                using (piece)
                using (var mass = AreaMassProperties.Compute(piece))
                    area += mass?.Area ?? double.NaN;
            }
            double expected = rings.Sum(points =>
            {
                var start = points[0];
                return points.Zip(points.Skip(1), (a, b) =>
                    (a.X - start.X) * (b.Y - start.Y) - (a.Y - start.Y) * (b.X - start.X)).Sum() * .5;
            });
            bool valid = double.IsFinite(area) && Math.Abs(area - expected) <= Math.Max(1e-7 * expected, tolerance * tolerance);
            if (!valid) failed++;
            results.Add(FormattableString.Invariant(
                $"{(valid ? "PASS" : "FAIL")} {header[0]}, {fill.Count} pieces, area {area:G14}; expected {expected:G14}."));
        }
        return $"{results.Count - failed}/{results.Count} rejected regions recovered." + Environment.NewLine +
            string.Join(Environment.NewLine, results);
    }
}

public static class VerifyV2DrawingArchive
{
    private sealed record Prepared(V2Drawing Drawing, UnitSystem Units, double Fit,
        IReadOnlyDictionary<string, (string Path, Color Color)> Styles, string Report);
    private sealed record Scene(UnitSystem Units, double Fit, double Angle,
        IReadOnlyDictionary<string, (string Path, Color Color)> Styles);

    public static string Run()
    {
        string source = Environment.GetEnvironmentVariable("BMA2D_V2_SOURCE") ?? throw new ArgumentException("Source");
        string folder = Environment.GetEnvironmentVariable("BMA2D_V2_OUTPUT") ?? throw new ArgumentException("Output folder");
        string viewName = Environment.GetEnvironmentVariable("BMA2D_V2_VIEW");
        string baseline = Environment.GetEnvironmentVariable("BMA2D_V2_BASELINE");
        string stage = Environment.GetEnvironmentVariable("BMA2D_V2_STAGE");
        string drawingFile = Path.Combine(folder, "主立面_二维图面.3dm");
        int tileLimit = TileOption("BMA2D_V2_TILE_SEGMENTS", V2TiledArrangement.DefaultSegmentLimit);
        int minimumTileDepth = TileOption("BMA2D_V2_MIN_TILE_DEPTH", 0);
        string inspectionFile = Environment.GetEnvironmentVariable("BMA2D_V2_INSPECT");
        var inspection = string.IsNullOrEmpty(inspectionFile) ? null : File.ReadLines(inspectionFile).Skip(1)
            .Select(row => row.Split('\t')).Where(row => row.Length >= 6 && row[5] != "correct")
            .Select(row => new Point2d(double.Parse(row[0], CultureInfo.InvariantCulture),
                double.Parse(row[1], CultureInfo.InvariantCulture))).ToArray();
        Directory.CreateDirectory(folder);
        string focusText = Environment.GetEnvironmentVariable("BMA2D_V2_FOCUS");
        SourceFaceId? focus = null;
        if (!string.IsNullOrWhiteSpace(focusText))
        {
            int separator = focusText.LastIndexOf('/');
            focus = new SourceFaceId(focusText[..separator], int.Parse(focusText[(separator + 1)..], CultureInfo.InvariantCulture));
        }
        if (stage == "audit")
            return AuditSaved();
        var prepared = WithScene(Solve);
        using (prepared.Drawing)
        {
            long save = Stopwatch.GetTimestamp();
            if (prepared.Drawing.Segments > V2TiledArrangement.DefaultSegmentLimit)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            V2DrawingOutput.Write(prepared.Drawing, drawingFile, prepared.Units, prepared.Fit, prepared.Styles);
            string line = FormattableString.Invariant($"save and reopen\t{Stopwatch.GetElapsedTime(save).TotalSeconds:F6}");
            File.AppendAllLines(Path.Combine(folder, "v2分步耗时.tsv"), new[] { line });
        }
        return stage == "solve" ? prepared.Report : prepared.Report + Environment.NewLine + AuditSaved();

        string AuditSaved()
        {
            long begin = Stopwatch.GetTimestamp();
            string audit = WithScene((scene, inputs, _) => Audit(inputs, scene.Fit,
                drawingFile, baseline, folder, focus));
            File.WriteAllText(Path.Combine(folder, "射线审计摘要.txt"), audit);
            File.AppendAllLines(Path.Combine(folder, "v2分步耗时.tsv"), new[]
            {
                FormattableString.Invariant($"independent raw audit with reopen\t{Stopwatch.GetElapsedTime(begin).TotalSeconds:F6}")
            });
            return audit;
        }

        T WithScene<T>(Func<Scene, IReadOnlyList<Input>, IReadOnlyDictionary<string, int[]>, T> use)
        {
            if (string.IsNullOrEmpty(viewName))
                return OriginalFaceStages.WithSavedFaces(source, (file, inputs, registered, _, _) =>
                {
                    var selected = SelectInputs(inputs);
                    return use(ReadSettings(file, stage == "audit" ? null : CheckpointStyles(file, selected)), selected, registered);
                });
            List<Input> read;
            Scene settings;
            using (var raw = File3dm.Read(source) ?? throw new IOException(source))
            {
                read = ReadArchive(raw, out var styles);
                settings = ReadSettings(raw, styles);
            }
            try { return use(settings, SelectInputs(read), null); }
            finally { foreach (var input in read) input.Dispose(); }
        }

        static Scene ReadSettings(File3dm model, IReadOnlyDictionary<string, (string Path, Color Color)> styles) =>
            new(model.Settings.ModelUnitSystem, model.Settings.ModelAbsoluteTolerance,
                model.Settings.ModelAngleToleranceRadians, styles);

        List<Input> ReadArchive(File3dm raw, out IReadOnlyDictionary<string, (string Path, Color Color)> archiveStyles)
        {
            string viewArchive = Environment.GetEnvironmentVariable("BMA2D_V2_VIEW_ARCHIVE");
            using var viewFile = string.IsNullOrEmpty(viewArchive) ? null :
                File3dm.Read(viewArchive) ?? throw new IOException(viewArchive);
            var view = OriginalFaceStages.SelectView(viewFile ?? raw, viewName);
            using var viewport = view.Viewport;
            string windowText = Environment.GetEnvironmentVariable("BMA2D_V2_VIEW_WINDOW");
            Rect? window = null;
            if (!string.IsNullOrEmpty(windowText))
            {
                var units = windowText.Split(',').Select(Number).ToArray();
                if (units.Length != 4 || units[0] >= units[2] || units[1] >= units[3])
                    throw new ArgumentException("ViewWindow requires left,bottom,right,top in model units.");
                window = new Rect(units[0], units[1], units[2], units[3]);
            }
            double? near = Optional("BMA2D_V2_NEAR_CLIP"), far = Optional("BMA2D_V2_FAR_CLIP");
            string depthLayer = Environment.GetEnvironmentVariable("BMA2D_V2_DEPTH_CLIP_LAYER");
            bool cropped = window.HasValue || near.HasValue || far.HasValue;
            var origin = cropped ? viewport.CameraLocation : Point3d.Origin;
            var toView = Transform.PlaneToPlane(new Plane(origin, viewport.CameraX, viewport.CameraY), Plane.WorldXY);
            if (cropped) File.AppendAllText(Path.Combine(folder, "进度.txt"),
                $"View {view.Name}; camera {origin}; direction {viewport.CameraDirection}; up {viewport.CameraY}.{Environment.NewLine}");
            List<Input> read;
            using (var placed = new V2ArchiveScene(raw, source, toView, value =>
                File.AppendAllText(Path.Combine(folder, "进度.txt"), value + Environment.NewLine),
                LayerOverrides("BMA2D_V2_LINKS_ON"), LayerOverrides("BMA2D_V2_LINKS_OFF")))
            {
                archiveStyles = placed.Styles;
                if (!string.IsNullOrWhiteSpace(depthLayer) && !archiveStyles.Values.Any(style =>
                    style.Path == depthLayer || style.Path.StartsWith(depthLayer + "::", StringComparison.Ordinal)))
                    throw new ArgumentException($"Depth clip layer was not found: {depthLayer}.");
                read = cropped ? new V2ViewCrop(window, near, far, string.IsNullOrWhiteSpace(depthLayer) ? null : depthLayer,
                    raw.Settings.ModelAbsoluteTolerance).Read(placed.Objects, archiveStyles, stage == "solve") :
                    ProjectionPipeline.ReadObjects(placed.Objects, compactMeshes: stage == "solve");
                if (cropped) File.AppendAllText(Path.Combine(folder, "进度.txt"),
                    $"Scoped view {view.Name}; origin {origin}; {read.Count}/{placed.Objects.Count} source objects retained.{Environment.NewLine}");
            }
            return read;

            static HashSet<string> LayerOverrides(string key) =>
                (Environment.GetEnvironmentVariable(key) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.Ordinal);
            static double? Optional(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? Number(value) : null;
            static double Number(string value)
            {
                double number = double.Parse(value, CultureInfo.InvariantCulture);
                if (!double.IsFinite(number)) throw new ArgumentException("View coordinates must be finite.");
                return number;
            }
        }

        IReadOnlyList<Input> SelectInputs(IReadOnlyList<Input> inputs)
        {
            string inspectedObjects = Environment.GetEnvironmentVariable("BMA2D_V2_INSPECT_OBJECTS");
            if (inspection == null || string.IsNullOrWhiteSpace(inspectedObjects)) return inputs;
            var selectedSources = inspectedObjects.Split(',').ToHashSet(StringComparer.Ordinal);
            if (focus is SourceFaceId target) selectedSources.Add(target.Source);
            return inputs.Where(input => selectedSources.Contains(input.Source)).ToArray();
        }

        static IReadOnlyDictionary<string, (string Path, Color Color)> CheckpointStyles(
            File3dm model, IReadOnlyList<Input> inputs)
        {
            var objectStyles = model.Objects.Where(item => item != null).ToDictionary(item => item.Id);
            var bySource = model.Objects.Where(item => item != null &&
                item.Attributes.GetUserString("BetterMake2D.Source") != null).ToDictionary(
                    item => item.Attributes.GetUserString("BetterMake2D.Source"), StringComparer.Ordinal);
            var layer = model.AllLayers.ToDictionary(entry => entry.Index);
            return inputs.ToDictionary(item => item.Source, item =>
            {
                var tag = item.Source[(item.Source.LastIndexOf('/') + 1)..];
                var original = bySource.GetValueOrDefault(item.Source) ??
                    (Guid.TryParse(tag, out Guid id) ? objectStyles.GetValueOrDefault(id) : null);
                if (original == null || !layer.TryGetValue(original.Attributes.LayerIndex, out var style))
                    return ("Default", Color.Black);
                return (original.Attributes.GetUserString("OriginalLayer") ?? style.FullPath, style.Color);
            }, StringComparer.Ordinal);
        }

        Prepared Solve(Scene scene, IReadOnlyList<Input> inputs, IReadOnlyDictionary<string, int[]> registered)
        {
            var styles = scene.Styles;
            double fit = scene.Fit;
            long begin = Stopwatch.GetTimestamp();
            long lastProgress = begin;
            var progress = Path.Combine(folder, "进度.txt");
            int rejectedRings = 0;
            var drawing = V2DrawingSolver.Solve(inputs, fit, scene.Angle, () =>
            {
                if (Stopwatch.GetElapsedTime(lastProgress).TotalSeconds < 5) return;
                lastProgress = Stopwatch.GetTimestamp();
                File.AppendAllText(progress, $"Solving at {Stopwatch.GetElapsedTime(begin).TotalSeconds:F1} s.{Environment.NewLine}");
            }, focus, value => File.AppendAllText(progress, value + Environment.NewLine), inspection, tileLimit, minimumTileDepth,
                registered, (id, rings, recovered) =>
                {
                    using var writer = File.CreateText(Path.Combine(folder, $"拒收环_{++rejectedRings:D3}.tsv"));
                    writer.WriteLine(FormattableString.Invariant($"{id.Source}/{id.FaceIndex}\t{recovered}\t{fit:R}"));
                    foreach (var ring in rings)
                    {
                        foreach (var p in ring) writer.WriteLine(FormattableString.Invariant($"{p.X:R}\t{p.Y:R}"));
                        writer.WriteLine();
                    }
                }, layerOf: source => styles[source].Item1,
                millimetersPerUnit: RhinoMath.UnitScale(scene.Units, UnitSystem.Millimeters),
                overlapTileArrangement: Environment.GetEnvironmentVariable("BMA2D_V2_OVERLAP_TILES") == "1",
                hiddenMode: Environment.GetEnvironmentVariable("BMA2D_V2_HIDDEN") switch
                {
                    "occluded" => V2HiddenMode.Occluded, "backs" => V2HiddenMode.IncludeBackFaces, _ => V2HiddenMode.None
                }, hiddenLayer: Environment.GetEnvironmentVariable("BMA2D_V2_HIDDEN_LAYER"),
                enableHiZ: Environment.GetEnvironmentVariable("BMA2D_V2_NO_HIZ") != "1",
                accuracy: Environment.GetEnvironmentVariable("BMA2D_V2_FAST_MESH") == "1" ? V2Accuracy.Fast : V2Accuracy.Exact,
                tangentEdges: Environment.GetEnvironmentVariable("BMA2D_V2_TANGENT") == "1");
            double solve = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            File.WriteAllLines(Path.Combine(folder, "未解记录.txt"), drawing.Issues);
            File.WriteAllLines(Path.Combine(folder, "v2分步耗时.tsv"), new[] {"stage\tseconds"}.Concat(
                drawing.Timing.Select(entry => FormattableString.Invariant($"{entry.Key}\t{entry.Value.TotalSeconds:F6}"))));
            File.WriteAllLines(Path.Combine(folder, "连通簇.tsv"), new[] {"faces\tsegments\tcells\tbackground"}.Concat(
                drawing.ClusterSizes.Select(cluster => $"{cluster.Faces}\t{cluster.Segments}\t{cluster.Cells}\t{cluster.Background}")));
            File.WriteAllLines(Path.Combine(folder, "Tile分布.tsv"),
                new[] {"x0\ty0\tx1\ty1\tsegments\tcells\tvisible edges\tarrangement seconds\tdepth seconds"}.Concat(
                    drawing.Tiles.Select(tile => FormattableString.Invariant(
                        $"{tile.X0}\t{tile.Y0}\t{tile.X1}\t{tile.Y1}\t{tile.Segments}\t{tile.Cells}\t{tile.VisibleEdges}\t{tile.ArrangementSeconds:F6}\t{tile.DepthSeconds:F6}"))));
            string report = $"V2 geometry solve {solve:F3} s. {drawing.Regions.Count} regions; {drawing.Curves.Count} edges " +
                $"({drawing.Curves.Count(edge => edge.IsHidden)} hidden); " +
                $"{drawing.Regions.Sum(region => region.Brep.Faces[0].Loops.Count - 1)} interior loops; " +
                $"{drawing.InputFaces} input faces, {drawing.HiddenFaces} proven covered; {drawing.Segments} projected segments; " +
                $"{drawing.Clusters} clusters; {drawing.Backgrounds} backgrounds; {drawing.DistributionCells} cells; " +
                $"{drawing.Edges} DCEL edges; {drawing.SnapRounds} snap rounds across clusters; {drawing.Contacts} native contact queries; " +
                $"{drawing.Rays} depth probes, {drawing.NativeRays} native depths and {drawing.MeshHits} mesh hints; {drawing.Issues.Count} issues." +
                Environment.NewLine + $"Depth: {drawing.PlaneChecks} planar checks ({drawing.PlaneSeconds:F3} s), " +
                $"{drawing.NativeRays} exact curved queries ({drawing.NativeSeconds:F3} s). " +
                $"BVH hits/rays {Fraction(drawing.MeshHits, drawing.Rays):P2}, hits/lookups {Fraction(drawing.MeshHits, drawing.MeshLookups):P2}, " +
                $"{drawing.MeshLookups} lookups, {drawing.CachedMeshFaces} faces with render mesh, {drawing.MeshTriangles} triangles." +
                Environment.NewLine + $"Curved mesh inventory: {drawing.CurvedRenderFaces} archived, {drawing.GeneratedHintFaces} generated " +
                $"({drawing.GeneratedHintTriangles} triangles). Depth: {drawing.ParallelDepthTiles} parallel tiles, {drawing.DepthLanes} lanes." +
                Environment.NewLine + $"Native meshes: {drawing.NativeMeshes} owners, {drawing.NativeMeshTriangles} triangles, " +
                $"{drawing.MeshContactPairs} local 3D contact pairs. Curved proxies: {drawing.ProxyFaces} originals, {drawing.ProxyTriangles} triangles." +
                Environment.NewLine + $"Conservative bounds: {drawing.BoundRayPrunes} depth queries pruned, " +
                $"{drawing.BoundContactPrunes} coarse contact queries pruned, {drawing.ContactTreePrunes} refined tree separations. " +
                $"{drawing.ManagedPlaneChecks} managed planar checks, " +
                $"{drawing.PlanarEarlyExits} planar and {drawing.CurvedHintEarlyExits} curved certified mesh-hint exits, " +
                $"{drawing.AdjacencyHits} certified adjacent witnesses." +
                Environment.NewLine + $"Curve quality: {drawing.StraightChains} straight, {drawing.SmoothChains} restored curved, " +
                $"{drawing.PolylineChains} polyline, {drawing.RejectedUpgrades} rejected local upgrades, {drawing.RecoveredRegions} recovered regions." +
                Environment.NewLine + $"Sub-tolerance seam cleanup: {drawing.AbsorbedSlivers} absorbed bands, " +
                $"{drawing.FlattenedSteps} flattened steps. Styled linework: {drawing.LinesBeforeJoin} before joining, " +
                $"{drawing.Curves.Count} after, {drawing.ClosedLinework} closed." +
                Environment.NewLine + $"Shared boundaries: {drawing.BoundaryEdges} half-edge pairs, " +
                $"{drawing.SharedChains} chains, {drawing.RestoredChains} restored, {drawing.SkippedCurveSources} curve sources over budget." +
                Environment.NewLine + $"Tangent edges: {drawing.TangentChains} shared chains, {drawing.TangentChainsFiltered} filtered." +
                Environment.NewLine + $"3D contact: {drawing.ContactCandidates} box pairs, {drawing.Contacts} BrepBrep calls, " +
                $"{drawing.AnalyticPlanarPairs} analytic planar pairs, {drawing.AnalyticContactSegments} analytic segments, " +
                $"{drawing.PlanarContactFallbacks} planar fallbacks, {drawing.PerspectiveDepthPrunes} perspective depth separations, " +
                $"{drawing.EmptyContacts} empty, {drawing.PointContacts} point-only, {drawing.SeparatePlanes} plane-box separations, " +
                $"{drawing.ExactCoincidence} exact coincidences. {drawing.SilhouetteCalls} silhouette calls." +
                Environment.NewLine + $"Contact batches: {drawing.ParallelContactAttempts} parallel attempts, " +
                $"{drawing.ParallelEmptyContacts} certified empty, {drawing.ContactLanes} lanes, {drawing.ContactPilotAttempts} pilot calls." +
                Environment.NewLine + $"Largest arrangement: {drawing.LargestClusterFaces} faces, {drawing.LargestClusterSegments} " +
                $"segments and {drawing.LargestClusterCells} cells (per-metric maxima)." +
                Environment.NewLine + drawing.TileSummary +
                (drawing.LazyCandidates == 0 ? "" : Environment.NewLine + drawing.LazyContactSummary +
                    Environment.NewLine + string.Join(Environment.NewLine, drawing.LazyContactPasses)) +
                Environment.NewLine + $"Conservative Hi-Z: {drawing.HiZFaces} faces, {drawing.HiZPixels} certified pixels, " +
                $"{drawing.HiZOccluders} occluders including {drawing.HiZCurvedOccluders} curved faces." +
                Environment.NewLine + $"Face inventory: {drawing.RegisteredFaces} registered, {drawing.SupportFaces} support, " +
                $"{drawing.InputFaces - drawing.HiddenFaces} candidates, {drawing.UnregisteredCandidates} of them outside the registry." +
                Environment.NewLine + $"Tiles: {drawing.Tiles.Count}, maximum {drawing.Tiles.Select(tile => tile.Segments).DefaultIfEmpty().Max()} segments, " +
                $"{drawing.Tiles.Select(tile => tile.Cells).DefaultIfEmpty().Max()} cells.";
            File.WriteAllText(Path.Combine(folder, "求解记录.txt"), report);
            return new Prepared(drawing, scene.Units, fit, styles, report);
        }

        static double Fraction(int numerator, int denominator) => denominator == 0 ? 0 : (double)numerator / denominator;
        static int TileOption(string name, int fallback) => Environment.GetEnvironmentVariable(name) is string value
            ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
    }

    private static string Audit(IReadOnlyList<Input> inputs, double fit, string output, string baseline, string folder, SourceFaceId? focus)
    {
        using var saved = File3dm.Read(output) ?? throw new IOException(output);
        using var old = string.IsNullOrEmpty(baseline) ? null : File3dm.Read(baseline) ?? throw new IOException(baseline);
        var trial = Read(saved);
        var previous = Read(old);
        string topology = Describe(saved, trial, "V2") + (old == null ? "" : Environment.NewLine + Describe(old, previous, "V1"));
        File.WriteAllText(Path.Combine(folder, "二维回读.txt"), topology);
        using var focusBrep = focus is SourceFaceId focused
            ? inputs.Single(input => input.Source == focused.Source).Brep.Faces[focused.FaceIndex].DuplicateFace(false)
            : null;
        using var originalIndex = new RTree();
        using var originalFaceIndex = new RTree();
        var boxes = inputs.Select(input => input.Bounds).ToArray();
        double planeFit = fit * .00001;
        var indexedFaces = new List<(int Input, BrepFace Face, BoundingBox Bounds, Plane? Plane)>();
        var faceCandidates = new List<(int Face, double Upper)>();
        for (int i = 0; i < boxes.Length; i++)
        {
            var brep = inputs[i].Brep;
            if (brep == null || brep.Faces.Count < 24)
            {
                originalIndex.Insert(Rect.From(boxes[i]).Grow(fit).Box, i);
                continue;
            }
            foreach (var face in brep.Faces)
            {
                var box = face.GetBoundingBox(true);
                if (!box.IsValid) box = boxes[i];
                originalFaceIndex.Insert(Rect.From(box).Grow(fit).Box, indexedFaces.Count);
                Plane? plane = face.TryGetPlane(out var planar, planeFit) && Math.Abs(planar.Normal.Z) >= .01 ? planar : null;
                indexedFaces.Add((i, face, box, plane));
            }
        }
        if (indexedFaces.Count > 0) File.AppendAllText(Path.Combine(folder, "进度.txt"),
            $"Audit indexed {indexedFaces.Count} trimmed faces of high-face-count raw objects.{Environment.NewLine}");
        using var trialIndex = Index(trial);
        using var previousIndex = Index(previous);
        var extent = BoundingBox.Empty;
        foreach (var box in boxes) extent.Union(box);
        var frame = focus is SourceFaceId id
            ? Rect.From(inputs.Single(input => input.Source == id.Source).Brep.Faces[id.FaceIndex].GetBoundingBox(true))
            : Rect.From(extent).Grow(Math.Max(extent.Diagonal.X, extent.Diagonal.Y) * .015);
        using var table = new StreamWriter(Path.Combine(folder, "同源射线.tsv"));
        table.WriteLine("x\ty\texpected source/face\tv2\tv1\tv2 status\tv1 status\tfront objects");
        string resumePath = Environment.GetEnvironmentVariable("BMA2D_V2_RESUME_AUDIT");
        using var resume = string.IsNullOrEmpty(resumePath) ? null : new StreamReader(resumePath);
        if (resume != null && (old != null || resume.ReadLine() !=
            "x\ty\texpected source/face\tv2\tv1\tv2 status\tv1 status\tfront objects"))
            throw new InvalidDataException("The audit prefix must use the same drawing and have no legacy comparison.");
        bool readingPrefix = resume != null;
        int rows = 0, fixedPoints = 0, introduced = 0;
        long meshTicks = 0, bodyTicks = 0, faceTicks = 0, filledTicks = 0;
        var after = new Dictionary<string, int>();
        var before = new Dictionary<string, int>();
        var auditedPoints = new HashSet<(double X, double Y)>();
        for (int y = 0; y < 31; y++)
            for (int x = 0; x < 109; x++)
            {
                double sx = frame.Left + (frame.Right - frame.Left) * (x + .37) / 109;
                double sy = frame.Bottom + (frame.Top - frame.Bottom) * (y + .61) / 31;
                Probe(new Point3d(sx, sy, 0));
            }
        bool fixedOnly = Environment.GetEnvironmentVariable("BMA2D_V2_FIXED_AUDIT") == "1";
        if (!fixedOnly) ProbeOriginals();
        string replay = Environment.GetEnvironmentVariable("BMA2D_V2_REPLAY_RAYS");
        if (!string.IsNullOrEmpty(replay))
            foreach (string row in File.ReadLines(replay).Skip(1))
            {
                var columns = row.Split('\t');
                Probe(new Point3d(double.Parse(columns[0], CultureInfo.InvariantCulture),
                    double.Parse(columns[1], CultureInfo.InvariantCulture), 0));
            }
        foreach (var fill in fixedOnly ? Array.Empty<(string Source, Brep Shape)>() : trial.Concat(previous))
        {
            var face = fill.Shape.Faces[0];
            foreach (var (u, v) in new[] {(.5, .5), (.23, .37), (.77, .63)})
            {
                double a = face.Domain(0).ParameterAt(u), b = face.Domain(1).ParameterAt(v);
                if (face.IsPointOnFace(a, b, fit * .02) != PointFaceRelation.Interior) continue;
                var p = face.PointAt(a, b);
                Probe(new Point3d(p.X, p.Y, 0));
                break;
            }
        }
        return $"Independent raw-ray audit {rows} probes. V2 {Counts(after)}." +
            (old == null ? "" : $" V1 {Counts(before)}. Corrected {fixedPoints}, introduced {introduced}.");

        void ProbeOriginals()
        {
            foreach (var input in inputs)
            {
                if (focus.HasValue && input.Source != focus.Value.Source) continue;
                if (input.NativeMesh is Mesh mesh)
                {
                    int stride = Math.Max(1, mesh.Faces.Count / 30);
                    for (int i = 0; i < mesh.Faces.Count; i += stride)
                    {
                        var triangle = mesh.Faces[i];
                        var a = mesh.Vertices.Point3dAt(triangle.A);
                        var b = mesh.Vertices.Point3dAt(triangle.B);
                        var c = mesh.Vertices.Point3dAt(triangle.C);
                        Probe(new Point3d((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, 0));
                    }
                    continue;
                }
                foreach (var face in input.Brep.Faces)
                {
                    if (focus.HasValue && face.FaceIndex != focus.Value.FaceIndex) continue;
                    using var trim = face.OuterLoop?.To2dCurve();
                    if (trim == null) continue;
                    var uv = trim.GetBoundingBox(true);
                    foreach (var (u, v) in new[] {(.23, .37), (.5, .5), (.77, .63)})
                    {
                        double a = uv.Min.X + uv.Diagonal.X * u, b = uv.Min.Y + uv.Diagonal.Y * v;
                        if (face.IsPointOnFace(a, b, fit * .02) != PointFaceRelation.Interior) continue;
                        var p = face.PointAt(a, b);
                        Probe(new Point3d(p.X, p.Y, 0));
                    }
                }
            }
        }

        void Probe(Point3d point)
        {
            if (!auditedPoints.Add((point.X, point.Y))) return;
            if (readingPrefix)
            {
                string previousRow = resume.ReadLine();
                if (previousRow != null && !resume.EndOfStream)
                {
                    string[] columns = previousRow.Split('\t');
                    if (columns.Length != 8 || !double.TryParse(columns[0], CultureInfo.InvariantCulture, out double x) ||
                        !double.TryParse(columns[1], CultureInfo.InvariantCulture, out double y) || point.X != x || point.Y != y)
                        throw new InvalidDataException($"The audit prefix differs at ray {rows + 1}.");
                    table.WriteLine(previousRow);
                    string status = columns[5];
                    after[status] = after.GetValueOrDefault(status) + 1;
                    rows++;
                    return;
                }
                readingPrefix = false;
                File.AppendAllText(Path.Combine(folder, "进度.txt"), $"Reused {rows} complete independent raw-ray rows.{Environment.NewLine}");
            }
            using var ray = new LineCurve(new Point3d(point.X, point.Y, extent.Min.Z - fit * 4),
                new Point3d(point.X, point.Y, extent.Max.Z + fit * 4));
            var box = new BoundingBox(point, point);
            var hits = new List<(int Input, int Face, Point3d Point)>();
            double depth = double.NegativeInfinity, unresolved = double.NegativeInfinity;
            originalIndex.Search(box, (_, candidate) =>
            {
                int i = candidate.Id;
                if (boxes[i].Max.Z + fit < depth) return;
                if (inputs[i].NativeMesh is Mesh mesh)
                {
                    // Every triangle of this native mesh has the same audited owner. Only its nearest hit can win.
                    var meshRay = new Ray3d(ray.PointAtEnd, -Vector3d.ZAxis);
                    long start = Stopwatch.GetTimestamp();
                    double parameter = Intersection.MeshRay(mesh, meshRay);
                    meshTicks += Stopwatch.GetTimestamp() - start;
                    if (parameter >= 0 && parameter <= ray.PointAtEnd.Z - ray.PointAtStart.Z)
                    {
                        var hit = meshRay.PointAt(parameter);
                        hits.Add((i, -1, hit));
                        depth = Math.Max(depth, hit.Z);
                    }
                    return;
                }
                long bodyStart = Stopwatch.GetTimestamp();
                bool ok = Intersection.CurveBrep(ray, inputs[i].Brep, fit * .02, out var overlaps, out var points);
                bodyTicks += Stopwatch.GetTimestamp() - bodyStart;
                Collect(i, -1, boxes[i].Max.Z, ok, overlaps, points);
            });
            if (indexedFaces.Count > 0)
            {
                faceCandidates.Clear();
                originalFaceIndex.Search(box, (_, candidate) =>
                {
                    var entry = indexedFaces[candidate.Id];
                    double upper = entry.Bounds.Max.Z;
                    if (entry.Plane is Plane p)
                        upper = Math.Min(upper, PlaneDepth(p) + 2 * planeFit / Math.Abs(p.Normal.Z));
                    if (upper + fit >= depth) faceCandidates.Add((candidate.Id, upper));
                });
                faceCandidates.Sort(static (a, b) => b.Upper.CompareTo(a.Upper));
                foreach (var candidate in faceCandidates)
                {
                    if (candidate.Upper + fit < depth) break;
                    var entry = indexedFaces[candidate.Face];
                    long start = Stopwatch.GetTimestamp();
                    if (entry.Plane is Plane p)
                    {
                        var location = new Point3d(point.X, point.Y, PlaneDepth(p));
                        if (entry.Face.ClosestPoint(location, out double u, out double v))
                        {
                            var exact = entry.Face.PointAt(u, v);
                            double roundoff = Math.Max(1e-8, location.MaximumCoordinate * 1e-13);
                            if (Math.Abs(exact.X - point.X) <= roundoff && Math.Abs(exact.Y - point.Y) <= roundoff)
                            {
                                var relation = entry.Face.IsPointOnFace(u, v, fit * .02);
                                if (relation == PointFaceRelation.Interior)
                                {
                                    hits.Add((entry.Input, entry.Face.FaceIndex, exact));
                                    depth = Math.Max(depth, exact.Z);
                                }
                                faceTicks += Stopwatch.GetTimestamp() - start;
                                if (relation != PointFaceRelation.Boundary) continue;
                                start = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                    bool ok = Intersection.CurveBrepFace(ray, entry.Face, fit * .02, out var overlaps, out var points);
                    faceTicks += Stopwatch.GetTimestamp() - start;
                    Collect(entry.Input, entry.Face.FaceIndex, entry.Bounds.Max.Z, ok, overlaps, points);
                }
            }

            double PlaneDepth(Plane plane) => plane.Origin.Z - (plane.Normal.X * (point.X - plane.Origin.X) +
                plane.Normal.Y * (point.Y - plane.Origin.Y)) / plane.Normal.Z;

            void Collect(int input, int face, double upper, bool ok, Curve[] overlaps, Point3d[] points)
            {
                if (!ok) unresolved = Math.Max(unresolved, upper);
                foreach (var overlap in overlaps ?? Array.Empty<Curve>())
                {
                    unresolved = Math.Max(unresolved, overlap.GetBoundingBox(true).Max.Z);
                    overlap.Dispose();
                }
                foreach (var p in points ?? Array.Empty<Point3d>())
                {
                    hits.Add((input, face, p));
                    depth = Math.Max(depth, p.Z);
                }
            }
            var owners = new HashSet<string>(StringComparer.Ordinal);
            if (focus is SourceFaceId target)
            {
                if (hits.Any(hit => hit.Point.Z >= depth - fit && inputs[hit.Input].Source == target.Source))
                {
                    bool ok = Intersection.CurveBrep(ray, focusBrep, fit * .02, out var overlaps, out var points);
                    if (!ok) unresolved = Math.Max(unresolved, depth);
                    foreach (var overlap in overlaps ?? Array.Empty<Curve>())
                    {
                        unresolved = Math.Max(unresolved, overlap.GetBoundingBox(true).Max.Z);
                        overlap.Dispose();
                    }
                    if (points?.Any(hit => hit.Z >= depth - fit) == true)
                        owners.Add(Label(target.Source, target.FaceIndex));
                }
            }
            else
            {
                foreach (var hit in hits.Where(hit => hit.Point.Z >= depth - fit))
                {
                    var input = inputs[hit.Input];
                    if (input.NativeMesh != null)
                    {
                        owners.Add(Label(input.Source, 0));
                        continue;
                    }
                    if (hit.Face >= 0)
                    {
                        var face = input.Brep.Faces[hit.Face];
                        if (face.ClosestPoint(hit.Point, out double u, out double v) &&
                            face.IsPointOnFace(u, v, fit * .02) == PointFaceRelation.Interior)
                            owners.Add(Label(input.Source, hit.Face));
                        else
                            unresolved = Math.Max(unresolved, hit.Point.Z);
                        continue;
                    }
                    bool found = input.Brep.ClosestPoint(hit.Point, out _, out var component,
                        out _, out _, fit, out _);
                    if (found && component.ComponentIndexType == ComponentIndexType.BrepFace)
                        owners.Add(Label(input.Source, component.Index));
                    else
                        unresolved = Math.Max(unresolved, hit.Point.Z);
                }
            }
            string expected = string.Join(",", owners.OrderBy(owner => owner, StringComparer.Ordinal));
            long fillStart = Stopwatch.GetTimestamp();
            var v2Owners = At(trial, trialIndex);
            var v1Owners = At(previous, previousIndex);
            filledTicks += Stopwatch.GetTimestamp() - fillStart;
            string v2 = Status(v2Owners, after);
            string v1 = old == null ? "" : Status(v1Owners, before);
            if (v2 is "extra/wrong" or "missing" && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BMA2D_V2_INSPECT")))
                foreach (var hit in hits.Where(hit => hit.Point.Z >= depth - fit).Distinct())
                {
                    var input = inputs[hit.Input];
                    if (input.NativeMesh != null) continue;
                    if (!input.Brep.ClosestPoint(hit.Point, out _, out var component, out _, out _, fit, out _)
                        || component.ComponentIndexType != ComponentIndexType.BrepFace) continue;
                    var face = input.Brep.Faces[component.Index];
                    bool planar = face.TryGetPlane(out var plane, fit * .01);
                    double normalZ = face.ClosestPoint(hit.Point, out double u, out double v) ? face.NormalAt(u, v).Z : double.NaN;
                    File.AppendAllText(Path.Combine(folder, "原档探点诊断.txt"), FormattableString.Invariant(
                        $"XY ({point.X:R}, {point.Y:R}); {Label(input.Source, face.FaceIndex)} z={hit.Point.Z:R}; normal z={normalZ:R}; solid={input.Brep.SolidOrientation}; planar={planar}; plane z={plane.Normal.Z:R}; reversed={face.OrientationIsReversed}; face bounds={face.GetBoundingBox(true)}; type={face.UnderlyingSurface().GetType().Name}.{Environment.NewLine}"));
                    foreach (var member in input.Brep.Faces)
                    {
                        if (!Rect.From(member.GetBoundingBox(true)).Grow(fit).Box.Contains(point)) continue;
                        Intersection.CurveBrepFace(ray, member, fit * .02, out var memberOverlaps, out var memberHits);
                        foreach (var overlap in memberOverlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                        if (memberHits is not { Length: > 0 }) continue;
                        member.TryGetPlane(out var p, fit * .01);
                        File.AppendAllText(Path.Combine(folder, "原档探点诊断.txt"),
                            $"  Face {member.FaceIndex} reversed={member.OrientationIsReversed}; plane normal={p.Normal}; depths={string.Join(", ", memberHits.Select(p => p.Z))}.{Environment.NewLine}");
                    }
                }
            rows++;
            if ((rows & 511) == 0)
            {
                File.AppendAllText(Path.Combine(folder, "进度.txt"), FormattableString.Invariant(
                    $"Audited {rows} rays. Native seconds since last report: meshes {Seconds(meshTicks):F3}, bodies {Seconds(bodyTicks):F3}, faces {Seconds(faceTicks):F3}, output fills {Seconds(filledTicks):F3}.{Environment.NewLine}"));
                meshTicks = bodyTicks = faceTicks = filledTicks = 0;
            }
            if (old != null && v1 != "uncertain" && v2 != "uncertain")
            {
                if (v2 == "correct" && v1 != "correct") fixedPoints++;
                if (v1 == "correct" && v2 != "correct") introduced++;
            }
            string frontObjects = string.Join(",", hits.Where(hit => hit.Point.Z >= depth - fit)
                .Select(hit => inputs[hit.Input].Source).Distinct());
            table.WriteLine(FormattableString.Invariant(
                $"{point.X:R}\t{point.Y:R}\t{expected}\t{string.Join(",", v2Owners)}\t{string.Join(",", v1Owners)}\t{v2}\t{v1}\t{frontObjects}"));

            static double Seconds(long ticks) => ticks / (double)Stopwatch.Frequency;

            string[] At((string Source, Brep Shape)[] fills, RTree index)
            {
                var found = new List<string>();
                index.Search(box, (_, hit) =>
                {
                    if (VerifyV2Drawing.Contains(fills[hit.Id].Shape, point, fit * .01))
                        found.Add(fills[hit.Id].Source);
                });
                return found.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            }

            string Status(string[] found, Dictionary<string, int> totals)
            {
                string status = double.IsFinite(unresolved) && unresolved >= depth - fit ? "uncertain" :
                    found.Length == 0 && owners.Count != 0 ? "missing" :
                    found.Any(source => !owners.Contains(source)) ? "extra/wrong" :
                    found.Length > 1 ? "overlap" : "correct";
                totals[status] = totals.GetValueOrDefault(status) + 1;
                return status;
            }
        }

        static string Counts(Dictionary<string, int> counts) => string.Join(", ", counts.Select(pair => $"{pair.Value} {pair.Key}"));
        static string Describe(File3dm file, (string Source, Brep Shape)[] fills, string version)
        {
            int invalid = 0, open = 0, holes = 0;
            foreach (var fill in fills)
            {
                if (!fill.Shape.IsValid || fill.Shape.Faces.Count != 1) { invalid++; continue; }
                bool isOpen = false;
                foreach (var loop in fill.Shape.Faces[0].Loops)
                {
                    using var rim = loop.To3dCurve();
                    isOpen |= rim is not { IsValid: true, IsClosed: true };
                    if (loop.LoopType == BrepLoopType.Inner) holes++;
                }
                if (isOpen) open++;
            }
            int lines = file.Objects.Count(item => item.Geometry is Curve);
            int seams = file.Objects.Count(item => item.Attributes.GetUserString("BoundaryType") == V2TiledArrangement.SeamKind);
            return $"{version}: {fills.Length} regions, {holes} holes, {open} regions with open loops, " +
                $"{invalid} invalid regions, {lines} output curves, {seams} artificial seam curves.";
        }
        (string Source, Brep Shape)[] Read(File3dm file) => file?.Objects.Where(item => item.Geometry is Brep &&
                (focus is not SourceFaceId target || item.Attributes.GetUserString("SourceObject") == target.Source &&
                    item.Attributes.GetUserString("SourceFace") == target.FaceIndex.ToString(CultureInfo.InvariantCulture)))
            .Select(item => ($"{item.Attributes.GetUserString("SourceObject")}/{item.Attributes.GetUserString("SourceFace")}",
                (Brep)item.Geometry)).ToArray() ??
            Array.Empty<(string, Brep)>();
        static string Label(string source, int face) => FormattableString.Invariant($"{source}/{face}");
        static RTree Index((string Source, Brep Shape)[] fills)
        {
            var result = new RTree();
            for (int i = 0; i < fills.Length; i++)
                result.Insert(Rect.From(fills[i].Shape.GetBoundingBox(false)).Box, i);
            return result;
        }
    }
}

public static class VerifyV2FaceRegistryAudit
{
    public static string Run()
    {
        string source = Environment.GetEnvironmentVariable("BMA2D_V2_SOURCE") ?? throw new ArgumentException("Source");
        string rays = Environment.GetEnvironmentVariable("BMA2D_V2_REGISTRY_RAYS") ?? throw new ArgumentException("Rays");
        return OriginalFaceStages.WithSavedFaces(source, (_, _, registered, _, _) =>
        {
            var listed = registered.SelectMany(pair => pair.Value.Select(index =>
                FormattableString.Invariant($"{pair.Key}/{index}"))).ToHashSet(StringComparer.Ordinal);
            var required = new HashSet<string>(StringComparer.Ordinal);
            int probes = 0;
            foreach (string row in File.ReadLines(rays).Skip(1))
            {
                var columns = row.Split('\t');
                if (columns[2].Length == 0 || columns[5] == "uncertain") continue;
                var owners = columns[2].Split(',');
                if (owners.Any(listed.Contains)) continue;
                probes++;
                required.UnionWith(owners);
            }
            return $"{probes} determinate non-background rays require an original outside the registry. " +
                $"{required.Count} distinct unregistered originals occur in those expected owner sets.";
        });
    }
}
