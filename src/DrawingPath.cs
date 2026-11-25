using System;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class DrawingPath
{
    internal const double FitMillimeters = .004;

    internal static Curve FilledLoop(BrepLoop loop, DraftRegion region, double millimeters)
    {
        if (loop.LoopType != BrepLoopType.Outer && loop.LoopType != BrepLoopType.Inner)
            throw new InvalidOperationException($"Unexpected loop type in {region.Source}, face {region.FaceIndex}.");
        var curve = loop.To3dCurve();
        bool closed = curve is { IsClosed: true, IsValid: true };
        var bounds = closed ? curve.GetBoundingBox(true) : BoundingBox.Empty;
        bool collapsed = closed && bounds.IsValid && (bounds.Diagonal.X <= 0 || bounds.Diagonal.Y <= 0);
        var orientation = closed ? curve.ClosedCurveOrientation(Plane.WorldXY) : CurveOrientation.Undefined;
        if (!closed || collapsed || orientation == CurveOrientation.Undefined)
        {
            using var uv = loop.To2dCurve();
            // Joining the 3D edges can collapse the width of a valid thin planar trim.
            // Lift the saved trim on its own support at a finer tolerance in that case.
            double fit = (closed ? 1e-8 : FitMillimeters * .25) / millimeters;
            var lifted = uv is { IsClosed: true, IsValid: true }
                ? loop.Face.Pushup(uv, fit) : null;
            if (lifted is { IsClosed: true, IsValid: true })
            {
                curve?.Dispose();
                curve = lifted;
                orientation = curve.ClosedCurveOrientation(Plane.WorldXY);
            }
            else lifted?.Dispose();
        }
        try
        {
            CheckFillBounds(curve, $"{region.Source}, face {region.FaceIndex}, loop {loop.LoopIndex}", millimeters);
            // Preserve opposite winding for the outer boundary and holes.
            var desired = loop.LoopType == BrepLoopType.Outer ? CurveOrientation.Clockwise : CurveOrientation.CounterClockwise;
            if (orientation == CurveOrientation.Undefined || (orientation != desired && !curve.Reverse()))
                throw new InvalidOperationException($"Invalid fill boundary winding in {region.Source}, face {region.FaceIndex}.");
            return curve;
        }
        catch
        {
            curve?.Dispose();
            throw;
        }
    }

    private static void CheckFillBounds(Curve curve, string source, double millimeters)
    {
        if (curve is not { IsValid: true, IsClosed: true })
            throw new InvalidOperationException($"Invalid fill path from {source}.");
        var bounds = curve.GetBoundingBox(true);
        double deviation = Math.Max(Math.Abs(bounds.Min.Z), Math.Abs(bounds.Max.Z)) * millimeters;
        if (!bounds.IsValid || deviation > .01 || bounds.Diagonal.X <= 0 || bounds.Diagonal.Y <= 0)
            throw new InvalidOperationException($"Fill path from {source} does not have a valid XY projection.");
    }

    internal static (int Segments, double SampleError) Write(Curve curve, double millimeters,
        Action<Point3d> move, Action<Point3d> line, Action<Point3d, Point3d, Point3d> cubic,
        double fitMillimeters = FitMillimeters)
    {
        move(curve.PointAtStart);
        int segments = 0;
        double maxError = 0;
        if (curve.TryGetPolyline(out var polyline))
        {
            for (int i = 1; i < polyline.Count; i++)
            {
                if (polyline[i].DistanceToSquared(polyline[i - 1]) == 0)
                    continue;
                line(polyline[i]);
                segments++;
            }
        }
        else
        {
            double start = curve.Domain.T0;
            var point = curve.PointAtStart;
            for (int i = 0; i < curve.SpanCount; i++)
            {
                var span = curve.SpanDomain(i);
                if (span.T1 <= start)
                    continue;
                if (span.T0 > start)
                {
                    var next = curve.PointAt(span.T0);
                    WriteSpan(start, span.T0, point, next, 0);
                    start = span.T0;
                    point = next;
                }
                var end = curve.PointAt(span.T1);
                WriteSpan(start, span.T1, point, end, 0);
                start = span.T1;
                point = end;
            }
            if (start < curve.Domain.T1)
                WriteSpan(start, curve.Domain.T1, point, curve.PointAtEnd, 0);
        }
        return (segments, maxError);

        void WriteSpan(double start, double end, Point3d p, Point3d q, int depth)
        {
            double width = end - start;
            var firstSample = curve.PointAt(start + width / 3);
            var secondSample = curve.PointAt(start + width * (2.0 / 3));
            // The cubic interpolates both endpoints and the two interior samples.
            var first = p + ((firstSample - p) * 3 - (secondSample - p) * 1.5 + (q - p) / 3);
            var second = p + ((secondSample - p) * 3 - (firstSample - p) * 1.5 - (q - p) * (5.0 / 6));
            double error = 0;
            for (int i = 1; i < 12; i++)
            {
                double t = i / 12.0;
                var a = Blend(p, first, t);
                var b = Blend(first, second, t);
                var c = Blend(second, q, t);
                var fitted = Blend(Blend(a, b, t), Blend(b, c, t), t);
                error = Math.Max(error, fitted.DistanceTo(curve.PointAt(start + width * t)) * millimeters);
            }
            if (error <= fitMillimeters)
            {
                double lineDeviation = Math.Max(first.DistanceTo(Blend(p, q, 1.0 / 3)),
                    second.DistanceTo(Blend(p, q, 2.0 / 3))) * millimeters;
                if (lineDeviation < .001 && error + lineDeviation <= fitMillimeters)
                {
                    line(q);
                    error += lineDeviation;
                }
                else
                    cubic(first, second, q);
                maxError = Math.Max(maxError, error);
                segments++;
                return;
            }
            double middle = start + width / 2;
            if (depth >= 20 || middle == start || middle == end)
                throw new InvalidOperationException("Could not approximate a projected curve within the vector fitting tolerance.");
            var point = curve.PointAt(middle);
            WriteSpan(start, middle, p, point, depth + 1);
            WriteSpan(middle, end, point, q, depth + 1);
        }
    }

    private static Point3d Blend(Point3d a, Point3d b, double fraction) => a + (b - a) * fraction;
}
