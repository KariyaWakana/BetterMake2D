using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2Terrain
{
    public static string Run()
    {
        const double fit = .01;
        using var terrain = new Mesh();
        const int n = 19;
        var points = new List<Point3d>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var p = new Point3d(x, y, 2 * Math.Sin(x * .38) + .12 * y);
                points.Add(p);
                terrain.Vertices.Add(p);
                if (x > 0 && y > 0)
                {
                    int i = y * n + x;
                    terrain.Faces.AddFace(i - n - 1, i - n, i, i - 1);
                }
            }
        using var pile = new Cylinder(new Circle(new Plane(new Point3d(3, 9, -2),
            new Vector3d(.3, .18, 1)), .8), 8).ToBrep(true, true);
        using var plane = new PlaneSurface(Plane.WorldXY, new Interval(8, 16), new Interval(3, 14));
        using var wall = plane.ToBrep();
        var slope = Transform.Identity;
        slope.M20 = .18;
        slope.M23 = -1.8;
        Require(wall.Transform(slope), "Wall slope");
        using var smooth = NurbsSurface.CreateFromPoints(points, n, n, 3, 3);
        using var ground = smooth.ToBrep();
        var native = new Input(null, "ground") { NativeMesh = terrain };
        Run(native, true);
        Run(new Input(ground, "ground"), false);

        using var ridge = new Mesh();
        foreach (var point in new[]
        {
            new Point3d(0, 0, 0), new Point3d(4, 0, 0), new Point3d(8, 0, 8),
            new Point3d(0, 6, 0), new Point3d(4, 6, 0), new Point3d(8, 6, 8)
        }) ridge.Vertices.Add(point);
        ridge.Faces.AddFace(0, 1, 4, 3);
        ridge.Faces.AddFace(1, 2, 5, 4);
        using var cliff = V2DrawingSolver.Solve(new[] { new Input(null, "cliff") { NativeMesh = ridge } },
            fit, .04, minimumTileDepth: 2);
        Require(cliff.Regions.Count == 1 && cliff.Regions[0].Brep.IsValid &&
            cliff.Curves.Count(edge => edge.Boundary == "MeshCrease") == 1 &&
            cliff.Curves.All(edge => edge.Boundary is "MeshBoundary" or "MeshCrease"),
            $"A terrain crease or quad diagonal was emitted incorrectly: {string.Join(", ", cliff.Curves.Select(edge => edge.Boundary))}.");
        VerifyWindow(terrain, wall);
        VerifyLongPolygon();
        return "PASS: native wave mesh and NURBS terrain, inserted round pile, crossing wall, crop and layer-scoped depth, exact long polygon with a hole, and no triangle diagonals.";

        void Run(Input surface, bool mesh)
        {
            var inputs = new[] { surface, new Input(pile, "pile"), new Input(wall, "wall") };
            using var drawing = V2DrawingSolver.Solve(inputs, fit, .04, minimumTileDepth: 2);
            Require(drawing.Regions.Any(region => region.Source == "pile") &&
                drawing.Regions.Any(region => region.Source == "wall") &&
                drawing.Regions.Any(region => region.Source == "ground") &&
                drawing.Regions.All(region => region.Brep.IsValid) &&
                drawing.Issues.All(issue => !issue.StartsWith("Invalid projected region", StringComparison.Ordinal)),
                $"The visible ground, pile and wall did not close: {string.Join("; ", drawing.Issues.Take(12))}.");
            Require(mesh ? drawing.NativeMeshes == 1 && drawing.NativeMeshTriangles == 648 &&
                drawing.MeshContactPairs > 0 : drawing.NativeMeshes == 0 && drawing.Contacts > 0,
                "The native terrain and local contact routes were not used.");
            int checkedPoints = 0;
            for (int y = 0; y < n - 1; y++)
                for (int x = 0; x < n - 1; x++)
                {
                    var p = new Point3d(x + .21, y + .43, 0);
                    var line = new Line(new Point3d(p.X, p.Y, -20), new Point3d(p.X, p.Y, 20));
                    using var ray = new LineCurve(line);
                    var hits = new List<(string Owner, double Z)>();
                    foreach (var input in inputs)
                    {
                        if (input.NativeMesh != null)
                        {
                            foreach (var q in Intersection.MeshLine(input.NativeMesh, line) ?? Array.Empty<Point3d>())
                                hits.Add((input.Source, q.Z));
                            continue;
                        }
                        Intersection.CurveBrep(ray, input.Brep, fit * .02, out var overlaps, out var intersections);
                        foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                        foreach (var q in intersections ?? Array.Empty<Point3d>()) hits.Add((input.Source, q.Z));
                    }
                    if (hits.Count == 0) continue;
                    double front = hits.Max(hit => hit.Z);
                    var owners = hits.Where(hit => hit.Z > front - fit * 2).Select(hit => hit.Owner).Distinct().ToArray();
                    if (owners.Length != 1) continue;
                    var regions = drawing.Regions.Where(region => Contains(region.Brep.Faces[0], p)).ToArray();
                    if (regions.Length != 1 || regions[0].Source != owners[0])
                    {
                        var log = new List<string>();
                        using var single = V2DrawingSolver.Solve(new[] { new Input(pile, "pile") }, fit, .04);
                        using var diagnostic = V2DrawingSolver.Solve(inputs, fit, .04, minimumTileDepth: 2,
                            inspection: new[] { new Point2d(p.X, p.Y) }, progress: log.Add);
                        using var unsplit = V2DrawingSolver.Solve(inputs, fit, .04);
                        throw new InvalidOperationException($"Raw {(mesh ? "mesh" : "NURBS")} ray at {p} expected {owners[0]}, " +
                            $"found {string.Join(",", regions.Select(region => region.Source))}. " +
                            $"Pile alone {single.Regions.Any(region => Contains(region.Brep.Faces[0], p))}; " +
                            $"untiled [{string.Join(",", unsplit.Regions.Where(region => Contains(region.Brep.Faces[0], p)).Select(region => region.Source))}]. " +
                            string.Join(Environment.NewLine, log.Take(55)));
                    }
                    checkedPoints++;
                }
            Require(checkedPoints >= 250, "Too few determinate terrain probes.");
        }
    }

    private static void VerifyWindow(Mesh terrain, Brep wall)
    {
        var scoped = new V2ViewCrop(new Rect(3, 2, 15, 15), 10, 14, "Site", .01);
        var placement = Transform.Translation(0, 0, -12);
        var styles = new Dictionary<string, (string, Color)> { ["terrain"] = ("Site::Ground", Color.Gray),
            ["wall"] = ("Architecture", Color.Black) };
        var objects = new[] { new ProjectionObject(terrain, "terrain", placement),
            new ProjectionObject(wall, "wall", Transform.Identity) };
        var cropped = scoped.Read(objects, styles, false);
        try
        {
            Require(cropped.Count == 2 && cropped[1].Brep.IsValid, "A view crop lost the layer-exempt wall.");
            var mesh = cropped[0].NativeMesh;
            foreach (var p in mesh.Vertices.ToPoint3dArray())
                Require(p.X >= 3 && p.X <= 15 && p.Y >= 2 && p.Y <= 15 && p.Z >= -14 && p.Z <= -10,
                    $"Clipped mesh vertex lies outside the view: {p}.");
            var trimBounds = new BoundingBox(cropped[1].Brep.Vertices.Select(vertex => vertex.Location));
            Require(trimBounds.Min.Z > -10 && Math.Abs(trimBounds.Max.X - 15) < 1e-7,
                $"The exempt layer was depth clipped or did not reach the view boundary: {trimBounds}.");
        }
        finally { foreach (var input in cropped) input.Dispose(); }
    }

    private static void VerifyLongPolygon()
    {
        const int count = 1440;
        var rim = Enumerable.Range(0, count)
            .Select(i => new Point3d(i, i % 2 == 0 ? 0 : .25, 0))
            .Concat(new[] { new Point3d(count - 1, 20, 0), new Point3d(0, 20, 0), Point3d.Origin }).ToArray();
        var hole = new[] { new Point3d(10, 2, 0), new Point3d(10, 10, 0),
            new Point3d(15, 10, 0), new Point3d(15, 2, 0), new Point3d(10, 2, 0) };
        var pieces = V2PlanarFill.Polygon(new[] { rim, hole }, .0001);
        Require(pieces is { Count: > 0 }, "Exact long polygon fill failed.");
        try
        {
            Require(pieces.All(piece => piece.IsValid && piece.Faces.Count == 1), "Invalid partition fill.");
            Require(!pieces.Any(piece => Contains(piece.Faces[0], new Point3d(12, 5, 0))) &&
                pieces.Count(piece => Contains(piece.Faces[0], new Point3d(12, 12, 0))) == 1,
                "The exact polygon partition changed ownership or lost the hole.");
        }
        finally { foreach (var piece in pieces) piece.Dispose(); }
    }

    private static bool Contains(BrepFace face, Point3d point) =>
        face.ClosestPoint(point, out double u, out double v) &&
        face.IsPointOnFace(u, v, 1e-6) == PointFaceRelation.Interior;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
