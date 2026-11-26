using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;

namespace BetterMake2D;

internal static class V2DrawingOutput
{
    internal const string HiddenLinetypeName = "BetterMake2D Hidden";
    internal static bool LargeDrawing(V2Drawing drawing) =>
        (long)drawing.Regions.Count + drawing.Curves.Count >= 50000 || drawing.ProxyTriangles >= 1_000_000;

    internal static Linetype HiddenDefinition()
    {
        var definition = new Linetype { Name = HiddenLinetypeName };
        definition.AppendSegment(.7, true);
        definition.AppendSegment(.4, false);
        return definition;
    }

    internal static void MarkHidden(ObjectAttributes attributes, int linetype)
    {
        attributes.SetUserString("Visibility", "Hidden");
        attributes.LinetypeIndex = linetype;
        attributes.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
    }

    internal static void Write(V2Drawing drawing, string file, UnitSystem units, double tolerance,
        IReadOnlyDictionary<string, (string Path, Color Color)> styles, bool releaseDrawing = false)
    {
        string pending = file + ".pending.3dm";
        try
        {
            int count = WriteModel(drawing, pending, units, tolerance, styles, releaseDrawing);
            if (releaseDrawing) CollectArchiveStorage();
            using (var saved = File3dm.Read(pending) ?? throw new IOException(pending))
                if (saved.Settings.ModelUnitSystem != units || saved.Objects.Count != count)
                    throw new IOException("The v2 2D drawing did not reopen with its original units and objects.");
            File.Move(pending, file, true);
        }
        finally
        {
            if (File.Exists(pending)) File.Delete(pending);
        }
    }

    private static int WriteModel(V2Drawing drawing, string file, UnitSystem units, double tolerance,
        IReadOnlyDictionary<string, (string Path, Color Color)> styles, bool releaseDrawing)
    {
        using var model = new File3dm();
        model.Settings.ModelUnitSystem = units;
        model.Settings.ModelAbsoluteTolerance = tolerance;
        model.Strings.SetString("BetterMake2D.Stage", "Drawing");
        var layerIds = new Dictionary<string, (int Index, Guid Id)>(StringComparer.Ordinal);
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        int hiddenType = -1;
        if (drawing.Curves.Any(edge => edge.IsHidden))
        {
            using var definition = HiddenDefinition();
            model.AllLinetypes.Add(definition);
            hiddenType = model.AllLinetypes.FindName(HiddenLinetypeName)?.LinetypeIndex ?? -1;
            if (hiddenType < 0) throw new IOException("Could not save the hidden line pattern.");
        }
        foreach (var region in drawing.Regions)
        {
            using var attr = Attributes(region.Source, region.FaceIndex, "闭合区");
            if (model.Objects.AddBrep(region.Brep, attr) == Guid.Empty)
                throw new IOException($"Could not save region {region.Source}/{region.FaceIndex}.");
            if (releaseDrawing) region.Brep.Dispose();
        }
        foreach (var edge in drawing.Curves)
        {
            using var attr = Attributes(edge.Source, edge.FaceIndex, edge.IsHidden ? "Hidden" :
                edge.Boundary == "SectionCut" ? "SectionCuts" : "线条");
            if (edge.IsHidden) MarkHidden(attr, hiddenType);
            attr.SetUserString("BoundaryType", edge.Boundary);
            if (edge.JoinedSources != null) attr.SetUserString("JoinedSourceFaces", edge.JoinedSources);
            if (edge.Occluder.Length != 0) attr.SetUserString("BoundarySource", edge.Occluder);
            if (model.Objects.AddCurve(edge.Curve, attr) == Guid.Empty)
                throw new IOException($"Could not save edge {edge.Source}/{edge.FaceIndex}.");
            if (releaseDrawing) edge.Curve.Dispose();
        }
        if (releaseDrawing)
        {
            drawing.Regions.Clear();
            drawing.Curves.Clear();
            CollectArchiveStorage();
        }
        var options = new File3dmWriteOptions { Version = 8, SaveRenderMeshes = false, SaveAnalysisMeshes = false };
        if (model.Objects.Count == 0 || !model.Write(file, options))
            throw new IOException($"Could not save the 2D drawing {file}.");
        return model.Objects.Count;

        ObjectAttributes Attributes(string source, int face, string category)
        {
            var style = styles.TryGetValue(source, out var found) ? found : ("Default", Color.Black);
            string path = style.Item1;
            var layer = Layer(category + "::" + path, style.Item2);
            var attributes = new ObjectAttributes { LayerIndex = layer.Index };
            attributes.SetUserString("SourceObject", source);
            attributes.SetUserString("SourceFace", face.ToString(CultureInfo.InvariantCulture));
            attributes.SetUserString("OriginalLayer", path);
            if (!groups.TryGetValue(source, out int group))
                groups.Add(source, group = model.AllGroups.AddGroup());
            if (group >= 0) attributes.AddToGroup(group);
            return attributes;
        }

        (int Index, Guid Id) Layer(string path, Color color)
        {
            if (layerIds.TryGetValue(path, out var exists)) return exists;
            int at = path.LastIndexOf("::", StringComparison.Ordinal);
            Guid parent = at < 0 ? Guid.Empty : Layer(path[..at], color).Id;
            string name = at < 0 ? path : path[(at + 2)..];
            int index = model.AllLayers.AddLayer(name, color, parent);
            if (index < 0) throw new IOException($"Could not create original layer {path}.");
            var added = (index, model.AllLayers.FindIndex(index).Id);
            layerIds.Add(path, added);
            return added;
        }
    }

    private static void CollectArchiveStorage()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
