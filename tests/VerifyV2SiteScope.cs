using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ViewInfo = Rhino.DocObjects.ViewInfo;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2SiteScope
{
    public static string Run()
    {
        if (Environment.GetEnvironmentVariable("BMA2D_SITE_READBACK") is { Length: > 0 } drawing)
            return ReadDrawing(drawing);
        string source = Environment.GetEnvironmentVariable("BMA2D_SITE_BUILDING") ?? throw new ArgumentException("Building");
        string terrain = Environment.GetEnvironmentVariable("BMA2D_SITE_TERRAIN") ?? throw new ArgumentException("Terrain");
        string output = Environment.GetEnvironmentVariable("BMA2D_SITE_REPORT") ?? throw new ArgumentException("Report");
        var values = (Environment.GetEnvironmentVariable("BMA2D_SITE_FRAME") ?? "").Split(',')
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length != 4) throw new ArgumentException("Expected frame angle, origin X/Y, projected X offset.");
        double angle = values[0] * Math.PI / 180;
        var frame = new Plane(new Point3d(values[1], values[2], 0), new Vector3d(Math.Cos(angle), Math.Sin(angle), 0), Vector3d.ZAxis);
        if (Environment.GetEnvironmentVariable("BMA2D_SITE_VIEW_EXPORT") is { Length: > 0 } cameraArchive)
        {
            using var cameras = new File3dm();
            using var projection = new Rhino.Display.RhinoViewport();
            using var viewport = new Rhino.DocObjects.ViewportInfo(projection);
            viewport.ChangeToParallelProjection(true);
            viewport.SetCameraLocation(frame.Origin + frame.ZAxis * 450000);
            viewport.SetCameraDirection(-frame.ZAxis);
            viewport.SetCameraUp(Vector3d.ZAxis);
            viewport.SetFrustum(-195275, 247000, -36000, 53000, 1, 1000000);
            if (!projection.SetViewProjection(viewport, true)) throw new InvalidOperationException("Could not apply archived camera.");
            using var view = new ViewInfo(projection) { Name = "主立面" };
            cameras.AllNamedViews.Add(view);
            if (!cameras.Write(cameraArchive, 8)) throw new IOException(cameraArchive);
            using var reopened = File3dm.Read(cameraArchive) ?? throw new IOException(cameraArchive);
            using var savedView = OriginalFaceStages.SelectView(reopened, view.Name);
            using var saved = savedView.Viewport;
            var expected = frame.Origin + frame.ZAxis * 450000;
            if (saved.CameraLocation.DistanceTo(expected) > .001 ||
                (saved.CameraDirection + frame.ZAxis).Length > .00000001 ||
                (saved.CameraY - Vector3d.ZAxis).Length > .00000001)
                throw new InvalidOperationException($"Unexpected saved camera: {saved.CameraLocation}; {saved.CameraDirection}; {saved.CameraY}");
            return cameraArchive + " camera " + saved.CameraLocation + " direction " + saved.CameraDirection;
        }
        var map = Transform.Translation(values[3], 0, 0) * Transform.PlaneToPlane(frame, Plane.WorldXY);
        if (!map.TryGetInverse(out var inverse)) throw new InvalidOperationException("Camera frame is singular.");
        using var building = File3dm.Read(source) ?? throw new IOException(source);
        using var site = File3dm.Read(terrain) ?? throw new IOException(terrain);
        var meshes = site.Objects.Where(item => item.Geometry is Mesh).Select(item =>
        {
            var mesh = (Mesh)item.Geometry;
            var path = site.AllLayers.FindIndex(item.Attributes.LayerIndex).FullPath;
            return (mesh, path, item.Id, bounds: mesh.GetBoundingBox(map));
        }).Where(item => item.path.StartsWith("10_", StringComparison.Ordinal) ||
            item.path.StartsWith("20_", StringComparison.Ordinal) || item.path.StartsWith("11_", StringComparison.Ordinal)).ToArray();
        var bodies = building.Objects.Where(item => item.Geometry is Brep or Extrusion or Surface)
            .Select(item => item.Geometry).ToArray();
        var extent = BoundingBox.Empty;
        using var index = new RTree();
        for (int i = 0; i < bodies.Length; i++)
        {
            var box = bodies[i].GetBoundingBox(false);
            extent.Union(box);
            index.Insert(Rect.From(box).Box, i);
        }
        using var report = File.CreateText(output);
        report.WriteLine("Building in projection " + extent);
        foreach (var item in meshes)
            report.WriteLine($"{item.path}\t{item.Id:D}\t{item.mesh.Faces.Count}\t{item.bounds}");
        var totals = new Dictionary<(double Near, string Layer, Guid Object), (int Count, int Above)>();
        int buildingHits = 0, aboveHits = 0;
        const int columns = 59, rows = 23;
        double[] nearDepths = { 30000, 60000, 90000, 120000, 200000, 450000, double.PositiveInfinity };
        for (int u = 0; u < columns; u++)
            for (int v = 0; v < rows; v++)
            {
                double x = extent.Min.X + extent.Diagonal.X * (u + .27) / columns;
                double y = extent.Min.Y + extent.Diagonal.Y * (v + .47) / rows;
                var point = new Point3d(x, y, 0);
                var ray = new Line(new Point3d(x, y, extent.Min.Z - 1), new Point3d(x, y, extent.Max.Z + 1));
                using var curve = new LineCurve(ray);
                double z = double.NegativeInfinity;
                index.Search(new BoundingBox(point, point), (_, hit) =>
                {
                    using var converted = bodies[hit.Id] is Brep ? null : Brep.TryConvertBrep(bodies[hit.Id]);
                    var brep = bodies[hit.Id] as Brep ?? converted;
                    if (brep == null) return;
                    Intersection.CurveBrep(curve, brep, .002, out var overlaps, out var points);
                    foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                    foreach (var found in points ?? Array.Empty<Point3d>()) z = Math.Max(z, found.Z);
                });
                if (!double.IsFinite(z)) continue;
                buildingHits++;
                if (y >= 0) aboveHits++;
                var nearest = new (double Depth, string Layer, Guid Id)[nearDepths.Length];
                var groundRay = new Line(new Point3d(x, y, z), new Point3d(x, y, 30000000));
                groundRay.Transform(inverse);
                foreach (var item in meshes)
                {
                    var b = item.bounds;
                    if (x < b.Min.X || x > b.Max.X || y < b.Min.Y || y > b.Max.Y || z > b.Max.Z ||
                        item.path.StartsWith("11_", StringComparison.Ordinal)) continue;
                    foreach (var hit in Intersection.MeshLine(item.mesh, groundRay) ?? Array.Empty<Point3d>())
                    {
                        var at = hit;
                        at.Transform(map);
                        if (at.Z <= z + .1) continue;
                        for (int j = 0; j < nearDepths.Length; j++)
                            if (at.Z <= nearDepths[j] && (nearest[j].Layer == null || at.Z > nearest[j].Depth))
                                nearest[j] = (at.Z, item.path, item.Id);
                    }
                }
                for (int j = 0; j < nearDepths.Length; j++)
                    if (nearest[j].Layer != null)
                    {
                        var key = (nearDepths[j], nearest[j].Layer, nearest[j].Id);
                        var count = totals.GetValueOrDefault(key);
                        totals[key] = (count.Count + 1, count.Above + (y >= 0 ? 1 : 0));
                    }
            }
        report.WriteLine($"building probes\t{buildingHits}\tabove TP0\t{aboveHits}");
        foreach (var entry in totals.OrderBy(item => item.Key.Near).ThenByDescending(item => item.Value.Count))
            report.WriteLine(FormattableString.Invariant($"{entry.Key.Near:R}\t{entry.Key.Layer}\t{entry.Key.Object:D}\t{entry.Value.Count}\t{entry.Value.Above}"));
        return $"Independent terrain and building rays: {buildingHits} hits, {aboveHits} above TP0. {output}";
    }

    private static string ReadDrawing(string path)
    {
        using var file = File3dm.Read(path) ?? throw new IOException(path);
        var layers = new Dictionary<string, string>(StringComparer.Ordinal);
        var fills = new List<(string Layer, BoundingBox Bounds)>();
        var hidden = new List<(string Layer, string Occluder, BoundingBox Bounds)>();
        foreach (var item in file.Objects)
        {
            var attributes = item.Attributes;
            string source = attributes.GetUserString("SourceObject");
            string layer = attributes.GetUserString("OriginalLayer");
            if (source == null || layer == null) continue;
            layers.TryAdd(source, layer);
            if (item.Geometry is Brep && layer.StartsWith("10_地形与海床::", StringComparison.Ordinal))
                fills.Add((layer, item.Geometry.GetBoundingBox(true)));
            if (attributes.GetUserString("Visibility") != "Hidden") continue;
            hidden.Add((layer, attributes.GetUserString("BoundarySource") ?? "", item.Geometry.GetBoundingBox(true)));
        }
        var lines = new List<string> { $"Units: {file.Settings.ModelUnitSystem}; model tolerance: {file.Settings.ModelAbsoluteTolerance:R}." };
        foreach (var group in fills.GroupBy(item => item.Layer).OrderBy(group => group.Key))
            lines.Add($"{group.Key}: {group.Count()} saved terrain fills.");
        foreach (var group in hidden.GroupBy(item => layers.GetValueOrDefault(item.Occluder, item.Occluder))
            .OrderByDescending(group => group.Count()))
            lines.Add($"{group.Count()} hidden paths; representative front layer {group.Key}; height range " +
                $"{group.Min(item => item.Bounds.Min.Y):F3}..{group.Max(item => item.Bounds.Max.Y):F3} model units.");
        if (Environment.GetEnvironmentVariable("BMA2D_SITE_PROBE_SOURCE") is { Length: > 0 } probe)
            lines.Add($"Fixed ray original {probe}: {layers.GetValueOrDefault(probe, "no emitted geometry")}");
        return string.Join(Environment.NewLine, lines);
    }
}
