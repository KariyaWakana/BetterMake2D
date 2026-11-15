using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;

namespace Project2.VisibleLines
{
    [Guid("05DD102A-B697-429C-81BF-20260A160211")]
    public sealed class VisibleLinesPlugin : PlugIn
    {
    }

    [Guid("7323390A-28DE-4172-94FC-DA9312921ECD")]
    public sealed class FeatureLineCommand : Command
    {
        public override string EnglishName => "P2VisibleLines";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) => Project(doc, hiddenOnly: false);

        internal static Result Project(RhinoDoc doc, bool hiddenOnly)
        {
            var view = doc.Views.ActiveView?.ActiveViewport;
            if (view == null)
            {
                RhinoApp.WriteLine(UiText.Get("An active viewport is required.", "请先激活一个工作视图。", "作業するビューポートを選択してください。"));
                return Result.Failure;
            }

            using var get = new GetObject();
            get.SetCommandPrompt(UiText.Get("Select parts and any objects that should occlude them",
                "选择要投影的构件及其遮挡物", "投影する部材と遮蔽物を選択"));
            get.GeometryFilter = ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion |
                ObjectType.InstanceReference | ObjectType.Mesh;
            var fill = new OptionToggle(false, "No", "Yes");
            var smooth = new OptionToggle(false, "No", "Yes");
            var refine = new OptionToggle(false, "No", "Yes");
            var contacts = new OptionToggle(false, "No", "Yes");
            if (!hiddenOnly)
                get.AddOptionToggle("ClosedFill", ref fill);
            get.AddOptionToggle("SmoothTransitions", ref smooth);
            if (!hiddenOnly)
            {
                get.AddOptionToggle("RefineDetails", ref refine);
                get.AddOptionToggle("DetailContacts", ref contacts);
            }
            while (get.GetMultiple(1, 0) == GetResult.Option)
                continue;
            if (get.CommandResult() != Result.Success)
                return get.CommandResult();

            var settings = new HiddenLineDrawingParameters
            {
                AbsoluteTolerance = doc.ModelAbsoluteTolerance,
                Flatten = false,
                IncludeHiddenCurves = hiddenOnly,
                IncludeTangentEdges = true,
                IncludeTangentSeams = true
            };
            settings.SetViewport(view);
            var clips = new List<Plane>();
            foreach (var clipping in doc.Objects.GetObjectList(ObjectType.ClipPlane).OfType<ClippingPlaneObject>())
                if (clipping.ClippingPlaneGeometry.ViewportIds().Contains(view.Id))
                    clips.Add(clipping.ClippingPlaneGeometry.Plane);
            foreach (var clip in clips) settings.AddClippingPlane(clip);

            var parts = new List<VisibleFill.Part>();
            int leaves = 0;
            for (int i = 0; i < get.ObjectCount; i++)
                leaves += AddObject(settings, get.Object(i).Object(), Transform.Identity, string.Empty, parts);
            if (leaves == 0)
                return Result.Nothing;
            var detailParts = new List<VisibleFill.Part>();
            if (fill.CurrentValue && refine.CurrentValue)
            {
                doc.Objects.UnselectAll();
                using var detail = new GetObject();
                detail.SetCommandPrompt(UiText.Get("Select source parts to refine for closed fill",
                    "选择需要细化闭合填充的原始构件", "閉じた塗りつぶしを詳細化する元の部材を選択"));
                detail.GeometryFilter = get.GeometryFilter;
                detail.EnablePreSelect(false, true);
                detail.GetMultiple(1, 0);
                if (detail.CommandResult() != Result.Success)
                    return detail.CommandResult();
                var roots = Enumerable.Range(0, detail.ObjectCount).Select(i => detail.Object(i).ObjectId.ToString())
                    .ToHashSet();
                detailParts.AddRange(parts.Where(p => roots.Contains(p.Instance.Split('/')[0])));
                doc.Objects.UnselectAll();
                if (detailParts.Count == 0)
                {
                    RhinoApp.WriteLine(UiText.Get("Refinement requires parts from the first selection.",
                        "细化对象须来自第一轮选择。", "詳細化する部材は最初に選択した部材から選んでください。"));
                    return Result.Failure;
                }
            }
            using var drawing = HiddenLineDrawing.Compute(settings, true);
            if (drawing == null)
                return Result.Failure;
            using var projectedView = drawing.Viewport;

            var counters = new Dictionary<FeatureLine, int>();
            var curves = new List<(Curve Curve, FeatureLine Kind, string Source)>();
            var hiddenCurves = new List<(Curve Curve, FeatureLine Kind, string Source)>();
            var boundaries = new List<Curve>();
            foreach (var segment in drawing.Segments)
            {
                bool visible = segment.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Visible;
                bool hidden = hiddenOnly && segment.SegmentVisibility == HiddenLineDrawingSegment.Visibility.Hidden;
                if (!visible && !hidden)
                    continue;
                var parent = segment.ParentCurve;
                if (parent.SourceObject.Tag is not SourceTag tag)
                    continue;
                var kind = FeatureLinePolicy.Classify(parent.SilhouetteType, tag.Brep, parent.SourceObjectComponentIndex);
                if (visible)
                    counters[kind] = counters.GetValueOrDefault(kind) + 1;
                if (!FeatureLinePolicy.ShouldDraw(kind, smooth.CurrentValue))
                    continue;
                var curve = segment.CurveGeometry?.DuplicateCurve();
                if (curve == null)
                    continue;
                if (!curve.Transform(Transform.PlanarProjection(Plane.WorldXY)) ||
                    !curve.IsValid || curve.GetLength() <= doc.ModelAbsoluteTolerance)
                {
                    curve.Dispose();
                    continue;
                }
                if (hidden)
                    hiddenCurves.Add((curve, kind, tag.InstancePath));
                else
                {
                    curves.Add((curve, kind, tag.InstancePath));
                    if (fill.CurrentValue && FeatureLinePolicy.ShouldDraw(kind))
                        boundaries.Add(curve);
                }
            }
            if (hiddenOnly)
            {
                try
                {
                    return HiddenLineOutput.Add(doc, curves.Select(c => c.Curve), hiddenCurves);
                }
                finally
                {
                    foreach (var entry in curves.Concat(hiddenCurves))
                        entry.Curve.Dispose();
                }
            }
            if (curves.Count == 0)
                return Result.Nothing;
            RhinoApp.WriteLine(UiText.Get("Drawing contains {0} visible segments from {1} model parts.",
                "已从 {1} 个构件提取 {0} 段可见线。", "{1} 個の部材から可視線を {0} 本抽出しました。"), curves.Count, leaves);
            if (detailParts.Count > 0)
            {
                int added = AppendDetailBoundaries(boundaries, detailParts, view, clips, drawing.WorldToHiddenLine,
                    doc.ModelAbsoluteTolerance);
                RhinoApp.WriteLine(UiText.Get("Fill refinement: {0} original projected curves from {1} detail parts.",
                    "细化填充时从 {1} 个构件补充了 {0} 条原始投影曲线。",
                    "塗りつぶしの詳細化に {1} 個の部材から投影曲線を {0} 本追加しました。"), added, detailParts.Count);
            }
            if (contacts.CurrentValue && detailParts.Count > 0)
            {
                int contactCount = AppendContactBoundaries(boundaries, detailParts, projectedView, clips, drawing.WorldToHiddenLine,
                    doc.ModelAbsoluteTolerance);
                RhinoApp.WriteLine(UiText.Get("Detail contact boundaries: {0}.", "局部接触边界 {0} 条。", "部材の接触境界は {0} 本です。"), contactCount);
            }
            int unresolved = 0;
            var fills = fill.CurrentValue
                ? VisibleFill.Compute(boundaries, parts, drawing.WorldToHiddenLine, projectedView, clips,
                    contacts.CurrentValue && detailParts.Count > 0 ? doc.ModelAbsoluteTolerance / 2.2 : doc.ModelAbsoluteTolerance, out unresolved)
                : new List<VisibleFill.Region>();
            var placed = RhinoGet.GetPoint(UiText.Get("Pick insertion point for the two-dimensional drawing",
                "指定二维图的插入点", "2D 図面の挿入点を指定"), false, out var point);
            if (placed != Result.Success)
                return placed;
            var box = BoundingBox.Empty;
            foreach (var entry in curves)
                box.Union(entry.Curve.GetBoundingBox(true));
            var move = Transform.Translation(point - box.Min);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var layers = new Dictionary<FeatureLine, int>();
            int count = 0;
            foreach (var entry in curves.OrderBy(g => g.Kind))
            {
                if (!layers.TryGetValue(entry.Kind, out int li))
                {
                    var layer = new Layer { Name = $"BetterMake2D-{stamp}-{entry.Kind}" };
                    li = doc.Layers.Add(layer);
                    if (li < 0)
                        return Result.Failure;
                    layers.Add(entry.Kind, li);
                }
                entry.Curve.Transform(move);
                var attributes = new ObjectAttributes { LayerIndex = li, Name = entry.Kind.ToString() };
                attributes.SetUserString("SourceInstance", entry.Source);
                if (doc.Objects.AddCurve(entry.Curve, attributes) != Guid.Empty)
                    count++;
            }
            int fillsAdded = 0;
            if (fills.Count > 0)
            {
                int fillLayer = doc.Layers.Add(new Layer { Name = $"BetterMake2D-{stamp}-ClosedFills" });
                int loopLayer = doc.Layers.Add(new Layer { Name = $"BetterMake2D-{stamp}-FillBoundaries", IsVisible = false });
                if (fillLayer < 0 || loopLayer < 0) return Result.Failure;
                foreach (var region in fills)
                {
                    region.Hatch.Transform(move);
                    var attr = new ObjectAttributes { LayerIndex = fillLayer, Name = "VisibleFill",
                        ColorSource = ObjectColorSource.ColorFromObject, ObjectColor = VisibleFill.ColorFor(region.Instance) };
                    attr.SetUserString("SourceInstance", region.Instance);
                    if (doc.Objects.AddHatch(region.Hatch, attr) != Guid.Empty)
                        fillsAdded++;
                    attr.LayerIndex = loopLayer;
                    foreach (var loop in region.Loops)
                    {
                        loop.Transform(move);
                        doc.Objects.AddCurve(loop, attr);
                    }
                }
            }
            doc.Views.Redraw();
            if (fill.CurrentValue)
                RhinoApp.WriteLine(UiText.Get("Closed fills: {0}; unresolved regions skipped: {1}.",
                    "生成闭合填充 {0} 块，跳过未能闭合的区域 {1} 块。",
                    "閉じた塗りつぶしは {0} 領域で、処理できなかった {1} 領域は省略しました。"), fillsAdded, unresolved);
            RhinoApp.WriteLine(UiText.Get("Added {0} curves. Parameter seams omitted: {1}.",
                "已添加 {0} 条曲线，省略了 {1} 条参数接缝线。", "曲線を {0} 本追加し、パラメータの継ぎ目を {1} 本省略しました。"),
                count, counters.GetValueOrDefault(FeatureLine.ParameterSeam));
            return count == curves.Count && fillsAdded == fills.Count ? Result.Success : Result.Failure;
        }

        private static int AppendContactBoundaries(List<Curve> boundaries, List<VisibleFill.Part> parts,
            ViewportInfo viewport, List<Plane> clips, Transform worldToDrawing, double tolerance)
        {
            var solids = parts.Where(part => part.Brep != null).Select(part =>
            {
                var brep = part.Brep.DuplicateBrep();
                brep.Transform(part.Placement);
                return (Brep: brep, Box: brep.GetBoundingBox(true));
            }).ToArray();
            int originalCount = boundaries.Count;
            var projection = Transform.PlanarProjection(Plane.WorldXY) * worldToDrawing;
            double intersectionTolerance = tolerance / 18;
            var perspectiveContacts = new List<Curve>();
            try
            {
                for (int i = 0; i < solids.Length; i++)
                for (int j = i + 1; j < solids.Length; j++)
                {
                    if (!BoundingBox.Intersection(solids[i].Box, solids[j].Box).IsValid)
                        continue;
                    if (!Rhino.Geometry.Intersect.Intersection.BrepBrep(solids[i].Brep, solids[j].Brep,
                            intersectionTolerance, out var curves, out _) || curves == null)
                        continue;
                    foreach (var curve in curves)
                    {
                        if (viewport.IsPerspectiveProjection)
                            perspectiveContacts.Add(curve);
                        else if (curve.Transform(projection) && curve.IsValid && curve.GetLength() > intersectionTolerance)
                            boundaries.Add(curve);
                        else
                            curve.Dispose();
                    }
                }
                if (perspectiveContacts.Count > 0)
                {
                    var settings = new HiddenLineDrawingParameters { AbsoluteTolerance = intersectionTolerance, Flatten = false };
                    settings.SetViewport(viewport);
                    foreach (var clip in clips) settings.AddClippingPlane(clip);
                    foreach (var curve in perspectiveContacts) settings.AddGeometry(curve, null);
                    using var contactDrawing = HiddenLineDrawing.Compute(settings, true);
                    AppendProjectedSegments(boundaries, contactDrawing, worldToDrawing, intersectionTolerance, null, filterFeatures: false);
                }
            }
            finally
            {
                foreach (var curve in perspectiveContacts) curve.Dispose();
                foreach (var solid in solids)
                    solid.Brep.Dispose();
            }
            return boundaries.Count - originalCount;
        }

        private static int AppendDetailBoundaries(List<Curve> boundaries, List<VisibleFill.Part> parts,
            Rhino.Display.RhinoViewport view, List<Plane> clips, Transform worldToDrawing, double tolerance)
        {
            int initialCount = boundaries.Count;
            var detailTolerance = tolerance / 4.5;
            foreach (var part in parts)
            {
                var settings = new HiddenLineDrawingParameters
                {
                    AbsoluteTolerance = detailTolerance,
                    Flatten = false,
                    IncludeHiddenCurves = false,
                    IncludeTangentEdges = true,
                    IncludeTangentSeams = false
                };
                settings.SetViewport(view);
                foreach (var clip in clips)
                    settings.AddClippingPlane(clip);
                if (!settings.AddGeometry(part.Geometry, part.Placement, part.Instance))
                    continue;

                using var projection = HiddenLineDrawing.Compute(settings, false);
                AppendProjectedSegments(boundaries, projection, worldToDrawing, detailTolerance / 4, part.Brep, filterFeatures: true);
            }
            return boundaries.Count - initialCount;
        }

        private static void AppendProjectedSegments(List<Curve> boundaries, HiddenLineDrawing drawing,
            Transform worldToDrawing, double tolerance, Brep sourceBrep, bool filterFeatures)
        {
            if (drawing == null || !drawing.WorldToHiddenLine.TryGetInverse(out var localToWorld))
                return;
            var toDrawing = Transform.PlanarProjection(Plane.WorldXY) * worldToDrawing * localToWorld;
            foreach (var segment in drawing.Segments)
            {
                if (segment.SegmentVisibility != HiddenLineDrawingSegment.Visibility.Visible)
                    continue;
                if (filterFeatures)
                {
                    var source = segment.ParentCurve;
                    var kind = FeatureLinePolicy.Classify(source.SilhouetteType, sourceBrep, source.SourceObjectComponentIndex);
                    if (!FeatureLinePolicy.ShouldDraw(kind, true))
                        continue;
                }
                var curve = segment.CurveGeometry.DuplicateCurve();
                if (curve.Transform(toDrawing) && curve.IsValid && curve.GetLength() > tolerance)
                    boundaries.Add(curve);
                else
                    curve.Dispose();
            }
        }

        private static int AddObject(HiddenLineDrawingParameters settings, RhinoObject obj,
            Transform transform, string path, List<VisibleFill.Part> parts)
        {
            if (obj == null || obj.IsHidden)
                return 0;
            path = string.IsNullOrEmpty(path) ? obj.Id.ToString() : path + "/" + obj.Id;
            if (obj is InstanceObject block)
                return block.InstanceDefinition.GetObjects()
                    .Sum(child => AddObject(settings, child, transform * block.InstanceXform, path, parts));

            var geometry = obj.Geometry;
            Brep brep = geometry as Brep ?? (geometry as Extrusion)?.ToBrep() ??
                (geometry as Surface)?.ToBrep();
            if (brep != null)
                geometry = brep;
            if (geometry == null || !settings.AddGeometry(geometry, transform, new SourceTag(brep, path)))
                return 0;
            parts.Add(new VisibleFill.Part(geometry, brep, transform, path));
            return 1;
        }

        private sealed record SourceTag(Brep Brep, string InstancePath);
    }

    [Guid("5EC98992-8936-44F4-A3B2-598C6E617B3E")]
    public sealed class HiddenLineCommand : Command
    {
        public override string EnglishName => "P2HiddenLines";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) => FeatureLineCommand.Project(doc, hiddenOnly: true);
    }

    [Guid("CFBB194C-2DF8-4B71-8339-12E5809F80D5")]
    public sealed class BetterMake2DCommand : Command
    {
        public override string EnglishName => "BetterMake2D";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) => FeatureLineCommand.Project(doc, hiddenOnly: false);
    }

    [Guid("03937D0B-82A4-4235-AD37-B01B0618904F")]
    public sealed class BetterMake2DHiddenCommand : Command
    {
        public override string EnglishName => "BetterMake2DHidden";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) => FeatureLineCommand.Project(doc, hiddenOnly: true);
    }
}
