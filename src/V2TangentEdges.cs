using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

// Classifies output strokes only. Region loops continue using the shared Twin geometry.
internal sealed class V2TangentEdges(V2Face[] faces, IProjectionCamera camera, double fit, double angle) : IDisposable
{
    private readonly Dictionary<int, int> originals = new();
    private readonly Dictionary<int, (double Start, double End, double Margin)> proxySpans = new();
    private readonly Dictionary<(string Source, int Edge), (BrepTrim A, BrepTrim B)> smooth = new();
    private readonly double cosine = Math.Cos(Math.Clamp(angle, 0, Math.PI / 2));
    private Dictionary<string, Brep> detached;

    internal void Remember(int curveId, BrepEdge edge) => originals.TryAdd(curveId, edge.EdgeIndex);

    internal bool Detach(V2PlanarArrangement plan, Action cancel)
    {
        var owners = new HashSet<int>();
        foreach (var edge in plan.EnumerateVisibleEdges())
        {
            if (edge.Left >= 0 && faces[edge.Left].Face != null) owners.Add(edge.Left);
            if (edge.Right >= 0 && faces[edge.Right].Face != null) owners.Add(edge.Right);
        }
        // Cleanup can reassign a cell only to an owner already present here.
        // A source with a single visible face cannot supply a two-sided seam.
        const long copyBudget = 192L * 1024 * 1024;
        var copies = new Dictionary<string, Brep>(StringComparer.Ordinal);
        long bytes = 0;
        try
        {
            foreach (var owner in owners.Order().GroupBy(id => faces[id].Id.Source, StringComparer.Ordinal))
            {
                cancel?.Invoke();
                if (!owner.Skip(1).Any()) continue;
                var parent = faces[owner.First()].Face.Brep;
                bytes += parent.MemoryEstimate();
                if (bytes > copyBudget) return false;
                var copy = parent.DuplicateBrep();
                if (copy == null || copy.Edges.Count != parent.Edges.Count || copy.Faces.Count != parent.Faces.Count)
                {
                    copy?.Dispose();
                    return false;
                }
                copies.Add(owner.Key, copy);
            }
            detached = copies;
            return true;
        }
        finally
        {
            if (detached == null)
                foreach (var copy in copies.Values) copy.Dispose();
        }
    }

    public void Dispose()
    {
        smooth.Clear();
        if (detached == null) return;
        foreach (var copy in detached.Values) copy.Dispose();
        detached.Clear();
    }

    internal bool RememberProxy(int curveId, V2Face face, IReadOnlyList<BrepEdge> edges, Line chord)
    {
        double tolerance = face.ProxyChord * 2;
        foreach (var edge in edges)
        {
            if (!edge.ClosestPoint(chord.From, out double start, tolerance) ||
                !edge.ClosestPoint(chord.To, out double end, tolerance) ||
                !edge.ClosestPoint(chord.PointAt(.5), out _, tolerance) || start == end) continue;
            Remember(curveId, edge);
            proxySpans.Add(curveId, (start, end, camera.ScreenMargin(new BoundingBox(chord.From, chord.To), tolerance)));
            return true;
        }
        return false;
    }

    internal bool IsTangent(V2PlanarArrangement plan, V2EdgeChain chain)
    {
        if (chain.Left < 0 || chain.Right < 0 || chain.Left == chain.Right) return false;
        var left = faces[chain.Left];
        var right = faces[chain.Right];
        if (left.Face == null || right.Face == null || left.Id.Source != right.Id.Source) return false;
        var parent = detached == null ? left.Face.Brep : detached.GetValueOrDefault(left.Id.Source);
        if (parent == null) return false;

        foreach (int side in chain.Sides)
        {
            var sources = plan.Sources(side);
            foreach (var span in sources)
                if (span.Kind is "Silhouette" or "Contact" or "SectionCut" or "ViewClip" or "MeshSilhouette")
                    return false;

            var a = plan.Point(plan.Start(side));
            var b = plan.Point(plan.Start(side ^ 1));
            var chord = new Line(a, b);
            bool proved = false;
            foreach (var span in sources)
            {
                if (span.Kind != "Original" || span.Face != chain.Left && span.Face != chain.Right ||
                    !originals.TryGetValue(span.CurveId, out int edgeIndex)) continue;
                var edge = parent.Edges[edgeIndex];
                var (first, second) = SmoothTrims(left.Id.Source, parent, edge);
                if (first == null ||
                    !(first.Face.FaceIndex == left.Id.FaceIndex && second.Face.FaceIndex == right.Id.FaceIndex ||
                      second.Face.FaceIndex == left.Id.FaceIndex && first.Face.FaceIndex == right.Id.FaceIndex)) continue;
                if (!SameFacing(edge, first, second, span, chord)) continue;
                proved = true;
                break;
            }
            if (!proved) return false;
        }
        return true;
    }

    private (BrepTrim, BrepTrim) SmoothTrims(string source, Brep parent, BrepEdge edge)
    {
        var key = (source, edge.EdgeIndex);
        if (smooth.TryGetValue(key, out var pair)) return pair;
        var ids = edge.TrimIndices();
        if (ids.Length == 2 && edge.IsSmoothManifoldEdge(angle))
        {
            var trims = parent.Trims;
            pair = (trims[ids[0]], trims[ids[1]]);
        }
        smooth.Add(key, pair);
        return pair;
    }

    private bool SameFacing(BrepEdge edge, BrepTrim a, BrepTrim b, V2SourceSpan span, Line chord)
    {
        var domain = edge.Domain;
        double margin = fit * .2;
        double start = span.From, end = span.To;
        if (proxySpans.TryGetValue(span.CurveId, out var proxy))
        {
            start = proxy.Start + (proxy.End - proxy.Start) * start;
            end = proxy.Start + (proxy.End - proxy.Start) * end;
            if (double.IsFinite(proxy.Margin)) margin += proxy.Margin;
        }
        double prior = 0;
        for (int i = 0; i <= 4; i++)
        {
            double parameter = start + (end - start) * (i * .25);
            if (!domain.IncludesParameter(parameter)) return false;
            var p = edge.PointAt(parameter);
            var xy = camera.Project(p);
            if (!xy.IsValid || chord.DistanceTo(new Point3d(xy.X, xy.Y, 0), true) > margin ||
                !TryNormal(a, parameter, out var first) || !TryNormal(b, parameter, out var second) ||
                first * second < cosine) return false;
            var view = camera.Perspective ? Point3d.Origin - p : Vector3d.ZAxis;
            if (!view.Unitize()) return false;
            double da = first * view, db = second * view;
            if (Math.Abs(da) <= 1e-8 || Math.Abs(db) <= 1e-8 || da * db <= 0 || prior * da < 0) return false;
            prior = da;
        }
        return true;
    }

    private static bool TryNormal(BrepTrim trim, double parameter, out Vector3d normal)
    {
        normal = Vector3d.Unset;
        if (!trim.GetTrimParameter(parameter, out double trimParameter)) return false;
        var uv = trim.PointAt(trimParameter);
        var face = trim.Face;
        normal = face.UnderlyingSurface().NormalAt(uv.X, uv.Y);
        if (face.OrientationIsReversed) normal.Reverse();
        return normal.Unitize();
    }
}
