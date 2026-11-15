using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace Project2.VisibleLines
{
    internal sealed class VisibleFill
    {
        internal readonly record struct Part(GeometryBase Geometry, Brep Brep, Transform Placement, string Instance);
        internal readonly record struct Region(Hatch Hatch, string Instance, Curve[] Loops);

        internal static List<Region> Compute(IEnumerable<Curve> boundaries, List<Part> parts,
            Transform worldToDrawing, ViewportInfo viewport, List<Plane> clipping, double tolerance,
            out int unresolved)
        {
            unresolved = 0;
            var output = new List<Region>();
            if (!worldToDrawing.TryGetInverse(out var drawingToWorld))
                return output;

            var direction = viewport.CameraDirection;
            if (!direction.Unitize()) return output;
            bool perspective = viewport.IsPerspectiveProjection;
            var camera = viewport.CameraLocation;
            double frustumNear = perspective ? viewport.FrustumNear : 0;
            double frustumFar = perspective ? viewport.FrustumFar : 0;
            if (perspective && (!(frustumNear > 0) || !(frustumFar > frustumNear))) return output;
            var drawingBounds = BoundingBox.Empty;
            foreach (var curve in boundaries)
                drawingBounds.Union(curve.GetBoundingBox(true));
            if (!drawingBounds.IsValid) return output;

            var projected = parts.Select(p =>
            {
                var shape = p.Geometry.Duplicate();
                shape.Transform(p.Placement);
                return (Part: p, Shape: shape, WorldBounds: shape.GetBoundingBox(true));
            }).ToArray();

            using var index = new RTree();
            double near = double.NegativeInfinity, far = double.PositiveInfinity;
            var origin = Point3d.Origin;
            origin.Transform(drawingToWorld);
            var front = -direction;
            for (int i = 0; i < projected.Length; i++)
            {
                var shape = projected[i];
                var corners = shape.WorldBounds.GetCorners();
                BoundingBox bounds;
                if (perspective)
                {
                    var depths = corners.Select(p => (p - camera) * direction).ToArray();
                    if (depths.Length == 0 || depths.Max() < frustumNear || depths.Min() > frustumFar) continue;
                    // A box crossing the near plane can project outside its transformed corner bounds.
                    bounds = depths.Min() < frustumNear ? drawingBounds :
                        new BoundingBox(corners.Select(p => worldToDrawing * p));
                }
                else
                {
                    bounds = shape.Shape.GetBoundingBox(worldToDrawing);
                    foreach (var p in corners)
                    {
                        double z = (p - origin) * front;
                        near = Math.Max(near, z);
                        far = Math.Min(far, z);
                    }
                }
                if (bounds.IsValid)
                    index.Insert(new BoundingBox(bounds.Min.X, bounds.Min.Y, -1, bounds.Max.X, bounds.Max.Y, 1), i);
            }
            if (!perspective)
            {
                double padding = Math.Max(tolerance * 32, (near - far) * .01);
                near += padding;
                far -= padding;
            }

            double rayTolerance = tolerance * .12;
            double containmentTolerance = tolerance * .035;
            double edgeProbeOffset = tolerance * 12;

            int Nearest(Point3d p)
            {
                var found = new List<(int Id, double Depth)>();
                var world = p;
                world.Transform(drawingToWorld);
                var rayDirection = direction;
                Point3d start;
                Point3d stop;
                if (perspective)
                {
                    rayDirection = world - camera;
                    if (!rayDirection.Unitize()) return -1;
                    double axialComponent = rayDirection * direction;
                    if (axialComponent <= 0) return -1;
                    start = camera + rayDirection * (frustumNear / axialComponent);
                    stop = camera + rayDirection * (frustumFar / axialComponent);
                }
                else
                {
                    start = world + front * near;
                    stop = world + front * far;
                }
                using var ray = new LineCurve(start, stop);
                var d = new Vector3d(tolerance, tolerance, .25);
                index.Search(new BoundingBox(p - d, p + d), (sender, item) =>
                {
                    var shape = projected[item.Id].Shape;
                    if (shape is Brep brep)
                    {
                        Intersection.CurveBrep(ray, brep, rayTolerance, out _, out var hits);
                        if (hits == null) return;
                        double best = hits.Where(q => clipping.All(pl => pl.DistanceTo(q) <= tolerance))
                            .Select(q => (q - start) * rayDirection).Where(z => z >= 0)
                            .DefaultIfEmpty(double.PositiveInfinity).Min();
                        if (double.IsFinite(best)) found.Add((item.Id, best));
                    }
                    else if (shape is Mesh mesh)
                    {
                        double delta = Intersection.MeshRay(mesh, new Ray3d(start, rayDirection));
                        if (delta >= 0 && delta <= start.DistanceTo(stop) &&
                            clipping.All(pl => pl.DistanceTo(start + rayDirection * delta) <= tolerance))
                            found.Add((item.Id, delta));
                    }
                });
                var ordered = found.OrderBy(h => h.Depth).Take(2).ToArray();
                if (ordered.Length == 0) return -1;
                return ordered.Length == 2 && ordered[1].Depth - ordered[0].Depth <= tolerance * .6 ? -2 : ordered[0].Id;
            }

            using var regions = Curve.CreateBooleanRegions(boundaries, Plane.WorldXY, false, tolerance);
            if (regions == null)
                return output;
            for (int i = 0; i < regions.RegionCount; i++)
            {
                var loops = regions.RegionCurves(i);
                if (loops.Length == 0 || loops.Any(c => c == null || !c.IsValid || !c.IsClosed || !c.IsPlanar(tolerance)))
                {
                    unresolved++;
                    continue;
                }
                var box = loops[0].GetBoundingBox(true);
                var votes = new HashSet<int>();
                bool Inside(Point3d p) =>
                    loops[0].Contains(p, Plane.WorldXY, containmentTolerance) == PointContainment.Inside &&
                    loops.Skip(1).All(h => h.Contains(p, Plane.WorldXY, containmentTolerance) == PointContainment.Outside);
                void Test(Point3d p) { if (Inside(p)) votes.Add(Nearest(p)); }
                for (int u = 0; u < 9; u++)
                    for (int v = 0; v < 9; v++)
                        Test(new Point3d(box.Min.X + box.Diagonal.X * (u + .41) / 9,
                            box.Min.Y + box.Diagonal.Y * (v + .59) / 9, 0));
                foreach (var loop in loops)
                {
                    var segments = loop.DuplicateSegments();
                    foreach (var edge in segments.Length == 0 ? new[] { loop } : segments)
                        if (edge.NormalizedLengthParameter(.47, out var t))
                        {
                            var v = edge.TangentAt(t);
                            var n = new Vector3d(-v.Y, v.X, 0);
                            if (!n.Unitize()) continue;
                            var p = edge.PointAt(t);
                            Test(p + n * edgeProbeOffset);
                            Test(p - n * edgeProbeOffset);
                        }
                }
                if (votes.Count == 1 && votes.Contains(-1))
                    continue;
                if (votes.Count != 1 || votes.Single() < 0)
                {
                    unresolved++;
                    continue;
                }
                for (int j = 0; j < loops.Length; j++)
                    if ((loops[j].ClosedCurveOrientation(Plane.WorldXY) == CurveOrientation.CounterClockwise) != (j == 0))
                        loops[j].Reverse();
                var hatch = Hatch.Create(Plane.WorldXY, loops[0], loops.Skip(1), 0, 0, 1);
                if (hatch == null || !hatch.IsValid)
                {
                    unresolved++;
                    continue;
                }
                output.Add(new Region(hatch, projected[votes.Single()].Part.Instance, loops));
            }
            return output;
        }

        internal static Color ColorFor(string instance)
        {
            var key = instance.Split('/').Last();
            var bytes = Guid.Parse(key).ToByteArray();
            return Color.FromArgb(85 + bytes[0] / 2, 100 + bytes[1] / 2, 105 + bytes[2] / 2);
        }
    }
}
