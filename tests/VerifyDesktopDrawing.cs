using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyDesktopDrawing
{
    public static string Run()
    {
        string folder = System.Environment.GetEnvironmentVariable("BMA2D_DESKTOP_TEST") ?? throw new ArgumentException("test output folder");
        Directory.CreateDirectory(folder);
        const double fit = .001;
        var color = Color.FromArgb(110, 69, 130);
        var options = new DesktopOptions { Frame = true, Parent = "Test Drawing", Group = true, Export = true, Svg = true };
        var view = new DesktopView(false, 1, 100, 1, -10, 10, -8, 8, Array.Empty<DesktopPlane>(), true);
        var request = new DesktopRequest(options, view);
        using var exterior = Rectangle(-12, -6, 12, 6, 0);
        using var hole = new Circle(new Point3d(-6, 0, 0), 1.5).ToNurbsCurve();
        using var wall = Brep.CreatePlanarBreps(new[] { exterior, hole }, fit).Single();
        using var mask = Rectangle(0, -7, 3, 7, 2);
        using var blocker = Brep.CreatePlanarBreps(mask, fit).Single();
        using var terrain = new Mesh();
        terrain.Vertices.Add(5000, -100, 5);
        terrain.Vertices.Add(19000, -100, 5);
        terrain.Vertices.Add(19000, 100, 5);
        terrain.Vertices.Add(5000, 100, 5);
        terrain.Faces.AddFace(0, 1, 2, 3);
        using (var scene = new File3dm())
        {
            scene.Settings.ModelUnitSystem = UnitSystem.Millimeters;
            scene.Settings.ModelAbsoluteTolerance = fit;
            scene.Settings.ModelAngleToleranceRadians = .01;
            Add(wall, "Wall", color, true);
            Add(blocker, "Occluder", Color.Black, false);
            Add(terrain, "Terrain", Color.Green, false);
            Require(scene.Write(Path.Combine(folder, "scene.3dm"), 8), "Could not write fixture");
            void Add(GeometryBase geometry, string source, Color layerColor, bool selected)
            {
                int index = scene.AllLayers.AddLayer(source, layerColor);
                using var attr = new ObjectAttributes { LayerIndex = index };
                attr.SetUserString("BetterMake2D.Source", source);
                attr.SetUserString("OriginalLayer", source);
                if (selected) attr.SetUserString(DesktopData.OutputKey, "1");
                scene.Objects.Add(geometry, attr);
            }
        }
        File.WriteAllText(Path.Combine(folder, "request.json"), JsonSerializer.Serialize(request));
        var counts = DesktopWorker.Run(folder, request);
        Require(counts.CulledObjects == 1 && counts.Regions == 2, $"Frame cull / visible faces failed {counts}");
        string presentation = Path.Combine(folder, "presentation.3dm");
        using var drawing = File3dm.Read(Path.Combine(folder, "drawing.3dm"));
        Require(drawing.Objects.All(obj => obj.Attributes.GetUserString("SourceObject") == "Wall"),
            "Occlusion-only objects leaked into the result");
        var fills = drawing.Objects.Select(obj => obj.Geometry).OfType<Brep>().ToArray();
        Require(fills.Length == 2 && fills.All(face => face.IsValid && face.Loops.All(loop => loop.To3dCurve().IsClosed)) &&
            fills.Sum(brep => brep.Loops.Count) == 3, "Open fill or missing hole");
        int rays = 0;
        for (int x = 0; x < 41; x++)
            for (int y = 0; y < 17; y++)
            {
                var p = new Point3d(-12 + (x + .31) * 24 / 41, -7 + (y + .47) * 14 / 17, 0);
                using var ray = new LineCurve(p + Vector3d.ZAxis * 7, p - Vector3d.ZAxis);
                bool expected = p.X > -10 && p.X < 10 && Hit(wall, ray) && !Hit(blocker, ray);
                bool actual = fills.Any(item => Hit(item, ray));
                Require(expected == actual, $"Ray mismatch at {p}");
                rays++;
            }
        using var result = File3dm.Read(presentation);
        var hatches = result.Objects.Where(item => item.Geometry is Hatch).ToArray();
        Require(hatches.Length == 2 && hatches.All(item => item.Geometry.IsValid &&
            result.AllLayers.FindIndex(item.Attributes.LayerIndex).Color.ToArgb() == color.ToArgb()) &&
            hatches.Sum(item => ((Hatch)item.Geometry).Get3dCurves(false).Length) == 1, "Hatch hole/colour lost after reopen");
        using var doc = RhinoDoc.CreateHeadless(null);
        doc.ModelUnitSystem = UnitSystem.Millimeters;
        doc.UndoRecordingEnabled = true;
        var bbox = DesktopOutput.Insert(doc, presentation, options, Point3d.Origin, out var ids);
        Require(bbox.Min.DistanceTo(Point3d.Origin) < fit && ids.All(id => doc.Objects.FindId(id) != null),
            "Insertion point or output IDs invalid");
        Require(doc.Undo() && ids.All(id => doc.Objects.FindId(id) == null || doc.Objects.FindId(id).IsDeleted) &&
            doc.Layers.FindByFullPath(options.Parent, -1) < 0, "One Undo did not remove output and layer tree");
        Guid picked = doc.Objects.AddBrep(wall);
        doc.Objects.AddBrep(blocker);
        Require(doc.Objects.Select(picked) && DesktopData.Selected(doc).Single().Id == picked,
            "Rhino preselection did not retain its original object identity");
        using var viewport = new RhinoViewport();
        viewport.SetProjection(DefinedViewportProjection.Top, "Top", false);
        var choice = new DesktopViewChoice("Top", viewport);
        var selected = DesktopData.Selected(doc);
        var snapshots = new List<(int Completed, int Total)>();
        using var snapshot = DesktopData.Snapshot(doc, choice, options with { AutoOcclusion = false, Frame = false },
            selected, out _, (completed, total) => snapshots.Add((completed, total)));
        Require(snapshots[0] == (0, 1) && snapshots[^1] == (1, 1) && snapshot.Objects.Count == 1,
            "Snapshot progress did not report the selected source in order");
        string snapshotPath = Path.Combine(folder, "snapshot-progress.3dm");
        Require(snapshot.Write(snapshotPath, 8), "Could not save the collected snapshot");
        using var reopened = File3dm.Read(snapshotPath);
        var source = reopened?.Objects.OfType<File3dmObject>().SingleOrDefault();
        Require(source?.Attributes?.GetUserString(DesktopData.OutputKey) == "1",
            "The reopened snapshot lost the selected source");
        bool cancelled = false;
        try
        {
            using var abandoned = DesktopData.Snapshot(doc, choice, options with { AutoOcclusion = false, Frame = false },
                selected, out _, (completed, _) => { if (completed == 1) throw new OperationCanceledException(); });
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Snapshot creation continued after cancellation");
        string both = Path.Combine(folder, "both.3dm");
        DesktopOutput.CreatePresentation(Path.Combine(folder, "drawing.3dm"), both, DesktopFill.Both, out int hatchFallbacks);
        using var doubleOutput = File3dm.Read(both);
        Require(hatchFallbacks == 0 && doubleOutput.Objects.Count(item => item.Geometry is Hatch) == 2 &&
            doubleOutput.Objects.Count(item => item.Geometry is Brep) == 2, "Both formats must be present in a single file");
        var events = new List<string>();
        using var tiled = V2DrawingSolver.Solve(new[] { new Input(wall, "Wall"), new Input(blocker, "Occluder") },
            fit, .01, progress: events.Add, minimumTileDepth: 2, reportTileTotal: true);
        Require(tiled.Tiles.Count >= 4 && events.Contains($"Tile plan {tiled.Tiles.Count}") &&
            events.Count(message => message.StartsWith("Tile ") && message.Contains("segments,")) == tiled.Tiles.Count,
            "The progress plan and completed tile counts disagree");
        string review = Path.Combine(folder, "paper-review");
        Directory.CreateDirectory(review);
        File.Copy(Path.Combine(folder, "scene.3dm"), Path.Combine(review, "scene.3dm"));
        var oversized = DesktopWorker.Run(review, new DesktopRequest(options with { Scale = .002 }, view));
        string guarded = Path.Combine(review, "drawing.svg");
        Require(oversized.VectorExtent.OnPaper(.002).LongEdge > PaperSizing.StandardLimit && !File.Exists(guarded),
            "Must inspect exact output size before writing SVG");
        double recommendation = PaperSizing.Recommend(oversized.VectorExtent, .002);
        File.WriteAllText(Path.Combine(review, "paper.json"), JsonSerializer.Serialize(new PaperExportSettings(recommendation, false)));
        DesktopWorker.ExportSvg(review);
        Require(File.Exists(guarded), "SVG export after scale choice failed");
        return $"PASS native preselection, viewport crop and automatic occlusion ({rays} independent rays, zero differences), "
            + "2 hatches with 1 hole and original color, both fill formats, snapshot progress and cancellation, "
            + "exact tile progress, one Undo, and deferred vector export after size review.";
        
        static bool Hit(Brep face, LineCurve ray)
        {
            Intersection.CurveBrepFace(ray, face.Faces[0], .00001, out var overlaps, out var hits);
            foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
            return hits is { Length: > 0 };
        }

        static Curve Rectangle(double left, double bottom, double right, double top, double z) =>
            new PolylineCurve(new[] { new Point3d(left, bottom, z), new Point3d(right, bottom, z),
                new Point3d(right, top, z), new Point3d(left, top, z), new Point3d(left, bottom, z) });
    }

    private static void Require(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
