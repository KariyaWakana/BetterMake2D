using System;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class V2AnalyticSupport
{
    internal static bool Same(Surface a, Surface b, double span, double tolerance)
    {
        // Fit each primitive more tightly than the shared support distance.
        // Reserve the remaining error for the differences between primitives.
        double shapeTolerance = tolerance * .125;
        double matchTolerance = tolerance * .25;
        if (a.TryGetCylinder(out var ca, shapeTolerance) && b.TryGetCylinder(out var cb, shapeTolerance))
            return Math.Abs(ca.Radius - cb.Radius) <= matchTolerance &&
                Parallel(ca.Axis, cb.Axis) &&
                Vector3d.CrossProduct(cb.Center - ca.Center, ca.Axis).Length <= matchTolerance;
        if (a.TryGetSphere(out var sa, shapeTolerance) && b.TryGetSphere(out var sb, shapeTolerance))
            return Math.Abs(sa.Radius - sb.Radius) <= matchTolerance &&
                sa.Center.DistanceTo(sb.Center) <= matchTolerance;
        if (a.TryGetCone(out var qa, shapeTolerance) && b.TryGetCone(out var qb, shapeTolerance))
            return qa.ApexPoint.DistanceTo(qb.ApexPoint) <= matchTolerance && Parallel(qa.Axis, qb.Axis) &&
                qa.Axis * qb.Axis > 0 &&
                Math.Abs(qa.AngleInRadians() - qb.AngleInRadians()) * span <= matchTolerance;
        if (a.TryGetTorus(out var ta, shapeTolerance) && b.TryGetTorus(out var tb, shapeTolerance))
            return Math.Abs(ta.MajorRadius - tb.MajorRadius) <= matchTolerance &&
                Math.Abs(ta.MinorRadius - tb.MinorRadius) <= matchTolerance &&
                ta.Plane.Origin.DistanceTo(tb.Plane.Origin) <= matchTolerance &&
                Parallel(ta.Plane.Normal, tb.Plane.Normal);
        return false;

        bool Parallel(Vector3d x, Vector3d y) =>
            Vector3d.CrossProduct(x, y).Length * span <= matchTolerance;
    }
}
