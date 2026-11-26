using System;
using System.Collections.Generic;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace BetterMake2D;

// Positive-weight Bézier patches lie in the convex hull of their control vertices.
// An unavailable proof always leaves the full trimmed face as a candidate.
internal sealed class V2SurfaceBounds
{
    private const int PatchBudget = 64;
    private readonly Patch[][] boxes;
    private readonly Rect[][] projectedBoxes;
    private readonly Vector3d?[] meshAxes;
    private readonly ContactTree[] contactTrees;
    private readonly bool[] contactRead;
    private readonly V2OrientedBounds?[] planarBounds;
    private readonly Quad[][] hints;
    private int contactLeafBudget = 400_000;

    private readonly record struct ContactNode(BoundingBox Box, int Left, int Right, int First, int Count);
    private sealed record ContactTree(Patch[] Patches, ContactNode[] Nodes);
    internal readonly record struct Quad(Point3f A, Point3f B, Point3f C, Point3f D);

    // The chord plane provides a direction, not a distance estimate. The entire
    // positive-weight control hull supplies the signed distance interval.
    private readonly record struct Patch(BoundingBox Box, Point3d Origin, Vector3d Normal,
        double Low, double High, V2OrientedBounds Oriented)
    {
        internal double UpperDepth(Point2d point)
        {
            if (Math.Abs(Normal.Z) < .01) return Box.Max.Z;
            double bound = Normal.Z > 0 ? High : Low;
            return Math.Min(Box.Max.Z, Origin.Z +
                (bound - Normal.X * (point.X - Origin.X) - Normal.Y * (point.Y - Origin.Y)) / Normal.Z);
        }

        internal double UpperDepth(Point3d unitDepth)
        {
            double near = -Box.Max.Z, far = -Box.Min.Z;
            if (!Restrict(unitDepth.X, Box.Min.X, Box.Max.X, ref near, ref far) ||
                !Restrict(unitDepth.Y, Box.Min.Y, Box.Max.Y, ref near, ref far))
                return double.NegativeInfinity;
            double origin = Normal * (Origin - Point3d.Origin);
            double along = Normal * (unitDepth - Point3d.Origin);
            if (!Restrict(along, origin + Low, origin + High, ref near, ref far))
                return double.NegativeInfinity;
            if (!Oriented.Restrict(Point3d.Origin, unitDepth - Point3d.Origin, ref near, ref far))
                return double.NegativeInfinity;
            return -near;

            static bool Restrict(double direction, double low, double high, ref double near, ref double far)
            {
                if (direction == 0) return low <= 0 && high >= 0;
                double a = low / direction, b = high / direction;
                if (!double.IsFinite(a) || !double.IsFinite(b)) return true;
                double front = Math.Min(a, b), back = Math.Max(a, b);
                double pad = Math.Max(1, Math.Max(Math.Abs(front), Math.Abs(back))) * 1e-12;
                near = Math.Max(near, front - pad);
                far = Math.Min(far, back + pad);
                return near <= far;
            }
        }

        internal bool Separates(BoundingBox other)
        {
            if (Normal.IsZero) return false;
            var center = other.Center;
            var half = other.Diagonal * .5;
            double radius = Math.Abs(Normal.X) * half.X + Math.Abs(Normal.Y) * half.Y + Math.Abs(Normal.Z) * half.Z;
            double distance = Normal * (center - Origin);
            return distance - radius > High || distance + radius < Low;
        }

        internal (double Low, double High) Project(Vector3d axis)
        {
            var full = Range(Box, axis);
            double alignment = axis * Normal;
            var residual = Range(Box, axis - alignment * Normal);
            double center = alignment * (Normal * (Origin - Point3d.Origin));
            double a = center + alignment * Low, b = center + alignment * High;
            double lower = Math.Max(full.Low, residual.Low + Math.Min(a, b));
            double upper = Math.Min(full.High, residual.High + Math.Max(a, b));
            if (!Oriented.IsValid) return (lower, upper);
            double mid = axis * (Oriented.Center - Point3d.Origin), radius = Oriented.Radius(axis);
            return (Math.Max(lower, mid - radius), Math.Min(upper, mid + radius));
        }
    }

    internal V2SurfaceBounds(V2Face[] faces, double tolerance, Action cancel)
    {
        boxes = new Patch[faces.Length][];
        projectedBoxes = new Rect[faces.Length][];
        meshAxes = new Vector3d?[faces.Length];
        contactTrees = new ContactTree[faces.Length];
        contactRead = new bool[faces.Length];
        planarBounds = new V2OrientedBounds?[faces.Length];
        hints = new Quad[faces.Length][];
        int patchBudget = faces.Length > 70000 ? 8 : PatchBudget;
        for (int i = 0; i < faces.Length; i++)
        {
            cancel?.Invoke();
            if (faces[i].Face == null || faces[i].Plane.HasValue || faces[i].Triangles != null) continue;
            var quads = faces[i].Face.GetMesh(MeshType.Render) == null ? new List<Quad>() : null;
            boxes[i] = Build(faces[i], tolerance, budget: patchBudget, hints: quads);
            if (quads is { Count: > 0 } && boxes[i] != null) hints[i] = quads.ToArray();
        }
    }

    internal IReadOnlyList<Quad> HintQuads(int id) => hints[id];
    internal void ReleaseHints() => Array.Clear(hints);

    internal void ReleaseContactTrees()
    {
        Array.Clear(contactTrees);
        Array.Clear(contactRead);
        Array.Clear(projectedBoxes);
        contactLeafBudget = 400_000;
    }

    internal double UpperDepth(int face, Rect window, IProjectionCamera camera, double fallback) =>
        DepthRange(face, window, camera, double.NegativeInfinity, fallback).Upper;

    internal (double Lower, double Upper) DepthRange(int face, Rect window, IProjectionCamera camera,
        double fallbackLower, double fallbackUpper)
    {
        var patches = boxes[face];
        if (patches == null) return (fallbackLower, fallbackUpper);
        double lower = double.PositiveInfinity, upper = double.NegativeInfinity;
        var screens = projectedBoxes[face];
        if (screens == null)
        {
            screens = new Rect[patches.Length];
            for (int i = 0; i < screens.Length; i++) screens[i] = camera.Bounds(patches[i].Box);
            projectedBoxes[face] = screens;
        }
        for (int i = 0; i < screens.Length; i++)
            if (V2TileWindow.Touches(screens[i], window))
            {
                lower = Math.Min(lower, patches[i].Box.Min.Z);
                upper = Math.Max(upper, patches[i].Box.Max.Z);
            }
        return double.IsFinite(upper)
            ? (Math.Max(lower, fallbackLower), Math.Min(upper, fallbackUpper))
            : (fallbackLower, fallbackUpper);
    }

    internal double UpperDepth(int face, Point2d point, double fallback, Point3d? unitDepth = null)
    {
        var patches = boxes[face];
        if (patches == null) return fallback;
        double upper = double.NegativeInfinity;
        if (unitDepth is Point3d perspectiveRay)
        {
            foreach (var patch in patches)
                upper = Math.Max(upper, patch.UpperDepth(perspectiveRay));
            return Math.Min(upper, fallback);
        }
        foreach (var patch in patches)
            if (patch.Box.Min.X <= point.X && patch.Box.Max.X >= point.X &&
                patch.Box.Min.Y <= point.Y && patch.Box.Max.Y >= point.Y)
                upper = Math.Max(upper, patch.UpperDepth(point));
        return Math.Min(upper, fallback);
    }

    internal bool Separated(int a, int b, V2Face[] faces, double tolerance) =>
        SeparatedOrCovered(a, b, faces, tolerance, null, out _);

    internal bool SeparatedOrCovered(int a, int b, V2Face[] faces, double tolerance, V2HiZ hiZ, out bool usedCoverage)
    {
        usedCoverage = false;
        var left = boxes[a];
        var right = boxes[b];
        if (left == null && right == null) return false;
        if (left == null) return SeparatedOrCovered(b, a, faces, tolerance, hiZ, out usedCoverage);
        var other = faces[b];
        int allowance = hiZ?.Available == true ? 8 : 0;
        var extent = other.Box;
        if (allowance > 0) extent.Inflate(tolerance);
        foreach (var patch in left)
        {
            if (!Overlap(patch.Box, other.Box, tolerance) || patch.Separates(other.Box)) continue;
            if (other.Plane is Plane plane && V2DrawingSolver.Separates(plane, patch.Box, tolerance)) continue;
            if (right == null)
            {
                if (!Covered(patch.Box, extent)) return false;
                usedCoverage = true;
                continue;
            }
            foreach (var candidate in right)
            {
                if (!Overlap(patch.Box, candidate.Box, 0) ||
                    patch.Separates(candidate.Box) || candidate.Separates(patch.Box)) continue;
                if (!Covered(patch.Box, candidate.Box)) return false;
                usedCoverage = true;
            }
        }
        return true;

        bool Covered(BoundingBox first, BoundingBox second) =>
            allowance-- > 0 && hiZ.Hides(BoundingBox.Intersection(first, second));
    }

    internal bool SeparatedOnRenderAxis(int a, int b, V2Face[] faces, double tolerance)
    {
        if (faces[a].Face == null || faces[b].Face == null || boxes[a] == null && boxes[b] == null)
            return false;
        var first = Axis(a);
        if (On(first)) return true;
        var second = Axis(b);
        return Math.Abs(first * second) < .999 && On(second);

        bool On(Vector3d axis)
        {
            if (axis.IsZero) return false;
            // Mesh triangles only suggest axes. The positive-weight original hull
            // (or the original bounding box) certifies both intervals.
            var left = RangeOf(a, axis);
            var right = RangeOf(b, axis);
            double scale = Math.Max(Math.Max(Math.Abs(left.Low), Math.Abs(left.High)),
                Math.Max(Math.Abs(right.Low), Math.Abs(right.High)));
            double slack = tolerance + 1e-12 * Math.Max(1, scale);
            return left.High + slack < right.Low || right.High + slack < left.Low;
        }

        (double Low, double High) RangeOf(int id, Vector3d axis)
        {
            if (boxes[id] is not { Length: > 0 } patches) return Range(faces[id].Box, axis);
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var patch in patches)
            {
                var node = patch.Project(axis);
                min = Math.Min(min, node.Low);
                max = Math.Max(max, node.High);
            }
            return (min, max);
        }

        Vector3d Axis(int id)
        {
            if (meshAxes[id] is Vector3d axis) return axis;
            axis = Vector3d.Zero;
            var mesh = faces[id].Face.GetMesh(MeshType.Render);
            if (mesh != null)
            {
                double largest = 0;
                int stride = Math.Max(1, mesh.Faces.Count / 12);
                for (int i = 0; i < mesh.Faces.Count; i += stride)
                {
                    var f = mesh.Faces[i];
                    var a = mesh.Vertices.Point3dAt(f.A);
                    var b = mesh.Vertices.Point3dAt(f.B);
                    var c = mesh.Vertices.Point3dAt(f.C);
                    var n = Vector3d.CrossProduct(b - a, c - a);
                    double size = n.SquareLength;
                    if (!n.IsValid || size <= largest) continue;
                    axis = n;
                    largest = size;
                }
                axis.Unitize();
            }
            meshAxes[id] = axis;
            return axis;
        }
    }

    internal bool SeparatedByContactTree(int a, int b, V2Face[] faces, double tolerance)
    {
        if (faces[a].Face == null || faces[b].Face == null) return false;
        if (faces[a].Plane is Plane p)
            return Read(b) is ContactTree other && PlaneClear(other, p, Planar(a, p), 0);
        if (faces[b].Plane is Plane q)
            return Read(a) is ContactTree surface && PlaneClear(surface, q, Planar(b, q), 0);
        if (Read(a) is not ContactTree left || Read(b) is not ContactTree right) return false;
        int tests = 256;
        return Clear(0, 0);

        bool Clear(int i, int j)
        {
            var x = left.Nodes[i];
            var y = right.Nodes[j];
            if (!Overlap(x.Box, y.Box, tolerance)) return true;
            if (--tests == 0) return false;
            if (x.Count > 0 && y.Count > 0)
            {
                for (int m = x.First; m < x.First + x.Count; m++)
                    for (int n = y.First; n < y.First + y.Count; n++)
                    {
                        var u = left.Patches[m];
                        var v = right.Patches[n];
                        if (Overlap(u.Box, v.Box, tolerance) && !u.Separates(v.Box) && !v.Separates(u.Box) &&
                            !u.Oriented.Separated(v.Oriented, tolerance)) return false;
                    }
                return true;
            }
            if (y.Count > 0 || x.Count == 0 && x.Box.Diagonal.SquareLength >= y.Box.Diagonal.SquareLength)
                return Clear(x.Left, j) && Clear(x.Right, j);
            return Clear(i, y.Left) && Clear(i, y.Right);
        }

        bool PlaneClear(ContactTree tree, Plane plane, V2OrientedBounds trimmed, int address)
        {
            var item = tree.Nodes[address];
            if (V2DrawingSolver.Separates(plane, item.Box, tolerance)) return true;
            if (item.Count == 0) return PlaneClear(tree, plane, trimmed, item.Left) && PlaneClear(tree, plane, trimmed, item.Right);
            var normal = plane.Normal;
            double origin = normal * (plane.Origin - Point3d.Origin);
            for (int i = item.First; i < item.First + item.Count; i++)
            {
                if (tree.Patches[i].Oriented.Separated(trimmed, tolerance)) continue;
                var range = tree.Patches[i].Project(normal);
                double slack = tolerance + Math.Max(1, Math.Abs(origin)) * 1e-12;
                if (range.Low <= origin + slack && range.High >= origin - slack) return false;
            }
            return true;
        }

        V2OrientedBounds Planar(int id, Plane plane)
        {
            if (planarBounds[id] is V2OrientedBounds cached) return cached;
            var face = faces[id].Face;
            using var rim = face.OuterLoop?.To3dCurve();
            planarBounds[id] = default(V2OrientedBounds);
            if (rim == null || !rim.IsClosed) return default;
            var box = rim.GetBoundingBox(plane);
            if (!box.IsValid) return default;
            double pad = tolerance * 3 + faces[id].Box.Center.MaximumCoordinate * 1e-12;
            foreach (var trim in face.OuterLoop.Trims)
            {
                if (trim.Edge is not BrepEdge edge || !double.IsFinite(edge.Tolerance) || edge.Tolerance < 0)
                    return default;
                pad = Math.Max(pad, edge.Tolerance + tolerance * 3);
            }
            box.Inflate(pad);
            var middle = box.Center;
            var result = new V2OrientedBounds(plane.PointAt(middle.X, middle.Y, middle.Z),
                plane.XAxis, plane.YAxis, plane.ZAxis, box.Diagonal * .5);
            planarBounds[id] = result;
            return result;
        }

        ContactTree Read(int id)
        {
            if (contactRead[id]) return contactTrees[id];
            contactRead[id] = true;
            if (contactLeafBudget < 256 || faces[id].Plane.HasValue || boxes[id] == null) return null;
            // Refine the original positive-weight hull. A render-mesh gap alone
            // cannot bound an unsampled bulge of a trimmed NURBS face.
            var patches = Build(faces[id], tolerance, 256, 7);
            if (patches == null || patches.Length <= boxes[id].Length) return null;
            contactLeafBudget -= patches.Length;
            var mesh = faces[id].Face.GetMesh(MeshType.Render);
            var meshBounds = mesh?.GetBoundingBox(false) ?? BoundingBox.Empty;
            var nodes = new List<ContactNode>();
            Partition(0, patches.Length);
            return contactTrees[id] = new ContactTree(patches, nodes.ToArray());

            int Partition(int first, int count)
            {
                var box = BoundingBox.Empty;
                for (int i = first; i < first + count; i++) box.Union(patches[i].Box);
                int index = nodes.Count;
                nodes.Add(new ContactNode(box, 0, 0, first, count));
                if (count <= 4) return index;
                var d = index == 0 && meshBounds.IsValid ? meshBounds.Diagonal : box.Diagonal;
                int axis = d.X >= d.Y ? d.X >= d.Z ? 0 : 2 : d.Y >= d.Z ? 1 : 2;
                Array.Sort(patches, first, count, Comparer<Patch>.Create((u, v) => u.Box.Center[axis].CompareTo(v.Box.Center[axis])));
                int lower = count / 2;
                int l = Partition(first, lower), r = Partition(first + lower, count - lower);
                nodes[index] = new ContactNode(box, l, r, 0, 0);
                return index;
            }
        }
    }

    private static (double Low, double High) Range(BoundingBox box, Vector3d axis)
    {
        var half = box.Diagonal * .5;
        double radius = Math.Abs(axis.X) * half.X + Math.Abs(axis.Y) * half.Y + Math.Abs(axis.Z) * half.Z;
        double center = axis * (box.Center - Point3d.Origin);
        return (center - radius, center + radius);
    }

    private static bool Overlap(BoundingBox a, BoundingBox b, double pad) =>
        a.Min.X <= b.Max.X + pad && b.Min.X <= a.Max.X + pad &&
        a.Min.Y <= b.Max.Y + pad && b.Min.Y <= a.Max.Y + pad &&
        a.Min.Z <= b.Max.Z + pad && b.Min.Z <= a.Max.Z + pad;

    private static Patch[] Build(V2Face entry, double tolerance, int budget = PatchBudget, int maxDepth = 4, List<Quad> hints = null)
    {
        using var nurbs = entry.Face.ToNurbsSurface();
        if (nurbs == null) return null;
        var u = Spans(nurbs.KnotsU, nurbs.OrderU);
        var v = Spans(nurbs.KnotsV, nurbs.OrderV);
        long count = (long)u.Count * v.Count;
        if (count == 0 || count > budget) return null;
        int depth = 0;
        while (count * 2 <= budget && depth < maxDepth)
        {
            count *= 2;
            depth++;
        }
        var result = new List<Patch>((int)count);
        var extent = entry.Box;
        extent.Inflate(tolerance * 2);
        foreach (int first in u)
            foreach (int second in v)
            {
                using var patch = nurbs.ConvertSpanToBezier(first, second);
                if (patch == null || !Collect(patch, depth, 0)) return null;
            }
        return result.Count == 0 ? null : result.ToArray();

        bool Collect(BezierSurface patch, int remaining, int direction)
        {
            int nu = patch.ControlVertexCount(0), nv = patch.ControlVertexCount(1);
            var origin = patch.GetControlVertex3d(0, 0);
            var uEnd = patch.GetControlVertex3d(nu - 1, 0);
            var vEnd = patch.GetControlVertex3d(0, nv - 1);
            var diagonal = patch.GetControlVertex3d(nu - 1, nv - 1);
            var uAxis = uEnd - origin;
            var vAxis = vEnd - origin;
            if (maxDepth > 4)
            {
                double uSpan = Math.Max(uAxis.SquareLength, (diagonal - vEnd).SquareLength);
                double vSpan = Math.Max(vAxis.SquareLength, (diagonal - uEnd).SquareLength);
                if (uSpan > 4 * vSpan) direction = 0;
                if (vSpan > 4 * uSpan) direction = 1;
            }
            if (remaining > 0 && patch.Split(direction, .5, out var left, out var right))
            {
                using (left)
                using (right)
                    return Collect(left, remaining - 1, 1 - direction) && Collect(right, remaining - 1, 1 - direction);
            }
            var box = BoundingBox.Empty;
            var normal = Vector3d.CrossProduct(uAxis, vAxis);
            if (!normal.Unitize()) normal = Vector3d.Zero;
            if (vAxis.SquareLength > uAxis.SquareLength) uAxis = vAxis;
            uAxis.Unitize();
            vAxis = Vector3d.CrossProduct(normal, uAxis);
            double low = 0, high = 0;
            double minU = 0, maxU = 0, minV = 0, maxV = 0;
            for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    if (patch.IsRational && !(patch.GetControlVertex4d(i, j).W > 0)) return false;
                    var point = patch.GetControlVertex3d(i, j);
                    if (!point.IsValid) return false;
                    box.Union(point);
                    var offset = point - origin;
                    double distance = normal * offset;
                    low = Math.Min(low, distance);
                    high = Math.Max(high, distance);
                    double u = uAxis * offset, v = vAxis * offset;
                    minU = Math.Min(minU, u);
                    maxU = Math.Max(maxU, u);
                    minV = Math.Min(minV, v);
                    maxV = Math.Max(maxV, v);
                }
            if (!box.IsValid) return false;
            double scale = Math.Max(box.Min.MaximumCoordinate, box.Max.MaximumCoordinate);
            double pad = tolerance * 2 + scale * 1e-12;
            box.Inflate(pad);
            box = BoundingBox.Intersection(box, extent);
            if (box.IsValid)
            {
                V2OrientedBounds oriented = normal.IsZero ? default : new V2OrientedBounds(
                    origin + uAxis * ((minU + maxU) * .5) + vAxis * ((minV + maxV) * .5) + normal * ((low + high) * .5),
                    uAxis, vAxis, normal, new Vector3d((maxU - minU) * .5 + pad, (maxV - minV) * .5 + pad, (high - low) * .5 + pad));
                result.Add(new Patch(box, origin, normal, low - pad, high + pad, oriented));
                hints?.Add(new Quad(ToFloat(origin), ToFloat(uEnd), ToFloat(diagonal), ToFloat(vEnd)));
            }
            return true;
        }

        static Point3f ToFloat(Point3d point) => new((float)point.X, (float)point.Y, (float)point.Z);

        static List<int> Spans(Rhino.Geometry.Collections.NurbsSurfaceKnotList knots, int order)
        {
            var spans = new List<int>();
            int start = order - 2;
            for (int i = start; i < knots.Count - order + 1; i++)
                if (knots[i + 1] > knots[i])
                    spans.Add(i - start);
            return spans;
        }
    }
}
