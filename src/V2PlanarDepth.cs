using System;
using Rhino.Geometry;

namespace BetterMake2D;

// z is affine in a parallel view. In perspective, 1/(-z) is affine
// provided the support plane does not pass through the eye.
internal readonly record struct V2PlanarDepth(double X, double Y, double Origin, bool Reciprocal)
{
    internal double At(double x, double y) => Origin + X * x + Y * y;
    internal double Minimum(Rect box) => At(X >= 0 ? box.Left : box.Right, Y >= 0 ? box.Bottom : box.Top);
    internal double Maximum(Rect box) => At(X >= 0 ? box.Right : box.Left, Y >= 0 ? box.Top : box.Bottom);

    internal bool TryExtrema(Rect box, out double back, out double front)
    {
        double slack = 1e-12 * (1e-12 + Math.Abs(Origin) +
            Math.Abs(X) * Math.Max(Math.Abs(box.Left), Math.Abs(box.Right)) +
            Math.Abs(Y) * Math.Max(Math.Abs(box.Bottom), Math.Abs(box.Top)));
        back = Minimum(box) - slack;
        front = Maximum(box) + slack;
        if (Reciprocal)
        {
            if (!(back > 0)) return false;
            back = -1 / back;
            front = -1 / front;
        }
        return double.IsFinite(back) && double.IsFinite(front);
    }

    internal static bool TryCreate(Plane plane, IProjectionCamera camera, double retreat, out V2PlanarDepth depth)
    {
        depth = default;
        var n = plane.Normal;
        double k = n * (plane.Origin - Point3d.Origin);
        if (camera.Perspective)
        {
            if (Math.Abs(k) <= Math.Abs(retreat)) return false;
            k += Math.CopySign(1, k) * retreat;
            double unit = camera.AtDepth(new Point2d(1, 0), -1).X;
            depth = new(n.X * unit / k, n.Y * unit / k, -n.Z / k, true);
        }
        else
        {
            if (Math.Abs(n.Z) < .01) return false;
            depth = new(-n.X / n.Z, -n.Y / n.Z, k / n.Z - retreat / Math.Abs(n.Z), false);
        }
        return double.IsFinite(depth.X) && double.IsFinite(depth.Y) && double.IsFinite(depth.Origin);
    }

    internal static bool StrictlyBehind(Plane a, Plane b, Rect overlap, double fit, IProjectionCamera camera)
    {
        if (!overlap.HasArea || !camera.Perspective ||
            !TryCreate(a, camera, -fit, out var backForward) ||
            !TryCreate(b, camera, fit, out var frontBack))
            return false;
        double aMin = backForward.Minimum(overlap), bMin = frontBack.Minimum(overlap);
        double aMax = backForward.Maximum(overlap), bMax = frontBack.Maximum(overlap);
        double aError = Error(backForward), bError = Error(frontBack);
        aMax += aError;
        bMax += bError;
        double near = -camera.ViewRay(Point2d.Origin).From.Z;
        if (!(aMin > aError && bMin > bError && near > 0) || Math.Max(aMax, bMax) > 1 / near)
            return false;
        var gap = new V2PlanarDepth(frontBack.X - backForward.X, frontBack.Y - backForward.Y,
            frontBack.Origin - backForward.Origin, true);
        double slack = aError + bError + Error(gap);
        return gap.Minimum(overlap) > fit * aMax * bMax + slack;

        double Error(V2PlanarDepth field) => 1e-12 * (Math.Abs(field.Origin) +
            Math.Abs(field.X) * Math.Max(Math.Abs(overlap.Left), Math.Abs(overlap.Right)) +
            Math.Abs(field.Y) * Math.Max(Math.Abs(overlap.Bottom), Math.Abs(overlap.Top)));
    }
}
