using System;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2SameSupport
{
    public static string Run()
    {
        const double fit = .01;
        using var arc = new ArcCurve(new Arc(new Point3d(-10, 0, 0),
            new Point3d(0, 0, 3), new Point3d(10, 0, 0)));
        using var general = Curve.CreateInterpolatedCurve(new[]
        {
            new Point3d(-10, 0, 0), new Point3d(-5, 0, 1.6), new Point3d(0, 0, 2.1),
            new Point3d(5, 0, 1.8), new Point3d(10, 0, 0)
        }, 3);
        foreach (var profile in new Curve[] { arc, general })
        {
            using var wall = Extrude(profile, 0, 8, 0);
            foreach (double noise in new[] { -1e-10, 1e-10 })
            {
                using var band = Extrude(profile, 3, .5, noise);
                CheckDepth(wall, band, true, "coincident band");
                using var drawing = V2DrawingSolver.Solve(new[] { new Input(wall, "wall"), new Input(band, "band") },
                    fit, .04);
                Require(drawing.Regions.Count >= 2 && drawing.Regions.All(region => region.Brep.IsValid &&
                    region.Brep.Edges.All(edge => edge.IsValid)), "coincident band: invalid fill");
                for (int x = -9; x <= 9; x++)
                {
                    Require(drawing.Regions.Any(region => region.Source == "band" &&
                        VerifyV2Drawing.Contains(region.Brep, new Point3d(x, 3.25, 0), fit * .01)),
                        "coincident band lost fill");
                    foreach (double y in new[] { 3, 3.5 })
                    {
                        var p = new Point3d(x, y, 0);
                        Require(drawing.Curves.Any(line => line.Curve.ClosestPoint(p, out double t) &&
                            line.Curve.PointAt(t).DistanceTo(p) <= fit * .1), "coincident band has a stroke gap");
                    }
                }
            }
            using var behind = Extrude(profile, 3, .5, -.02);
            CheckDepth(wall, behind, false, "0.02 mm rear band");
        }

        using var tangentA = new ArcCurve(new Arc(new Point3d(-10, 0, -5),
            Point3d.Origin, new Point3d(10, 0, -5)));
        using var tangentB = new ArcCurve(new Arc(new Point3d(-8, 0, -5),
            Point3d.Origin, new Point3d(8, 0, -5)));
        using (var wall = Extrude(tangentA, 0, 8, 0))
        using (var band = Extrude(tangentB, 3, .5, 0))
            CheckDepth(wall, band, false, "single tangent generator");

        using var straight = new LineCurve(new Point3d(-9, 0, 0), new Point3d(9, 0, 0));
        using (var wall = Extrude(straight, 0, 8, 0))
        using (var band = Extrude(straight, 3, .5, 0))
        {
            Plane plane = Plane.WorldXY;
            CheckDepth(wall, band, true, "mixed representation", plane, null);
            CheckDepth(wall, band, true, "mixed representation reversed", null, plane);
        }
        CheckShallowCrossing();
        return "PASS shared curved support: cylindrical and freeform bands at ±1e-10 mm, continuous strokes and valid fills; 0.02 mm offset and single tangent generator rejected; mixed representations symmetric. Near-tangent crossing keeps the genuinely exposed portion.";

        static Brep Extrude(Curve profile, double height, double width, double offset)
        {
            // Different trims and parameter domains share the same geometric support.
            using var edge = profile.DuplicateCurve();
            edge.Domain = new Interval(height, height + width * 2);
            edge.Translate(0, height, offset);
            using var surface = Surface.CreateExtrusion(edge, new Vector3d(0, width, 0));
            return surface.ToBrep();
        }

        void CheckDepth(Brep wall, Brep band, bool same, string label, Plane? wallPlane = null, Plane? bandPlane = null)
        {
            foreach (bool reversed in new[] { false, true })
            {
                var entries = new[] { Entry(wall, "wall", wallPlane), Entry(band, "band", bandPlane) };
                if (reversed) Array.Reverse(entries);
                using var index = new V2DepthIndex(entries, new V2ParallelCamera(-40, 40), fit, renderHints: false);
                Require(index.SameSurfaceSupport(0, 1) == same && index.SameSurfaceSupport(1, 0) == same,
                    label + ": wrong support certificate");
                for (int x = -7; x <= 7; x++)
                {
                    if (!same && x == 0) continue;
                    var answer = index.Classify(new Point2d(x, 3.25));
                    Require(answer.Owner >= 0 && entries[answer.Owner].Id.Source == (same ? "band" : "wall"),
                        label + ": wrong depth owner");
                }
            }
        }

        static V2Face Entry(Brep brep, string name, Plane? plane)
            => new(new SourceFaceId(name, 0), brep.Faces[0], brep.GetBoundingBox(true), plane, false);

        static void CheckShallowCrossing()
        {
            const double modelingTolerance = .055;
            using var profile = NurbsCurve.Create(false, 2, new[]
            {
                new Point3d(0, 0, 0), new Point3d(10, 0, -.002), new Point3d(20, 0, .004)
            });
            using var curved = Extrude(profile, -5, 210, 0);
            using var flat = Brep.CreateFromCornerPoints(new Point3d(0, 0, 0), new Point3d(20, 0, 0),
                new Point3d(20, 200, 0), new Point3d(0, 200, 0), 1e-7);
            using var drawing = V2DrawingSolver.Solve(new[] { new Input(curved, "curved"), new Input(flat, "flat") },
                modelingTolerance, .04);
            bool HasOwner(string name, double x) => drawing.Regions.Any(region => region.Source == name &&
                VerifyV2Drawing.Contains(region.Brep, new Point3d(x, 100, 0), 1e-5));
            Require(HasOwner("flat", 5) && !HasOwner("curved", 5) &&
                HasOwner("curved", 15) && !HasOwner("flat", 15), "near-tangent crossing lost its depth order");
            var witness = new Point3d(10, 100, 0);
            Require(drawing.Curves.Any(line => line.Curve.ClosestPoint(witness, out double t) &&
                line.Curve.PointAt(t).DistanceTo(witness) <= modelingTolerance * .1),
                "near-tangent crossing lost its visible trim boundary");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
