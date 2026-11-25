using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2RayUncertainty
{
    public static string Run()
    {
        string source = Environment.GetEnvironmentVariable("BMA2D_V2_SOURCE") ?? throw new ArgumentException("Source");
        string issues = Environment.GetEnvironmentVariable("BMA2D_V2_ISSUES") ?? throw new ArgumentException("Issues");
        const string prefix = "Uncertain owner at ";
        var points = File.ReadLines(issues).Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => line[prefix.Length..].TrimEnd('.').Split(','))
            .Select(c => new Point3d(double.Parse(c[0], CultureInfo.InvariantCulture),
                double.Parse(c[1], CultureInfo.InvariantCulture), 0)).Distinct().ToArray();
        int count = Math.Min(48, points.Length);
        return OriginalFaceStages.WithSavedFaces(source, (file, inputs, _, _, _) =>
        {
            var extent = BoundingBox.Empty;
            var groups = new List<(SourceFaceId Id, BrepFace Face, BoundingBox Box)>[count];
            for (int i = 0; i < count; i++) groups[i] = new();
            using var probes = new RTree();
            double fit = file.Settings.ModelAbsoluteTolerance;
            for (int i = 0; i < count; i++)
                probes.Insert(new BoundingBox(points[i * points.Length / count], points[i * points.Length / count]), i);
            foreach (var input in inputs)
                foreach (var face in input.Brep.Faces)
                {
                    var box = face.GetBoundingBox(true);
                    if (!box.IsValid || !Rect.From(box).HasArea) continue;
                    extent.Union(box);
                    probes.Search(Rect.From(box).Grow(fit).Box, (_, hit) =>
                        groups[hit.Id].Add((new SourceFaceId(input.Source, face.FaceIndex), face, box)));
                }
            int failed = 0, glancing = 0, both = 0, noEvidence = 0;
            var rows = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var p = points[i * points.Length / count];
                using var ray = new LineCurve(new Point3d(p.X, p.Y, extent.Max.Z + fit * 4),
                    new Point3d(p.X, p.Y, extent.Min.Z - fit * 4));
                double front = double.NegativeInfinity;
                var tentative = new List<(SourceFaceId Id, double Max, bool Failed, bool Overlap)>();
                foreach (var (id, face, box) in groups[i].OrderByDescending(entry => entry.Box.Max.Z))
                {
                    if (box.Max.Z < front - fit) break;
                    if (face.TryGetPlane(out var plane, fit * .01))
                    {
                        if (Math.Abs(plane.Normal.Z) <= 1e-6) continue;
                        var n = plane.Normal;
                        var sample = new Point3d(p.X, p.Y, plane.Origin.Z -
                            (n.X * (p.X - plane.Origin.X) + n.Y * (p.Y - plane.Origin.Y)) / n.Z);
                        if (face.ClosestPoint(sample, out double u, out double v) &&
                            face.PointAt(u, v).DistanceTo(sample) <= fit &&
                            face.IsPointOnFace(u, v, fit * .01) != PointFaceRelation.Exterior)
                            front = Math.Max(front, sample.Z);
                        continue;
                    }
                    bool ok = Intersection.CurveBrepFace(ray, face, fit * .02, out var overlaps, out var hits);
                    bool overlap = overlaps is { Length: > 0 };
                    foreach (var curve in overlaps ?? Array.Empty<Curve>()) curve.Dispose();
                    if (!ok || overlap) tentative.Add((id, box.Max.Z, !ok, overlap));
                    foreach (var hit in hits ?? Array.Empty<Point3d>()) front = Math.Max(front, hit.Z);
                }
                var relevant = tentative.Where(item => item.Max >= front - fit).ToArray();
                bool f = relevant.Any(hit => hit.Failed), o = relevant.Any(hit => hit.Overlap);
                if (f && o) both++;
                else if (f) failed++;
                else if (o) glancing++;
                else noEvidence++;
                rows.Add(FormattableString.Invariant($"{p.X:R},{p.Y:R} native failure={f}, overlap={o}; ") +
                    string.Join(", ", relevant.Select(hit => $"{hit.Id.Source}/{hit.Id.FaceIndex}")));
            }
            return $"Sampled {count} of {points.Length} unique uncertain cells. " +
                $"{failed} native failure only; {glancing} native overlap only; {both} both; {noEvidence} neither reproduced." +
                Environment.NewLine + string.Join(Environment.NewLine, rows);
        });
    }
}
