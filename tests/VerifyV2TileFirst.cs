using System;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2TileFirst
{
    public static string Run()
    {
        const double fit = .001;
        using var outer = Loop(-60, -40, 60, 40, 5);
        using var window = Loop(-6, -7, 6, 7, 5);
        using var slit = Loop(15, -5, 15.05, 5, 5);
        using var wall = Brep.CreatePlanarBreps(new Curve[] { outer, window, slit }, fit * .01).Single();
        using var rear = Panel(-55, -32, 66, 32, 0);
        var rooms = Enumerable.Range(0, 6).Select(i => new Input(
            Panel(-48 + i * 6, 21, -46 + i * 6, 23, -2), $"room-{i}")).ToArray();
        var inputs = new[] { new Input(wall, "wall"), new Input(rear, "rear") }.Concat(rooms).ToArray();
        try
        {
            using var reference = V2DrawingSolver.Solve(inputs, fit, .02, tileLimit: 0, enableHiZ: false,
                hiddenMode: V2HiddenMode.Occluded);
            using var visible = V2DrawingSolver.Solve(inputs, fit, .02, minimumTileDepth: 6);
            using var hidden = V2DrawingSolver.Solve(inputs, fit, .02, minimumTileDepth: 6,
                hiddenMode: V2HiddenMode.Occluded);
            Require(visible.PlannedTiles > 1 && visible.Tiles.Count > 1 && visible.ProvenTiles > 0 &&
                visible.TileFaceCandidates > 0 && visible.TileFacesHidden > 0,
                $"Expected partial-face tile proof, got {visible.PlannedTiles} planned, {visible.ProvenTiles} proven, " +
                $"{visible.TileFaceCandidates} face candidates and {visible.TileFacesHidden} skipped.");
            Require(visible.HiZFaces <= rooms.Length,
                "The whole-face Hi-Z pass hid a face that should remain visible.");

            int samples = 0;
            foreach (var drawing in new[] { reference, visible, hidden })
            {
                Require(drawing.Regions.All(region => region.Brep.IsValid) &&
                    drawing.Curves.All(edge => edge.Curve.IsValid && edge.Boundary != V2TiledArrangement.SeamKind),
                    "Invalid output or a synthetic seam escaped to drawing geometry.");
                CheckArea(drawing, "wall", 120 * 80 - 12 * 14 - .05 * 10);
                CheckArea(drawing, "rear", 6 * 64 + 12 * 14 + .05 * 10);
                Require(drawing.Regions.Count == 4, "A seam split an expected connected region.");
                foreach (double x in new[] { -60.3, -59.7, -36.7, -.23, .27, 5.9, 6.1, 14.99, 15.012,
                    15.025, 15.038, 15.06, 29.2, 59.9, 60.1, 65.9, 66.1 })
                    foreach (double y in new[] { -40.1, -39.9, -31.9, -6.9, -4.9, -.17, .19, 4.9, 6.9, 7.1, 31.9, 32.1, 39.9, 40.1 })
                        CheckPoint(drawing, x, y, ref samples);
            }

            // Sample both sides of actual tile cuts, including points far from fixture trim vertices.
            foreach (var tile in visible.Tiles)
            {
                double x = tile.X1 * visible.ImageGrid, y = tile.Y1 * visible.ImageGrid;
                double midX = (tile.X0 + tile.X1) * .5 * visible.ImageGrid;
                double midY = (tile.Y0 + tile.Y1) * .5 * visible.ImageGrid;
                CheckSeam(visible, x - .017, midY + .023, ref samples);
                CheckSeam(visible, x + .017, midY + .023, ref samples);
                CheckSeam(visible, midX + .023, y - .017, ref samples);
                CheckSeam(visible, midX + .023, y + .017, ref samples);
            }

            foreach (var drawing in new[] { reference, hidden })
            {
                AtHiddenEdge(drawing, new Point3d(-25, 32, 0), true);
                AtHiddenEdge(drawing, new Point3d(59.9, 32, 0), true);
                AtHiddenEdge(drawing, new Point3d(60.1, 32, 0), false);
            }
            Require(visible.Curves.All(edge => !edge.IsHidden), "Visible-only mode emitted hidden linework.");
            int contactPrunes = CoveredContact();
            return $"PASS tile-first exact: {visible.ProvenTiles}/{visible.PlannedTiles} proven tiles, " +
                $"{visible.TileFacesHidden}/{visible.TileFaceCandidates} face candidates skipped, " +
                $"{samples} owner checks, {contactPrunes} covered contact boxes skipped.";
        }

        finally
        {
            foreach (var room in rooms) room.Dispose();
        }

        static int CoveredContact()
        {
            using var blocker = Panel(-4, -4, 4, 4, 5);
            using var first = Sloped(-7, 1, .3);
            using var second = Sloped(-1, 7, -.3);
            var padding = Enumerable.Range(0, 5)
                .Select(i => new Input(Panel(-3 + i, 2, -2.5 + i, 2.5, -2), $"auxiliary-{i}")).ToArray();
            var scene = new[] {new Input(blocker, "blocker"), new Input(first, "left"), new Input(second, "right")}
                .Concat(padding).ToArray();
            try
            {
                using var drawing = V2DrawingSolver.Solve(scene, fit, .02, minimumTileDepth: 4);
                Require(drawing.ContactBoxPrunes > 0, "A covered true contact reached 3D intersection.");
                Require(drawing.Curves.All(edge => edge.Boundary != "Contact") &&
                    drawing.Regions.Select(region => region.Source).ToHashSet().SetEquals(new[] {"left", "blocker", "right"}),
                    "Contact culling altered the exposed ends of the rear panels.");
                return drawing.ContactBoxPrunes;
            }
            finally { foreach (var input in padding) input.Dispose(); }

            static Brep Sloped(double left, double right, double slope)
            {
                using var rim = new PolylineCurve(new[]
                {
                    new Point3d(left, -1, left * slope), new Point3d(right, -1, right * slope),
                    new Point3d(right, 1, right * slope), new Point3d(left, 1, left * slope),
                    new Point3d(left, -1, left * slope)
                });
                return Brep.CreatePlanarBreps(rim, fit * .01).Single();
            }
        }
        static void CheckArea(V2Drawing drawing, string source, double expected)
        {
            double area = 0;
            foreach (var region in drawing.Regions.Where(region => region.Source == source))
            {
                using var properties = AreaMassProperties.Compute(region.Brep);
                area += properties.Area;
            }
            Require(Math.Abs(area - expected) < .01, $"{source} area {area:G17}, expected {expected:G17}.");
        }

        static void CheckSeam(V2Drawing drawing, double x, double y, ref int samples)
        {
            if (new[] { -60, -55, -6, 6, 15, 15.05, 60, 66 }.Any(edge => Math.Abs(x - edge) < fit) ||
                new[] { -40, -32, -7, -5, 5, 7, 32, 40 }.Any(edge => Math.Abs(y - edge) < fit))
                return;
            CheckPoint(drawing, x, y, ref samples);
        }

        static void CheckPoint(V2Drawing drawing, double x, double y, ref int samples)
        {
            bool opening = (x > -6 && x < 6 && Math.Abs(y) < 7) ||
                (x > 15 && x < 15.05 && Math.Abs(y) < 5);
            string expected = Math.Abs(x) < 60 && Math.Abs(y) < 40 && !opening ? "wall" :
                x > -55 && x < 66 && Math.Abs(y) < 32 ? "rear" : "";
            var point = new Point3d(x, y, 0);
            var owners = drawing.Regions.Where(region =>
            {
                var face = region.Brep.Faces[0];
                return face.ClosestPoint(point, out double u, out double v) &&
                    face.IsPointOnFace(u, v, fit * .01) == PointFaceRelation.Interior;
            }).Select(region => region.Source).ToArray();
            Require(expected == "" ? owners.Length == 0 : owners.Length == 1 && owners[0] == expected,
                $"Owner at ({x:G17}, {y:G17}): expected [{expected}], got [{string.Join(", ", owners)}].");
            samples++;
        }

        static void AtHiddenEdge(V2Drawing drawing, Point3d point, bool expected)
        {
            bool found = drawing.Curves.Any(edge => edge.IsHidden && edge.Source == "rear" &&
                edge.Curve.ClosestPoint(point, out double t, fit * .1) &&
                edge.Curve.PointAt(t).DistanceTo(point) < fit * .1);
            Require(found == expected, $"Unexpected rear-line visibility at {point}.");
        }
    }

    private static Brep Panel(double x0, double y0, double x1, double y1, double z)
    {
        using var outline = Loop(x0, y0, x1, y1, z);
        return Brep.CreatePlanarBreps(outline, 1e-7).Single();
    }

    private static Curve Loop(double x0, double y0, double x1, double y1, double z) => new PolylineCurve(new[]
    {
        new Point3d(x0, y0, z), new Point3d(x1, y0, z), new Point3d(x1, y1, z),
        new Point3d(x0, y1, z), new Point3d(x0, y0, z)
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
