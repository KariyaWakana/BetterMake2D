using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;

[assembly: AssemblyTitle("BetterMake2D")]
[assembly: AssemblyDescription(
    "从 Rhino 模型生成可编辑的平面、立面、剖面、轴测与透视图。输出可见线、隐藏线、截平面交线和闭合填色，按原图层组织。"
    + "运行 _BetterMake2D，可选择精确（NURBS）或快速（精细网格）模式，将结果插入当前文件或导出为 3DM、SVG、AI。AI 导出需要 Adobe Illustrator。")]
[assembly: AssemblyCompany("BetterMake2D")]
[assembly: AssemblyProduct("BetterMake2D")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
[assembly: AssemblyInformationalVersion("1.6.0")]
[assembly: Guid(BetterMake2D.BetterMake2DPlugin.PluginId)]
[assembly: ComVisible(false)]
[assembly: PlugInDescription(DescriptionType.Organization, "BetterMake2D")]
[assembly: PlugInDescription(DescriptionType.Country, "中国")]
[assembly: PlugInDescription(DescriptionType.Email, "contact@kariyawakana.com")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/KariyaWakana/BetterMake2D")]
[assembly: TargetFramework(".NETCoreApp,Version=v10.0", FrameworkDisplayName = ".NET 10.0")]

namespace BetterMake2D;

[Guid(BetterMake2DPlugin.PluginId)]
public sealed class BetterMake2DPlugin : PlugIn
{
    internal const string PluginId = "4A30881F-DC1F-4FA2-80E6-F5E233255108";
}

[Guid("5D721950-ED9D-4C3B-B2A8-54FB27615EE0")]
public sealed class BetterMake2DCommand : Command
{
    public override string EnglishName => "BetterMake2D";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => mode == RunMode.Scripted
        ? RunScripted(doc)
        : DesktopInteractive.Run(doc, (BetterMake2DPlugin)PlugIn);

    private static Result RunScripted(RhinoDoc doc)
    {
        var view = doc.Views.ActiveView?.ActiveViewport;
        if (view == null)
        {
            RhinoApp.WriteLine("未找到当前视口。");
            return Result.Failure;
        }

        using var picker = new GetObject();
        picker.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion |
            ObjectType.Mesh | ObjectType.InstanceReference;
        picker.SubObjectSelect = false;
        picker.SetCommandPrompt("选择要出图或参与遮挡的曲面、实体、挤出、网格或块");
        var hiZ = new OptionToggle(true, "No", "Yes");
        var hiddenLines = new OptionToggle(false, "No", "Yes");
        var hiddenBackFaces = new OptionToggle(false, "No", "Yes");
        var clippingPlanes = new OptionToggle(true, "No", "Yes");
        var tangentEdges = new OptionToggle(false, "No", "Yes");
        int accuracy = 0;
        int accuracyOption = picker.AddOptionList("Accuracy", new[] { "Exact", "Fast" }, accuracy);
        picker.AddOptionToggle("HiZ", ref hiZ);
        picker.AddOptionToggle("HiddenLines", ref hiddenLines);
        picker.AddOptionToggle("HiddenBackFaces", ref hiddenBackFaces);
        picker.AddOptionToggle("ClippingPlanes", ref clippingPlanes);
        picker.AddOptionToggle("TangentEdges", ref tangentEdges);
        int layerOption = picker.AddOption("HiddenLayer");
        string hiddenLayer = "";
        while (picker.GetMultiple(1, 0) == GetResult.Option)
        {
            if (picker.OptionIndex() == accuracyOption)
                accuracy = picker.Option().CurrentListOptionIndex;
            else if (picker.OptionIndex() == layerOption)
            {
                string choice = hiddenLayer;
                var selection = RhinoGet.GetString("隐藏线原图层路径，输入 All 输出全部图层", false, ref choice);
                if (selection != Result.Success) return selection;
                hiddenLayer = string.Equals(choice, "All", StringComparison.OrdinalIgnoreCase) ? "" : choice.Trim();
            }
        }
        if (picker.CommandResult() != Result.Success)
            return picker.CommandResult();

        var objects = Enumerable.Range(0, picker.ObjectCount).Select(i => picker.Object(i).Object()).ToArray();
        var worldBox = BoundingBox.Empty;
        foreach (var obj in objects)
            worldBox.Union(obj.Geometry.GetBoundingBox(false));
        if (!worldBox.IsValid)
            return Result.Nothing;
        var frame = new Plane(view.IsParallelProjection ? worldBox.Center : view.CameraLocation, view.CameraX, view.CameraY);
        var toView = Transform.PlaneToPlane(frame, Plane.WorldXY);
        IReadOnlyList<Input> inputs = Array.Empty<Input>();
        bool cancel = false;
        EventHandler handler = (_, _) => cancel = true;
        RhinoApp.EscapeKeyPressed += handler;
        try
        {
            var sources = new List<ProjectionObject>();
            var layers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var obj in objects)
                Collect(obj, Transform.Identity, string.Empty, toView, sources, layers);
            long lastEvents = Stopwatch.GetTimestamp();
            void CheckCancel()
            {
                if (Stopwatch.GetElapsedTime(lastEvents).TotalMilliseconds >= 50)
                {
                    RhinoApp.Wait();
                    lastEvents = Stopwatch.GetTimestamp();
                }
                if (cancel)
                    throw new OperationCanceledException();
            }

            IReadOnlyList<ProjectionObject> retained = sources;
            IProjectionCamera projection = null;
            var clips = clippingPlanes.CurrentValue ? ReadActiveClippingPlanes(doc, view.Id, toView) : Array.Empty<Plane>();
            if (!view.IsParallelProjection)
            {
                using var viewportInfo = new ViewportInfo(view);
                if (!viewportInfo.GetFrustum(out _, out _, out _, out _, out double near, out double far))
                    throw new InvalidOperationException("无法读取透视视锥。");
                double focal = view.CameraLocation.DistanceTo(view.CameraTarget);
                projection = new V2PerspectiveCamera(near, far, focal > 0 ? focal : near, clips);
                retained = retained.Where(item => !projection.Clips.Discards(item.Geometry.GetBoundingBox(item.ToView))).ToArray();
            }
            inputs = ProjectionPipeline.ReadObjects(retained, CheckCancel,
                compactMeshes: true, viewClips: projection?.ViewClips);
            using var drawing = V2DrawingSolver.Solve(inputs, doc.ModelAbsoluteTolerance,
                doc.ModelAngleToleranceRadians, CheckCancel, progress: message => RhinoApp.WriteLine(message),
                layerOf: source => doc.Layers[layers[source]].FullPath,
                millimetersPerUnit: RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters),
                hiddenMode: !hiddenLines.CurrentValue ? V2HiddenMode.None :
                    hiddenBackFaces.CurrentValue ? V2HiddenMode.IncludeBackFaces : V2HiddenMode.Occluded,
                hiddenLayer: hiddenLayer, enableHiZ: hiZ.CurrentValue, clipPlanes: clips, projection: projection,
                tangentEdges: tangentEdges.CurrentValue, accuracy: (V2Accuracy)accuracy);
            RhinoApp.WriteLine($"相切公共边链 {drawing.TangentChains} 条，已隐藏 {drawing.TangentChainsFiltered} 条。");
            if (drawing.Regions.Count == 0 && drawing.Curves.Count == 0) return Result.Nothing;
            var placement = RhinoGet.GetPoint("指定二维图面左下角在世界 XY 平面上的插入点", false, out var point);
            if (placement != Result.Success) return placement;
            var extent = BoundingBox.Empty;
            foreach (var region in drawing.Regions) extent.Union(region.Brep.GetBoundingBox(false));
            foreach (var edge in drawing.Curves) extent.Union(edge.Curve.GetBoundingBox(false));
            var move = Transform.Translation(point.X - extent.Min.X, point.Y - extent.Min.Y, 0);
            WriteV2Output(doc, drawing, move, layers, CheckCancel);
            RhinoApp.WriteLine($"BetterMake2D V2 已按原图层写出 {drawing.Regions.Count} 个闭合区、" +
                $"{drawing.Curves.Count} 条线。{drawing.Issues.Count} 条求解记录待复核。");
            return Result.Success;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancel;
        }
        catch (Exception e)
        {
            RhinoApp.WriteLine($"BetterMake2D: {e.Message}");
            return Result.Failure;
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= handler;
            foreach (var input in inputs)
                input.Dispose();
        }
    }

    internal static ClippingPlaneObject[] ActiveClippingPlanes(RhinoDoc doc, Guid viewport) =>
        doc.Objects.GetObjectList(ObjectType.ClipPlane).OfType<ClippingPlaneObject>()
            .Where(clip => !clip.IsHidden && clip.ClippingPlaneGeometry.ViewportIds().Contains(viewport)).ToArray();

    internal static Plane[] ReadActiveClippingPlanes(RhinoDoc doc, Guid viewport, Transform toView) =>
        ReadClippingPlanes(ActiveClippingPlanes(doc, viewport), toView);

    internal static Plane[] ReadClippingPlanes(IEnumerable<ClippingPlaneObject> clips, Transform toView)
    {
        var planes = new List<Plane>();
        foreach (var clip in clips)
        {
            var surface = clip.ClippingPlaneGeometry;
            var plane = surface.Plane;
            if (!plane.Transform(toView) || !plane.IsValid)
                throw new InvalidOperationException("无法读取当前视口的活动截平面。");
            planes.Add(plane);
            if (surface.PlaneDepthEnabled && surface.PlaneDepth >= 0)
                planes.Add(new Plane(plane.Origin - plane.Normal * surface.PlaneDepth, -plane.Normal));
        }
        return planes.ToArray();
    }

    internal static void Collect(RhinoObject obj, Transform placement, string parent, Transform toView,
        List<ProjectionObject> output, Dictionary<string, int> layers = null, bool parentOpaque = true,
        Func<RhinoObject, bool> visibleMember = null)
    {
        if (obj == null) return;
        string path = parent + obj.Id;
        bool opaque = obj.Attributes.MaterialSource == ObjectMaterialSource.MaterialFromParent
            ? parentOpaque : (obj.GetMaterial(true)?.Transparency ?? 0) == 0;
        if (obj is InstanceObject instance)
        {
            foreach (var member in instance.InstanceDefinition.GetObjects())
                if (visibleMember == null || visibleMember(member))
                    Collect(member, placement * instance.InstanceXform, path + "/", toView, output, layers, opaque, visibleMember);
            return;
        }

        var geometry = obj.Geometry;
        if (geometry is not (Brep or Surface or Extrusion or Mesh) || geometry is ClippingPlaneSurface) return;
        output.Add(new ProjectionObject(geometry, path, toView * placement) { Opaque = opaque });
        if (layers != null) layers[path] = obj.Attributes.LayerIndex;
    }

    internal static void WriteV2Output(RhinoDoc doc, V2Drawing drawing, Transform translation,
        IReadOnlyDictionary<string, int> sourceLayers, Action cancel = null)
    {
        var objects = new List<Guid>();
        var addedLayers = new List<int>();
        var layers = new Dictionary<(int Source, Guid Root), int>();
        using var hiddenPattern = drawing.Curves.Any(edge => edge.IsHidden) ? V2DrawingOutput.HiddenDefinition() : null;
        var run = "BetterMake2D-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        try
        {
            Guid parent = doc.Layers[AddLayer(run, Color.Black, Guid.Empty)].Id;
            Guid fillRoot = doc.Layers[AddLayer("ClosedRegions", Color.Black, parent)].Id;
            Guid lineRoot = doc.Layers[AddLayer("VisibleLines", Color.Black, parent)].Id;
            Guid sectionRoot = drawing.Curves.Any(edge => !edge.IsHidden && edge.Boundary == "SectionCut")
                ? doc.Layers[AddLayer("SectionCuts", Color.Black, parent)].Id : Guid.Empty;
            Guid hiddenRoot = hiddenPattern == null ? Guid.Empty :
                doc.Layers[AddLayer("HiddenLines", Color.Black, parent)].Id;
            foreach (var region in drawing.Regions)
            {
                cancel?.Invoke();
                using var geometry = region.Brep.DuplicateBrep();
                if (!geometry.Transform(translation))
                    throw new InvalidOperationException("无法定位闭合填色区。");
                using var attributes = Styled(region.Source, region.FaceIndex, fillRoot);
                Add(doc.Objects.AddBrep(geometry, attributes));
            }
            foreach (var edge in drawing.Curves)
            {
                cancel?.Invoke();
                using var geometry = edge.Curve.DuplicateCurve();
                if (!geometry.Transform(translation))
                    throw new InvalidOperationException("无法定位二维曲线。");
                using var attributes = Styled(edge.Source, edge.FaceIndex, edge.IsHidden ? hiddenRoot :
                    edge.Boundary == "SectionCut" ? sectionRoot : lineRoot);
                if (edge.IsHidden)
                {
                    attributes.SetUserString("Visibility", "Hidden");
                    attributes.LinetypeSource = ObjectLinetypeSource.LinetypeFromObject;
                    attributes.SetCustomLinetype(hiddenPattern);
                }
                attributes.SetUserString("BoundaryType", edge.Boundary);
                if (edge.JoinedSources != null) attributes.SetUserString("JoinedSourceFaces", edge.JoinedSources);
                if (edge.Occluder.Length != 0) attributes.SetUserString("BoundarySource", edge.Occluder);
                Add(doc.Objects.AddCurve(geometry, attributes));
            }
            cancel?.Invoke();
        }
        catch
        {
            foreach (var id in objects) doc.Objects.Delete(id, true);
            foreach (int index in addedLayers.AsEnumerable().Reverse()) doc.Layers.Delete(index, true);
            throw;
        }
        finally { doc.Views.Redraw(); }

        void Add(Guid id)
        {
            if (id == Guid.Empty) throw new InvalidOperationException("Rhino 无法加入输出对象。");
            objects.Add(id);
        }

        ObjectAttributes Styled(string source, int face, Guid root)
        {
            int index = sourceLayers.TryGetValue(source, out int found) ? found : -1;
            var style = index < 0 ? null : doc.Layers[index];
            var attributes = Attributes(Mirror(style, root), source);
            attributes.SetUserString("SourceFace", face.ToString(System.Globalization.CultureInfo.InvariantCulture));
            attributes.SetUserString("OriginalLayer", style?.FullPath ?? "Default");
            return attributes;
        }

        int Mirror(Layer original, Guid root)
        {
            var key = (original?.Index ?? -1, root);
            if (layers.TryGetValue(key, out int index)) return index;
            Guid parent = root;
            if (original != null && original.ParentLayerId != Guid.Empty)
            {
                var originalParent = doc.Layers.FindId(original.ParentLayerId);
                if (originalParent != null) parent = doc.Layers[Mirror(originalParent, root)].Id;
            }
            index = AddLayer(original?.Name ?? "Default", original?.Color ?? Color.Black, parent);
            layers.Add(key, index);
            return index;
        }

        int AddLayer(string name, Color color, Guid parentId)
        {
            using var layer = new Layer { Name = name, Color = color, ParentLayerId = parentId };
            int index = doc.Layers.Add(layer);
            if (index < 0) throw new InvalidOperationException($"无法创建图层 {name}。");
            addedLayers.Add(index);
            return index;
        }
    }

    private static ObjectAttributes Attributes(int layer, string source)
    {
        var attributes = new ObjectAttributes { LayerIndex = layer };
        attributes.SetUserString("SourceObject", source);
        return attributes;
    }
}

