using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2WidePerspective
{
    private const double Near = 1, Far = 80, Focal = 20, Fit = .003;
    private static readonly Rect Frame = new(-.9, -.85, 1.35, 1.1);

    public static string Run()
    {
        var camera = new V2PerspectiveCamera(Near, Far, Focal, nearWindow: Frame);
        using var ground = new Mesh();
        const int columns = 192, rows = 52;
        for (int y = 0; y <= rows; y++)
            for (int x = 0; x <= columns; x++)
            {
                double z = -.25 - 1.2 * y;
                ground.Vertices.Add(3d * (x - columns / 2), -4 + .015 * z, z);
            }
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int a = y * (columns + 1) + x;
                ground.Faces.AddFace(a, a + 1, a + columns + 2, a + columns + 1);
            }
        using var road = Brep.CreateFromCornerPoints(new(.16, -.68, -.2), new(2.8, -.68, -.2),
            new(6.3, -4.65, -19), new(1.2, -4.65, -19), Fit);
        using var storey = Box(-4, -3.7, -18, 1, 1.4, -12);
        using var attic = Box(-3.2, 1.4, -18, .8, 3.1, -13);
        using var rear = Box(-1, -3.8, -24, 4, 2.2, -19);
        using var strip = Brep.CreateFromCornerPoints(new(5, 2, -14), new(5.05, 2, -14),
            new(5.05, 5, -14), new(5, 5, -14), Fit * .01);
        using var outside = new Brep();
        for (int i = 0; i < 96; i++)
        {
            double x = (i % 2 == 0 ? 1 : -1) * (52 + i * .4);
            using var box = Box(x, -3, -21, x + .3, 5, -13);
            outside.Append(box);
        }
        using var offscreen = Box(70, -3, -17, 74, 3, -11);
        using var column = new Cylinder(new Circle(new Plane(new Point3d(-4.9, -3.75, -11),
            Vector3d.YAxis), .65), 6).ToBrep(true, true);
        AttachRenderMesh(column.Faces[0]);
        var visible = new[] { new Input(null, "ground") { NativeMesh = ground },
            new(road, "road"), new(storey, "storey"), new(attic, "attic"), new(rear, "rear"), new(strip, "strip"), new(column, "column") };
        var inputs = visible.Concat(new[] { new Input(outside, "outside-faces"), new Input(offscreen, "outside-object") }).ToArray();
        var watch = Stopwatch.StartNew();
        using var drawing = V2DrawingSolver.Solve(inputs, Fit, .025, projection: camera);
        double elapsed = watch.Elapsed.TotalMilliseconds;
        Require(drawing.InputFaces < drawing.SupportFaces / 5 && drawing.RejectedViewTriangles > drawing.SourceMeshTriangles * .65,
            $"Insufficient front-end culling: {drawing.InputFaces}/{drawing.SupportFaces} faces, " +
            $"{drawing.RejectedViewTriangles}/{drawing.SourceMeshTriangles} mesh triangles");
        Require(drawing.Regions.Count >= 5 && drawing.Regions.All(region => region.Brep.IsValid &&
            region.Brep.Loops.All(loop => { using var curve = loop.To3dCurve(); return curve.IsClosed; })) &&
            drawing.Regions.Any(region => region.Source == "strip") &&
            drawing.Curves.Any(curve => curve.Boundary == "ViewClip") && drawing.Curves.All(curve => curve.Boundary != "SectionCut"),
            "The crop has an invalid fill, lost strip, or incorrect boundary semantics. " + string.Join(" | ", drawing.Issues.Take(8)));
        Require(drawing.Regions.All(region =>
        {
            var b = region.Brep.GetBoundingBox(true);
            return b.Min.X >= Frame.Left * Focal - drawing.ImageGrid * 2 && b.Max.X <= Frame.Right * Focal + drawing.ImageGrid * 2 &&
                b.Min.Y >= Frame.Bottom * Focal - drawing.ImageGrid * 2 && b.Max.Y <= Frame.Top * Focal + drawing.ImageGrid * 2;
        }), "A fill escaped the asymmetric viewport");
        using (var control = V2DrawingSolver.Solve(visible, Fit, .025, projection: camera))
            Require(drawing.Contacts == control.Contacts && drawing.MeshContactPairs == control.MeshContactPairs &&
                drawing.Segments == control.Segments, "Out-of-frustum originals entered contact or 2D arrangement");
        int rays = 0;
        foreach (var point in ProbePoints())
        {
            var line = new Line(new Point3d(point.X * Near / Focal, point.Y * Near / Focal, -Near),
                new Point3d(point.X * Far / Focal, point.Y * Far / Focal, -Far));
            using var ray = new LineCurve(line);
            double depth = double.NegativeInfinity;
            string expected = null;
            foreach (var input in visible)
            {
                if (input.NativeMesh != null) Add(Intersection.MeshLine(input.NativeMesh, line), input.Source);
                else
                {
                    Intersection.CurveBrep(ray, input.Brep, Fit * .005, out var overlaps, out var hits);
                    foreach (var curve in overlaps ?? Array.Empty<Curve>()) curve.Dispose();
                    Add(hits, input.Source);
                }
            }
            var owners = drawing.Regions.Where(region => region.Brep.Faces[0] is BrepFace face &&
                face.ClosestPoint(new Point3d(point.X, point.Y, 0), out double u, out double v) &&
                face.IsPointOnFace(u, v, 1e-9) == PointFaceRelation.Interior).Select(region => region.Source).ToArray();
            Require(owners.Length < 2 && owners.SingleOrDefault() == expected,
                $"Independent wide ray {point}: expected {expected}, got {string.Join(",", owners)}. " +
                string.Join(" | ", drawing.Issues.Take(8)));
            rays++;

            void Add(Point3d[] hits, string source)
            {
                foreach (var hit in hits ?? Array.Empty<Point3d>())
                    if (hit.Z >= -Far && hit.Z <= -Near && hit.Z > depth)
                        (depth, expected) = (hit.Z, source);
            }
        }
        var guarded = new V2PerspectiveCamera(Near, Far, Focal);
        Require(guarded.ViewClips.Discards(new BoundingBox(new Point3d(500, -1, -1.5), new Point3d(501, 1, -.3))),
            "Disabling viewport crop exposed the near-eye singularity");
        var boxAtDepth = new BoundingBox(new Point3d(-10, -10, -Far), new Point3d(10, 10, -Near));
        var zoom = new V2PerspectiveCamera(Near, Far, Focal * 4, nearWindow: Frame);
        double tolerance = camera.ImageTolerance(.055, boxAtDepth, 1);
        Require(Math.Abs(zoom.ImageTolerance(.055, boxAtDepth, 1) / tolerance - 4) < 1e-12 &&
            tolerance <= .05 * (Focal / Far) * (1 + 1e-12), "Image tolerance did not scale with zoom or protect 0.05 mm");
        return $"PASS wide perspective: {drawing.SupportFaces - drawing.InputFaces}/{drawing.SupportFaces} originals kept out of contact, " +
            $"{drawing.RejectedViewTriangles}/{drawing.SourceMeshTriangles} native triangles discarded, " +
            $"{drawing.Contacts} Brep calls, {rays} independent rays with zero differences, {drawing.Regions.Count} closed valid fills, " +
            $"{drawing.ImageGrid:G5} image grid, {drawing.Segments} segments, {elapsed:F1} ms. {VerifyHints(camera)}";

        IEnumerable<Point2d> ProbePoints()
        {
            for (int y = 0; y < 31; y++)
                for (int x = 0; x < 39; x++)
                    yield return new Point2d(Focal * (Frame.Left + (Frame.Right - Frame.Left) * (x + .317) / 39),
                        Focal * (Frame.Bottom + (Frame.Top - Frame.Bottom) * (y + .473) / 31));
            yield return camera.Project(new Point3d(5.025, 3.5, -14));
        }
    }

    private static string VerifyHints(V2PerspectiveCamera camera)
    {
        using var cylinder = new Cylinder(new Circle(new Plane(new Point3d(0, -3, -8), Vector3d.YAxis), 1.8),
            6).ToBrep(false, false);
        AttachRenderMesh(cylinder.Faces[0]);
        var other = new List<Brep>();
        try
        {
            for (int i = 0; i < 7; i++)
            {
                using var surface = NurbsSurface.Create(3, false, 3, 3, 3, 3);
                surface.KnotsU.CreateUniformKnots(1);
                surface.KnotsV.CreateUniformKnots(1);
                for (int u = 0; u < 3; u++)
                    for (int v = 0; v < 3; v++)
                        surface.Points.SetPoint(u, v, new Point3d(-6 + u * 7, -5 + v * 5,
                            (u == 2 ? -2.5 : -29 + u * 6 + v * .17) - .34 * i));
                other.Add(surface.ToBrep());
            }
            var entries = new[] { cylinder }.Concat(other).Select((brep, i) =>
            {
                var face = brep.Faces[0];
                var bounds = face.GetBoundingBox(true);
                return new V2Face(new SourceFaceId($"curved:{i}", 0), face, bounds, null, false)
                    { ProjectedBounds = camera.Bounds(bounds) };
            }).ToArray();
            var proof = new V2SurfaceBounds(entries, Fit, null);
            using var baseline = new V2DepthIndex(entries, camera, Fit, proof, renderHints: false);
            using var indexed = new V2DepthIndex(entries, camera, Fit, proof);
            var points = Enumerable.Range(0, 400).Select(i => new Point2d((i % 20 - 9.3) * .25, (i / 20 - 9.4) * .25)).ToArray();
            var watch = Stopwatch.StartNew();
            foreach (var point in points) Require(baseline.Classify(point).Owner == 0, "Unexpected baseline curved owner");
            double old = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            foreach (var point in points) Require(indexed.Classify(point).Owner == 0, "Render mesh changed the exact owner");
            Require(indexed.MeshHits >= points.Length * .95 && indexed.NativeQueries < baseline.NativeQueries * .65,
                $"Perspective hints did not reduce exact calls: {indexed.MeshHits}/{points.Length} hits, " +
                $"{baseline.NativeQueries} → {indexed.NativeQueries} queries");
            return $"Paired curved-depth queries {baseline.NativeQueries} → {indexed.NativeQueries}, " +
                $"mesh hits {indexed.MeshHits}/{indexed.Rays} ({indexed.MeshHits / (double)indexed.Rays:P1}), " +
                $"query wall {old:F1} → {watch.Elapsed.TotalMilliseconds:F1} ms, " +
                $"native {baseline.NativeTicks * 1000d / Stopwatch.Frequency:F1} → " +
                $"{indexed.NativeTicks * 1000d / Stopwatch.Frequency:F1} ms.";
        }
        finally { foreach (var brep in other) brep.Dispose(); }
    }

    private static void AttachRenderMesh(BrepFace face)
    {
        using var part = face.DuplicateFace(false);
        var meshes = Mesh.CreateFromBrep(part, MeshingParameters.FastRenderMesh);
        try { Require(meshes is { Length: 1 } && face.SetMesh(MeshType.Render, meshes[0]), "Render mesh fixture failed"); }
        finally { foreach (var mesh in meshes ?? Array.Empty<Mesh>()) mesh.Dispose(); }
    }

    private static Brep Box(double x, double y, double z, double a, double b, double c) =>
        Brep.CreateFromBox(new BoundingBox(new Point3d(x, y, z), new Point3d(a, b, c)));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
