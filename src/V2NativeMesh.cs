using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal sealed class V2NativeMesh
{
    internal readonly record struct Triangle(Point3d A, Point3d B, Point3d C)
    {
        internal Vector3d Normal => Vector3d.CrossProduct(B - A, C - A);
        internal BoundingBox Bounds => new(
            new Point3d(Math.Min(A.X, Math.Min(B.X, C.X)), Math.Min(A.Y, Math.Min(B.Y, C.Y)), Math.Min(A.Z, Math.Min(B.Z, C.Z))),
            new Point3d(Math.Max(A.X, Math.Max(B.X, C.X)), Math.Max(A.Y, Math.Max(B.Y, C.Y)), Math.Max(A.Z, Math.Max(B.Z, C.Z))));

        internal double Depth(Point2d point)
        {
            double ax = B.X - A.X, ay = B.Y - A.Y, bx = C.X - A.X, by = C.Y - A.Y;
            double det = ax * by - ay * bx;
            if (det == 0) return double.NegativeInfinity;
            double x = point.X - A.X, y = point.Y - A.Y;
            double u = (x * by - y * bx) / det, v = (ax * y - ay * x) / det;
            const double roundoff = 2e-12;
            return u >= -roundoff && v >= -roundoff && u + v <= 1 + roundoff
                ? A.Z + u * (B.Z - A.Z) + v * (C.Z - A.Z)
                : double.NegativeInfinity;
        }
    }

    internal readonly record struct Rim(Point3d A, Point3d B, string Kind);
    private readonly record struct Node(BoundingBox Box, int First, int Count, int Left, int Right);
    private readonly Point3d[] vertices;
    private readonly int[] indices;
    private readonly int[] order;
    private readonly Node[] nodes;
    private int used;
    internal int TriangleCount => order.Length;
    internal int SourceTriangles { get; }
    internal int RejectedViewTriangles { get; }
    internal V2ClipPlanes ViewClips { get; }
    internal BoundingBox Bounds => used == 0 ? BoundingBox.Empty : nodes[0].Box;
    internal long StorageBytes => 24L * vertices.Length + 4L * (indices.Length + (long)order.Length) + 64L * nodes.Length;

    internal Rect ScreenBounds(IProjectionCamera camera)
    {
        if (!camera.Perspective) return Rect.From(Bounds);
        double left = double.PositiveInfinity, bottom = double.PositiveInfinity;
        double right = double.NegativeInfinity, top = double.NegativeInfinity;
        foreach (var world in vertices)
        {
            var p = camera.Project(world);
            if (!p.IsValid) continue;
            left = Math.Min(left, p.X);
            right = Math.Max(right, p.X);
            bottom = Math.Min(bottom, p.Y);
            top = Math.Max(top, p.Y);
        }
        return new Rect(left, bottom, right, top);
    }

    internal IEnumerable<Triangle> ExactTriangles()
    {
        foreach (int id in order) yield return At(id);
    }

    internal double UpperDepth(Rect window, IProjectionCamera camera) => DepthRange(window, camera).Upper;

    internal (double Lower, double Upper) DepthRange(Rect window, IProjectionCamera camera)
    {
        if (used == 0) return (double.NegativeInfinity, double.PositiveInfinity);
        int budget = 48;
        var range = Visit(0);
        return double.IsFinite(range.Upper) ? range : (Bounds.Min.Z, Bounds.Max.Z);

        (double Lower, double Upper) Visit(int id)
        {
            var node = nodes[id];
            if (!V2TileWindow.Touches(camera.Bounds(node.Box), window))
                return (double.PositiveInfinity, double.NegativeInfinity);
            if (node.Count != 0 || --budget <= 0) return (node.Box.Min.Z, node.Box.Max.Z);
            var left = Visit(node.Left);
            var right = Visit(node.Right);
            return (Math.Min(left.Lower, right.Lower), Math.Max(left.Upper, right.Upper));
        }
    }

    internal V2NativeMesh(Mesh mesh, Action cancel, Transform? placement = null, V2ClipPlanes viewClips = null)
    {
        vertices = mesh.Vertices.ToPoint3dArray();
        if (placement is Transform transform && !transform.IsIdentity)
            for (int i = 0; i < vertices.Length; i++)
            {
                if ((i & 65535) == 0) cancel?.Invoke();
                vertices[i].Transform(transform);
            }
        indices = mesh.Faces.ToIntArray(true);
        SourceTriangles = indices.Length / 3;
        ViewClips = viewClips ?? V2ClipPlanes.Empty;
        if (ViewClips.Active && !ViewClips.Keeps(new BoundingBox(vertices)))
            (vertices, indices, RejectedViewTriangles) = ClipMesh(vertices, indices, ViewClips, cancel);
        int count = indices.Length / 3;
        order = new int[count];
        int valid = 0;
        for (int i = 0; i < count; i++)
        {
            if ((i & 65535) == 0) cancel?.Invoke();
            int j = i * 3;
            if ((uint)indices[j] >= vertices.Length || (uint)indices[j + 1] >= vertices.Length ||
                (uint)indices[j + 2] >= vertices.Length) continue;
            var triangle = At(i);
            if (triangle.A.IsValid && triangle.B.IsValid && triangle.C.IsValid && !triangle.Normal.IsZero)
                order[valid++] = i;
        }
        if (valid != count) Array.Resize(ref order, valid);
        nodes = new Node[Math.Max(1, valid / 8 + 1)];
        if (valid != 0) Build(0, valid, cancel);
    }

    private static (Point3d[] Vertices, int[] Indices, int Rejected) ClipMesh(
        Point3d[] vertices, int[] indices, V2ClipPlanes view, Action cancel)
    {
        var outcodes = new byte[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            if ((i & 65535) == 0) cancel?.Invoke();
            outcodes[i] = view.ViewOutcode(vertices[i]);
        }
        var remap = new int[vertices.Length];
        var kept = new List<Point3d>();
        var faces = new List<int>();
        var crossings = new Dictionary<Point3d, int>();
        Span<Point3d> polygon = stackalloc Point3d[V2ClipPlanes.ViewPolygonCapacity];
        int rejected = 0;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            if ((i & 16383) == 0) cancel?.Invoke();
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if ((uint)a >= vertices.Length || (uint)b >= vertices.Length || (uint)c >= vertices.Length)
                continue;
            int combined = outcodes[a] | outcodes[b] | outcodes[c];
            if (combined == 0)
            {
                faces.Add(Original(a));
                faces.Add(Original(b));
                faces.Add(Original(c));
                continue;
            }
            if ((outcodes[a] & outcodes[b] & outcodes[c]) != 0)
            {
                rejected++;
                continue;
            }
            polygon[0] = vertices[a];
            polygon[1] = vertices[b];
            polygon[2] = vertices[c];
            int count = view.ClipViewTriangle(polygon, combined);
            if (count == 0) { rejected++; continue; }
            int first = Index(polygon[0], a, b, c);
            for (int j = 1; j + 1 < count; j++)
            {
                if (Vector3d.CrossProduct(polygon[j] - polygon[0], polygon[j + 1] - polygon[0]).IsZero) continue;
                faces.Add(first);
                faces.Add(Index(polygon[j], a, b, c));
                faces.Add(Index(polygon[j + 1], a, b, c));
            }
        }
        return (kept.ToArray(), faces.ToArray(), rejected);

        int Original(int i)
        {
            if (remap[i] != 0) return remap[i] - 1;
            kept.Add(vertices[i]);
            remap[i] = kept.Count;
            return kept.Count - 1;
        }

        int Index(Point3d point, int a, int b, int c)
        {
            if (point == vertices[a]) return Original(a);
            if (point == vertices[b]) return Original(b);
            if (point == vertices[c]) return Original(c);
            if (crossings.TryGetValue(point, out int i)) return i;
            i = kept.Count;
            crossings.Add(point, i);
            kept.Add(point);
            return i;
        }
    }

    internal double Depth(Point2d point, IProjectionCamera camera)
    {
        if (used == 0) return double.NegativeInfinity;
        if (camera.Perspective) return RayDepth(camera.ViewRay(point), camera);
        return Search(0, double.NegativeInfinity);

        double Search(int index, double front)
        {
            var node = nodes[index];
            var box = node.Box;
            if (box.Min.X > point.X || box.Max.X < point.X || box.Min.Y > point.Y ||
                box.Max.Y < point.Y || box.Max.Z < front) return front;
            if (node.Count == 0)
            {
                int first = node.Left, second = node.Right;
                if (nodes[first].Box.Max.Z < nodes[second].Box.Max.Z) (first, second) = (second, first);
                return Search(second, Search(first, front));
            }
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                double z = At(order[i]).Depth(point);
                if (z > front && camera.Includes(new Point3d(point.X, point.Y, z))) front = z;
            }
            return front;
        }
    }

    private double RayDepth(Line ray, IProjectionCamera camera)
    {
        var direction = ray.Direction;
        return Search(0, double.NegativeInfinity);

        double Search(int index, double front)
        {
            var node = nodes[index];
            double start = 0, end = 1;
            if (node.Box.Max.Z < front) return front;
            for (int axis = 0; axis < 3; axis++)
            {
                double origin = ray.From[axis], delta = direction[axis];
                double low = node.Box.Min[axis], high = node.Box.Max[axis];
                if (delta == 0)
                {
                    if (origin < low || origin > high) return front;
                    continue;
                }
                double a = (low - origin) / delta, b = (high - origin) / delta;
                start = Math.Max(start, Math.Min(a, b));
                end = Math.Min(end, Math.Max(a, b));
                if (start > end) return front;
            }
            if (node.Count == 0)
            {
                int first = node.Left, second = node.Right;
                if (nodes[first].Box.Max.Z < nodes[second].Box.Max.Z) (first, second) = (second, first);
                return Search(second, Search(first, front));
            }
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                var tri = At(order[i]);
                var uAxis = tri.B - tri.A;
                var vAxis = tri.C - tri.A;
                var perpendicular = Vector3d.CrossProduct(direction, vAxis);
                double determinant = uAxis * perpendicular;
                if (determinant == 0) continue;
                var toOrigin = ray.From - tri.A;
                double u = (toOrigin * perpendicular) / determinant;
                var q = Vector3d.CrossProduct(toOrigin, uAxis);
                double v = (direction * q) / determinant;
                if (u < -2e-12 || v < -2e-12 || u + v > 1 + 2e-12) continue;
                double t = (vAxis * q) / determinant;
                if (t < -2e-12 || t > 1 + 2e-12) continue;
                var hit = ray.PointAt(t);
                if (hit.Z > front && camera.Includes(hit)) front = hit.Z;
            }
            return front;
        }
    }

    internal void Visit(BoundingBox region, double pad, Action<Triangle> hit)
    {
        if (used == 0) return;
        region.Inflate(pad);
        Search(0);

        void Search(int index)
        {
            var node = nodes[index];
            if (!Overlaps(region, node.Box)) return;
            if (node.Count == 0)
            {
                Search(node.Left);
                Search(node.Right);
                return;
            }
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                var triangle = At(order[i]);
                if (Overlaps(region, triangle.Bounds)) hit(triangle);
            }
        }
    }

    internal void VisitPairs(V2NativeMesh other, double tolerance, Action cancel, Action<Triangle, Triangle> hit)
    {
        if (used == 0 || other.used == 0) return;
        int tests = 0;
        Search(0, 0);

        void Search(int i, int j)
        {
            if ((tests++ & 4095) == 0) cancel?.Invoke();
            var a = nodes[i];
            var b = other.nodes[j];
            if (!Near(a.Box, b.Box)) return;
            if (a.Count > 0 && b.Count > 0)
            {
                for (int x = a.First; x < a.First + a.Count; x++)
                {
                    var first = At(order[x]);
                    var box = first.Bounds;
                    if (!Near(box, b.Box)) continue;
                    for (int y = b.First; y < b.First + b.Count; y++)
                    {
                        var second = other.At(other.order[y]);
                        if (Near(box, second.Bounds)) hit(first, second);
                    }
                }
            }
            else if (b.Count > 0 || a.Count == 0 && a.Box.Diagonal.SquareLength >= b.Box.Diagonal.SquareLength)
            {
                Search(a.Left, j);
                Search(a.Right, j);
            }
            else
            {
                Search(i, b.Left);
                Search(i, b.Right);
            }
        }
        bool Near(BoundingBox a, BoundingBox b) =>
            a.Min.X <= b.Max.X + tolerance && b.Min.X <= a.Max.X + tolerance &&
            a.Min.Y <= b.Max.Y + tolerance && b.Min.Y <= a.Max.Y + tolerance &&
            a.Min.Z <= b.Max.Z + tolerance && b.Min.Z <= a.Max.Z + tolerance;
    }

    internal void VisitPlane(BoundingBox region, Plane plane, double tolerance, Action cancel, Action<Triangle> hit)
    {
        if (used == 0) return;
        region.Inflate(tolerance);
        int tests = 0;
        Search(0);
        void Search(int id)
        {
            if ((tests++ & 4095) == 0) cancel?.Invoke();
            var node = nodes[id];
            if (!Overlaps(region, node.Box) || V2DrawingSolver.Separates(plane, node.Box, tolerance)) return;
            if (node.Count == 0)
            {
                Search(node.Left);
                Search(node.Right);
                return;
            }
            for (int i = node.First; i < node.First + node.Count; i++)
            {
                var triangle = At(order[i]);
                if (Overlaps(region, triangle.Bounds)) hit(triangle);
            }
        }
    }

    internal IEnumerable<Rim> Rims(double creaseAngle, Action cancel, bool perspective = false)
    {
        // Coincident vertex positions share topology even across texture or normal seams.
        var welded = new int[vertices.Length];
        var positions = new Dictionary<Point3d, int>(vertices.Length);
        for (int i = 0; i < vertices.Length; i++)
        {
            if ((i & 65535) == 0) cancel?.Invoke();
            if (!positions.TryGetValue(vertices[i], out int anchor)) positions.Add(vertices[i], anchor = i);
            welded[i] = anchor;
        }
        positions = null;
        var keys = new ulong[checked(order.Length * 3)];
        var faces = new int[keys.Length];
        int count = 0;
        foreach (int id in order)
        {
            if ((count & 65535) == 0) cancel?.Invoke();
            int offset = id * 3;
            int a = welded[indices[offset]], b = welded[indices[offset + 1]], c = welded[indices[offset + 2]];
            Add(a, b, id);
            Add(b, c, id);
            Add(c, a, id);
        }
        welded = null;
        Array.Sort(keys, faces);
        double cosine = Math.Cos(creaseAngle);
        for (int i = 0; i < keys.Length;)
        {
            int end = i + 1;
            while (end < keys.Length && keys[end] == keys[i]) end++;
            if ((i & 65535) == 0) cancel?.Invoke();
            var a = vertices[(int)(keys[i] >> 32)];
            var b = vertices[(int)(keys[i] & uint.MaxValue)];
            if (i + 1 == end)
                yield return new Rim(a, b, ViewClips.OnViewBoundary(a, b) ? "ViewClip" : "MeshBoundary");
            else
            {
                bool firstForward = faces[i] >= 0;
                var first = At(firstForward ? faces[i] : ~faces[i]).Normal;
                first.Unitize();
                var view = perspective ? Point3d.Origin - a : Vector3d.ZAxis;
                view.Unitize();
                double facing = first * view;
                bool positive = facing > 1e-12, negative = facing < -1e-12;
                bool tangent = !positive && !negative, crease = false;
                for (int k = i + 1; k < end; k++)
                {
                    bool forward = faces[k] >= 0;
                    var normal = At(forward ? faces[k] : ~faces[k]).Normal;
                    // A consistent manifold visits a shared edge in opposite directions.
                    if (forward == firstForward) normal = -normal;
                    if (!normal.Unitize()) continue;
                    facing = normal * view;
                    positive |= facing > 1e-12;
                    negative |= facing < -1e-12;
                    tangent |= Math.Abs(facing) <= 1e-12;
                    crease |= first * normal < cosine;
                }
                if (positive && negative || tangent && (positive || negative))
                    yield return new Rim(a, b, "MeshSilhouette");
                else if (crease)
                    yield return new Rim(a, b, "MeshCrease");
            }
            i = end;
        }

        void Add(int a, int b, int face)
        {
            keys[count] = ((ulong)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            faces[count++] = a <= b ? face : ~face;
        }
    }

    private Triangle At(int id)
    {
        int index = id * 3;
        return new Triangle(vertices[indices[index]], vertices[indices[index + 1]], vertices[indices[index + 2]]);
    }

    private int Build(int first, int count, Action cancel)
    {
        int id = used++;
        if ((id & 8191) == 0) cancel?.Invoke();
        var box = At(order[first]).Bounds;
        int end = first + count;
        for (int i = first + 1; i < end; i++) box.Union(At(order[i]).Bounds);
        if (count <= 32)
        {
            nodes[id] = new Node(box, first, count, 0, 0);
            return id;
        }
        int axis = box.Diagonal.X >= box.Diagonal.Y ? 0 : 1;
        double limit = box.Center[axis] * 3;
        int middle = first;
        for (int i = first; i < end; i++)
        {
            var triangle = At(order[i]);
            if (triangle.A[axis] + triangle.B[axis] + triangle.C[axis] >= limit) continue;
            (order[middle], order[i]) = (order[i], order[middle]);
            middle++;
        }
        int minLeaf = Math.Max(16, count / 8);
        if (middle - first < minLeaf || end - middle < minLeaf) middle = first + count / 2;
        int left = Build(first, middle - first, cancel), right = Build(middle, end - middle, cancel);
        nodes[id] = new Node(box, 0, 0, left, right);
        return id;
    }

    private static bool Overlaps(BoundingBox a, BoundingBox b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y &&
        b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
}
