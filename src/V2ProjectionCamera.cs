using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

internal interface IProjectionCamera
{
    Point2d Project(Point3d viewPoint);
    Line ViewRay(Point2d point);
    bool Includes(Point3d viewPoint);
    Curve ProjectCurve(Curve viewCurve);
    V2ClipPlanes Clips { get; }
    V2ClipPlanes ViewClips => V2ClipPlanes.Empty;
    bool Perspective => false;
    bool IsSection(Plane plane) => true;
    Rect Bounds(BoundingBox box) => Rect.From(box);
    Point3d AtDepth(Point2d p, double z) => new(p.X, p.Y, z);
    double ScreenMargin(BoundingBox box, double worldMargin) => worldMargin;
    double ImageTolerance(double modelTolerance, BoundingBox extent, double millimetersPerUnit) => modelTolerance;
}

internal sealed class V2ParallelCamera(double far, double near, IReadOnlyList<Plane> clips = null) : IProjectionCamera
{
    public V2ClipPlanes Clips { get; } = new(clips ?? Array.Empty<Plane>());
    public Point2d Project(Point3d viewPoint) => new(viewPoint.X, viewPoint.Y);
    public Line ViewRay(Point2d point) => new(new Point3d(point.X, point.Y, near), new Point3d(point.X, point.Y, far));
    public bool Includes(Point3d viewPoint) => viewPoint.Z >= far && viewPoint.Z <= near && Clips.Includes(viewPoint);

    public Curve ProjectCurve(Curve viewCurve)
    {
        var projected = viewCurve.ToNurbsCurve();
        if (projected != null && projected.Transform(Transform.PlanarProjection(Plane.WorldXY)) && projected.IsValid)
            return projected;
        projected?.Dispose();
        return null;
    }
}

// All coordinates are relative to the eye. Z is physical view depth, negative in front.
internal sealed class V2PerspectiveCamera : IProjectionCamera
{
    private readonly double near, far, focal;
    private readonly Rect imageWindow;
    private readonly bool framed;
    private readonly Plane[] sections;

    internal V2PerspectiveCamera(double near, double far, double focal, IReadOnlyList<Plane> sections = null,
        Rect? nearWindow = null)
    {
        if (!(near > 0 && far > near && focal > 0) || !double.IsFinite(far) || !double.IsFinite(focal))
            throw new ArgumentOutOfRangeException(nameof(near), "A perspective camera needs finite positive optical distances.");
        this.near = near;
        this.far = far;
        this.focal = focal;
        framed = nearWindow.HasValue;
        this.sections = sections?.ToArray() ?? Array.Empty<Plane>();
        // With no viewport crop, retain an 80-degree half-angle about the optical axis.
        double safety = Math.Tan(80 * Math.PI / 180);
        var slopes = nearWindow is Rect frame
            ? new Rect(frame.Left / near, frame.Bottom / near, frame.Right / near, frame.Top / near)
            : new Rect(-safety, -safety, safety, safety);
        if (!slopes.HasArea || !double.IsFinite(slopes.Left + slopes.Right + slopes.Bottom + slopes.Top))
            throw new ArgumentOutOfRangeException(nameof(nearWindow), "The perspective frame must have finite positive area.");
        imageWindow = new Rect(slopes.Left * focal, slopes.Bottom * focal, slopes.Right * focal, slopes.Top * focal);
        var optics = new[]
        {
            new Plane(new Point3d(0, 0, -near), Vector3d.ZAxis),
            new Plane(new Point3d(0, 0, -far), -Vector3d.ZAxis),
            new Plane(Point3d.Origin, new Vector3d(-1, 0, -slopes.Left)),
            new Plane(Point3d.Origin, new Vector3d(1, 0, slopes.Right)),
            new Plane(Point3d.Origin, new Vector3d(0, -1, -slopes.Bottom)),
            new Plane(Point3d.Origin, new Vector3d(0, 1, slopes.Top))
        };
        ViewClips = new V2ClipPlanes(optics);
        Clips = this.sections.Length == 0 ? ViewClips : new V2ClipPlanes(optics.Concat(this.sections).ToArray());
    }

    public bool Perspective => true;
    public V2ClipPlanes Clips { get; }
    public V2ClipPlanes ViewClips { get; }
    public bool IsSection(Plane plane) => Array.IndexOf(sections, plane) >= 0;
    public bool Includes(Point3d p) => p.Z < 0 && Clips.Includes(p);
    public Point2d Project(Point3d p) => p.Z < 0
        ? new Point2d(focal * (p.X / -p.Z), focal * (p.Y / -p.Z)) : Point2d.Unset;
    public Point3d AtDepth(Point2d p, double z) => new(p.X * (-z / focal), p.Y * (-z / focal), z);
    public Line ViewRay(Point2d p) => new(AtDepth(p, -near), AtDepth(p, -far));

    public double ScreenMargin(BoundingBox box, double worldMargin)
    {
        double distance = -box.Max.Z;
        if (distance <= worldMargin) return double.PositiveInfinity;
        double tangent = Math.Max(Math.Max(Math.Abs(box.Min.X), Math.Abs(box.Max.X)),
            Math.Max(Math.Abs(box.Min.Y), Math.Abs(box.Max.Y))) / distance;
        // Perturbations of X/Y and distance both change the image coordinate.
        return Math.Sqrt(2) * focal * worldMargin * (1 + tangent) / (distance - worldMargin);
    }

    public Rect Bounds(BoundingBox box)
    {
        if (!box.IsValid || ViewClips.Discards(box)) return default;
        double a = Math.Max(-far, box.Min.Z), b = Math.Min(-near, box.Max.Z);
        if (a > b) return default;
        return new Rect(
            Math.Min(focal * (box.Min.X / -a), focal * (box.Min.X / -b)),
            Math.Min(focal * (box.Min.Y / -a), focal * (box.Min.Y / -b)),
            Math.Max(focal * (box.Max.X / -a), focal * (box.Max.X / -b)),
            Math.Max(focal * (box.Max.Y / -a), focal * (box.Max.Y / -b))).Intersection(imageWindow);
    }

    public double ImageTolerance(double modelTolerance, BoundingBox extent, double millimetersPerUnit)
    {
        double farthest = Math.Clamp(-extent.Min.Z, near, far);
        // Scale with focal length without covering more physical detail at the back of the scene.
        return Math.Min(modelTolerance, .05 / millimetersPerUnit) * (focal / farthest);
    }

    internal double ImageGrid(double imageTolerance, BoundingBox extent, double millimetersPerUnit)
    {
        var frame = framed ? imageWindow : Bounds(extent);
        double visual = (frame.Right - frame.Left) / 1_500_000;
        double farthest = Math.Clamp(-extent.Min.Z, near, far);
        double protectedWidth = (.05 / millimetersPerUnit) * (focal / farthest);
        // Keep at least eight grid intervals across a screen-parallel 0.05 mm
        // feature even at the farthest retained scene depth.
        return Math.Max(imageTolerance * .01, Math.Min(visual, Math.Min(protectedWidth / 8, imageTolerance * .25)));
    }

    public Curve ProjectCurve(Curve model)
    {
        var box = model.GetBoundingBox(true);
        return box.IsValid && box.Max.Z < 0 ? Map(model, 0) : null;
    }

    private Curve Map(Curve curve, int level)
    {
        using var source = curve.ToNurbsCurve();
        if (source == null) return null;
        var points = new Point4d[source.Points.Count];
        for (int i = 0; i < points.Length; i++)
        {
            var cv = source.Points[i];
            var p = cv.Location;
            double weight = -p.Z * cv.Weight;
            if (!(weight > 0) || !double.IsFinite(weight))
            {
                if (level >= 16) return null;
                var parts = curve.Split(curve.Domain.Mid);
                if (parts is not { Length: 2 }) return null;
                using var left = parts[0];
                using var right = parts[1];
                using var a = Map(left, level + 1);
                using var b = Map(right, level + 1);
                if (a == null || b == null) return null;
                var joined = new PolyCurve();
                if (joined.AppendSegment(a) && joined.AppendSegment(b) && joined.IsValid)
                {
                    joined.Domain = curve.Domain;
                    return joined;
                }
                joined.Dispose();
                return null;
            }
            points[i] = new Point4d((focal * cv.Weight) * p.X, (focal * cv.Weight) * p.Y, 0, weight);
        }
        var result = new NurbsCurve(3, true, source.Order, points.Length);
        for (int i = 0; i < source.Knots.Count; i++) result.Knots[i] = source.Knots[i];
        for (int i = 0; i < points.Length; i++) result.Points.SetPoint(i, points[i]);
        if (result.IsValid) return result;
        result.Dispose();
        return null;
    }
}
