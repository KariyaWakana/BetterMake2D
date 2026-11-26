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
                piercing.LazyContactPasses[0].Native != 1)
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
                triple.LazyContactPasses[0].Native != 2 || triple.LazyContactPasses[1].Native != 1)
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
                pass.RebuiltTiles > 0 && pass.RebuiltTiles < pass.Tiles) ||
                tiled.LazyDirtyCells == 0 || tiled.LazyFrozenCells == 0)
                throw new InvalidOperationException("A contact confined to part of the frame must reuse unaffected tiles.");
        }
        CheckDirtyPatch();
        return "PASS lazy native contact: two-round piercing, bounded dirty patches, frozen holes, replaced spans and clipped tile seams.";
    }

    private static void CheckDirtyPatch()
    {
        var segments = new System.Collections.Generic.List<V2Segment>();
        const double grid = 1;
        var window = new V2TileWindow(0, 0, 120, 100);
        int id = 0;
        segments.Add(Segment(0, 0, 120, 0, V2TiledArrangement.SeamKind));
        segments.Add(Segment(120, 0, 120, 100, V2TiledArrangement.SeamKind));
        segments.Add(Segment(120, 100, 0, 100, V2TiledArrangement.SeamKind));
        segments.Add(Segment(0, 100, 0, 0, V2TiledArrangement.SeamKind));
        segments.Add(Segment(40, 0, 40, 100));
        segments.Add(Segment(80, 0, 80, 100));
        segments.Add(Segment(10, 10, 20, 10));
        segments.Add(Segment(20, 10, 20, 20));
        segments.Add(Segment(20, 20, 10, 20));
        segments.Add(Segment(10, 20, 10, 10));
        segments.Add(Segment(12, 30, 18, 30));

        var plan = V2PlanarArrangement.Build(segments, grid, null);
        foreach (var cell in plan.Cells) cell.Owner = 77;
        using var store = new V2TileTopology();
        plan = store.Load(store.Save(plan), grid);
        var contact = Segment(0, 50, 40, 50, "Contact");
        var first = plan.Repair(window, new[] {new V2TileSegmentEdit(null, contact)}, p => Classify(p, 50), null);
        if (first.FrozenCells != 2 || Owner(first.Plan, 15, 15) != 77 || Owner(first.Plan, 100, 10) != 77 ||
            Owner(first.Plan, 10, 45) != 1 || Owner(first.Plan, 10, 55) != 2)
            throw new InvalidOperationException("A contact edit disturbed a frozen cell or failed to split its dirty cell.");

        var moved = contact with {A = new Point2d(0, 60), B = new Point2d(40, 60)};
        var second = first.Plan.Repair(window, new[] {new V2TileSegmentEdit(contact, moved)}, p => Classify(p, 60), null);
        if (Owner(second.Plan, 10, 55) != 1 || Owner(second.Plan, 10, 65) != 2 ||
            Owner(second.Plan, 15, 15) != 77 || Owner(second.Plan, 100, 10) != 77 ||
            Enumerable.Range(0, second.Plan.EdgeCount).Any(i => second.Plan.Start(i * 2).Y == 50 &&
                second.Plan.Start(i * 2 + 1).Y == 50 && second.Plan.Sources(i * 2).Contains(contact.Source)))
            throw new InvalidOperationException("A moved source left its obsolete contact in the saved topology.");

        var third = second.Plan.Repair(window, new[]
        {
            new V2TileSegmentEdit(null, Segment(18, 30, 15, 37, "Contact")),
            new V2TileSegmentEdit(null, Segment(15, 37, 12, 30, "Contact"))
        }, local =>
        {
            Classify(local, 60);
            local.Cells.Single(cell => local.Contains(cell, new Point2d(15, 32))).Owner = 4;
        }, null);
        if (Owner(third.Plan, 15, 32) != 4 || Owner(third.Plan, 15, 15) != 77)
            throw new InvalidOperationException("The local edit failed to close an existing dangling source.");

        void Classify(V2PlanarArrangement local, double y)
        {
            foreach (var cell in local.Cells)
            {
                if (!local.TryInterior(cell, out var interior) || interior.X > 80 ||
                    interior.X > 10 && interior.X < 20 && interior.Y > 10 && interior.Y < 20)
                    throw new InvalidOperationException("A frozen cell was sent back for depth classification.");
                cell.Owner = interior.X > 40 ? 3 : interior.Y < y ? 1 : 2;
            }
        }
        V2Segment Segment(double ax, double ay, double bx, double by, string kind = "Original") =>
            new(new Point2d(ax, ay), new Point2d(bx, by), new V2SourceSpan(id++, 0, kind, 0, 1));
        static int Owner(V2PlanarArrangement drawing, double x, double y) =>
            drawing.Cells.Single(cell => drawing.Contains(cell, new Point2d(x, y))).Owner;
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
