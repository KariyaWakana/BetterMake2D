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
                RhinoApp.WriteLine("No visible projection to use as an insertion reference.");
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
                RhinoApp.WriteLine("No separate hidden edges in the selected projection.");
                return Result.Nothing;
            }

            var result = RhinoGet.GetPoint("Pick the same insertion point as the visible drawing", false, out var point);
            if (result != Result.Success)
                return result;
            int linetype = FindDashPattern(doc);
            if (linetype < 0)
                return Result.Failure;
            var layer = new Layer
            {
                Name = $"P2HiddenLines-{DateTime.Now:yyyyMMdd-HHmmss-fff}-Hidden",
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
            RhinoApp.WriteLine($"Added {added} hidden curves. Coincident projections omitted: {coincident}.");
            return added == output.Count ? Result.Success : Result.Failure;
        }

        private static int FindDashPattern(RhinoDoc doc)
        {
            const double dash = 75;
            const double gap = 55;
            const string baseName = "P2 Hidden 75-55";
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
