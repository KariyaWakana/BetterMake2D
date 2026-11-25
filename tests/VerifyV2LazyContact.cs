using System;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2LazyContact
{
    private const double Fit = .001;
    private const double Width = 12;
    private const double Height = 4;

    public static string Run()
    {
        using var plane = Brep.CreateFromCornerPoints(new(0, 0, 0), new(Width, 0, 0),
            new(Width, Height, 0), new(0, Height, 0), Fit);
        using var first = Ribbon(8);
        using var second = Ribbon(8.75);

        // Identical projected rims give a single initial bounded cell. The rectangle's
        // interior probe is at x = 6, where both ribbons are below the plane.
        using (var piercing = V2DrawingSolver.Solve(new[] { new Input(plane, "w"), new Input(first, "u") },
            Fit, .04, enableHiZ: false, renderMeshHints: false))
        {
            OwnerAt(piercing, 6, "w");
            OwnerAt(piercing, 8, "u");
            OwnerAt(piercing, 10, "w");
            LineAt(piercing, 7);
            LineAt(piercing, 9);
            if (piercing.LazyInitialOwners != 1 || piercing.LazyContactPasses.Count != 1 ||
                piercing.LazyContactPasses[0].Native != 1 || piercing.LazyContactPasses[0].Completion)
                throw new InvalidOperationException("The piercing must be exposed by the first native frontier batch.");
        }

        // The w/u and w/v contacts reveal the curved owners. Their visible switch
        // at x = 8.375 lies strictly inside both positive intervals and needs u/v.
        using (var triple = V2DrawingSolver.Solve(new[] { new Input(plane, "w"), new Input(first, "u"),
            new Input(second, "v") }, Fit, .04, enableHiZ: false, renderMeshHints: false))
        {
            OwnerAt(triple, 6, "w");
            OwnerAt(triple, 7.5, "u");
            OwnerAt(triple, 8.25, "u");
            OwnerAt(triple, 8.5, "v");
            OwnerAt(triple, 9.5, "v");
            OwnerAt(triple, 10, "w");
            LineAt(triple, 8.375);
            if (triple.LazyInitialOwners != 1 || triple.LazyContactPasses.Count != 2 ||
                triple.LazyContactPasses[0].Native != 2 || triple.LazyContactPasses[1].Native != 1 ||
                triple.LazyContactPasses.Any(pass => pass.Completion))
                throw new InvalidOperationException("The curved pair must be exposed by the second native frontier batch.");
        }

        using (var tiled = V2DrawingSolver.Solve(new[] { new Input(plane, "w"), new Input(first, "u"),
            new Input(second, "v") }, Fit, .04, minimumTileDepth: 3, enableHiZ: false, renderMeshHints: false))
        {
            OwnerAt(tiled, 6, "w");
            OwnerAt(tiled, 8.25, "u");
            OwnerAt(tiled, 8.5, "v");
            LineAt(tiled, 8.375);
            if (tiled.Tiles.Count < 8 || !tiled.LazyContactPasses.Any(pass =>
                pass.RebuiltTiles > 0 && pass.RebuiltTiles < pass.Tiles))
                throw new InvalidOperationException("A contact confined to part of the frame must reuse unaffected tiles.");
        }
        return "PASS lazy native contact: an interior piercing, a curved pair exposed in round two, and reuse of unaffected tiles.";
    }

    // A quadratic Bézier section evaluates to z = 1 - (x - center)^2.
    private static Brep Ribbon(double center)
    {
        double start = 1 - center * center;
        double end = Width - center;
        using var section = NurbsCurve.Create(false, 2, new[]
        {
            new Point3d(0, 0, start),
            new Point3d(Width * .5, 0, start + center * Width),
            new Point3d(Width, 0, 1 - end * end)
        });
        using var surface = Surface.CreateExtrusion(section, new Vector3d(0, Height, 0));
        return surface.ToBrep();
    }

    private static void OwnerAt(V2Drawing drawing, double x, string expected)
    {
        var point = new Point3d(x, Height * .5, 0);
        var owners = drawing.Regions.Where(region => VerifyV2Drawing.Contains(region.Brep, point, Fit * .01))
            .Select(region => region.Source).ToArray();
        if (owners.Length != 1 || owners[0] != expected)
            throw new InvalidOperationException($"At {point}, expected {expected}; got [{string.Join(", ", owners)}].");
    }

    private static void LineAt(V2Drawing drawing, double x)
    {
        var point = new Point3d(x, Height * .5, 0);
        if (!drawing.Curves.Any(edge => edge.Curve.ClosestPoint(point, out _, Fit * 2)))
            throw new InvalidOperationException($"Expected an exact visible contact line at {point}.");
    }
}
