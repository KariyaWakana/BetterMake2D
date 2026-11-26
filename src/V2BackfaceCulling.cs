using System;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class V2BackfaceCulling
{
    internal static int OutwardSign(Input input, BoundingBox bounds, IProjectionCamera camera,
        V2ClipPlanes clips, double tolerance)
    {
        var body = input.Brep;
        if (!input.Opaque || body == null || !bounds.IsValid || !body.IsSolid)
            return 0;
        bounds.Inflate(tolerance);
        if (!clips.Keeps(bounds)) return 0;
        if (camera != null)
        {
            if (camera.Perspective && (bounds.Contains(Point3d.Origin) ||
                !camera.ViewClips.Active || !camera.ViewClips.Keeps(bounds)))
                return 0;
            if (!camera.Includes(bounds.Min) || !camera.Includes(bounds.Max)) return 0;
        }
        if (!body.IsValid) return 0;
        return body.SolidOrientation switch
        {
            BrepSolidOrientation.Outward => 1,
            BrepSolidOrientation.Inward => -1,
            _ => 0
        };
    }

    internal static bool IsBackPlane(BrepFace face, Plane fitted, BoundingBox bounds, int outwardSign,
        bool perspective, double tolerance)
    {
        double magnitude = Math.Max(1, Math.Max(bounds.Min.MaximumCoordinate, bounds.Max.MaximumCoordinate));
        double roundoff = 256 * 2.220446049250313e-16 * magnitude;
        double margin = Math.Max(tolerance, roundoff);
        var outward = fitted.Normal;
        if (!outward.Unitize() ||
            (perspective ? Math.Abs(outward * (fitted.Origin - Point3d.Origin)) <= margin : Math.Abs(outward.Z) <= 1e-8))
            return false;
        if (!TryInteriorNormal(face, roundoff, out var normal) || Math.Abs(outward * normal) < 1 - 1e-8)
            return false;
        if ((outward * normal < 0) ^ face.OrientationIsReversed ^ (outwardSign < 0)) outward.Reverse();
        if (perspective ? outward * (fitted.Origin - Point3d.Origin) <= margin : outward.Z >= -1e-8)
            return false;

        // The ordinary modeling-tolerance plane fit is insufficient here.
        // Only a plane or a positive-weight control net confined to that
        // plane to roundoff can certify constant orientation over the face.
        using var support = face.UnderlyingSurface();
        if (support is PlaneSurface) return true;
        using var net = support?.ToNurbsSurface();
        if (net == null) return false;
        for (int u = 0; u < net.Points.CountU; u++)
        for (int v = 0; v < net.Points.CountV; v++)
        {
            var point = net.Points.GetControlPoint(u, v);
            if (!(point.Weight > 0) || Math.Abs(fitted.DistanceTo(point.Location)) > roundoff)
                return false;
        }
        return true;
    }

    internal static bool SharesCurvedRim(BrepFace face, double planarTolerance)
    {
        var body = face.Brep;
        foreach (int edge in face.AdjacentEdges())
            foreach (int neighbor in body.Edges[edge].AdjacentFaces())
                if (neighbor != face.FaceIndex && !body.Faces[neighbor].TryGetPlane(out _, planarTolerance))
                    return true;
        return false;
    }

    private static bool TryInteriorNormal(BrepFace face, double tolerance, out Vector3d normal)
    {
        var u = face.Domain(0);
        var v = face.Domain(1);
        ReadOnlySpan<double> positions = [.5, .25, .75];
        foreach (double s in positions)
        foreach (double t in positions)
        {
            double x = u.ParameterAt(s), y = v.ParameterAt(t);
            if (face.IsPointOnFace(x, y, tolerance) != PointFaceRelation.Interior) continue;
            normal = face.NormalAt(x, y);
            // RhinoCommon 9 already applies the face orientation here.
            // Recover the support normal before the explicit Brep XOR.
            if (face.OrientationIsReversed) normal.Reverse();
            if (normal.Unitize()) return true;
        }
        normal = Vector3d.Unset;
        return false;
    }
}
