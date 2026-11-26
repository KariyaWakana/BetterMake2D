using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal sealed record V2Face(SourceFaceId Id, BrepFace Face, BoundingBox Box, Plane? Plane, bool Mesh)
{
    internal V2NativeMesh Triangles { get; init; }
    internal double ProxyChord { get; init; }
    internal bool Opaque { get; init; } = true;
    internal Rect? ProjectedBounds { get; init; }
    internal Rect Screen => ProjectedBounds ?? Rect.From(Box);
}

internal sealed class V2DepthIndex : IDisposable
{
    private readonly V2Face[] faces;
    private readonly IProjectionCamera camera;
    private readonly V2SurfaceBounds surfaceBounds;
    private readonly double fit;
    private readonly RTree originals = new();
    private readonly List<Triangle> triangles = new();
    private readonly Node root;
    private readonly List<int> candidates = new();
    private readonly List<(int Face, double Depth)> contenders = new(4);
    private readonly List<(int Face, double Depth, double Area)> coplanar = new(4);
    private readonly double?[] projectedAreas;
    private readonly ConcurrentDictionary<(int, int), bool> sameSupports;
    private readonly PlanarFootprint[] footprints;
    private readonly bool[] footprintRead;
    private readonly Comparison<int> nearFirst;
    private readonly EventHandler<RTreeEventArgs> collect;
    private readonly ShapeCopies copies;
    private readonly IReadOnlySet<SourceFaceId> excluded;
    private readonly double searchMargin;
    private bool disposed;
    // These coordinates come from MeshVertexList as floats. Mesh hits only seed exact queries.
    private const int TriangleBudget = 3_000_000;
    private readonly record struct Triangle(int Face, Point3f A, Point3f B, Point3f C)
    {
        internal BoundingBox Bounds => new(
            new Point3d(Math.Min(A.X, Math.Min(B.X, C.X)), Math.Min(A.Y, Math.Min(B.Y, C.Y)), Math.Min(A.Z, Math.Min(B.Z, C.Z))),
            new Point3d(Math.Max(A.X, Math.Max(B.X, C.X)), Math.Max(A.Y, Math.Max(B.Y, C.Y)), Math.Max(A.Z, Math.Max(B.Z, C.Z))));
        internal double Center(bool x) => x
            ? ((double)Math.Min(A.X, Math.Min(B.X, C.X)) + Math.Max(A.X, Math.Max(B.X, C.X))) * .5
            : ((double)Math.Min(A.Y, Math.Min(B.Y, C.Y)) + Math.Max(A.Y, Math.Max(B.Y, C.Y))) * .5;
    }
    private sealed record Node(BoundingBox Box, int Start, int Count, Node Left, Node Right);
    internal int Rays, NativeQueries, MeshHits, MeshLookups, CachedMeshFaces, PlaneChecks, BoundPrunes;
    internal int CurvedRenderFaces, GeneratedHintFaces, GeneratedHintTriangles;
    internal int PlanarEarlyExits, CurvedEarlyExits, ManagedPlaneChecks, AdjacencyHits;
    internal long PlaneTicks, NativeTicks;
    internal int TriangleCount => triangles.Count;

    internal V2DepthIndex(V2Face[] faces, IProjectionCamera camera, double fit, V2SurfaceBounds surfaceBounds = null,
        IReadOnlySet<SourceFaceId> hiddenOnly = null, double? imageTolerance = null, bool renderHints = true)
    {
        this.faces = faces;
        this.camera = camera;
        this.fit = fit;
        this.surfaceBounds = surfaceBounds;
        excluded = hiddenOnly;
        searchMargin = imageTolerance ?? fit;
        projectedAreas = new double?[faces.Length];
        sameSupports = new();
        footprints = new PlanarFootprint[faces.Length];
        footprintRead = new bool[faces.Length];
        nearFirst = (a, b) =>
        {
            int depth = faces[b].Box.Max.Z.CompareTo(faces[a].Box.Max.Z);
            return depth != 0 ? depth : a.CompareTo(b);
        };
        collect = (_, hit) => candidates.Add(hit.Id);
        int triangleBudget = faces.Sum(face => (long)(face.Triangles?.TriangleCount ?? 0)) > 1_000_000
            ? 750_000 : TriangleBudget;
        if (faces.Length > 70000) triangleBudget = Math.Min(triangleBudget, 1_000_000);
        if (camera.Perspective) triangleBudget = Math.Min(triangleBudget, 500_000);
        Span<Point3d> polygon = stackalloc Point3d[V2ClipPlanes.ViewPolygonCapacity];
        for (int i = 0; i < faces.Length; i++)
        {
            if (hiddenOnly?.Contains(faces[i].Id) == true) continue;
            originals.Insert(faces[i].Screen.Grow(searchMargin).Box, i);
            if (faces[i].Triangles != null || !renderHints) continue;
            if (triangles.Count >= triangleBudget) continue;
            var mesh = faces[i].Face.GetMesh(MeshType.Render);
            int before = triangles.Count;
            if (mesh == null)
            {
                var quads = surfaceBounds?.HintQuads(i);
                if (quads == null) continue;
                foreach (var quad in quads)
                {
                    if (triangles.Count >= triangleBudget) break;
                    Add(quad.A, quad.B, quad.C, polygon);
                    Add(quad.A, quad.C, quad.D, polygon);
                }
                if (triangles.Count > before)
                {
                    GeneratedHintFaces++;
                    GeneratedHintTriangles += triangles.Count - before;
                }
                continue;
            }
            if ((long)mesh.Faces.Count * 2 + triangles.Count > triangleBudget) continue;
            foreach (var face in mesh.Faces)
            {
                if (triangles.Count >= triangleBudget) break;
                Add(mesh.Vertices[face.A], mesh.Vertices[face.B], mesh.Vertices[face.C], polygon);
                if (face.IsQuad) Add(mesh.Vertices[face.A], mesh.Vertices[face.C], mesh.Vertices[face.D], polygon);
            }
            if (triangles.Count > before)
            {
                CachedMeshFaces++;
                if (!faces[i].Plane.HasValue) CurvedRenderFaces++;
            }
            void Add(Point3f p, Point3f q, Point3f r, Span<Point3d> clipped)
            {
                if (!p.IsValid || !q.IsValid || !r.IsValid || triangles.Count >= triangleBudget) return;
                if (!camera.Perspective)
                {
                    triangles.Add(new Triangle(i, p, q, r));
                    return;
                }
                clipped[0] = p;
                clipped[1] = q;
                clipped[2] = r;
                var view = camera.ViewClips;
                int pa = view.ViewOutcode(clipped[0]), pb = view.ViewOutcode(clipped[1]), pc = view.ViewOutcode(clipped[2]);
                if ((pa & pb & pc) != 0) return;
                int mask = pa | pb | pc;
                int count = mask == 0 ? 3 : view.ClipViewTriangle(clipped, mask);
                if (count < 3) return;
                var first = Image(clipped[0]);
                var previous = Image(clipped[1]);
                for (int j = 2; j < count && triangles.Count < triangleBudget; j++)
                {
                    var next = Image(clipped[j]);
                    if (first.IsValid && previous.IsValid && next.IsValid)
                        triangles.Add(new Triangle(i, first, previous, next));
                    previous = next;
                }

                Point3f Image(Point3d point)
                {
                    var screen = camera.Project(point);
                    return new Point3f((float)screen.X, (float)screen.Y, (float)point.Z);
                }
            }
        }
        if (triangles.Count != 0)
            root = Build(0, triangles.Count);
    }

    private V2DepthIndex(V2DepthIndex original, object cloneGate)
    {
        faces = original.faces;
        camera = original.camera;
        fit = original.fit;
        surfaceBounds = original.surfaceBounds;
        excluded = original.excluded;
        searchMargin = original.searchMargin;
        triangles = original.triangles;
        root = original.root;
        projectedAreas = new double?[faces.Length];
        sameSupports = original.sameSupports;
        footprints = new PlanarFootprint[faces.Length];
        footprintRead = new bool[faces.Length];
        nearFirst = original.nearFirst;
        collect = (_, hit) => candidates.Add(hit.Id);
        copies = new ShapeCopies(faces, cloneGate);
        for (int i = 0; i < faces.Length; i++)
            if (excluded?.Contains(faces[i].Id) != true) originals.Insert(faces[i].Screen.Grow(searchMargin).Box, i);
    }

    internal V2DepthIndex Fork(object cloneGate) => new(this, cloneGate);

    internal void AddMeasurements(V2DepthIndex worker)
    {
        Rays += worker.Rays;
        NativeQueries += worker.NativeQueries;
        MeshHits += worker.MeshHits;
        MeshLookups += worker.MeshLookups;
        PlaneChecks += worker.PlaneChecks;
        ManagedPlaneChecks += worker.ManagedPlaneChecks;
        BoundPrunes += worker.BoundPrunes;
        PlanarEarlyExits += worker.PlanarEarlyExits;
        CurvedEarlyExits += worker.CurvedEarlyExits;
        PlaneTicks += worker.PlaneTicks;
        NativeTicks += worker.NativeTicks;
    }

    private BrepFace Face(int id) => copies == null ? faces[id].Face : copies.Get(id);

    internal double DepthAt(int index, Point2d point)
    {
        var entry = faces[index];
        if (entry.Triangles is V2NativeMesh mesh) return mesh.Depth(point, camera);
        var face = Face(index);
        if (face == null) return double.NegativeInfinity;
        if (entry.Plane is Plane plane && OnPlane(plane, point) is Point3d location)
        {
            return camera.Includes(location) && face.ClosestPoint(location, out double u, out double v) &&
                face.IsPointOnFace(u, v, fit * .01) != PointFaceRelation.Exterior
                ? location.Z : double.NegativeInfinity;
        }
        using var query = new LineCurve(camera.ViewRay(point));
        Intersection.CurveBrepFace(query, face, fit * .02, out var overlaps, out var points);
        foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
        double depth = double.NegativeInfinity;
        foreach (var hit in points ?? Array.Empty<Point3d>())
            if (camera.Includes(hit)) depth = Math.Max(depth, hit.Z);
        return depth;
    }

    internal (int Owner, bool Uncertain) Classify(Point2d point, int target = -1, int adjacent = -1)
    {
        Rays++;
        int hint = -1;
        contenders.Clear();
        double hintedDepth = double.NegativeInfinity, depth = double.NegativeInfinity, uncertainDepth = double.NegativeInfinity;
        LineCurve query = null;
        Point3d? unitDepth = camera.Perspective && surfaceBounds != null ? camera.AtDepth(point, -1) : null;
        try
        {
            if (target < 0 && adjacent >= 0 && TryPlanarDepth(adjacent, point, out double adjacentDepth) &&
                double.IsFinite(adjacentDepth))
            {
                FindCandidates();
                if (OnlyFront(adjacent, adjacentDepth))
                {
                    AdjacencyHits++;
                    return (adjacent, false);
                }
            }
            double targetDepth = double.NegativeInfinity;
            if (target >= 0)
            {
                Check(target);
                targetDepth = depth;
                if (!double.IsFinite(targetDepth)) return (-1, double.IsFinite(uncertainDepth));
            }
            if (root != null)
            {
                MeshLookups++;
                Hint(root);
            }
            if (hint >= 0) MeshHits++;
            if (hint >= 0 && hint != target) Check(hint);
            if (target >= 0 && depth > targetDepth + fit) return (-1, false);
            FindCandidates();
            if (target < 0 && hint >= 0 && double.IsFinite(depth) && uncertainDepth < depth - fit && OnlyFront(hint, depth))
            {
                if (IsPlanar(hint)) PlanarEarlyExits++;
                else CurvedEarlyExits++;
                return (hint, false);
            }
            // Exact planar witnesses are cheap and can bound the rest of the ray
            // before any expensive curved-face intersection. Bounding depths
            // still retain every candidate in the coincidence band.
            foreach (int candidate in candidates)
            {
                if (candidate == hint || candidate == target || !IsPlanar(candidate)) continue;
                Check(candidate);
                if (target >= 0 && depth > targetDepth + fit) return (-1, false);
            }
            int remaining = 0;
            for (int j = 0; j < candidates.Count; j++)
            {
                int candidate = candidates[j];
                if (candidate != hint && candidate != target && !IsPlanar(candidate) &&
                    faces[candidate].Box.Max.Z >= depth - fit)
                    candidates[remaining++] = candidate;
            }
            candidates.RemoveRange(remaining, candidates.Count - remaining);
            candidates.Sort(nearFirst);
            foreach (int candidate in candidates)
            {
                if (faces[candidate].Box.Max.Z < depth - fit) break;
                Check(candidate);
                if (target >= 0 && depth > targetDepth + fit) return (-1, false);
            }
            return (ChooseOwner(point, depth), uncertainDepth >= depth - fit && double.IsFinite(uncertainDepth));
        }
        finally { query?.Dispose(); }

        bool IsPlanar(int i) => faces[i].Plane is Plane plane && (camera.Perspective || Math.Abs(plane.Normal.Z) > 1e-6);

        void FindCandidates()
        {
            candidates.Clear();
            var xy = new Point3d(point.X, point.Y, 0);
            originals.Search(new BoundingBox(xy, xy), collect);
        }

        bool OnlyFront(int owner, double front)
        {
            foreach (int other in candidates)
            {
                if (other == owner || faces[other].Box.Max.Z < front - fit) continue;
                // Use the same upper bounds as the complete search. In particular,
                // keep the full common-depth band and any unresolved curved hit.
                if (IsPlanar(other) || faces[other].Triangles != null || surfaceBounds == null ||
                    surfaceBounds.UpperDepth(other, point, faces[other].Box.Max.Z, unitDepth) >= front - fit)
                    return false;
            }
            return true;
        }

        void Check(int i)
        {
            var entry = faces[i];
            if (entry.Box.Max.Z < depth - fit)
                return;
            double best = double.NegativeInfinity;
            long start = Stopwatch.GetTimestamp();
            if (entry.Triangles is V2NativeMesh mesh)
                best = mesh.Depth(point, camera);
            else if (entry.Plane is Plane plane && OnPlane(plane, point) is Point3d projected)
            {
                PlaneChecks++;
                if (TryPlanarDepth(i, point, out best))
                    ManagedPlaneChecks++;
                else
                {
                    var face = Face(i);
                    if (face.ClosestPoint(projected, out double u, out double v) &&
                        face.PointAt(u, v).DistanceTo(projected) <= fit &&
                        face.IsPointOnFace(u, v, fit * .01) != PointFaceRelation.Exterior &&
                        camera.Includes(projected))
                        best = projected.Z;
                }
                PlaneTicks += Stopwatch.GetTimestamp() - start;
            }
            else
            {
                double upper = surfaceBounds?.UpperDepth(i, point, entry.Box.Max.Z, unitDepth) ?? entry.Box.Max.Z;
                if (double.IsNegativeInfinity(upper) || upper < depth - fit)
                {
                    BoundPrunes++;
                    return;
                }
                query ??= new LineCurve(camera.ViewRay(point));
                NativeQueries++;
                bool ok = Intersection.CurveBrepFace(query, Face(i), fit * .02, out var overlaps, out var points);
                if (!ok || overlaps is { Length: > 0 })
                    uncertainDepth = Math.Max(uncertainDepth, entry.Box.Max.Z);
                foreach (var overlap in overlaps ?? Array.Empty<Curve>()) overlap.Dispose();
                foreach (var hit in points ?? Array.Empty<Point3d>())
                    if (camera.Includes(hit)) best = Math.Max(best, hit.Z);
                NativeTicks += Stopwatch.GetTimestamp() - start;
            }
            if (double.IsFinite(best) && best >= depth - fit)
            {
                depth = Math.Max(depth, best);
                contenders.Add((i, best));
            }
        }

        void Hint(Node node)
        {
            if (node == null || node.Box.Min.X > point.X || node.Box.Max.X < point.X ||
                node.Box.Min.Y > point.Y || node.Box.Max.Y < point.Y || node.Box.Max.Z < hintedDepth)
                return;
            if (node.Left != null)
            {
                Hint(node.Left);
                Hint(node.Right);
                return;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                var item = triangles[i];
                double ax = (double)item.B.X - item.A.X, ay = (double)item.B.Y - item.A.Y;
                double bx = (double)item.C.X - item.A.X, by = (double)item.C.Y - item.A.Y;
                double det = ax * by - ay * bx;
                if (det == 0) continue;
                double x = point.X - item.A.X, y = point.Y - item.A.Y;
                double u = (x * by - y * bx) / det, v = (ax * y - ay * x) / det;
                if (u < 0 || v < 0 || u + v > 1) continue;
                double dz = camera.Perspective
                    ? 1 / ((1 - u - v) / item.A.Z + u / item.B.Z + v / item.C.Z)
                    : item.A.Z + u * ((double)item.B.Z - item.A.Z) + v * ((double)item.C.Z - item.A.Z);
                if (dz > hintedDepth && (!camera.Perspective || camera.Includes(camera.AtDepth(point, dz))))
                    (hint, hintedDepth) = (item.Face, dz);
            }
        }
    }

    private Point3d? OnPlane(Plane plane, Point2d point)
    {
        var n = plane.Normal;
        if (camera.Perspective)
        {
            var ray = camera.ViewRay(point);
            double a = plane.DistanceTo(ray.From), b = plane.DistanceTo(ray.To);
            double delta = a - b;
            return Math.Abs(delta) > 1e-14 ? ray.PointAt(a / delta) : null;
        }
        if (Math.Abs(n.Z) <= 1e-6) return null;
        return new Point3d(point.X, point.Y, plane.Origin.Z -
            (n.X * (point.X - plane.Origin.X) + n.Y * (point.Y - plane.Origin.Y)) / n.Z);
    }

    private bool TryPlanarDepth(int index, Point2d point, out double depth)
    {
        depth = double.NegativeInfinity;
        var entry = faces[index];
        if (entry.Plane is not Plane plane || !camera.Perspective && Math.Abs(plane.Normal.Z) < .01) return false;
        if (!footprintRead[index])
        {
            footprints[index] = PlanarFootprint.Read(Face(index), camera.Perspective ? plane : null, fit);
            footprintRead[index] = true;
        }
        var footprint = footprints[index];
        if (footprint == null) return false;
        Point3d? location = null;
        double margin = camera.Perspective ? 2 * fit : 2 * fit / Math.Abs(plane.Normal.Z);
        if (camera.Perspective)
        {
            location = OnPlane(plane, point);
            if (location is not Point3d hit) return false;
            point = PlanarFootprint.Local(plane, hit);
        }
        if (!footprint.Test(point, margin, out bool inside))
            return false;
        if (inside)
        {
            if ((location ?? OnPlane(plane, point)) is Point3d projected && camera.Includes(projected)) depth = projected.Z;
        }
        return true;
    }

    private sealed class PlanarFootprint
    {
        private readonly (Point2d[] Points, bool Outer)[] loops;

        private PlanarFootprint((Point2d[], bool)[] loops) => this.loops = loops;

        internal static Point2d Local(Plane plane, Point3d point)
        {
            var offset = point - plane.Origin;
            return new Point2d(plane.XAxis * offset, plane.YAxis * offset);
        }

        internal static PlanarFootprint Read(BrepFace face, Plane? frame, double tolerance)
        {
            var loops = new List<(Point2d[], bool)>();
            int count = 0, outers = 0;
            foreach (var loop in face.Loops)
            {
                if (loop.LoopType is not (BrepLoopType.Outer or BrepLoopType.Inner)) return null;
                if (frame.HasValue && loop.Trims.Any(trim => trim.Edge is not BrepEdge edge ||
                    !double.IsFinite(edge.Tolerance) || edge.Tolerance < 0 || edge.Tolerance > tolerance)) return null;
                using var rim = loop.To3dCurve();
                if (rim == null || !rim.TryGetPolyline(out var polyline) ||
                    polyline.Count < 4 || polyline[0] != polyline[^1]) return null;
                count += polyline.Count;
                if (count > 96) return null;
                bool outer = loop.LoopType == BrepLoopType.Outer;
                if (outer) outers++;
                var points = new Point2d[polyline.Count];
                for (int i = 0; i < points.Length; i++)
                    points[i] = frame is Plane plane ? Local(plane, polyline[i]) : new Point2d(polyline[i].X, polyline[i].Y);
                loops.Add((points, outer));
            }
            return outers == 1 ? new PlanarFootprint(loops.ToArray()) : null;
        }

        internal bool Test(Point2d point, double margin, out bool inside)
        {
            inside = false;
            bool exterior = false;
            margin = Math.Max(margin, Math.Max(Math.Abs(point.X), Math.Abs(point.Y)) * 1e-12);
            double margin2 = margin * margin;
            foreach (var (polygon, outer) in loops)
            {
                bool hit = false;
                for (int i = 1; i < polygon.Length; i++)
                {
                    var a = polygon[i - 1];
                    var b = polygon[i];
                    double dx = b.X - a.X, dy = b.Y - a.Y, x = point.X - a.X, y = point.Y - a.Y;
                    if (x >= Math.Min(dx, 0) - margin && x <= Math.Max(dx, 0) + margin &&
                        y >= Math.Min(dy, 0) - margin && y <= Math.Max(dy, 0) + margin)
                    {
                        double length2 = dx * dx + dy * dy;
                        double t = length2 == 0 ? 0 : Math.Clamp((x * dx + y * dy) / length2, 0, 1);
                        double px = x - dx * t, py = y - dy * t;
                        if (px * px + py * py <= margin2) return false;
                    }
                    if ((a.Y > point.Y) != (b.Y > point.Y) && x < y * dx / dy) hit = !hit;
                }
                exterior |= outer != hit;
            }
            inside = !exterior;
            return true;
        }
    }

    private int ChooseOwner(Point2d point, double front)
    {
        if (contenders.Count == 0) return -1;
        if (contenders.Count == 1) return contenders[0].Face;
        var nearest = contenders[0];
        foreach (var hit in contenders)
            if (hit.Depth > nearest.Depth || hit.Depth == nearest.Depth && CompareSource(hit.Face, nearest.Face) < 0)
                nearest = hit;
        var frontPlane = faces[nearest.Face].Plane;
        // The larger search tolerance remains an uncertainty budget. Plane/plane
        // ties require coincidence over the common projection, not a crossing.
        double scale = Math.Max(1, Math.Max(Math.Abs(front), Math.Max(Math.Abs(point.X), Math.Abs(point.Y))));
        double coincidence = Math.Max(fit * 1e-5, 128 * 2.220446049250313e-16 * scale);
        coplanar.Clear();
        double smallest = double.PositiveInfinity;
        Vector3d? normal = null;
        foreach (var hit in contenders)
        {
            if (hit.Face == nearest.Face || front - hit.Depth > coincidence) continue;
            if (frontPlane is Plane plane && faces[hit.Face].Plane is Plane other)
            {
                if (!Coincident(nearest.Face, plane, hit.Face, other, point, front - hit.Depth, coincidence))
                    continue;
            }
            else
            {
                if (!SameSurfaceSupport(nearest.Face, hit.Face)) continue;
                normal ??= SurfaceNormal(nearest.Face, camera.AtDepth(point, front), coincidence, false);
                var otherNormal = SurfaceNormal(hit.Face, camera.AtDepth(point, hit.Depth), coincidence, false);
                if (!NormalsAgree(normal.Value, otherNormal)) continue;
            }
            double area = ProjectedArea(hit.Face);
            if (!double.IsFinite(area)) continue;
            coplanar.Add((hit.Face, hit.Depth, area));
            smallest = Math.Min(smallest, area);
        }
        if (coplanar.Count == 0) return nearest.Face;
        double frontArea = ProjectedArea(nearest.Face);
        if (!double.IsFinite(frontArea)) return nearest.Face;
        coplanar.Add((nearest.Face, nearest.Depth, frontArea));
        smallest = Math.Min(smallest, frontArea);
        double areaTie = Math.Max(smallest * 1e-9, fit * fit * 1e-4);
        int owner = -1;
        double ownerDepth = double.NegativeInfinity;
        foreach (var hit in coplanar)
            if (hit.Area - smallest <= areaTie &&
                (owner < 0 || hit.Depth > ownerDepth + coincidence ||
                    Math.Abs(hit.Depth - ownerDepth) <= coincidence && CompareSource(hit.Face, owner) < 0))
                (owner, ownerDepth) = (hit.Face, hit.Depth);
        return owner;
    }

    private Vector3d SurfaceNormal(int index, Point3d hit, double tolerance, bool interior)
    {
        var face = Face(index);
        if (face == null || !face.ClosestPoint(hit, out double u, out double v) ||
            face.PointAt(u, v).DistanceTo(hit) > tolerance ||
            interior && face.IsPointOnFace(u, v, tolerance) != PointFaceRelation.Interior)
            return Vector3d.Unset;
        var normal = face.NormalAt(u, v);
        return normal.Unitize() ? normal : Vector3d.Unset;
    }

    private static bool NormalsAgree(Vector3d a, Vector3d b) =>
        a.IsValid && b.IsValid && Math.Abs(a * b) >= .99995;

    internal bool SameSurfaceSupport(int first, int second)
    {
        if (first > second) (first, second) = (second, first);
        var key = (first, second);
        if (sameSupports.TryGetValue(key, out bool same)) return same;
        // Each worker uses its own Rhino geometry copies. Publish the result once
        // and keep the accepting and rejecting cases for the whole solve.
        lock (sameSupports)
        {
            if (!sameSupports.TryGetValue(key, out same))
                sameSupports[key] = same = ProveSupport(first, second);
        }
        return same;
    }

    private bool ProveSupport(int a, int b)
    {
        var first = faces[a];
        var second = faces[b];
        var overlap = first.Screen.Intersection(second.Screen);
        if (!overlap.HasArea || first.Face == null || second.Face == null ||
            first.Triangles != null || second.Triangles != null)
            return false;

        var bounds = first.Box;
        bounds.Union(second.Box);
        double scale = Math.Max(1, Math.Max(bounds.Min.DistanceTo(Point3d.Origin), bounds.Max.DistanceTo(Point3d.Origin)));
        double tolerance = Math.Max(fit * 1e-5, 128 * 2.220446049250313e-16 * scale);
        using var surfaceA = Face(a).UnderlyingSurface();
        using var surfaceB = Face(b).UnderlyingSurface();
        if (GeometryBase.GeometryEquals(surfaceA, surfaceB) ||
            V2AnalyticSupport.Same(surfaceA, surfaceB, bounds.Diagonal.Length, tolerance))
            return true;

        // Probe both trimmed faces over an area. A grazing line or an isolated
        // crossing cannot certify support, and any conflicting witness vetoes it.
        Span<Point2d> witnesses = stackalloc Point2d[15];
        int count = 0;
        for (int row = 1; row <= 3; row++)
        for (int column = 1; column <= 5; column++)
        {
            double x = column / 6.0, y = row / 4.0;
            var point = new Point2d(overlap.Left + (overlap.Right - overlap.Left) * x,
                overlap.Bottom + (overlap.Top - overlap.Bottom) * y);
            double depthA = DepthAt(a, point), depthB = DepthAt(b, point);
            if (!double.IsFinite(depthA) || !double.IsFinite(depthB)) continue;
            if (Math.Abs(depthA - depthB) > tolerance) return false;
            var hit = camera.AtDepth(point, depthA);
            if (!NormalsAgree(SurfaceNormal(a, hit, tolerance, true), SurfaceNormal(b, hit, tolerance, true)))
                return false;
            witnesses[count++] = new Point2d(x, y);
        }
        for (int i = 2; i < count; i++)
        for (int j = 1; j < i; j++)
        {
            var u = witnesses[j] - witnesses[0];
            var v = witnesses[i] - witnesses[0];
            if (Math.Abs(u.X * v.Y - u.Y * v.X) >= .025) return true;
        }
        return false;
    }

    private bool Coincident(int first, Plane a, int second, Plane b, Point2d point, double gap, double tolerance)
    {
        if (camera.Perspective)
            return Math.Abs(a.Normal * b.Normal) >= 1 - 1e-12 && Math.Abs(a.DistanceTo(b.Origin)) <= tolerance;
        var overlap = faces[first].Screen.Intersection(faces[second].Screen);
        if (!overlap.HasArea || Math.Min(Math.Abs(a.Normal.Z), Math.Abs(b.Normal.Z)) <= 1e-6)
            return false;
        double dx = b.Normal.X / b.Normal.Z - a.Normal.X / a.Normal.Z;
        double dy = b.Normal.Y / b.Normal.Z - a.Normal.Y / a.Normal.Z;
        double left = dx * (overlap.Left - point.X), right = dx * (overlap.Right - point.X);
        double bottom = dy * (overlap.Bottom - point.Y), top = dy * (overlap.Top - point.Y);
        return gap + Math.Min(left, right) + Math.Min(bottom, top) >= -tolerance &&
            gap + Math.Max(left, right) + Math.Max(bottom, top) <= tolerance;
    }

    internal double ProjectedArea(int index)
    {
        if (projectedAreas[index] is double cached) return cached;
        var entry = faces[index];
        double area = 0;
        int windingBound = 0;
        var face = Face(index);
        if (face == null) return double.PositiveInfinity;
        foreach (var loop in face.Loops)
        {
            if (loop.LoopType is not (BrepLoopType.Outer or BrepLoopType.Inner)) continue;
            using var rim = loop.To3dCurve();
            bool project = camera.Perspective || !entry.Plane.HasValue;
            using var projected = project && rim != null ? camera.ProjectCurve(rim) : null;
            var contour = project ? projected : rim;
            // A projected loop may fold. Its signed area integrates the winding
            // number, and nonzero winding can only occur inside the face image.
            // A generic scanline meets each rational span at most Degree times;
            // half that crossing bound limits the winding of a closed loop.
            using var self = projected is { IsClosed: true } ? Intersection.CurveSelf(projected, fit * .01) : null;
            using var properties = contour is { IsClosed: true } ? AreaMassProperties.Compute(contour, fit * .01) : null;
            if (properties == null || !double.IsFinite(properties.Area))
            {
                area = double.PositiveInfinity;
                break;
            }
            area += (loop.LoopType == BrepLoopType.Outer ? 1 : -1) * Math.Abs(properties.Area);
            if (projected != null)
            {
                area -= Math.Abs(properties.AreaError);
                windingBound += self is { Count: 0 } ? 1 : Math.Max(1, projected.Degree * projected.SpanCount / 2);
            }
        }
        double projection = !camera.Perspective && entry.Plane is Plane plane
            ? area * Math.Abs(plane.Normal.Z) : area / Math.Max(1, windingBound);
        if (!double.IsFinite(projection) || projection <= 0)
        {
            // The projection of a periodic rim may cancel its own winding.
            // Use the upper bound of its footprint and, for orthographic
            // views, the trimmed area as a second upper bound.
            projection = entry.Screen.Area;
            if (!camera.Perspective)
            {
                using var trimmed = face.DuplicateFace(false);
                using var properties = trimmed == null ? null : AreaMassProperties.Compute(trimmed, true, false, false, false);
                if (properties is { Area: > 0 } && double.IsFinite(properties.Area))
                    projection = Math.Min(projection, properties.Area + Math.Abs(properties.AreaError));
            }
        }
        projectedAreas[index] = projection > 0 && double.IsFinite(projection) ? projection : double.PositiveInfinity;
        return projectedAreas[index].Value;
    }

    private int CompareSource(int a, int b)
    {
        if (a == b) return 0;
        var first = faces[a].Id;
        var second = faces[b].Id;
        int order = string.CompareOrdinal(first.Source, second.Source);
        return order != 0 ? order : first.FaceIndex.CompareTo(second.FaceIndex);
    }

    private Node Build(int start, int count)
    {
        var box = BoundingBox.Empty;
        for (int i = start; i < start + count; i++) box.Union(triangles[i].Bounds);
        if (count <= 12) return new Node(box, start, count, null, null);
        bool x = box.Diagonal.X >= box.Diagonal.Y;
        triangles.Sort(start, count, Comparer<Triangle>.Create((a, b) => a.Center(x).CompareTo(b.Center(x))));
        int left = count / 2;
        return new Node(box, start, count, Build(start, left), Build(start + left, count - left));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        originals.Dispose();
        copies?.Dispose();
    }

    private sealed class ShapeCopies(V2Face[] faces, object gate) : IDisposable
    {
        private readonly Dictionary<int, LinkedListNode<(int Id, Brep Shape, long Size)>> cache = new();
        private readonly LinkedList<(int Id, Brep Shape, long Size)> recent = new();
        private long bytes;
        internal BrepFace Get(int id)
        {
            if (cache.TryGetValue(id, out var node))
            {
                recent.Remove(node);
                recent.AddFirst(node);
                return node.Value.Shape.Faces[0];
            }
            var face = faces[id].Face;
            if (face == null) return null;
            Brep clone;
            lock (gate) clone = face.DuplicateFace(false);
            long cost = (long)clone.MemoryEstimate();
            while (recent.Count > 0 && (bytes + cost > 36L * 1024 * 1024 || recent.Count >= 2048))
            {
                var item = recent.Last.Value;
                recent.RemoveLast();
                cache.Remove(item.Id);
                item.Shape.Dispose();
                bytes -= item.Size;
            }
            cache.Add(id, recent.AddFirst((id, clone, cost)));
            bytes += cost;
            return clone.Faces[0];
        }

        public void Dispose()
        {
            foreach (var item in recent) item.Shape.Dispose();
            recent.Clear();
            cache.Clear();
        }
    }
}
