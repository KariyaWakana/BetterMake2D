using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;

namespace Project2.VisibleLines
{
    internal static class HiddenLineOutput
    {
        internal static Result Add(RhinoDoc doc, IEnumerable<Curve> visible,
            IEnumerable<(Curve Curve, FeatureLine Kind, string Source)> hidden)
        {
            var reference = visible.ToList();
            var alignment = BoundingBox.Empty;
            using var index = new RTree();
            for (int i = 0; i < reference.Count; i++)
            {
                var bounds = reference[i].GetBoundingBox(true);
                alignment.Union(bounds);
                index.Insert(bounds, i);
            }
            if (!alignment.IsValid)
            {
                RhinoApp.WriteLine(UiText.Get("No visible projection to use as an insertion reference.",
                    "没有可用作插入基准的可见投影。", "挿入基準にできる可視線の投影がありません。"));
                return Result.Nothing;
            }

            var output = new List<(Curve Curve, FeatureLine Kind, string Source)>();
            int coincident = 0;
            foreach (var entry in hidden)
            {
                double tolerance = doc.ModelAbsoluteTolerance;
                var bounds = entry.Curve.GetBoundingBox(true);
                var search = bounds;
                search.Inflate(tolerance);
                var samples = Enumerable.Range(0, 9)
                    .Select(i => entry.Curve.PointAtNormalizedLength(i / 8.0)).ToArray();
                var covered = new bool[samples.Length];
                index.Search(search, (_, hit) =>
                {
                    var curve = reference[hit.Id];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        if (!covered[i] && curve.ClosestPoint(samples[i], out var t, tolerance) &&
                            curve.PointAt(t).DistanceTo(samples[i]) <= tolerance)
                            covered[i] = true;
                    }
                    if (covered.All(v => v))
                        hit.Cancel = true;
                });
                if (covered.All(v => v))
                {
                    coincident++;
                    continue;
                }
                output.Add(entry);
                index.Insert(bounds, reference.Count);
                reference.Add(entry.Curve);
            }
            if (output.Count == 0)
            {
                RhinoApp.WriteLine(UiText.Get("No separate hidden edges in the selected projection.",
                    "当前投影中没有独立的隐藏边。", "この投影には独立した隠線がありません。"));
                return Result.Nothing;
            }

            var result = RhinoGet.GetPoint(UiText.Get("Pick the same insertion point as the visible drawing",
                "指定与可见线图相同的插入点", "可視線図と同じ挿入点を指定"), false, out var point);
            if (result != Result.Success)
                return result;
            int linetype = FindDashPattern(doc);
            if (linetype < 0)
                return Result.Failure;
            var layer = new Layer
            {
                Name = $"BetterMake2DHidden-{DateTime.Now:yyyyMMdd-HHmmss-fff}-Hidden",
                Color = Color.FromArgb(145, 151, 155),
                LinetypeIndex = linetype,
                PlotWeight = .08
            };
            int layerIndex = doc.Layers.Add(layer);
            if (layerIndex < 0)
                return Result.Failure;

            var move = Transform.Translation(point - alignment.Min);
            int added = 0;
            foreach (var entry in output)
            {
                if (!entry.Curve.Transform(move))
                    continue;
                var attributes = new ObjectAttributes
                {
                    LayerIndex = layerIndex,
                    Name = entry.Kind.ToString(),
                    LinetypeSource = ObjectLinetypeSource.LinetypeFromLayer
                };
                attributes.SetUserString("SourceInstance", entry.Source);
                if (doc.Objects.AddCurve(entry.Curve, attributes) != Guid.Empty)
                    added++;
            }
            doc.Views.Redraw();
            RhinoApp.WriteLine(UiText.Get("Added {0} hidden curves. Coincident projections omitted: {1}.",
                "已添加 {0} 条隐藏线，省略了 {1} 条重合投影。", "隠線を {0} 本追加し、重複する投影線を {1} 本省略しました。"), added, coincident);
            return added == output.Count ? Result.Success : Result.Failure;
        }

        private static int FindDashPattern(RhinoDoc doc)
        {
            const double dash = 75;
            const double gap = 55;
            const string baseName = "Better Make2D Hidden 75-55";
            for (int number = 1; ; number++)
            {
                string name = number == 1 ? baseName : $"{baseName} {number}";
                int index = doc.Linetypes.Find(name);
                if (index < 0)
                    return doc.Linetypes.Add(name, new[] { dash, -gap });
                var existing = doc.Linetypes.FindIndex(index);
                if (existing.SegmentCount != 2)
                    continue;
                existing.GetSegment(0, out double existingDash, out bool firstDrawn);
                existing.GetSegment(1, out double existingGap, out bool secondDrawn);
                if (firstDrawn && !secondDrawn && Math.Abs(existingDash - dash) < dash * 1e-8 &&
                    Math.Abs(existingGap - gap) < gap * 1e-8)
                    return existing.Index;
            }
        }
    }
}
