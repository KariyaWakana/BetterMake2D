using System;
using System.Collections.Generic;
using System.Diagnostics;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class V2Silhouettes
{
    internal static IEnumerable<Curve> Read(BrepFace face, IProjectionCamera camera, double fit, double angle,
        Action<long> measure = null)
    {
        var direction = camera.ViewRay(Point2d.Origin).Direction;
        if (!direction.Unitize()) yield break;
        long start = Stopwatch.GetTimestamp();
        const SilhouetteType types = SilhouetteType.Tangent | SilhouetteType.Projecting;
        var rims = camera.Perspective
            ? Silhouette.Compute(face, types, Point3d.Origin, fit, angle)
            : Silhouette.Compute(face, types, direction, fit, angle);
        measure?.Invoke(start);
        foreach (var rim in rims ?? Array.Empty<Silhouette>())
        {
            var curve = rim.Curve;
            if (curve != null && curve.IsValid)
                yield return curve;
            else
                curve?.Dispose();
        }
    }

}
