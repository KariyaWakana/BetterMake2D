using System;
using Rhino.Geometry;

namespace BetterMake2D;

// The axes only choose a frame. All extents enclose the complete source hull.
internal readonly record struct V2OrientedBounds(Point3d Center, Vector3d X, Vector3d Y, Vector3d Z, Vector3d Half)
{
    internal bool IsValid => !X.IsZero;

    internal double Radius(Vector3d axis) =>
        Math.Abs(axis * X) * Half.X + Math.Abs(axis * Y) * Half.Y + Math.Abs(axis * Z) * Half.Z;

    internal bool Separated(V2OrientedBounds other, double tolerance)
    {
        if (!IsValid || !other.IsValid) return false;
        var delta = other.Center - Center;
        var first = this;
        double pad = tolerance + Math.Max(Center.MaximumCoordinate, other.Center.MaximumCoordinate) * 1e-12;
        return Apart(X) || Apart(Y) || Apart(Z) || Apart(other.X) || Apart(other.Y) || Apart(other.Z) ||
            Cross(X, other.X) || Cross(X, other.Y) || Cross(X, other.Z) ||
            Cross(Y, other.X) || Cross(Y, other.Y) || Cross(Y, other.Z) ||
            Cross(Z, other.X) || Cross(Z, other.Y) || Cross(Z, other.Z);

        bool Apart(Vector3d axis) => Math.Abs(delta * axis) > first.Radius(axis) + other.Radius(axis) + pad;
        bool Cross(Vector3d a, Vector3d b)
        {
            var axis = Vector3d.CrossProduct(a, b);
            return axis.SquareLength > 1e-16 && axis.Unitize() && Apart(axis);
        }
    }

    internal bool Restrict(Point3d origin, Vector3d direction, ref double near, ref double far)
    {
        if (!IsValid) return true;
        var delta = Center - origin;
        return Limit(X, Half.X, delta, direction, ref near, ref far) &&
            Limit(Y, Half.Y, delta, direction, ref near, ref far) &&
            Limit(Z, Half.Z, delta, direction, ref near, ref far);
    }

    private static bool Limit(Vector3d axis, double half, Vector3d delta, Vector3d direction,
        ref double near, ref double far)
    {
        double along = axis * direction;
        double center = axis * delta;
        double pad = 1e-12 * Math.Max(1, Math.Abs(center) + half);
        if (along == 0) return Math.Abs(center) <= half + pad;
        double a = (center - half - pad) / along, b = (center + half + pad) / along;
        if (!double.IsFinite(a) || !double.IsFinite(b)) return true;
        near = Math.Max(near, Math.Min(a, b));
        far = Math.Min(far, Math.Max(a, b));
        return near <= far;
    }
}
