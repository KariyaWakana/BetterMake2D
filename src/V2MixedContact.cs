using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal static class V2MixedContact
{
    internal static bool NearTangent(BrepFace surface, Plane plane, Curve[] seeds, double fit)
    {
        foreach (var curve in seeds)
        {
            var point = curve.PointAt(curve.Domain.Mid);
            if (!surface.ClosestPoint(point, out double u, out double v) ||
                surface.PointAt(u, v).DistanceTo(point) > fit * .2)
                continue;
            var normal = surface.NormalAt(u, v);
            if (normal.Unitize() && Math.Abs(normal * plane.Normal) >= .99995)
                return true;
        }
        return false;
    }

    // The ordinary solve can report a marginal trim contact yet miss a
    // separate, nearly tangent crossing. Recover that contact without altering
    // the original trim, relaxing ownership, or discarding the prior curves.
    internal static List<Curve> Refine(Brep a, Brep b, Curve[] seeds, double fit)
    {
        bool ok = Intersection.BrepBrep(a, b, fit * .001, out var curves, out _);
        var extra = new List<Curve>();
        double same = fit * .01;
        foreach (var curve in curves ?? Array.Empty<Curve>())
        {
            if (ok && curve.IsValid && curve.GetLength() > fit * .001 && IsSeparated(curve))
                extra.Add(curve);
            else
                curve.Dispose();
        }
        return extra;

        bool IsSeparated(Curve candidate)
        {
            var first = candidate.PointAt(candidate.Domain.ParameterAt(.1));
            var middle = candidate.PointAt(candidate.Domain.Mid);
            var last = candidate.PointAt(candidate.Domain.ParameterAt(.9));
            foreach (var seed in seeds)
                if (Touches(seed, first) || Touches(seed, middle) || Touches(seed, last))
                    return false;
            return true;
        }

        bool Touches(Curve seed, Point3d point) => seed.ClosestPoint(point, out double t, same) &&
            seed.PointAt(t).DistanceTo(point) <= same;
    }
}
