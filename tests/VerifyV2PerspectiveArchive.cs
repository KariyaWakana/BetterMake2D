using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

// Audits a detached worker input against two independently reopened output files.
public static class VerifyV2PerspectiveArchive
{
    private sealed record FaceSample(BrepFace Face, string Owner);
    private sealed record RawSample(GeometryBase Shape, string Owner);
    private readonly record struct Readback(string[][] Owners, int Regions, int Lines, int Open, int Invalid);

    public static string Run()
    {
        string folder = Environment.GetEnvironmentVariable("BMA2D_PERSPECTIVE_AUDIT_SET") ??
            throw new ArgumentException("Set the detached perspective audit folder.");
        string previous = Environment.GetEnvironmentVariable("BMA2D_PERSPECTIVE_BASELINE") ??
            throw new ArgumentException("Set the baseline 2D drawing.");
        var view = JsonSerializer.Deserialize<DesktopRequest>(File.ReadAllText(Path.Combine(folder, "request.json"))).View;
        var probes = Environment.GetEnvironmentVariable("BMA2D_PERSPECTIVE_REPLAY") is string replay
            ? File.ReadLines(replay).Skip(1).Select(row => row.Split('\t')).Select(fields => new Point2d(
                double.Parse(fields[0], CultureInfo.InvariantCulture), double.Parse(fields[1], CultureInfo.InvariantCulture))).ToArray()
            : Probes(previous);
        var exact = Raw(Path.Combine(folder, "scene.3dm"), probes, view);
        var before = Read(previous, probes);
        var after = Read(Path.Combine(folder, "drawing.3dm"), probes);
        var summary = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using var log = File.CreateText(Path.Combine(folder, "independent-perspective-rays.tsv"));
        log.WriteLine("x\ty\traw\tprevious\tcurrent\tprevious status\tcurrent status");
        for (int i = 0; i < probes.Length; i++)
        {
            string a = Status(exact[i], before.Owners[i]);
            string b = Status(exact[i], after.Owners[i]);
            string transition = a + " => " + b;
            summary[transition] = summary.GetValueOrDefault(transition) + 1;
            log.WriteLine(string.Join('\t', probes[i].X.ToString("R", CultureInfo.InvariantCulture),
                probes[i].Y.ToString("R", CultureInfo.InvariantCulture), Join(exact[i]), Join(before.Owners[i]),
                Join(after.Owners[i]), a, b));
        }
        return $"Rhino reopened {after.Regions} regions, {after.Lines} lines, {after.Open} open loops and {after.Invalid} invalid objects. " +
            $"{probes.Length} independent archived perspective rays. " +
            string.Join("; ", summary.Select(item => $"{item.Key}: {item.Value}")) + ".";
    }

    private static Point2d[] Probes(string path)
    {
        using var file = File3dm.Read(path) ?? throw new IOException(path);
        var box = BoundingBox.Empty;
        var pieces = new List<BoundingBox>();
        foreach (var obj in file.Objects)
        {
            if (obj.Geometry is not Brep shape) continue;
            var bounds = shape.GetBoundingBox(true);
            box.Union(bounds);
            pieces.Add(bounds);
        }
        var result = new List<Point2d>();
        for (int y = 0; y < 38; y++)
            for (int x = 0; x < 70; x++)
                result.Add(new Point2d(box.Min.X + (x + .413) * box.Diagonal.X / 70,
                    box.Min.Y + (y + .617) * box.Diagonal.Y / 38));
        var sorted = pieces.OrderBy(box => box.Center.X).ThenBy(box => box.Center.Y).ToArray();
        for (int i = 0; i < Math.Min(900, sorted.Length); i++)
        {
            var piece = sorted[(int)((long)i * sorted.Length / Math.Min(900, sorted.Length))];
            result.Add(new Point2d(piece.Min.X + piece.Diagonal.X * .431, piece.Min.Y + piece.Diagonal.Y * .583));
        }
        return result.ToArray();
    }

    private static string[][] Raw(string path, Point2d[] probes, DesktopView view)
    {
        using var file = File3dm.Read(path) ?? throw new IOException(path);
        using var tree = new RTree();
        var shapes = new List<RawSample>();
        var owned = new List<Brep>();
        double tolerance = file.Settings.ModelAbsoluteTolerance;
        try
        {
            foreach (var obj in file.Objects)
            {
                var shape = obj.Geometry;
                if (shape is not (Brep or Mesh))
                {
                    var brep = Brep.TryConvertBrep(shape) ?? throw new InvalidOperationException($"Unsupported source object {shape.ObjectType}.");
                    owned.Add(brep);
                    shape = brep;
                }
                var box = shape.GetBoundingBox(true);
                double first = Math.Max(view.Near, -box.Max.Z), last = Math.Min(view.Far, -box.Min.Z);
                if (!box.IsValid || last < first) continue;
                double x0 = Math.Min(box.Min.X / first, box.Min.X / last) * view.Focal,
                    x1 = Math.Max(box.Max.X / first, box.Max.X / last) * view.Focal;
                double y0 = Math.Min(box.Min.Y / first, box.Min.Y / last) * view.Focal,
                    y1 = Math.Max(box.Max.Y / first, box.Max.Y / last) * view.Focal;
                tree.Insert(new BoundingBox(new Point3d(x0, y0, 0), new Point3d(x1, y1, 0)), shapes.Count);
                shapes.Add(new RawSample(shape, obj.Attributes.GetUserString("BetterMake2D.Source") ?? ""));
            }
            var hits = new List<(double Depth, string Owner)>();
            var candidates = new List<int>();
            var result = new string[probes.Length][];
            for (int i = 0; i < probes.Length; i++)
            {
                var p = probes[i];
                var line = new Line(At(view.Near), At(view.Far));
                using var curve = new LineCurve(line);
                candidates.Clear();
                tree.Search(Window(p, 1e-8), (_, hit) => candidates.Add(hit.Id));
                hits.Clear();
                foreach (int id in candidates)
                {
                    var item = shapes[id];
                    Point3d[] points;
                    if (item.Shape is Mesh mesh) points = Intersection.MeshLine(mesh, line);
                    else
                    {
                        Intersection.CurveBrep(curve, (Brep)item.Shape, tolerance * .01, out var overlaps, out points);
                        foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                    }
                    foreach (var hit in points ?? Array.Empty<Point3d>())
                    {
                        if (hit.Z > -view.Near || hit.Z < -view.Far) continue;
                        if (view.Sections.Any(section => section.Plane.DistanceTo(hit) > 1e-10)) continue;
                        hits.Add((hit.Z, item.Owner));
                    }
                }
                double closest = hits.Count == 0 ? double.NegativeInfinity : hits.Max(item => item.Depth);
                result[i] = Owners(hits.Where(item => closest - item.Depth <= tolerance * .1).Select(item => item.Owner));

                Point3d At(double distance) => new(p.X * (distance / view.Focal), p.Y * (distance / view.Focal), -distance);
            }
            return result;
        }
        finally { foreach (var shape in owned) shape.Dispose(); }
    }

    private static Readback Read(string path, Point2d[] probes)
    {
        using var file = File3dm.Read(path) ?? throw new IOException(path);
        using var tree = new RTree();
        var entries = new List<FaceSample>();
        int regions = 0, lines = 0, open = 0, invalid = 0;
        foreach (var obj in file.Objects)
        {
            var geometry = obj.Geometry;
            if (!geometry.IsValid) invalid++;
            if (geometry is Curve) { lines++; continue; }
            if (geometry is not Brep shape) throw new InvalidOperationException($"Unexpected 2D object {geometry.ObjectType}.");
            regions++;
            foreach (var loop in shape.Loops)
            {
                using var rim = loop.To3dCurve();
                if (rim is not {IsClosed: true}) open++;
            }
            foreach (var face in shape.Faces)
            {
                tree.Insert(face.GetBoundingBox(true), entries.Count);
                entries.Add(new FaceSample(face, obj.Attributes.GetUserString("SourceObject") ?? ""));
            }
        }
        var result = new string[probes.Length][];
        for (int i = 0; i < probes.Length; i++)
        {
            var p = new Point3d(probes[i].X, probes[i].Y, 0);
            var hits = new List<string>();
            tree.Search(Window(probes[i], 1e-9), (_, hit) =>
            {
                var entry = entries[hit.Id];
                if (entry.Face.ClosestPoint(p, out double u, out double v) &&
                    entry.Face.IsPointOnFace(u, v, 1e-9) == PointFaceRelation.Interior) hits.Add(entry.Owner);
            });
            result[i] = Owners(hits);
        }
        return new Readback(result, regions, lines, open, invalid);
    }

    private static BoundingBox Window(Point2d p, double fit) => new(new Point3d(p.X - fit, p.Y - fit, -fit),
        new Point3d(p.X + fit, p.Y + fit, fit));
    private static string[] Owners(IEnumerable<string> ids) => ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static string Join(string[] owners) => string.Join(",", owners);
    private static string Status(string[] exact, string[] result)
    {
        if (exact.Length == 0 && result.Length == 0 || result.Length == 1 && exact.Contains(result[0])) return "correct";
        if (result.Length == 0) return "missing";
        if (result.Length > 1 && result.Any(exact.Contains)) return "overlap";
        return "wrong";
    }
}
