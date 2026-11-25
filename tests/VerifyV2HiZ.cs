using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

public static class VerifyV2HiZ
{
    public static string Run()
    {
        const double fit = .005;
        using var outline = Box(-50, -40, 50, 40, 5);
        using var window = Box(-4, -5, 4, 5, 5);
        using var slit = Box(15, -5, 15.05, 5, 5);
        using var front = Brep.CreatePlanarBreps(new Curve[] { outline, window, slit }, fit * .001).Single();
        var inputs = new List<Input> { new(front, "wall") };
        try
        {
            for (int floor = 0; floor < 5; floor++)
                for (int room = 0; room < 12; room++)
                {
                    double x = -45 + 3 * room, y = -34 + 14 * floor;
                    inputs.Add(new Input(Face(x, y, x + 2, y + 5, 0), $"room-{floor}-{room}"));
                }
            inputs.Add(new Input(Face(-3, -4, 3, 4, 1), "window contents"));
            inputs.Add(new Input(Face(14, -4, 16, 4, 1), "slit contents"));

            long start = Stopwatch.GetTimestamp();
            using var reference = V2DrawingSolver.Solve(inputs, fit, .02, enableHiZ: false, hiddenMode: V2HiddenMode.Occluded);
            double baseline = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            using var drawing = V2DrawingSolver.Solve(inputs, fit, .02, hiddenMode: V2HiddenMode.Occluded);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (drawing.HiZFaces < 46 || drawing.HiZFaces > 60)
                throw new InvalidOperationException($"Hi-Z rejected {drawing.HiZFaces}/60 room faces.");
            if (!drawing.Curves.Any(edge => edge.IsHidden && edge.Source.StartsWith("room-")))
                throw new InvalidOperationException("Hi-Z removed geometry needed by hidden linework.");
            var perspective = new V2PerspectiveCamera(2, 200, 100);
            var translated = new List<Brep>();
            int perspectiveHidden;
            try
            {
                foreach (var input in inputs)
                {
                    var copy = input.Brep.DuplicateBrep();
                    translated.Add(copy);
                    if (!copy.Transform(Transform.Translation(0, 0, -100)))
                        throw new InvalidOperationException("Could not place the perspective Hi-Z fixture.");
                }
                var faces = translated.Select((shape, i) =>
                {
                    var face = shape.Faces[0];
                    var box = face.GetBoundingBox(true);
                    if (!face.TryGetPlane(out var plane, fit * .01)) throw new InvalidOperationException("Expected planar fixture.");
                    return new V2Face(new SourceFaceId(inputs[i].Source, 0), face, box, plane, false)
                        { ProjectedBounds = perspective.Bounds(box) };
                }).ToArray();
                var perspectiveHiZ = new V2HiZ(faces, fit, null, perspective);
                perspectiveHidden = faces.Skip(1).Take(60).Count(face => perspectiveHiZ.Hides(face.Box));
                if (perspectiveHidden != 60 || faces.TakeLast(2).Any(face => perspectiveHiZ.Hides(face.Box)))
                    throw new InvalidOperationException("Perspective Hi-Z lost the window, slit, or room coverage.");
            }
            finally { foreach (var copy in translated) copy.Dispose(); }
            int rays = 0;
            foreach (double y in new[] { -31.13, -3.83, -.23, 2.73, 18.11 })
                foreach (double x in Enumerable.Range(0, 50).Select(i => -48.53 + i * 1.97)
                             .Concat(new[] { -.23, 15.008, 15.025, 15.042, 15.08 }))
                {
                    var point = new Point3d(x, y, 0);
                    using var ray = new LineCurve(new Point3d(x, y, 10), new Point3d(x, y, -10));
                    var frontHit = (Source: "", Depth: double.NegativeInfinity);
                    foreach (var input in inputs)
                    {
                        Intersection.CurveBrep(ray, input.Brep, fit * .01, out var overlaps, out var hits);
                        foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                        foreach (var hit in hits ?? Array.Empty<Point3d>())
                            if (hit.Z > frontHit.Depth) frontHit = (input.Source, hit.Z);
                    }
                    if (At(drawing, point) != frontHit.Source || At(reference, point) != frontHit.Source)
                        throw new InvalidOperationException($"Hi-Z/raw discrepancy at {point}: expected {frontHit.Source}, found {At(drawing, point)}.");
                    rays++;
                }
            return $"PASS Hi-Z: {drawing.HiZFaces}/60 parallel and {perspectiveHidden}/60 perspective room faces; {rays} independent rays, 0 differences; " +
                $"Hi-Z off {baseline:F1} ms, on {elapsed:F1} ms (both with hidden linework); raster " +
                $"{drawing.Timing["conservative Hi-Z"].TotalMilliseconds:F1} ms, {drawing.HiZPixels} certified pixels. " +
                Tilted() + " " + ClippedAndCurved();
        }
        finally { foreach (var input in inputs.Skip(1)) input.Dispose(); }

        static string At(V2Drawing drawing, Point3d point) => drawing.Regions.Where(region =>
        {
            var face = region.Brep.Faces[0];
            return face.ClosestPoint(point, out double u, out double v) &&
                face.IsPointOnFace(u, v, 1e-7) == PointFaceRelation.Interior;
        }).Select(region => region.Source).SingleOrDefault() ?? "";

        static Brep Face(double x, double y, double a, double b, double z)
        {
            using var boundary = Box(x, y, a, b, z);
            return Brep.CreatePlanarBreps(boundary, 1e-6).Single();
        }
        static Curve Box(double x, double y, double a, double b, double z) => new PolylineCurve(new[]
        {
            new Point3d(x,y,z), new Point3d(a,y,z), new Point3d(a,b,z), new Point3d(x,b,z), new Point3d(x,y,z)
        });
    }

    private static string ClippedAndCurved()
    {
        const double fit = .001;
        var crop = new Rect(-1, -1, 1, 1);
        var section = new Plane(new Point3d(0, -9, 0), -Vector3d.YAxis);
        var camera = new V2PerspectiveCamera(2, 100, 30, new[] {section}, crop);
        var outline = new List<Point3d>();
        for (int i = 0; i <= 80; i++) outline.Add(new Point3d(-40 + i, -20, -25));
        outline.AddRange(new[] {new Point3d(40, 2, -25), new Point3d(6, 2, -25), new Point3d(6, 20, -25),
            new Point3d(-40, 20, -25), outline[0]});
        using var rim = new PolylineCurve(outline);
        using var hole = new Circle(new Point3d(-3, 1, -25), 2).ToNurbsCurve();
        using var slit = new PolylineCurve(new[] {new Point3d(3,-5,-25), new Point3d(3.05,-5,-25),
            new Point3d(3.05,0,-25), new Point3d(3,0,-25), new Point3d(3,-5,-25)});
        using var wall = Brep.CreatePlanarBreps(new Curve[] {rim, hole, slit}, fit * .01).Single();
        var cut = Index(wall, camera);
        if (!cut.Hides(Behind(-7, -4)) || !cut.Hides(Behind(8, 0)) || cut.Hides(Behind(8, 5)) ||
            cut.Hides(Behind(-3, 1)) || cut.Hides(Behind(3.025, -2)) || cut.Hides(Behind(0, -10)))
            throw new InvalidOperationException("Clipped concave Hi-Z filled the notch, circle, narrow slit, or sectioned-away area.");

        using var arc = new ArcCurve(new Arc(new Point3d(-8,-5,-19), new Point3d(0,-5,-15), new Point3d(8,-5,-19)));
        using var curved = Surface.CreateExtrusion(arc, Vector3d.YAxis * 10).ToBrep();
        var simpleCamera = new V2PerspectiveCamera(2, 100, 30);
        var curveDepth = Index(curved, simpleCamera);
        if (curveDepth.CurvedOccluders != 1 || !curveDepth.Hides(Behind(0, 0)) || curveDepth.Hides(Behind(0, 10)))
            throw new InvalidOperationException("Curved boundary winding did not preserve its certified coverage limits.");
        return "Clipped concave 85-vertex wall, rational circle hole, 0.05 mm slit, and exact curved coverage verified.";

        static BoundingBox Behind(double x, double y)
        {
            var p = new Point3d(x * 1.3, y * 1.3, -32.5);
            var r = new Vector3d(.008, .008, .008);
            return new BoundingBox(p - r, p + r);
        }

        static V2HiZ Index(Brep brep, IProjectionCamera camera)
        {
            var face = brep.Faces[0];
            var box = face.GetBoundingBox(true);
            Plane? plane = face.TryGetPlane(out var p, fit * .01) ? p : null;
            var item = new V2Face(new SourceFaceId("proof", 0), face, box, plane, false)
                {ProjectedBounds = camera.Bounds(box)};
            return new V2HiZ(new[] {item}.Concat(Enumerable.Range(0, 7).Select(i =>
                item with {Id = new SourceFaceId($"dummy {i}", 0), Opaque = false})).ToArray(), fit, null, camera);
        }
    }

    private static string Tilted()
    {
        const double fit = .003;
        Point3d On(double x, double y) => new(x, y, -40 + x * .5);
        using var outline = Loop(-40, -14, 40, 14);
        using var hole = Loop(17, -4, 21, 4);
        using var slit = Loop(26, -4, 26 + .05 / Math.Sqrt(1.25), 4);
        using var wall = Brep.CreatePlanarBreps(new Curve[] { outline, hole, slit }, 1e-6).Single();
        var front = wall.Faces[0];
        if (!front.TryGetPlane(out var plane, fit * .01)) throw new Exception("Missing inclined plane");
        var checks = 0;
        foreach (var camera in new IProjectionCamera[] { new V2PerspectiveCamera(2, 150, 43), new V2ParallelCamera(-150, -2) })
        {
            var bounds = front.GetBoundingBox(true);
            var carrier = new V2Face(new SourceFaceId("tilted", 0), front, bounds, plane, false)
                { ProjectedBounds = camera.Bounds(bounds) };
            var faces = new[] { carrier }.Concat(Enumerable.Range(0, 7).Select(i =>
                carrier with { Id = new SourceFaceId($"dummy {i}", 0), Opaque = false })).ToArray();
            var depth = new V2HiZ(faces, fit, null, camera);
            for (int i = 0; i < 36; i++)
            {
                double x = 3.5 + (i % 12) * .94, y = -7 + (i / 12) * 6.5;
                var target = BoxAt(x, y);
                if (target.Max.Z <= bounds.Min.Z || !depth.Hides(target))
                    throw new InvalidOperationException("Tilted-wall near patch did not hide a target missed by whole-face depth.");
                checks++;
            }
            if (depth.Hides(BoxAt(19, 0)) || depth.Hides(BoxAt(26.025, 0)))
                throw new InvalidOperationException("Tilted-wall Hi-Z filled a hole or the 0.05 mm slit.");

            BoundingBox BoxAt(double x, double y)
            {
                var point = On(x, y);
                if (camera.Perspective) point = Point3d.Origin + (point - Point3d.Origin) * 1.075;
                else point.Z -= 2;
                var v = new Vector3d(.007, .007, .007);
                return new BoundingBox(point - v, point + v);
            }
        }
        return $"{checks} inclined-perspective/parallel targets culled with hole and 0.05 mm slit retained.";

        Curve Loop(double x, double y, double a, double b) => new PolylineCurve(new[]
            { On(x, y), On(a, y), On(a, b), On(x, b), On(x, y) });
    }
}
