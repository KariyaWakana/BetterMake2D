using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2DetailProbes
{
    private sealed record Probe(string Label, Point2d Point);

    public static string Run()
    {
        string probeFile = Environment.GetEnvironmentVariable("BMA2D_V2_DETAIL_PROBES") ?? throw new ArgumentException("Probes");
        var probes = File.ReadLines(probeFile).Skip(1).Select(row => row.Split('\t')).Select(c =>
            new Probe(c[0], new Point2d(double.Parse(c[1], CultureInfo.InvariantCulture),
                double.Parse(c[2], CultureInfo.InvariantCulture)))).ToArray();
        string scene = Environment.GetEnvironmentVariable("BMA2D_V2_SOURCE");
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(scene))
            lines.AddRange(OriginalFaceStages.WithSavedFaces(scene, (file, inputs, _, _, _) =>
                Original(inputs, probes, file.Settings.ModelAbsoluteTolerance)));
        var drawing = Environment.GetEnvironmentVariable("BMA2D_V2_DETAIL_DRAWING");
        if (!string.IsNullOrWhiteSpace(drawing))
            lines.AddRange(Drawing(drawing, probes));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string VerifyLineWitnesses(string scene, string before, string after, IReadOnlyList<string> originalFaces)
    {
        var probes = OriginalFaceStages.WithSavedFaces(scene, (file, inputs, _, _, _) =>
        {
            var result = new List<Probe>();
            foreach (string label in originalFaces)
            {
                int separator = label.LastIndexOf('/');
                var input = inputs.Single(input => input.Source == label[..separator]);
                var face = input.Brep.Faces[int.Parse(label[(separator + 1)..], CultureInfo.InvariantCulture)];
                double height = face.GetBoundingBox(true).Diagonal.Y;
                foreach (int id in face.AdjacentEdges())
                {
                    var edge = input.Brep.Edges[id];
                    if (Math.Abs(edge.PointAtStart.Y - edge.PointAtEnd.Y) < height * .5) continue;
                    foreach (double fraction in new[] {.2, .35, .5, .65, .8})
                    {
                        var point = edge.PointAt(edge.Domain.ParameterAt(fraction));
                        result.Add(new Probe($"{label} edge {id}", new Point2d(point.X, point.Y)));
                    }
                }
            }
            return result.ToArray();
        });
        var old = Drawing(before, probes, lineToleranceMultiplier: .2);
        var current = Drawing(after, probes, lineToleranceMultiplier: .2);
        return string.Join(Environment.NewLine, Enumerable.Range(0, probes.Length).GroupBy(i => probes[i].Label)
            .Select(g => $"{g.Key}: {g.Count(i => old[i].Contains("line:"))}/{g.Count()} before, " +
                $"{g.Count(i => current[i].Contains("line:"))}/{g.Count()} after."));
    }

    private static IReadOnlyList<string> Original(IReadOnlyList<Input> inputs, Probe[] probes, double fit)
    {
        var zone = new Rect(probes.Min(probe => probe.Point.X), probes.Min(probe => probe.Point.Y),
            probes.Max(probe => probe.Point.X), probes.Max(probe => probe.Point.Y)).Grow(fit);
        var extent = BoundingBox.Empty;
        var faces = new List<V2Face>();
        foreach (var input in inputs.OrderBy(input => input.Source, StringComparer.Ordinal))
        {
            var box = input.Brep.GetBoundingBox(false);
            extent.Union(box);
            if (!Rect.From(box).Grow(fit).Intersection(zone).HasArea) continue;
            foreach (var face in input.Brep.Faces)
            {
                var bounds = face.GetBoundingBox(true);
                var rect = Rect.From(bounds).Grow(fit);
                if (!probes.Any(probe => rect.Left <= probe.Point.X && probe.Point.X <= rect.Right &&
                    rect.Bottom <= probe.Point.Y && probe.Point.Y <= rect.Top))
                    continue;
                Plane? plane = face.TryGetPlane(out var found, fit * .01) ? found : null;
                if (plane.HasValue && Math.Abs(plane.Value.Normal.Z) <= 1e-10) continue;
                faces.Add(new V2Face(new SourceFaceId(input.Source, face.FaceIndex), face, bounds, plane, input.FromMesh));
            }
        }
        var camera = new V2ParallelCamera(extent.Min.Z - fit * 4, extent.Max.Z + fit * 4);
        using var index = new V2DepthIndex(faces.ToArray(), camera, fit);
        var result = new List<string>();
        var inspect = Environment.GetEnvironmentVariable("BMA2D_V2_DETAIL_FACES")?.Split(',');
        foreach (var entry in faces.Where(face => inspect?.Contains(Label(face.Id)) == true))
        {
            double projectedArea = index.ProjectedArea(faces.IndexOf(entry));
            result.Add($"Face {Label(entry.Id)} {entry.Face.UnderlyingSurface().GetType().Name}; {entry.Box}; effective projected area {projectedArea:R}.");
            foreach (var loop in entry.Face.Loops)
            {
                using var rim = loop.To3dCurve();
                using var projected = rim == null ? null : camera.ProjectCurve(rim);
                using var self = projected == null ? null : Intersection.CurveSelf(projected, fit * .01);
                using var area = projected is { IsClosed: true } ? AreaMassProperties.Compute(projected, fit * .01) : null;
                result.Add($"  {loop.LoopType} projected closed={projected?.IsClosed}, self={self?.Count}, area={area?.Area}.");
                foreach (var trim in loop.Trims)
                {
                    if (trim.Edge is not BrepEdge edge) continue;
                    using var curve = camera.ProjectCurve(edge);
                    if (curve == null) { result.Add($"  Edge {edge.EdgeIndex}: projection failed."); continue; }
                    using var discretized = curve.ToPolyline(fit * .05, .35, 0, 0);
                    result.Add($"  Edge {edge.EdgeIndex} {trim.TrimType}: {curve.PointAtStart} - {curve.PointAtEnd}, " +
                        $"box {curve.GetBoundingBox(true)}, sampled box {discretized?.GetBoundingBox(true)}, " +
                        $"closed={curve.IsClosed}, linear={curve.IsLinear(fit * .025)}.");
                }
            }
            foreach (var silhouette in V2Silhouettes.Read(entry.Face, camera, fit * .05, .001))
                using (silhouette)
                    result.Add($"  Silhouette {silhouette.PointAtStart} - {silhouette.PointAtEnd}; " +
                        $"{silhouette.GetBoundingBox(true)}.");
        }
        foreach (var probe in probes)
        {
            var (owner, uncertain) = index.Classify(probe.Point);
            using var ray = new LineCurve(camera.ViewRay(probe.Point));
            var depths = new List<(string Label, double Depth, bool Plane, double X, double Y, double Residual)>();
            foreach (var face in faces)
            {
                var bounds = Rect.From(face.Box).Grow(fit);
                if (probe.Point.X < bounds.Left || probe.Point.X > bounds.Right ||
                    probe.Point.Y < bounds.Bottom || probe.Point.Y > bounds.Top) continue;
                Intersection.CurveBrepFace(ray, face.Face, fit * .02, out var overlaps, out var hits);
                foreach (var curve in overlaps ?? Array.Empty<Curve>()) curve.Dispose();
                if (hits is not { Length: > 0 }) continue;
                var hit = hits.MaxBy(p => p.Z);
                bool closest = face.Face.ClosestPoint(hit, out double u, out double v);
                var normal = closest ? face.Face.NormalAt(u, v) : Vector3d.Zero;
                double residual = closest ? face.Face.PointAt(u, v).DistanceTo(hit) : double.PositiveInfinity;
                depths.Add((Label(face.Id), hit.Z, face.Plane.HasValue, normal.X / normal.Z, normal.Y / normal.Z, residual));
            }
            string tagged = owner < 0 ? "-" : Label(faces[owner].Id);
            string nearby = depths.OrderByDescending(hit => hit.Depth).Take(4).Select(hit =>
                FormattableString.Invariant($"{hit.Label}@{hit.Depth:R}{(hit.Plane ? "p" : "s")}({hit.X:G7},{hit.Y:G7}; {hit.Residual:G4})"))
                .Aggregate("", (all, next) => all.Length == 0 ? next : $"{all}, {next}");
            result.Add(FormattableString.Invariant(
                $"{probe.Label}\t{probe.Point.X:R}\t{probe.Point.Y:R}\t{tagged}\t{uncertain}\t{nearby}"));
        }
        return result;
    }

    private static IReadOnlyList<string> Drawing(string drawingPath, Probe[] probes, double lineToleranceMultiplier = .01)
    {
        using var model = File3dm.Read(drawingPath) ?? throw new IOException(drawingPath);
        double fit = model.Settings.ModelAbsoluteTolerance * .01;
        double lineTolerance = model.Settings.ModelAbsoluteTolerance * lineToleranceMultiplier;
        bool strict = Environment.GetEnvironmentVariable("BMA2D_V2_DETAIL_STRICT") == "1";
        var found = new List<string>[probes.Length];
        using var probeIndex = new RTree();
        for (int i = 0; i < probes.Length; i++)
        {
            var point = Point(i);
            probeIndex.Insert(new BoundingBox(point, point), i);
            found[i] = new List<string>();
        }
        foreach (var item in model.Objects)
        {
            string owner = item.Attributes.GetUserString("SourceObject") + "/" + item.Attributes.GetUserString("SourceFace");
            if (item.Geometry is Brep brep)
            {
                probeIndex.Search(brep.GetBoundingBox(false), (_, hit) =>
                {
                    var p = Point(hit.Id);
                    if (!VerifyV2Drawing.Contains(brep, p, fit)) return;
                    if (!strict)
                    {
                        found[hit.Id].Add(owner);
                        return;
                    }
                    var face = brep.Faces[0];
                    if (!face.ClosestPoint(p, out double u, out double v)) return;
                    var relation = face.IsPointOnFace(u, v, 1e-10);
                    double distance = double.PositiveInfinity;
                    foreach (var loop in face.Loops)
                    {
                        using var rim = loop.To3dCurve();
                        if (rim != null && rim.ClosestPoint(p, out double t))
                            distance = Math.Min(distance, rim.PointAt(t).DistanceTo(p));
                    }
                    found[hit.Id].Add(FormattableString.Invariant($"{owner}:{relation}:{distance:R}mm"));
                });
            }
            else if (item.Geometry is Curve curve)
            {
                var box = curve.GetBoundingBox(false);
                box.Inflate(lineTolerance);
                probeIndex.Search(box, (sender, hit) =>
                {
                    if (curve.ClosestPoint(Point(hit.Id), out _, lineTolerance)) found[hit.Id].Add("line:" + owner);
                });
            }
        }
        var result = new List<string>();
        for (int i = 0; i < probes.Length; i++)
            result.Add(FormattableString.Invariant(
                $"{probes[i].Label}\t{probes[i].Point.X:R}\t{probes[i].Point.Y:R}\t{string.Join(",", found[i].Order())}"));
        return result;

        Point3d Point(int index) => new(probes[index].Point.X, probes[index].Point.Y, 0);
    }

    private static string Label(SourceFaceId id) => FormattableString.Invariant($"{id.Source}/{id.FaceIndex}");
}

public static class VerifyV2DetailLines
{
    public static string Run()
    {
        string Required(string name) => Environment.GetEnvironmentVariable(name) ?? throw new ArgumentException(name);
        return VerifyV2DetailProbes.VerifyLineWitnesses(Required("BMA2D_V2_SOURCE"),
            Required("BMA2D_V2_DETAIL_BASELINE"), Required("BMA2D_V2_DETAIL_DRAWING"),
            Required("BMA2D_V2_DETAIL_FACES").Split(','));
    }
}
