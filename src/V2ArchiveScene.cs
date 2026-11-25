using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

// Holds linked archives only while their geometry is being copied to the view.
internal sealed class V2ArchiveScene : IDisposable
{
    private readonly Dictionary<string, File3dm> links = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<ProjectionObject> Objects = new();
    internal readonly Dictionary<string, (string Path, Color Color)> Styles = new(StringComparer.Ordinal);

    internal V2ArchiveScene(File3dm root, string path, Transform toView, Action<string> report = null,
        IReadOnlySet<string> enabled = null, IReadOnlySet<string> disabled = null)
    {
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(path) };
        var unverified = new HashSet<string>(StringComparer.Ordinal);
        if (enabled != null) unverified.UnionWith(enabled);
        if (disabled != null) unverified.UnionWith(disabled);
        try
        {
            Read(root, Path.GetFullPath(path), "", toView);
            if (unverified.Count != 0)
                throw new IOException($"Linked layer paths were not found: {string.Join(", ", unverified)}.");
        }
        catch { Dispose(); throw; }

        void Read(File3dm file, string archive, string prefix, Transform placement)
        {
            var definitions = file.AllInstanceDefinitions.Where(definition => definition != null).ToDictionary(definition => definition.Id);
            var layers = file.AllLayers.ToDictionary(layer => layer.Index);
            var byId = layers.Values.ToDictionary(layer => layer.Id);
            bool inLinkedArchive = prefix.Length != 0;
            if (inLinkedArchive)
                foreach (var layer in layers.Values)
                    if (unverified.Remove(layer.FullPath))
                        report?.Invoke($"Linked layer override: {layer.FullPath} -> {(enabled?.Contains(layer.FullPath) == true ? "on" : "off")}.");
            var active = new HashSet<Guid>();
            foreach (var item in file.Objects)
                if (item != null && !item.Attributes.IsInstanceDefinitionObject)
                    Collect(item, prefix, placement);

            void Collect(File3dmObject item, string parent, Transform world)
            {
                var attributes = item.Attributes;
                if (attributes.Mode == ObjectMode.Hidden || !layers.TryGetValue(attributes.LayerIndex, out var layer) ||
                    !Visible(layer)) return;
                string source = parent + item.Id.ToString("D");
                if (item.Geometry is InstanceReferenceGeometry reference)
                {
                    if (!definitions.TryGetValue(reference.ParentIdefId, out var definition) ||
                        !active.Add(reference.ParentIdefId))
                        throw new IOException($"Missing or cyclic block definition at {source}.");
                    try
                    {
                        var transform = world * reference.Xform;
                        var members = definition.GetObjectIds();
                        if (members.Length == 0 && definition.UpdateType is InstanceDefinitionUpdateType.Linked or
                            InstanceDefinitionUpdateType.LinkedAndEmbedded)
                        {
                            string linked = Resolve(archive, definition.SourceArchive, definition.SourceArchiveRelativePath);
                            if (!open.Add(linked)) throw new IOException($"Cyclic external reference at {source}: {linked}.");
                            try
                            {
                                if (!links.TryGetValue(linked, out var document))
                                {
                                    document = File3dm.Read(linked) ?? throw new IOException($"Could not read {linked}.");
                                    links.Add(linked, document);
                                    report?.Invoke($"Read linked archive: {linked}");
                                }
                                if (document.Settings.ModelUnitSystem != file.Settings.ModelUnitSystem)
                                    throw new IOException($"External reference units differ at {source}: {linked}.");
                                Read(document, linked, source + "/", transform);
                            }
                            finally { open.Remove(linked); }
                        }
                        else
                            foreach (Guid id in members)
                                Collect(file.Objects.FindId(id) ?? throw new IOException($"Missing block member {id} at {source}."),
                                    source + "/", transform);
                    }
                    finally { active.Remove(reference.ParentIdefId); }
                    return;
                }
                if (item.Geometry is not (Brep or Extrusion or Surface or Mesh)) return;
                Objects.Add(new ProjectionObject(item.Geometry, source, world));
                Styles.Add(source, (attributes.GetUserString("OriginalLayer") ?? layer.FullPath, layer.Color));
            }

            bool Visible(Layer layer)
            {
                while (layer != null)
                {
                    if (inLinkedArchive && disabled?.Contains(layer.FullPath) == true ||
                        !layer.IsVisible && !(inLinkedArchive && enabled?.Contains(layer.FullPath) == true)) return false;
                    layer = byId.GetValueOrDefault(layer.ParentLayerId);
                }
                return true;
            }
        }
    }

    private static string Resolve(string archive, string absolute, string relative)
    {
        string directory = Path.GetDirectoryName(archive);
        var fallback = Path.GetFileName(absolute);
        foreach (string candidate in new[] { absolute, relative, fallback })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            string path = Path.GetFullPath(candidate, directory);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException($"Could not resolve linked archive {absolute} from {archive}.");
    }

    public void Dispose()
    {
        Objects.Clear();
        foreach (var link in links.Values) link.Dispose();
        links.Clear();
    }
}
