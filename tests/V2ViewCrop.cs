using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

internal sealed class V2ViewCrop(Rect? window, double? near, double? far, string depthLayer, double tolerance)
{
    private readonly record struct Bound(int Axis, double Value, bool Maximum)
    {
        internal bool Inside(Point3d point) => Maximum ? point[Axis] <= Value : point[Axis] >= Value;

        internal Plane Plane
        {
            get
            {
                var point = Point3d.Origin;
                point[Axis] = Value;
                var normal = Vector3d.Zero;
                normal[Axis] = Maximum ? 1 : -1;
                return new Plane(point, normal);
            }
        }

        internal Point3d Cross(Point3d a, Point3d b)
        {
            if (a.CompareTo(b) > 0) (a, b) = (b, a);
            var point = a + (b - a) * ((Value - a[Axis]) / (b[Axis] - a[Axis]));
            point[Axis] = Value;
            return point;
        }
    }

    internal List<Input> Read(IReadOnlyList<ProjectionObject> objects,
        IReadOnlyDictionary<string, (string Path, System.Drawing.Color Color)> styles, bool compact,
        Action cancel = null)
    {
        var inputs = new List<Input>(objects.Count);
        try
        {
            foreach (var item in objects)
            {
                cancel?.Invoke();
                var planes = For(styles[item.Source].Path);
                var box = item.Geometry.GetBoundingBox(item.ToView);
                if (!box.IsValid || planes.Any(plane => !plane.Inside(plane.Maximum ? box.Min : box.Max))) continue;
                if (planes.All(plane => plane.Inside(plane.Maximum ? box.Max : box.Min)))
                {
                    inputs.AddRange(ProjectionPipeline.ReadObjects(new[] { item }, cancel, compactMeshes: compact));
                    continue;
                }
                if (item.Geometry is Mesh mesh)
                {
                    var cut = CutMesh(mesh, item.ToView, planes, cancel);
                    if (cut.Faces.Count == 0) { cut.Dispose(); continue; }
                    if (compact)
                    {
                        using (cut)
                            inputs.Add(new Input(null, item.Source) { NativeMeshDepth = new V2NativeMesh(cut, cancel) });
                    }
                    else inputs.Add(new Input(null, item.Source) { NativeMesh = cut });
                    continue;
                }
                using var placed = ProjectionPipeline.ReadObjects(new[] { item }, cancel).Single();
                var pieces = CutBrep(placed.Brep, planes, item.Source);
                if (pieces == null) continue;
                inputs.Add(new Input(pieces, item.Source));
            }
            return inputs;
        }
        catch
        {
            foreach (var input in inputs) input.Dispose();
            throw;
        }
    }

    private Bound[] For(string layer)
    {
        var bounds = new List<Bound>(6);
        if (window is Rect r)
        {
            bounds.Add(new Bound(0, r.Left, false));
            bounds.Add(new Bound(0, r.Right, true));
            bounds.Add(new Bound(1, r.Bottom, false));
            bounds.Add(new Bound(1, r.Top, true));
        }
        if (depthLayer == null || layer == depthLayer || layer.StartsWith(depthLayer + "::", StringComparison.Ordinal))
        {
            if (near.HasValue) bounds.Add(new Bound(2, -near.Value, true));
            if (far.HasValue) bounds.Add(new Bound(2, -far.Value, false));
        }
        return bounds.ToArray();
    }

    private Brep CutBrep(Brep original, Bound[] planes, string source)
    {
        var pieces = new List<Brep> { original.DuplicateBrep() };
        try
        {
            foreach (var bound in planes)
                for (int i = pieces.Count - 1; i >= 0; i--)
                {
                    var piece = pieces[i];
                    var box = piece.GetBoundingBox(true);
                    if (bound.Inside(bound.Maximum ? box.Max : box.Min)) continue;
                    pieces.RemoveAt(i);
                    using (piece)
                        if (bound.Inside(bound.Maximum ? box.Min : box.Max))
                            pieces.AddRange(piece.Trim(bound.Plane, tolerance * .1) ?? Array.Empty<Brep>());
                }
            if (pieces.Count == 0) return null;
            var result = pieces[0].DuplicateBrep();
            try
            {
                for (int i = 1; i < pieces.Count; i++) result.Append(pieces[i]);
                if (!result.IsValid)
                {
                    result.Compact();
                    if (!result.IsValid) result.Repair(tolerance * .1);
                    if (!result.IsValidWithLog(out string diagnostic))
                        throw new InvalidOperationException($"Invalid view clip for {source}: {diagnostic}");
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        finally { foreach (var piece in pieces) piece.Dispose(); }
    }

    private static Mesh CutMesh(Mesh mesh, Transform transform, Bound[] planes, Action cancel)
    {
        var vertices = mesh.Vertices.ToPoint3dArray();
        for (int i = 0; i < vertices.Length; i++) vertices[i].Transform(transform);
        var triangles = mesh.Faces.ToIntArray(true);
        var output = new Mesh();
        output.Vertices.UseDoublePrecisionVertices = true;
        var indices = new Dictionary<Point3d, int>();
        Span<Point3d> polygon = stackalloc Point3d[12];
        Span<Point3d> next = stackalloc Point3d[12];
        try
        {
            for (int i = 0; i < triangles.Length; i += 3)
            {
                if (i % 65535 == 0) cancel?.Invoke();
                if ((uint)triangles[i] >= vertices.Length || (uint)triangles[i + 1] >= vertices.Length ||
                    (uint)triangles[i + 2] >= vertices.Length) continue;
                polygon[0] = vertices[triangles[i]];
                polygon[1] = vertices[triangles[i + 1]];
                polygon[2] = vertices[triangles[i + 2]];
                int count = 3;
                foreach (var bound in planes)
                {
                    int written = 0;
                    var a = polygon[count - 1];
                    bool inside = bound.Inside(a);
                    for (int j = 0; j < count; j++)
                    {
                        var b = polygon[j];
                        bool keep = bound.Inside(b);
                        if (inside != keep) next[written++] = bound.Cross(a, b);
                        if (keep) next[written++] = b;
                        (a, inside) = (b, keep);
                    }
                    count = written;
                    if (count < 3) break;
                    next[..count].CopyTo(polygon);
                }
                for (int j = 1; j + 1 < count; j++)
                {
                    var normal = Vector3d.CrossProduct(polygon[j] - polygon[0], polygon[j + 1] - polygon[0]);
                    if (!normal.IsZero) output.Faces.AddFace(Index(polygon[0]), Index(polygon[j]), Index(polygon[j + 1]));
                }
            }
            output.Compact();
            return output;
        }
        catch { output.Dispose(); throw; }

        int Index(Point3d point)
        {
            if (indices.TryGetValue(point, out int id)) return id;
            id = output.Vertices.Add(point);
            indices.Add(point, id);
            return id;
        }
    }
}
