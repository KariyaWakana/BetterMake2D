using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2Perspective
{
    public static string Run() => Check(V2Accuracy.Exact);

    internal static string Check(V2Accuracy accuracy)
    {
        bool fast = accuracy == V2Accuracy.Fast;
        const double fit = .003, near = 2, far = 40, focal = 12;
        var camera = new V2PerspectiveCamera(near, far, focal);
        using var rim = Rectangle(-6, -5, 6, 5, -14);
        using var hole = new Circle(new Point3d(-1.5, 0, -14), 2.1).ToNurbsCurve();
        using var wall = Brep.CreatePlanarBreps(new[] { rim, hole }, 1e-6).Single();
        using var angled = Quad(new(-2.4, -5, -17), new(4.8, -5, -8),
            new(4.8, 4.5, -8), new(-2.4, 4.5, -17));
        using var nearWall = Quad(new(1.5, -3.2, -.5), new(5, -3.2, -7),
            new(5, -1.5, -7), new(1.5, -1.5, -.5));
        using var cylinder = new Cylinder(new Circle(new Plane(new Point3d(-2.5, -3.8, -11),
            Vector3d.YAxis), 1.35), 7.3).ToBrep(true, true);
        using var torus = new Torus(new Plane(new Point3d(1.1, 2.4, -10), new Vector3d(.18, .65, 1)),
            1.05, .23).ToBrep();
        using var behind = Brep.CreateFromBox(new BoundingBox(new Point3d(-2, -2, 1), new Point3d(2, 2, 3)));
        using var mesh = new Mesh();
        mesh.Vertices.Add(-7, -5, -16);
        mesh.Vertices.Add(5, -5, -16);
        mesh.Vertices.Add(5, -2, -11);
        mesh.Vertices.Add(-7, -2, -11);
        mesh.Faces.AddFace(0, 1, 2, 3);
        var inputs = new[] { new Input(wall, "wall"), new Input(angled, "slanted"), new Input(nearWall, "near"),
            new Input(cylinder, "cylinder"), new Input(torus, "torus"), new Input(behind, "behind-eye"),
            new Input(null, "mesh") { NativeMesh = mesh } };
        using (var image = camera.ProjectCurve(hole))
            for (int i = 0; i <= 80; i++)
            {
                double t = hole.Domain.ParameterAt(i / 80d);
                var p = hole.PointAt(t);
                if (image == null || image.PointAt(t).DistanceTo(new Point3d(focal * p.X / -p.Z, focal * p.Y / -p.Z, 0)) > 1e-9)
                    throw new InvalidOperationException("Perspective NURBS lost its homogeneous control points.");
            }
        long start = Stopwatch.GetTimestamp();
        using var drawing = V2DrawingSolver.Solve(inputs, fit, .025, projection: camera, accuracy: accuracy);
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (drawing.Regions.Count == 0 || !drawing.Regions.Any(item => item.Source == "near") ||
            drawing.Regions.Any(item => item.Source == "behind-eye" || !item.Brep.IsValid ||
                item.Brep.Loops.Any(loop => { using var curve = loop.To3dCurve(); return curve?.IsClosed != true; })) ||
            drawing.Issues.Any(issue => issue.StartsWith("Invalid projected region", StringComparison.Ordinal)) ||
            (fast ? drawing.ProxyFaces < 2 || drawing.MeshContactPairs == 0 || drawing.NativeRays != 0 : drawing.Contacts == 0) ||
            !drawing.Curves.Any(edge => edge.Boundary == "ViewClip"))
            throw new InvalidOperationException("Perspective lost near clipping, intersection, or fill geometry: " +
                string.Join(" | ", drawing.Issues.Take(8)));
        int checkedRays = 0, differences = 0;
        for (int y = 0; y < 47; y++)
            for (int x = 0; x < 56; x++)
            {
                var p = new Point3d(-7.7 + .315 * x, -7.2 + .315 * y, 0);
                var first = new Point3d(p.X * near / focal, p.Y * near / focal, -near);
                var last = new Point3d(p.X * far / focal, p.Y * far / focal, -far);
                var line = new Line(first, last);
                using var ray = new LineCurve(line);
                var hits = new List<(string Source, double Z)>();
                foreach (var input in inputs)
                {
                    if (input.NativeMesh != null) Add(Intersection.MeshLine(input.NativeMesh, line), input.Source);
                    else
                    {
                        Intersection.CurveBrep(ray, input.Brep, fit * .005, out var overlaps, out var points);
                        foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                        Add(points, input.Source);
                    }
                }
                var order = hits.OrderByDescending(hit => hit.Z).ToArray();
                if (order.Length >= 2 && order[0].Source != order[1].Source && order[0].Z - order[1].Z <= fit * .03) continue;
                string expected = order.FirstOrDefault().Source;
                var actual = drawing.Regions.Where(item =>
                {
                    var face = item.Brep.Faces[0];
                    return face.ClosestPoint(p, out double u, out double v) &&
                        face.IsPointOnFace(u, v, 1e-9) == PointFaceRelation.Interior;
                }).Select(item => item.Source).ToArray();
                if (actual.Length > 1 || actual.SingleOrDefault() != expected)
                {
                    if (!fast || actual.Length > 1)
                        throw new InvalidOperationException($"Perspective raw ray at {p}: expected {expected}, got {string.Join(",", actual)}. " +
                            string.Join(" | ", drawing.Issues.Take(10)));
                    differences++;
                }
                checkedRays++;

                void Add(Point3d[] points, string source)
                {
                    foreach (var hit in points ?? Array.Empty<Point3d>())
                        if (hit.Z <= -near && hit.Z >= -far) hits.Add((source, hit.Z));
                }
            }
        if (fast && differences * 200 > checkedRays)
            throw new InvalidOperationException($"Fast perspective has {differences}/{checkedRays} independent ray differences.");
        return $"PASS {accuracy} perspective near plane, slanted face, holed wall, cylinder, torus, mesh, true contact; " +
            $"{checkedRays} independent rays, {differences} differences, {drawing.Regions.Count} valid fills, {elapsed:F1} ms solve. " +
            (fast ? $"{drawing.ProxyTriangles} proxy triangles and {drawing.Segments} projected segments. " : "") +
            VerifySlits(accuracy);

        static Curve Rectangle(double x, double y, double a, double b, double z) =>
            new PolylineCurve(new[] { new Point3d(x,y,z), new Point3d(a,y,z), new Point3d(a,b,z),
                new Point3d(x,b,z), new Point3d(x,y,z) });
        static Brep Quad(Point3d a, Point3d b, Point3d c, Point3d d) => Brep.CreateFromCornerPoints(a,b,c,d,1e-6);
    }

    private static string VerifySlits(V2Accuracy accuracy)
    {
        const double fit = .002;
        var camera = new V2PerspectiveCamera(2, 170, 54, nearWindow: new Rect(-.65, -.45, .65, .45));
        int checkedRays = 0;
        foreach (double depth in new[] { 12.0, 100.0 })
        {
            using var rim = Polygon(-25, -10, 25, 10, -depth);
            using var slit = Polygon(.3, -1, .35, 1, -depth);
            using var wall = Brep.CreatePlanarBreps(new[] {rim, slit}, fit * .001).Single();
            using var rear = Polygon(-25, -10, 25, 10, -depth - 4);
            using var back = Brep.CreatePlanarBreps(rear, fit * .001).Single();
            using var drawing = V2DrawingSolver.Solve(new[] {new Input(wall, "wall"), new Input(back, "rear")},
                fit, .02, projection: camera, accuracy: accuracy);
            if (depth == 100)
            {
                using var isolated = V2DrawingSolver.Solve(new[] {new Input(wall, "wall")}, fit, .02, projection: camera, accuracy: accuracy);
                if (isolated.ImageGrid <= drawing.ImageGrid ||
                    isolated.Regions.Sum(region => region.Brep.Loops.Count) != 2 ||
                    isolated.Regions.Any(region => !region.Brep.IsValid))
                    throw new InvalidOperationException("The isolated perspective grid did not preserve the slit as a hole.");
            }
            foreach (double x in new[] {.29, .303, .315, .325, .335, .347, .36})
            {
                var image = camera.Project(new Point3d(x, .37, -depth));
                var where = new Point3d(image.X, image.Y, 0);
                using var ray = new LineCurve(camera.ViewRay(image));
                Intersection.CurveBrep(ray, wall, fit * .001, out var overlaps, out var hits);
                foreach (var curve in overlaps ?? Array.Empty<Curve>()) curve.Dispose();
                string expected = hits is {Length: > 0} ? "wall" : "rear";
                string actual = drawing.Regions.Where(item =>
                {
                    var face = item.Brep.Faces[0];
                    return face.ClosestPoint(where, out double u, out double v) &&
                        face.IsPointOnFace(u, v, 1e-9) == PointFaceRelation.Interior;
                }).Select(item => item.Source).SingleOrDefault();
                if (expected != actual) throw new InvalidOperationException("Perspective grid sealed a 0.05 mm slit.");
                checkedRays++;
            }
        }
        return $"{checkedRays} exact rays preserved 0.05 mm openings at near and middle depths.";

        static Curve Polygon(double x, double y, double a, double b, double z) => new PolylineCurve(new[]
        {
            new Point3d(x,y,z), new Point3d(a,y,z), new Point3d(a,b,z), new Point3d(x,b,z), new Point3d(x,y,z)
        });
    }
}
