using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

internal enum DesktopFill { Hatch, Brep, Both }
internal enum DesktopPlacement { Origin, Arrange, Pick }

internal sealed record DesktopOptions
{
    public V2Accuracy Accuracy { get; set; } = V2Accuracy.Exact;
    public bool All { get; set; }
    public bool AutoOcclusion { get; set; } = true;
    public bool Frame { get; set; }
    public bool? FramePreference { get; set; }
    public bool Clip { get; set; } = true;
    public bool Lines { get; set; } = true;
    public bool Tangent { get; set; }
    public bool Cuts { get; set; } = true;
    public bool Hidden { get; set; }
    public bool BackFaces { get; set; }
    public string[] HiddenLayers { get; set; } = Array.Empty<string>();
    public bool Fills { get; set; } = true;
    public DesktopFill Fill { get; set; } = DesktopFill.Hatch;
    public bool Insert { get; set; } = true;
    public string Parent { get; set; } = "";
    public DesktopPlacement Placement { get; set; }
    public bool Select { get; set; }
    public bool Group { get; set; }
    public bool Export { get; set; }
    public bool Model { get; set; } = true;
    public bool Svg { get; set; }
    public bool Ai { get; set; }
    public string Folder { get; set; } = "";
    public string Prefix { get; set; } = "";
    public double Scale { get; set; } = 1000;
    public bool AllowLargeCanvas { get; set; }
    public bool HiZ { get; set; } = true;
}

internal sealed record DesktopViewChoice(string Name, RhinoViewport Scope, ViewInfo Saved = null)
{
    internal ViewportInfo ReadCamera() => Saved?.Viewport ?? new ViewportInfo(Scope);

    internal ClippingPlaneObject[] ReadClips(RhinoDoc doc) => Saved == null
        ? BetterMake2DCommand.ActiveClippingPlanes(doc, Scope.Id)
        : (Saved.ClippingPlanesIds ?? Array.Empty<Guid>()).Select(doc.Objects.FindId)
            .OfType<ClippingPlaneObject>().Where(clip => !clip.IsHidden).ToArray();
}

internal sealed record DesktopPlane(double X, double Y, double Z, double A, double B, double C)
{
    internal DesktopPlane(Plane plane) : this(plane.OriginX, plane.OriginY, plane.OriginZ,
        plane.Normal.X, plane.Normal.Y, plane.Normal.Z) { }
    internal Plane Plane => new(new Point3d(X, Y, Z), new Vector3d(A, B, C));
}

internal sealed record DesktopView(bool Perspective, double Near, double Far, double Focal,
    double Left, double Right, double Bottom, double Top, DesktopPlane[] Sections, bool Crop)
{
    internal IProjectionCamera Camera(BoundingBox extent, double tolerance)
    {
        var sections = Sections.Select(item => item.Plane).ToArray();
        if (Perspective)
            return new V2PerspectiveCamera(Near, Far, Focal, sections, Crop ? new Rect(Left, Bottom, Right, Top) : null);
        var result = sections.ToList();
        if (Crop)
        {
            result.Add(new Plane(new Point3d(Left, 0, 0), -Vector3d.XAxis));
            result.Add(new Plane(new Point3d(Right, 0, 0), Vector3d.XAxis));
            result.Add(new Plane(new Point3d(0, Bottom, 0), -Vector3d.YAxis));
            result.Add(new Plane(new Point3d(0, Top, 0), Vector3d.YAxis));
        }
        // Rhino's parallel viewport near/far planes are often display optimizations;
        // only perspective requires a bounded depth interval for defined projection.
        var projection = new V2ParallelCamera(extent.Min.Z - 4 * tolerance, extent.Max.Z + 4 * tolerance, result);
        return new DesktopProjection(projection, sections);
    }

    internal static DesktopView Capture(ViewportInfo view, Point3d target, Plane[] sections, bool crop)
    {
        if (!view.GetFrustum(out double left, out double right, out double bottom, out double top,
                out double near, out double far))
            throw new InvalidOperationException("Rhino could not read the viewport frustum.");
        double focal = target.IsValid ? Math.Max(near, view.CameraLocation.DistanceTo(target)) : near;
        return new(!view.IsParallelProjection, near, far, focal,
            left, right, bottom, top, sections.Select(plane => new DesktopPlane(plane)).ToArray(), crop);
    }

    private sealed class DesktopProjection(IProjectionCamera core, Plane[] sections) : IProjectionCamera
    {
        public bool Perspective => core.Perspective;
        public V2ClipPlanes Clips => core.Clips;
        public bool IsSection(Plane plane) => Array.IndexOf(sections, plane) >= 0;
        public Point2d Project(Point3d p) => core.Project(p);
        public Line ViewRay(Point2d p) => core.ViewRay(p);
        public bool Includes(Point3d p) => core.Includes(p);
        public Curve ProjectCurve(Curve curve) => core.ProjectCurve(curve);
        public Rect Bounds(BoundingBox box) => core.Bounds(box);
        public Point3d AtDepth(Point2d p, double z) => core.AtDepth(p, z);
        public double ScreenMargin(BoundingBox box, double margin) => core.ScreenMargin(box, margin);
    }
}

internal static class DesktopData
{
    internal const ObjectType Supported = ObjectType.Brep | ObjectType.Surface | ObjectType.Extrusion |
        ObjectType.Mesh | ObjectType.InstanceReference;
    internal const string OutputKey = "BetterMake2D.DesktopOutput";

    internal static RhinoObject[] Selected(RhinoDoc doc) => doc.Objects.GetSelectedObjects(false, false)
        .Where(obj => !obj.IsDeleted && (obj.ObjectType & Supported) != 0).ToArray();

    internal static RhinoObject[] Visible(RhinoDoc doc, RhinoViewport view, Action checkpoint = null) =>
        doc.Objects.GetObjectList(new ObjectEnumeratorSettings
        {
            NormalObjects = true, LockedObjects = true, HiddenObjects = false,
            ReferenceObjects = true, ObjectTypeFilter = Supported, ViewportFilter = view
        }).Where(obj =>
        {
            checkpoint?.Invoke();
            return !obj.IsDeleted && !obj.IsInstanceDefinitionGeometry && !obj.IsHidden &&
                VisibleLayer(doc, view, obj.Attributes.LayerIndex);
        }).ToArray();

    private static bool VisibleLayer(RhinoDoc doc, RhinoViewport view, int index)
    {
        for (var layer = doc.Layers.FindIndex(index); layer != null;
            layer = layer.ParentLayerId == Guid.Empty ? null : doc.Layers.FindId(layer.ParentLayerId))
            if (!layer.IsVisible || !layer.PerViewportIsVisible(view.Id)) return false;
        return true;
    }

    internal static PaperExtent EstimatePaper(RhinoDoc doc, DesktopViewChoice choice, IEnumerable<RhinoObject> targets,
        bool frame, bool clip)
    {
        using var view = choice.ReadCamera();
        var transform = Transform.PlaneToPlane(new Plane(view.CameraLocation, view.CameraX, view.CameraY), Plane.WorldXY);
        var sections = clip ? BetterMake2DCommand.ReadClippingPlanes(choice.ReadClips(doc), transform) : Array.Empty<Plane>();
        var settings = DesktopView.Capture(view, choice.Saved == null ? choice.Scope.CameraTarget : view.TargetPoint, sections, frame);
        var camera = settings.Camera(new BoundingBox(new Point3d(-1, -1, -1), new Point3d(1, 1, 1)), doc.ModelAbsoluteTolerance);
        double factor = settings.Perspective ? settings.Focal / settings.Near : 1;
        var window = new Rect(settings.Left * factor, settings.Bottom * factor, settings.Right * factor, settings.Top * factor);
        var projected = BoundingBox.Empty;
        foreach (var target in targets)
        {
            var box = target.Geometry.GetBoundingBox(false);
            box.Transform(transform);
            if (!box.IsValid || camera.Clips.Discards(box)) continue;
            var rect = camera.Bounds(box);
            if (frame) rect = rect.Intersection(window);
            if (!rect.HasArea) continue;
            projected.Union(new Point3d(rect.Left, rect.Bottom, 0));
            projected.Union(new Point3d(rect.Right, rect.Top, 0));
        }
        return projected.IsValid ? PaperExtent.FromModel(projected.Diagonal.X, projected.Diagonal.Y, doc.ModelUnitSystem) : default;
    }

    internal static int CountFaces(RhinoObject obj, Dictionary<Guid, int> cache)
    {
        if (obj is InstanceObject instance)
        {
            var definition = instance.InstanceDefinition;
            if (!cache.TryGetValue(definition.Id, out int count))
            {
                cache.Add(definition.Id, 0);
                cache[definition.Id] = count = definition.GetObjects().Sum(member => CountFaces(member, cache));
            }
            return count;
        }
        return obj.Geometry switch
        {
            Brep brep => brep.Faces.Count, Mesh mesh => mesh.Faces.Count,
            Extrusion solid => solid.ProfileCount + (solid.IsCappedAtBottom ? 1 : 0) + (solid.IsCappedAtTop ? 1 : 0),
            Surface => 1, _ => 0
        };
    }

    internal static File3dm Snapshot(RhinoDoc doc, DesktopViewChoice choice, DesktopOptions options,
        IReadOnlyCollection<RhinoObject> selected, out DesktopView camera, Action<int, int> progress = null)
    {
        using var view = choice.ReadCamera();
        var toView = Transform.PlaneToPlane(new Plane(view.CameraLocation, view.CameraX, view.CameraY), Plane.WorldXY);
        var sections = options.Clip
            ? BetterMake2DCommand.ReadClippingPlanes(choice.ReadClips(doc), toView) : Array.Empty<Plane>();
        camera = DesktopView.Capture(view, choice.Saved == null ? choice.Scope.CameraTarget : view.TargetPoint, sections, options.Frame);
        int completed = 0, total = 0;
        void Checkpoint() => progress?.Invoke(completed, total);
        var visible = options.All || options.AutoOcclusion ? Visible(doc, choice.Scope, Checkpoint) : Array.Empty<RhinoObject>();
        var extent = BoundingBox.Empty;
        var targets = options.All ? visible : selected;
        var ids = targets.Select(item => item.Id).ToHashSet();
        var objects = (options.All || options.AutoOcclusion ? visible.Concat(targets) : targets)
            .DistinctBy(obj => obj.Id).ToArray();
        total = objects.Length;
        Checkpoint();
        var collected = new List<ProjectionObject>();
        var sourceLayers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var obj in objects)
        {
            if (!obj.IsDeleted) extent.Union(obj.Geometry.GetBoundingBox(false));
            Checkpoint();
        }
        if (!extent.IsValid) return new File3dm();
        extent.Transform(toView);
        var clip = camera.Camera(extent, doc.ModelAbsoluteTolerance).Clips;
        var archive = new File3dm();
        try
        {
            archive.Settings.ModelUnitSystem = doc.ModelUnitSystem;
            archive.Settings.ModelAbsoluteTolerance = doc.ModelAbsoluteTolerance;
            archive.Settings.ModelAngleToleranceRadians = doc.ModelAngleToleranceRadians;
            var layers = new Dictionary<int, int>();
            foreach (var obj in objects)
            {
                if (!obj.IsDeleted)
                {
                    bool output = ids.Contains(obj.Id);
                    BetterMake2DCommand.Collect(obj, Transform.Identity, "", toView, collected, sourceLayers,
                        visibleMember: member =>
                        {
                            Checkpoint();
                            return !member.IsDeleted && !member.IsHidden && VisibleLayer(doc, choice.Scope, member.Attributes.LayerIndex);
                        });
                    foreach (var item in collected)
                    {
                        Checkpoint();
                        var layer = doc.Layers.FindIndex(sourceLayers[item.Source]);
                        if (layer == null) continue;
                        var bounds = item.Geometry.GetBoundingBox(false);
                        bounds.Transform(item.ToView);
                        if (!bounds.IsValid || clip.Discards(bounds)) continue;
                        using var copy = item.Geometry.Duplicate();
                        if (!copy.Transform(item.ToView)) throw new InvalidOperationException("Could not copy object " + item.Source);
                        using var attr = new ObjectAttributes { LayerIndex = MakeLayer(layer) };
                        attr.SetUserString("BetterMake2D.Source", item.Source);
                        attr.SetUserString("OriginalLayer", layer.FullPath);
                        attr.SetUserString("BetterMake2D.Opaque", item.Opaque ? "1" : "0");
                        if (output) attr.SetUserString(OutputKey, "1");
                        if (archive.Objects.Add(copy, attr) == Guid.Empty) throw new IOException("Could not write object " + item.Source);
                    }
                    collected.Clear();
                    sourceLayers.Clear();
                }
                completed++;
                Checkpoint();
            }
            return archive;
        
            int MakeLayer(Layer layer)
            {
                if (layers.TryGetValue(layer.Index, out int index)) return index;
                Guid parent = Guid.Empty;
                if (layer.ParentLayerId != Guid.Empty && doc.Layers.FindId(layer.ParentLayerId) is Layer source)
                    parent = archive.AllLayers.FindIndex(MakeLayer(source)).Id;
                index = archive.AllLayers.AddLayer(layer.Name, layer.Color, parent);
                if (index < 0) throw new IOException("Could not copy layer " + layer.FullPath);
                return layers[layer.Index] = index;
            }
        }
        catch { archive.Dispose(); throw; }

    }

    internal static DesktopOptions LoadSettings(PersistentSettings settings)
    {
        try { return JsonSerializer.Deserialize<DesktopOptions>(settings.GetString("DesktopOptions", "{}")) ?? new(); }
        catch (JsonException) { return new(); }
    }

    internal static void StoreSettings(PersistentSettings settings, DesktopOptions options) =>
        settings.SetString("DesktopOptions", JsonSerializer.Serialize(options));
}
