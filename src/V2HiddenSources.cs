using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal enum V2HiddenMode { None, Occluded, IncludeBackFaces }

internal sealed class V2HiddenSources : IDisposable
{
    private readonly record struct Source(Curve Curve, Line Line, Interval Domain, bool Collapsed, bool Owned);
    private readonly Dictionary<int, Source> sources = new();

    internal bool Contains(int id) => sources.ContainsKey(id);

    internal void Remember(int id, Line line, Interval domain)
    {
        sources.TryAdd(id, new Source(null, line, domain, false, false));
    }

    internal void Remember(int id, Curve model, Interval domain, bool collapsed = false)
    {
        if (sources.ContainsKey(id)) return;
        bool owned = model is not BrepEdge;
        var original = owned ? model.ToNurbsCurve() : model;
        if (original != null) sources.Add(id, new Source(original, Line.Unset, domain, collapsed, owned));
    }

    internal bool TryPoint(V2SourceSpan span, Point2d xy, Vector2d along, double margin, out Point3d point,
        IProjectionCamera camera = null)
    {
        point = Point3d.Unset;
        if (!sources.TryGetValue(span.CurveId, out var source)) return false;
        double t = (span.From + span.To) * .5;
        double normalized = source.Domain.NormalizedParameterAt(t);
        double near2 = margin * margin;
        if (camera?.Perspective == true)
        {
            if (!along.Unitize()) return false;
            var ray = camera.ViewRay(xy);
            var side = camera.ViewRay(xy + new Vector2d(-along.Y, along.X));
            var station = new Plane(Point3d.Origin, ray.Direction, side.Direction);
            if (source.Curve == null)
            {
                double a = station.DistanceTo(source.Line.From), b = station.DistanceTo(source.Line.To);
                if (a == b) return false;
                point = source.Line.PointAt(Math.Clamp(a / (a - b), 0, 1));
                return camera.Includes(point) && Distance2(point) <= near2;
            }
            if (FindPoint(station, out point)) return true;
            return false;
        }
        if (source.Curve == null)
        {
            var line = source.Line;
            double dx = line.To.X - line.From.X, dy = line.To.Y - line.From.Y;
            double length2 = dx * dx + dy * dy;
            if (length2 <= 0) return false;
            double exact = ((xy.X - line.From.X) * dx + (xy.Y - line.From.Y) * dy) / length2;
            point = line.PointAt(Math.Clamp(exact, 0, 1));
            return Distance2(point) <= near2;
        }

        var curve = source.Curve;
        var position = curve.PointAt(curve.Domain.ParameterAt(normalized));
        if (!source.Collapsed && Distance2(position) <= near2)
        {
            point = position;
            return true;
        }
        if (!along.Unitize()) return false;
        var plane = new Plane(new Point3d(xy.X, xy.Y, 0), new Vector3d(along.X, along.Y, 0));
        return FindPoint(plane, out point);

        bool FindPoint(Plane station, out Point3d hitPoint)
        {
            hitPoint = Point3d.Unset;
            using var hits = Intersection.CurvePlane(source.Curve, station, margin * .05);
            if (hits == null) return false;
            double minimum = source.Domain.NormalizedParameterAt(Math.Min(span.From, span.To)) - 1e-6;
            double maximum = source.Domain.NormalizedParameterAt(Math.Max(span.From, span.To)) + 1e-6;
            foreach (var hit in hits)
            {
                if (!hit.IsPoint || Distance2(hit.PointA) > near2 || camera?.Includes(hit.PointA) == false) continue;
                double parameter = source.Curve.Domain.NormalizedParameterAt(hit.ParameterA);
                if (!source.Collapsed && (parameter < minimum || parameter > maximum)) continue;
                if (!hitPoint.IsValid || hit.PointA.Z > hitPoint.Z) hitPoint = hit.PointA;
            }
            return hitPoint.IsValid;
        }

        double Distance2(Point3d p)
        {
            var projected = camera?.Project(p) ?? new Point2d(p.X, p.Y);
            double dx = projected.X - xy.X, dy = projected.Y - xy.Y;
            return dx * dx + dy * dy;
        }
    }

    public void Dispose()
    {
        foreach (var source in sources.Values)
            if (source.Owned) source.Curve.Dispose();
    }
}
