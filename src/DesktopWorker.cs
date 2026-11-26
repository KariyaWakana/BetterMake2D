using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

internal sealed record DesktopRequest(DesktopOptions Options, DesktopView View);
internal sealed record DesktopResult(int Regions, int Lines, int Objects, int CulledObjects,
    PaperExtent VectorExtent = default, int HatchFallbacks = 0);
internal sealed record PaperExportSettings(double Scale, bool AllowLargeCanvas);

public static class DesktopWorker
{
    public static int Run(string folder)
    {
        var config = JsonSerializer.Deserialize<DesktopRequest>(File.ReadAllText(Path.Combine(folder, "request.json")));
        var result = Run(folder, config, progress => Console.WriteLine("P|" + progress));
        File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(result));
        return 0;
    }

    public static int ExportSvg(string folder)
    {
        var settings = JsonSerializer.Deserialize<PaperExportSettings>(File.ReadAllText(Path.Combine(folder, "paper.json")));
        ElevationSvgExport.Write(Path.Combine(folder, "drawing.3dm"), Path.Combine(folder, "drawing.svg"),
            settings.Scale, settings.AllowLargeCanvas, Console.Error.WriteLine);
        return 0;
    }

    internal static DesktopResult Run(string folder, DesktopRequest request, Action<string> progress = null)
    {
        string input = Path.Combine(folder, "scene.3dm");
        var options = request.Options;
        progress?.Invoke("read");
        var styles = new Dictionary<string, (string Path, Color Color)>();
        var selected = new HashSet<string>();
        List<Input> inputs;
        UnitSystem units;
        double tolerance, angle;
        int culled;
        IProjectionCamera camera;
        using (var file = File3dm.Read(input) ?? throw new IOException(input))
        {
            units = file.Settings.ModelUnitSystem;
            tolerance = file.Settings.ModelAbsoluteTolerance;
            angle = file.Settings.ModelAngleToleranceRadians;
            var objects = file.Objects.Where(obj => obj != null).ToArray();
            var bbox = file.Objects.GetBoundingBox();
            camera = request.View.Camera(bbox, tolerance);
            var sources = new List<ProjectionObject>(objects.Length);
            foreach (var obj in objects)
            {
                var shape = obj.Geometry;
                if (camera.Clips.Discards(shape.GetBoundingBox(false))) continue;
                var attr = obj.Attributes;
                string source = attr.GetUserString("BetterMake2D.Source");
                string layer = attr.GetUserString("OriginalLayer");
                styles.Add(source, (layer, file.AllLayers.FindIndex(attr.LayerIndex).Color));
                if (attr.GetUserString(DesktopData.OutputKey) == "1") selected.Add(source);
                sources.Add(new(shape, source, Transform.Identity)
                    { Opaque = attr.GetUserString("BetterMake2D.Opaque") != "0" });
            }
            culled = objects.Length - sources.Count;
            if (selected.Count == 0) return new(0, 0, 0, culled);
            inputs = ProjectionPipeline.ReadObjects(sources, compactMeshes: true, viewClips: camera.ViewClips);
            culled = objects.Length - inputs.Count;
            progress?.Invoke($"View snapshot: {objects.Length} objects, {culled} rejected before face/contact search.");
        }
        try
        {
            progress?.Invoke("solve");
            string Layer(string source) => styles[source].Path;
            bool Hidden(string source) => selected.Contains(source) && (options.HiddenLayers.Length == 0 ||
                options.HiddenLayers.Any(layer => layer == Layer(source) || Layer(source).StartsWith(layer + "::",
                    StringComparison.Ordinal)));
            using var drawing = V2DrawingSolver.Solve(inputs, tolerance, angle, progress: progress,
                layerOf: Layer, projection: camera, millimetersPerUnit: RhinoMath.UnitScale(units, UnitSystem.Millimeters),
                hiddenMode: !options.Hidden ? V2HiddenMode.None : options.BackFaces
                    ? V2HiddenMode.IncludeBackFaces : V2HiddenMode.Occluded,
                enableHiZ: options.HiZ, tangentEdges: options.Tangent, hiddenSourceFilter: Hidden,
                lineLayerOf: source => (selected.Contains(source) ? "1" : "0") + Layer(source), reportTileTotal: true,
                overlapTileArrangement: true, accuracy: options.Accuracy, releaseInputs: ReleaseInputs);
            ReleaseInputs();
            progress?.Invoke($"Depth: {drawing.Rays} probes, {drawing.MeshHits}/{drawing.MeshLookups} render mesh hits, " +
                $"{drawing.NativeRays} exact Brep queries ({drawing.NativeSeconds:F2} s); " +
                $"{drawing.Contacts} Brep contacts, {drawing.Tiles.Count} tiles, image grid {drawing.ImageGrid:G5}.");
            progress?.Invoke($"Curved depth hints: {drawing.CurvedRenderFaces} render faces, " +
                $"{drawing.GeneratedHintFaces} generated faces with {drawing.GeneratedHintTriangles} triangles; " +
                $"{drawing.DepthLanes} depth lanes used on {drawing.ParallelDepthTiles} tiles, " +
                $"{drawing.CurvedHintEarlyExits} certified curved early exits.");
            progress?.Invoke($"Tile timing: {drawing.Timing.GetValueOrDefault("integer arrangement").TotalSeconds:F2} s arrangement, " +
                $"{drawing.Timing.GetValueOrDefault("independent cell depth").TotalSeconds:F2} s depth wall.");
            if (options.Accuracy == V2Accuracy.Fast)
                progress?.Invoke($"Fast mode: {drawing.ProxyFaces} original curved faces, {drawing.ProxyTriangles} triangles, " +
                    $"{drawing.MeshContactPairs} mesh contact pairs, " +
                    $"{drawing.Timing.GetValueOrDefault("mesh-local 3D contact").TotalSeconds:F2} s mesh contact.");
            progress?.Invoke($"Contacts: {drawing.Contacts} native BrepBrep calls " +
                $"({drawing.Timing.GetValueOrDefault("native BrepBrep").TotalSeconds:F2} s), " +
                $"{drawing.AnalyticPlanarPairs} analytic planar pairs, {drawing.AnalyticContactSegments} analytic segments, " +
                $"{drawing.PlanarContactFallbacks} planar fallbacks, {drawing.PerspectiveDepthPrunes} perspective depth separations, " +
                $"{drawing.ContactTreePrunes} refined tree separations ({drawing.Timing.GetValueOrDefault("refined contact tree").TotalSeconds:F2} s), " +
                $"{drawing.EmptyContacts} empty native calls.");
            progress?.Invoke($"Parallel contact: {drawing.ContactLanes} lanes, {drawing.ParallelContactAttempts} attempts, " +
                $"{drawing.ParallelEmptyContacts} empty results accepted, {drawing.ContactPilotAttempts} pilot calls; other attempts replayed serially.");
            progress?.Invoke($"Hi-Z: {drawing.HiZFaces}/{drawing.InputFaces} faces, {drawing.HiZPixels} certified pixels, " +
                $"{drawing.HiZOccluders} occluders including {drawing.HiZCurvedOccluders} curved faces.");
            drawing.Regions.RemoveAll(region =>
            {
                if (options.Fills && selected.Contains(region.Source)) return false;
                region.Brep.Dispose();
                return true;
            });
            drawing.Curves.RemoveAll(line =>
            {
                if (selected.Contains(line.Source) && (line.IsHidden ? options.Hidden : line.Boundary switch
                    { "SectionCut" => options.Cuts, "Tangent" => options.Tangent, _ => options.Lines })) return false;
                line.Curve.Dispose();
                return true;
            });
            int regions = drawing.Regions.Count, lines = drawing.Curves.Count;
            if (regions + lines == 0) return new(0, 0, 0, culled);
            bool exportVectors = options.Export && (options.Svg || options.Ai);
            PaperExtent vectorExtent = default;
            if (exportVectors)
            {
                var extent = BoundingBox.Empty;
                foreach (var region in drawing.Regions) extent.Union(region.Brep.GetBoundingBox(true));
                foreach (var curve in drawing.Curves) extent.Union(curve.Curve.GetBoundingBox(true));
                vectorExtent = PaperExtent.FromModel(extent.Diagonal.X, extent.Diagonal.Y, units);
            }
            progress?.Invoke("output");
            // The proxy meshes, BVHs and tiles are unreachable after Solve returns.
            // Recover that space before the archive writer allocates its own copy.
            bool largeDrawing = V2DrawingOutput.LargeDrawing(drawing);
            if (largeDrawing) CollectFinishedStage();
            string canonical = Path.Combine(folder, "drawing.3dm");
            V2DrawingOutput.Write(drawing, canonical, units, tolerance, styles, releaseDrawing: largeDrawing);
            drawing.Dispose();
            if (exportVectors)
            {
                double paper = vectorExtent.OnPaper(options.Scale).LongEdge;
                if (paper <= PaperSizing.LargeLimit && (paper <= PaperSizing.StandardLimit || options.AllowLargeCanvas))
                {
                    File.WriteAllText(Path.Combine(folder, "paper.json"),
                        JsonSerializer.Serialize(new PaperExportSettings(options.Scale, options.AllowLargeCanvas)));
                    ExportSvg(folder);
                }
            }
            int written = 0, hatchFallbacks = 0;
            if (options.Insert || options.Export && options.Model)
            {
                if (largeDrawing) CollectFinishedStage();
                written = DesktopOutput.CreatePresentation(canonical, Path.Combine(folder, "presentation.3dm"), options.Fill,
                    out hatchFallbacks);
            }
            return new(regions, lines, written, culled, vectorExtent, hatchFallbacks);
        }
        finally { ReleaseInputs(); }

        void ReleaseInputs()
        {
            foreach (var inputItem in inputs) inputItem.Dispose();
            inputs.Clear();
        }
    }

    private static void CollectFinishedStage()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}

internal static class DesktopOutput
{
    internal static int CreatePresentation(string input, string destination, DesktopFill fill, out int hatchFallbacks)
    {
        hatchFallbacks = 0;
        if (fill == DesktopFill.Brep)
        {
            File.Copy(input, destination);
            using var saved = File3dm.Read(destination) ?? throw new IOException(destination);
            return saved.Objects.Count;
        }
        using var source = File3dm.Read(input) ?? throw new IOException(input);
        using var solid = HatchPattern.Defaults.Solid;
        source.AllHatchPatterns.Add(solid);
        int pattern = source.AllHatchPatterns.FindName(solid.Name)?.Index ??
            throw new IOException("Could not save the solid hatch pattern.");
        int count = source.Objects.Count;
        // Convert the detached file in place to avoid holding two copies of the drawing.
        var objects = new List<Guid>(count);
        foreach (var obj in source.Objects) objects.Add(obj.Attributes.ObjectId);
        foreach (Guid objectId in objects)
        {
            var obj = source.Objects.FindId(objectId) ?? throw new IOException("An output object is missing.");
            obj.Attributes.RemoveFromAllGroups();
            if (obj.Geometry is not Brep region) continue;
            using var hatch = Hatch.CreateFromBrep(region, 0, pattern, 0, 1, Point3d.Origin);
            if (hatch is not { IsValid: true })
            {
                hatchFallbacks++;
                continue;
            }
            using var attributes = obj.Attributes.Duplicate();
            if (fill == DesktopFill.Both)
            {
                attributes.ObjectId = Guid.Empty;
                count++;
            }
            else if (!source.Objects.Delete(attributes.ObjectId))
                throw new IOException("Could not replace the planar region with its solid fill.");
            if (source.Objects.AddHatch(hatch, attributes) == Guid.Empty)
                throw new IOException("Could not save a solid fill.");
        }
        if (!source.Write(destination, 8)) throw new IOException(destination);
        source.Dispose();
        using var reopened = File3dm.Read(destination) ?? throw new IOException(destination);
        if (reopened.Objects.Count != count) throw new IOException("The presentation drawing did not reopen.");
        return count;
    }

    internal static BoundingBox Insert(RhinoDoc doc, string source, DesktopOptions options, Point3d location, out Guid[] ids)
    {
        using var file = File3dm.Read(source) ?? throw new IOException(source);
        var bounds = file.Objects.GetBoundingBox();
        if (!bounds.IsValid) throw new IOException("The drawing has no valid 2D bounds.");
        if (file.Settings.ModelUnitSystem != doc.ModelUnitSystem)
            throw new InvalidOperationException("The document units changed during calculation.");
        if (options.Placement == DesktopPlacement.Arrange)
        {
            var occupied = doc.Objects.BoundingBox;
            double gap = Math.Max(bounds.Diagonal.X * .05, doc.ModelAbsoluteTolerance * 100);
            location = occupied.IsValid ? new Point3d(occupied.Max.X + gap, occupied.Min.Y, 0) : Point3d.Origin;
        }
        var move = Transform.Translation(location - bounds.Min);
        uint undo = doc.BeginUndoRecord("BetterMake2D");
        if (undo == 0) throw new InvalidOperationException("Rhino could not open an undo record.");
        var created = new List<Guid>();
        var added = new List<int>();
        var layerMap = new Dictionary<int, Guid>();
        int group = -1;
        int patternIndex = -1, addedPattern = -1;
        using var pattern = V2DrawingOutput.HiddenDefinition();
        try
        {
            int root = AddLayer(UniqueName(options.Parent), Color.Black, Guid.Empty);
            using var solid = HatchPattern.Defaults.Solid;
            if (options.Group)
            {
                group = doc.Groups.Add();
                if (group < 0) throw new IOException("Could not create the output group.");
            }
            foreach (var obj in file.Objects)
            {
                var from = file.AllLayers.FindIndex(obj.Attributes.LayerIndex);
                using var attributes = obj.Attributes.Duplicate();
                attributes.ObjectId = Guid.Empty;
                attributes.RemoveFromAllGroups();
                attributes.LayerIndex = doc.Layers.FindId(Mirror(from, doc.Layers[root].Id)).Index;
                if (group >= 0) attributes.AddToGroup(group);
                if (attributes.GetUserString("Visibility") == "Hidden")
                {
                    attributes.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
                    attributes.SetCustomLinetype(pattern);
                }
                using var geometry = obj.Geometry.Duplicate();
                if (geometry is Hatch hatch)
                {
                    if (patternIndex < 0)
                    {
                        using var existing = doc.HatchPatterns.FindName(solid.Name);
                        patternIndex = existing != null && existing.FillType == solid.FillType ? existing.Index : -1;
                        if (patternIndex < 0)
                        {
                            if (existing != null) solid.Name = "BetterMake2D Solid";
                            using var previous = doc.HatchPatterns.FindName(solid.Name);
                            patternIndex = previous != null && previous.FillType == solid.FillType ? previous.Index :
                                addedPattern = doc.HatchPatterns.Add(solid);
                        }
                        if (patternIndex < 0) throw new IOException("Could not add the solid hatch pattern.");
                    }
                    hatch.PatternIndex = patternIndex;
                }
                if (!geometry.Transform(move)) throw new IOException("Could not place drawing geometry.");
                Guid id = geometry switch
                {
                    Hatch fill => doc.Objects.AddHatch(fill, attributes),
                    Brep brep => doc.Objects.AddBrep(brep, attributes),
                    Curve curve => doc.Objects.AddCurve(curve, attributes),
                    _ => Guid.Empty
                };
                if (id == Guid.Empty) throw new IOException("Rhino rejected " + geometry.ObjectType + " from the output.");
                created.Add(id);
            }
        }
        catch
        {
            foreach (var id in created) doc.Objects.Delete(id, true);
            if (group >= 0) doc.Groups.Delete(group);
            if (addedPattern >= 0) doc.HatchPatterns.Delete(addedPattern, true);
            foreach (int index in added.AsEnumerable().Reverse()) doc.Layers.Delete(index, true);
            throw;
        }
        finally { doc.EndUndoRecord(undo); }
        ids = created.ToArray();
        if (options.Select) doc.Objects.Select(ids);
        bounds.Transform(move);
        doc.Views.Redraw();
        return bounds;

        string UniqueName(string name)
        {
            string current = name;
            for (int suffix = 2; doc.Layers.FindByFullPath(current, -1) >= 0; suffix++) current = $"{name}_{suffix}";
            return current;
        }

        int AddLayer(string name, Color color, Guid parent)
        {
            using var layer = new Layer { Name = name, Color = color, ParentLayerId = parent };
            int index = doc.Layers.Add(layer);
            if (index < 0) throw new IOException("Could not create layer " + name);
            added.Add(index);
            return index;
        }

        Guid Mirror(Layer layer, Guid root)
        {
            if (layerMap.TryGetValue(layer.Index, out var id)) return id;
            string name = layer.Name switch { "闭合区" => "ClosedRegions", "线条" => "VisibleLines", "Hidden" => "HiddenLines", _ => layer.Name };
            var parent = file.AllLayers.FindId(layer.ParentLayerId);
            if (parent != null)
            {
                root = Mirror(parent, root);
                name = layer.Name;
            }
            id = doc.Layers[AddLayer(name, layer.Color, root)].Id;
            layerMap[layer.Index] = id;
            return id;
        }
    }
}
