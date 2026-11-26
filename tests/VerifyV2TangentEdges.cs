using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2TangentEdges
{
    public static string Run() => CheckAccuracy(V2Accuracy.Exact);

    internal static string CheckAccuracy(V2Accuracy accuracy)
    {
        const double fit = .001, angle = .01;
        var seam = Point3d.Origin;
        using var straight = new LineCurve(new Point3d(-8, 0, 0), seam);
        using var rounded = new ArcCurve(new Arc(seam, Vector3d.XAxis, new Point3d(4, 0, -2)));
        using var smooth = Wall(straight, rounded);
        using var coplanar = new LineCurve(seam, new Point3d(6, 0, 0));
        using var smoothPlane = Wall(straight, coplanar);
        Check("straight to round G1 seam", new[] {new Input(smooth, "rounded")}, new Point3d(0, 3, 0), true);
        Check("coplanar shared seam", new[] {new Input(smoothPlane, "coplanar")}, new Point3d(0, 3, 0), true);
        Check("tiled coplanar shared seam", new[] {new Input(smoothPlane, "coplanar")}, new Point3d(0, 3, 0), true,
            minimumTileDepth: 2);

        using var rising = new LineCurve(new Point3d(-5, 0, -5), seam);
        using var falling = new LineCurve(seam, new Point3d(5, 0, -5));
        using var crease = Wall(rising, falling);
        Check("90-degree crease", new[] {new Input(crease, "crease")}, new Point3d(0, 3, 0), false);

        double radiusAt45 = Math.Sqrt(12.5);
        var rim = new Point3d(5, 0, 0);
        using var near = new ArcCurve(new Arc(new Point3d(0, 0, 5), new Point3d(radiusAt45, 0, radiusAt45), rim));
        using var far = new ArcCurve(new Arc(rim, new Point3d(radiusAt45, 0, -radiusAt45), new Point3d(0, 0, -5)));
        using var outline = Wall(near, far);
        Require(outline.Edges.Single(edge => edge.Valence == EdgeAdjacency.Interior).IsSmoothManifoldEdge(angle),
            "Fixture outline must be a real G1 shared edge");
        Check("G1 seam on the silhouette", new[] {new Input(outline, "outline")}, new Point3d(5, 3, 0), false);

        using var shelf = new LineCurve(new Point3d(0, 0, 5), new Point3d(8, 0, 5));
        using var upright = new LineCurve(shelf.PointAtEnd, new Point3d(8, 0, 0));
        using var rear = new LineCurve(upright.PointAtEnd, new Point3d(-5, 0, 0));
        using var selfOccluding = Wall(shelf, upright, rear);
        Check("same-instance self-occlusion", new[] {new Input(selfOccluding, "shelf")}, new Point3d(0, 3, 0), false);

        using var panel = Rectangle(-8, -1, 8, 7);
        using var mullion = Rectangle(0, 0, .6, 6);
        Check("different-object coplanar mullion", new[] {new Input(panel, "panel"), new Input(mullion, "mullion")},
            new Point3d(0, 3, 0), false);
        if (accuracy == V2Accuracy.Exact &&
            Environment.GetEnvironmentVariable("BMA2D_V2_TANGENT_TEST_OUTPUT") is { Length: > 0 } path)
            CheckExports(path);
        return "PASS tangent on/off: planar and round G1 seams (including tiles), 90-degree crease, grazing silhouette, self-occlusion, separate coplanar mullion; identical fills and tangent export metadata.";

        void CheckExports(string path)
        {
            Directory.CreateDirectory(path);
            var inputs = new[] {new Input(smoothPlane, "coplanar")};
            var styles = new Dictionary<string, (string, Color)> { ["coplanar"] = ("Wall", Color.Black) };
            foreach (bool enabled in new[] {false, true})
            {
                string name = enabled ? "on" : "off";
                string drawingPath = Path.Combine(path, name + ".3dm");
                string svgPath = Path.Combine(path, name + ".svg");
                using var drawing = V2DrawingSolver.Solve(inputs, fit, angle, tangentEdges: enabled);
                V2DrawingOutput.Write(drawing, drawingPath, UnitSystem.Millimeters, fit, styles);
                using var reopened = File3dm.Read(drawingPath);
                Require(reopened.Objects.Count(item => item.Attributes.GetUserString("BoundaryType") == "Tangent") ==
                    (enabled ? 1 : 0), "Rhino tangent metadata did not round trip");
                ElevationSvgExport.Write(drawingPath, svgPath, 1);
                Require(File.ReadAllText(svgPath).Contains("data-boundary-type=\"Tangent\"") == enabled,
                    "Tangent visibility did not reach SVG");
            }
        }

        void Check(string label, Input[] inputs, Point3d probe, bool tangent, int minimumTileDepth = 0)
        {
            using var off = V2DrawingSolver.Solve(inputs, fit, angle, minimumTileDepth: minimumTileDepth, accuracy: accuracy);
            using var on = V2DrawingSolver.Solve(inputs, fit, angle, minimumTileDepth: minimumTileDepth,
                tangentEdges: true, accuracy: accuracy);
            Require(on.Regions.Count > 0 && off.Regions.Count == on.Regions.Count && off.Regions.Zip(on.Regions).All(pair =>
                pair.First.Source == pair.Second.Source && pair.First.FaceIndex == pair.Second.FaceIndex &&
                pair.First.Brep.IsValid && GeometryBase.GeometryEquals(pair.First.Brep, pair.Second.Brep)), label + ": fills differ");
            Require(HasStroke(on, probe, tangent ? "Tangent" : null), label + ": expected stroke absent with TangentEdges=Yes");
            Require(HasStroke(off, probe, null) != tangent, label + ": wrong stroke with TangentEdges=No");
            Require(off.TangentChainsFiltered == on.TangentChains && (on.TangentChains > 0) == tangent &&
                off.Curves.All(edge => edge.Boundary != "Tangent"), label + ": unexpected tangent count");
            if (minimumTileDepth == 0 || accuracy != V2Accuracy.Exact) return;
            var owned = inputs.Select(input => new Input(input.Brep.DuplicateBrep(), input.Source)).ToArray();
            bool released = false;
            try
            {
                using var detached = V2DrawingSolver.Solve(owned, fit, angle, minimumTileDepth: minimumTileDepth,
                    tangentEdges: true, accuracy: accuracy, spillDuringBoundaryCollection: true, releaseInputs: Release);
                Require(released && detached.TangentChains == on.TangentChains && HasStroke(detached, probe, "Tangent") &&
                    detached.Regions.Count == on.Regions.Count &&
                    detached.Regions.Zip(on.Regions).All(pair =>
                        GeometryBase.GeometryEquals(pair.First.Brep, pair.Second.Brep)),
                    "Releasing the input Brep changed a shared tangent edge or its closed regions.");
            }
            finally { Release(); }

            void Release()
            {
                if (released) return;
                foreach (var input in owned) input.Dispose();
                released = true;
            }
        }

        bool HasStroke(V2Drawing drawing, Point3d p, string kind) => drawing.Curves.Any(edge =>
            (kind == null || edge.Boundary == kind) && edge.Curve.ClosestPoint(p, out double t) &&
            edge.Curve.PointAt(t).DistanceTo(p) < fit * .2);

        Brep Wall(params Curve[] profiles)
        {
            var pieces = profiles.Select(curve =>
            {
                using var surface = Surface.CreateExtrusion(curve, new Vector3d(0, 6, 0));
                return surface.ToBrep();
            }).ToArray();
            try
            {
                var joined = Brep.JoinBreps(pieces, fit);
                Require(joined is { Length: 1 } && joined[0].Faces.Count == profiles.Length, "Invalid shared-edge fixture");
                return joined[0];
            }
            finally { foreach (var piece in pieces) piece.Dispose(); }
        }

        Brep Rectangle(double x0, double y0, double x1, double y1) => Brep.CreateFromCornerPoints(
            new Point3d(x0, y0, 0), new Point3d(x1, y0, 0), new Point3d(x1, y1, 0), new Point3d(x0, y1, 0), fit);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
