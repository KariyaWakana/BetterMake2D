using System;
using System.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2PlanarContact
{
    public static string Run()
    {
        const double fit = .003;
        using var outer = Loop(-7, -4, 7, 4);
        using var hole = Loop(-2, -1, 2, 1);
        using var slit = Loop(3, -1, 3.05, 1);
        using var sheet = Brep.CreatePlanarBreps(new Curve[] { outer, hole, slit }, 1e-6).Single();
        using var blade = Brep.CreateFromCornerPoints(new(-7, 0, -2), new(7, 0, -2), new(7, 0, 2), new(-7, 0, 2), fit);
        var faces = new[] { Entry(sheet, "sheet"), Entry(blade, "blade") };
        var analytic = new V2PlanarContact(faces, V2ClipPlanes.Empty, fit);
        var hit = analytic.Intersect(0, 1, faces[0].Plane.Value, faces[1].Plane.Value,
            BoundingBox.Intersection(faces[0].Box, faces[1].Box));
        var actual = analytic.Lines.Select(line => (Left: Math.Min(line.FromX, line.ToX), Right: Math.Max(line.FromX, line.ToX)))
            .OrderBy(range => range.Left).ToArray();
        var expected = new[] { (-7d, -2d), (2d, 3d), (3.05, 7d) };
        if (hit != V2PlanarContact.Result.Segments || actual.Length != expected.Length ||
            actual.Where((range, i) => Math.Abs(range.Left - expected[i].Item1) > 1e-9 ||
                Math.Abs(range.Right - expected[i].Item2) > 1e-9).Any())
            throw new InvalidOperationException("Analytic contact crossed a trim hole or dropped the 0.05 mm slit.");
        var outside = new BoundingBox(new Point3d(-1, 2, -1), new Point3d(1, 3, 1));
        if (analytic.Intersect(0, 1, faces[0].Plane.Value, faces[1].Plane.Value, outside) != V2PlanarContact.Result.Empty)
            throw new InvalidOperationException("Infinite line outside shared range was accepted.");

        var camera = new V2PerspectiveCamera(2, 100, 20);
        var back = new Plane(new Point3d(0, 0, -20), new Vector3d(-.6, 0, 1));
        var front = new Plane(new Point3d(0, 0, -18), new Vector3d(.6, 0, 1));
        var separate = new Rect(-.5, -1, .5, 1);
        if (!V2PlanarDepth.StrictlyBehind(back, front, separate, fit, camera) ||
            V2PlanarDepth.StrictlyBehind(front, back, separate, fit, camera) ||
            V2PlanarDepth.StrictlyBehind(back, front, new Rect(-10, -1, 10, 1), fit, camera))
            throw new InvalidOperationException("The perspective plane order ignored a depth switch or missed strict separation.");
        return "PASS analytic planar contact: three exact intervals around a hole and 0.05 mm slit, infinite line box rejection, and strict perspective order. " +
            RefinedCover();

        static V2Face Entry(Brep item, string source)
        {
            var face = item.Faces[0];
            if (!face.TryGetPlane(out var plane)) throw new InvalidOperationException("Expected a planar contact fixture.");
            return new V2Face(new SourceFaceId(source, 0), face, face.GetBoundingBox(true), plane, false);
        }
        static Curve Loop(double x, double y, double a, double b) => new PolylineCurve(new[]
        {
            new Point3d(x, y, 0), new Point3d(a, y, 0), new Point3d(a, b, 0),
            new Point3d(x, b, 0), new Point3d(x, y, 0)
        });
    }

    private static string RefinedCover()
    {
        const double fit = 1e-6, minimum = .43;
        using var fixedPlane = Brep.CreateFromCornerPoints(new(-.1,-.1,0), new(1.1,-.1,0),
            new(1.1,1.1,0), new(-.1,1.1,0), fit);
        using var clear = Ribbon(.005);
        using var crossing = Ribbon(-.005);
        var loose = new BoundingBox(new Point3d(0, 0, -.3), new Point3d(1, 1, .4));
        var faces = new[]
        {
            new V2Face(new SourceFaceId("flat", 0), fixedPlane.Faces[0], fixedPlane.GetBoundingBox(true), Plane.WorldXY, false),
            new V2Face(new SourceFaceId("clear", 0), clear.Faces[0], loose, null, false),
            new V2Face(new SourceFaceId("crossing", 0), crossing.Faces[0], loose, null, false),
        };
        var bounds = new V2SurfaceBounds(faces, fit, null);
        bool coarse = bounds.Separated(0, 1, faces, fit);
        bool refined = bounds.SeparatedByContactTree(0, 1, faces, fit);
        bool cut = bounds.SeparatedByContactTree(0, 2, faces, fit);
        if (coarse || !refined || cut)
            throw new InvalidOperationException($"Unexpected contact proof: coarse {coarse}, refined {refined}, crossing {cut}.");
        return "Refined contact rejected a shallow, separated curved ribbon and kept a true crossing.";

        static Brep Ribbon(double lift)
        {
            using var section = NurbsCurve.Create(false, 2, new[]
            {
                new Point3d(0, 0, minimum * minimum + lift),
                new Point3d(.5, 0, minimum * (minimum - 1) + lift),
                new Point3d(1, 0, (1 - minimum) * (1 - minimum) + lift)
            });
            using var surface = Surface.CreateExtrusion(section, Vector3d.YAxis);
            var ribbon = surface.ToBrep();
            var meshes = Mesh.CreateFromBrep(ribbon);
            try
            {
                if (meshes.Length != 1 || !ribbon.Faces[0].SetMesh(MeshType.Render, meshes[0]))
                    throw new InvalidOperationException("Could not attach the ribbon's render mesh.");
                return ribbon;
            }
            finally { foreach (var mesh in meshes) mesh.Dispose(); }
        }
    }
}
