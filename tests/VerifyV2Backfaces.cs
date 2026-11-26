using System;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2Backfaces
{
    public static string Run()
    {
        const double fit = .01;
        using var open = Brep.CreateFromCornerPoints(new Point3d(-4, -4, 0), new Point3d(-4, 4, 0),
            new Point3d(4, 4, 0), new Point3d(4, -4, 0), fit);
        using (var drawing = V2DrawingSolver.Solve(new[] {new Input(open, "open")}, fit, .04))
            Require(drawing.BackfaceCulled == 0 && OwnerAt(drawing, 0, 0) >= 0, "Open sheet was culled.");
        using var body = new Box(Plane.WorldXY, new Interval(-5, 5), new Interval(-5, 5),
            new Interval(-40, 5)).ToBrep();
        int rear = body.Faces.Single(face => face.GetBoundingBox(true).Max.Z < -39).FaceIndex;
        var clipped = new Plane(Point3d.Origin, Vector3d.ZAxis);
        using (var drawing = V2DrawingSolver.Solve(new[] {new Input(body, "body")}, fit, .04,
            projection: new V2ParallelCamera(-70, 70, new[] {clipped})))
            Require(drawing.BackfaceCulled == 0 && OwnerAt(drawing, 0, 0) == rear, "Cut box lost its visible inner wall.");

        using (var drawing = V2DrawingSolver.Solve(new[] {new Input(body, "body")}, fit, .04,
            projection: new V2PerspectiveCamera(1, 100, 10)))
        {
            var walls = new[] { OwnerAt(drawing, -4, 0), OwnerAt(drawing, 4, 0),
                OwnerAt(drawing, 0, -4), OwnerAt(drawing, 0, 4) };
            Require(drawing.BackfaceCulled == 0 && walls.All(owner => owner >= 0) && walls.Distinct().Count() == 4,
                "An inside perspective lost a surrounding wall.");
        }

        var inputs = new[] {new Input(body, "body")};
        using (var ordinary = V2DrawingSolver.Solve(inputs, fit, .04))
        using (var hidden = V2DrawingSolver.Solve(inputs, fit, .04, hiddenMode: V2HiddenMode.Occluded))
        using (var rearLines = V2DrawingSolver.Solve(inputs, fit, .04, hiddenMode: V2HiddenMode.IncludeBackFaces))
        {
            Require(ordinary.BackfaceCulled == 1 && hidden.BackfaceCulled == 1 && rearLines.BackfaceCulled == 0 &&
                OwnerAt(ordinary, 0, 0) == OwnerAt(rearLines, 0, 0) && OwnerAt(ordinary, 0, 0) != rear,
                "Exterior ownership or hidden-mode gating changed.");
        }
        using (var focus = V2DrawingSolver.Solve(inputs, fit, .04, focus: new SourceFaceId("body", rear)))
            Require(focus.BackfaceCulled == 0, "A diagnostic focus lost its original backing faces.");
        using (var transparent = V2DrawingSolver.Solve(new[] {new Input(body, "clear") {Opaque = false}}, fit, .04))
            Require(transparent.BackfaceCulled == 0, "Non-opaque solid was screened.");

        using var inward = body.DuplicateBrep();
        inward.Flip();
        using (var flipped = V2DrawingSolver.Solve(new[] {new Input(inward, "inward")}, fit, .04))
            Require(flipped.BackfaceCulled == 1 && OwnerAt(flipped, 0, 0) != rear, "Inward solid lost its exterior face.");
        using var outside = body.DuplicateBrep();
        outside.Translate(0, 0, -15);
        using (var perspective = V2DrawingSolver.Solve(new[] {new Input(outside, "outside")}, fit, .04,
            projection: new V2PerspectiveCamera(1, 100, 10)))
            Require(perspective.BackfaceCulled == 5 && OwnerAt(perspective, 0, 0) != rear,
                "Perspective exterior did not screen the strictly rear planes.");

        using var roundSolid = new Cylinder(new Circle(new Point3d(0, 0, -30), 2), 3).ToBrep(true, true);
        using (var round = V2DrawingSolver.Solve(new[] {new Input(roundSolid, "round solid")}, fit, .04,
            projection: new V2PerspectiveCamera(1, 100, 10)))
            Require(round.BackfaceCulled == 0 && OwnerAt(round, 0, 0) >= 0,
                "A cap supporting a curved rim was removed.");

        using var nearBody = new Box(Plane.WorldXY, new Interval(2, 5), new Interval(-3, 3),
            new Interval(-12, -.8)).ToBrep();
        using (var nearClipped = V2DrawingSolver.Solve(new[] {new Input(nearBody, "near-clipped")}, fit, .04,
            projection: new V2PerspectiveCamera(1, 100, 10)))
            Require(nearClipped.BackfaceCulled == 0, "A body crossing the near plane lost backing faces.");
        using var bowed = NurbsCurve.Create(false, 2, new[]
        {
            new Point3d(-5, 0, 0), new Point3d(0, 0, 1e-5), new Point3d(5, 0, 0)
        });
        using var curved = Surface.CreateExtrusion(bowed, new Vector3d(0, 5, 0));
        using var curvedFace = curved.ToBrep();
        var face = curvedFace.Faces[0];
        Require(face.TryGetPlane(out var approximation, fit * .01) &&
            !V2BackfaceCulling.IsBackPlane(face, approximation, face.GetBoundingBox(true), -1, false, fit) &&
            !V2BackfaceCulling.IsBackPlane(face, approximation, face.GetBoundingBox(true), 1, false, fit),
            "A loose planar fit was used to cull a genuinely curved surface.");

        return "PASS backfaces: open sheet retained; clipped interior retained; all four inside-perspective walls visible; " +
            "closed outward/inward polyhedra screened; curved-rim caps, near cut, curved fits, non-opaque, focused and full hidden geometry retained.";
    }

    private static int OwnerAt(V2Drawing drawing, double x, double y)
    {
        foreach (var region in drawing.Regions)
            if (VerifyV2Drawing.Contains(region.Brep, new Point3d(x, y, 0), 1e-6))
                return region.FaceIndex;
        return -1;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
