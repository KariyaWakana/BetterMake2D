using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2NestedOutput
{
    public static string Run()
    {
        using var doc = RhinoDoc.CreateHeadless(null);
        int layer = doc.Layers.Add("Structure", System.Drawing.Color.Black);
        using var attr = new ObjectAttributes { LayerIndex = layer };
        using var shape = Brep.CreateFromBox(new BoundingBox(new Point3d(1, 1, -1), new Point3d(3, 2, 1)));
        using var guide = new LineCurve(Point3d.Origin, new Point3d(0, 0, 2));
        int child = doc.InstanceDefinitions.Add("Part", "", Point3d.Origin, new GeometryBase[] { shape, guide },
            new[] { attr, attr });
        var rotation = Transform.Rotation(.72, Vector3d.ZAxis, Point3d.Origin);
        using var nested = new InstanceReferenceGeometry(doc.InstanceDefinitions[child].Id, rotation);
        int parent = doc.InstanceDefinitions.Add("Assembly", "", Point3d.Origin, nested, attr);
        var mirror = Transform.Mirror(Plane.WorldYZ);
        Guid id = doc.Objects.AddInstanceObject(parent, mirror, attr);
        var sources = new List<ProjectionObject>();
        var layers = new Dictionary<string, int>(StringComparer.Ordinal);
        BetterMake2DCommand.Collect(doc.Objects.FindId(id), Transform.Identity, "", Transform.Identity, sources, layers);
        if (sources.Count != 1 || sources[0].Source.Split('/').Length != 3 || layers[sources[0].Source] != layer)
            throw new InvalidOperationException("Nested entity provenance or original layer lost.");
        var placed = ProjectionPipeline.ReadObjects(sources);
        try
        {
            var expected = (mirror * rotation) * new Point3d(2, 1.5, 0);
            using var drawing = V2DrawingSolver.Solve(placed, .005, .02);
            if (!drawing.Regions.Any(region => region.Brep.Faces[0].ClosestPoint(expected, out double u, out double v) &&
                region.Brep.Faces[0].IsPointOnFace(u, v) == PointFaceRelation.Interior))
                throw new InvalidOperationException("Rotated, mirrored block projected in the wrong place.");
            string source = sources[0].Source;
            drawing.Curves.Add(new DrawnEdge(new LineCurve(new Point3d(-1, 1, 0), new Point3d(-1, 2, 0)),
                source, 0, "SectionCut", "", .005));
            drawing.Curves.Add(new DrawnEdge(new LineCurve(new Point3d(-2, 1, 0), new Point3d(-2, 2, 0)),
                source, 0, "Original", "", .005) { IsHidden = true });
            Guid[] beforeTypes = ActiveLineTypes();
            int checks = 0;
            try
            {
                BetterMake2DCommand.WriteV2Output(doc, drawing, Transform.Identity, layers, () =>
                {
                    if (++checks == 2) throw new OperationCanceledException();
                });
                throw new InvalidOperationException("Output ignored cancellation.");
            }
            catch (OperationCanceledException)
            {
                bool geometry = doc.Objects.Any(item => !item.IsDeleted && item.Attributes.GetUserString("SourceObject") == source);
                bool outputLayers = doc.Layers.Any(item => !item.IsDeleted && item.Name.StartsWith("BetterMake2D-", StringComparison.Ordinal));
                bool lineType = !beforeTypes.SequenceEqual(ActiveLineTypes());
                if (geometry || outputLayers || lineType)
                    throw new InvalidOperationException($"Rollback retained geometry={geometry}, layers={outputLayers}, line type={lineType}.");
            }
            BetterMake2DCommand.WriteV2Output(doc, drawing, Transform.Identity, layers);
            var written = doc.Objects.Where(item => item.Attributes.GetUserString("SourceObject") == source).ToArray();
            var hidden = written.Single(item => item.Attributes.GetUserString("Visibility") == "Hidden");
            using var linePattern = hidden.Attributes.GetCustomLinetype();
            if (linePattern == null || linePattern.SegmentCount != 2)
                throw new InvalidOperationException("Hidden output lost its per-object dash pattern.");
            var paths = written.Select(item => doc.Layers[item.Attributes.LayerIndex].FullPath).ToArray();
            foreach (var style in new[] { "ClosedRegions", "VisibleLines", "SectionCuts", "HiddenLines" })
                if (!paths.Any(path => path.EndsWith($"::{style}::Structure", StringComparison.Ordinal)))
                    throw new InvalidOperationException($"Missing original layer below {style}.");
            return $"PASS mirrored and rotated nested block, four output branches, {paths.Length} written objects, and Esc rollback.";

            Guid[] ActiveLineTypes() => doc.Linetypes.Where(item => !item.IsDeleted).Select(item => item.Id).Order().ToArray();
        }
        finally { foreach (var input in placed) input.Dispose(); }
    }
}
