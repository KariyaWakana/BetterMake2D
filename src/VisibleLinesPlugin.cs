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
            if (view == null || !view.IsParallelProjection)
            {
                RhinoApp.WriteLine("Use a parallel viewport.");
                return Result.Failure;
            }

            using var get = new GetObject();
            get.SetCommandPrompt("Select parts and any objects that should occlude them");
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
                detail.SetCommandPrompt("Select source parts to refine for closed fill");
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
                    RhinoApp.WriteLine("Refinement requires parts from the first selection.");
                    return Result.Failure;
                }
            }
            using var drawing = HiddenLineDrawing.Compute(settings, true);
            if (drawing == null)
                return Result.Failure;

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
            RhinoApp.WriteLine($"Drawing contains {curves.Count} visible segments from {leaves} model parts.");
            if (detailParts.Count > 0)
            {
                int added = AppendDetailBoundaries(boundaries, detailParts, view, clips, drawing.WorldToHiddenLine,
                    doc.ModelAbsoluteTolerance);
                RhinoApp.WriteLine($"Fill refinement: {added} original projected curves from {detailParts.Count} detail parts.");
            }
            if (contacts.CurrentValue && detailParts.Count > 0)
            {
                int contactCount = AppendContactBoundaries(boundaries, detailParts, drawing.WorldToHiddenLine, doc.ModelAbsoluteTolerance);
                RhinoApp.WriteLine($"Detail contact boundaries: {contactCount}.");
            }
            int unresolved = 0;
            var fills = fill.CurrentValue
                ? VisibleFill.Compute(boundaries, parts, drawing.WorldToHiddenLine, view.CameraDirection, clips,
                    contacts.CurrentValue && detailParts.Count > 0 ? doc.ModelAbsoluteTolerance / 2.2 : doc.ModelAbsoluteTolerance, out unresolved)
                : new List<VisibleFill.Region>();
            var placed = RhinoGet.GetPoint("Pick insertion point for the two-dimensional drawing", false, out var point);
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
                    var layer = new Layer { Name = $"P2VisibleLines-{stamp}-{entry.Kind}" };
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
                int fillLayer = doc.Layers.Add(new Layer { Name = $"P2VisibleLines-{stamp}-ClosedFills" });
                int loopLayer = doc.Layers.Add(new Layer { Name = $"P2VisibleLines-{stamp}-FillBoundaries", IsVisible = false });
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
                RhinoApp.WriteLine($"Closed fills: {fillsAdded}; unresolved regions skipped: {unresolved}.");
            RhinoApp.WriteLine($"Added {count} curves. Parameter seams omitted: {counters.GetValueOrDefault(FeatureLine.ParameterSeam)}.");
            return count == curves.Count && fillsAdded == fills.Count ? Result.Success : Result.Failure;
        }

        private static int AppendContactBoundaries(List<Curve> boundaries, List<VisibleFill.Part> parts,
            Transform worldToDrawing, double tolerance)
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
                        curve.Transform(projection);
                        if (curve.IsValid && curve.GetLength() > intersectionTolerance)
                            boundaries.Add(curve);
                        else
                            curve.Dispose();
                    }
                }
            }
            finally
            {
                foreach (var solid in solids)
                    solid.Brep.Dispose();
            }
            return boundaries.Count - originalCount;
        }

        private static int AppendDetailBoundaries(List<Curve> boundaries, List<VisibleFill.Part> parts,
            Rhino.Display.RhinoViewport view, List<Plane> clips, Transform worldToDrawing, double tolerance)
        {
            int initialCount = boundaries.Count;
            var origin = worldToDrawing * Point3d.Origin;
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
                if (projection == null)
                    continue;
                var localOrigin = projection.WorldToHiddenLine * Point3d.Origin;
                var toDrawing = Transform.PlanarProjection(Plane.WorldXY) * Transform.Translation(origin - localOrigin);
                foreach (var segment in projection.Segments)
                {
                    if (segment.SegmentVisibility != HiddenLineDrawingSegment.Visibility.Visible)
                        continue;
                    var source = segment.ParentCurve;
                    var kind = FeatureLinePolicy.Classify(source.SilhouetteType, part.Brep, source.SourceObjectComponentIndex);
                    if (!FeatureLinePolicy.ShouldDraw(kind, true))
                        continue;
                    var curve = segment.CurveGeometry.DuplicateCurve();
                    curve.Transform(toDrawing);
                    if (curve.IsValid && curve.GetLength() > detailTolerance / 4)
                        boundaries.Add(curve);
                }
            }
            return boundaries.Count - initialCount;
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
}
