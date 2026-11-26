using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal sealed class V2BoundaryCollector(V2Face[] faces, V2Drawing drawing, IProjectionCamera camera,
    V2ClipPlanes clips, double tolerance, double imageTolerance, double chordTolerance, double angle,
    Action cancel, Func<string, long, TimeSpan> measure, bool detachOriginals = false)
{
    private int nextCurve;

    internal int NextId() => nextCurve++;

    internal IEnumerable<V2Segment> Read(int i, V2ProjectedCurves originals,
        Dictionary<(string Source, int Edge), int> edgeIds, Dictionary<int, Line> meshRims,
        V2HiddenSources hiddenSources, V2TangentEdges tangencies, Action<int, Curve> rememberSilhouette,
        Action<int, BrepEdge> rememberTrim = null, Action<string> sample = null)
    {
        var face = faces[i];
        var segments = new List<V2Segment>();
        if (face.Triangles is V2NativeMesh mesh)
        {
            long begin = Stopwatch.GetTimestamp();
            var proxyEdges = face.ProxyChord > 0 ? face.Face.AdjacentEdges()
                .Select(edge => face.Face.Brep.Edges[edge]).Where(edge => edge.Valence == EdgeAdjacency.Interior).ToArray()
                : Array.Empty<BrepEdge>();
            foreach (var rim in mesh.Rims(Math.Max(Math.PI / 6, angle), cancel, camera.Perspective))
            {
                MeshLine(new Line(rim.A, rim.B), rim.Kind);
            }
            foreach (var plane in clips.Crossing(face.Box).Where(plane =>
                camera.IsSection(plane) || !mesh.ViewClips.Planes.Contains(plane)))
                foreach (var triangle in mesh.ExactTriangles())
                {
                    cancel?.Invoke();
                    if (V2MeshContact.Section(triangle, plane, tolerance * .1, out var cut))
                        MeshLine(cut, camera.IsSection(plane) ? "SectionCut" : "ViewClip");
                }
            measure("mesh boundary extraction", begin);
            return segments;

            void MeshLine(Line line, string kind)
            {
                if (!clips.Trim(ref line)) return;
                var a = camera.Project(line.From);
                var b = camera.Project(line.To);
                if (a.DistanceTo(b) < drawing.ImageGrid) return;
                int id = NextId();
                if (kind == "MeshBoundary" && proxyEdges.Length > 0 &&
                    tangencies?.RememberProxy(id, face, proxyEdges, line) == true) kind = "Original";
                if (kind is "MeshCrease" or "MeshSilhouette" or "SectionCut")
                    meshRims.Add(id, line);
                hiddenSources?.Remember(id, line, new Interval(0, 1));
                segments.Add(new V2Segment(a, b, new V2SourceSpan(id, i, kind, 0, 1)));
            }
        }
        // Exact bounds and closest-point tests populate native acceleration
        // data. Keep it on owned curves that expire with this face, instead of
        // on the borrowed topology of the archive's long-lived Breps.
        using var detached = detachOriginals ? new CurveScope() : null;
        var trims = new List<(Curve Edge, int Curve)>();
        var readEdges = new HashSet<int>();
        foreach (var loop in face.Face.Loops)
            foreach (var trim in loop.Trims)
                if (trim.Edge is BrepEdge edge && readEdges.Add(edge.EdgeIndex))
                {
                    var key = (face.Id.Source, edge.EdgeIndex);
                    if (!edgeIds.TryGetValue(key, out int id)) edgeIds.Add(key, id = NextId());
                    int before = segments.Count;
                    long start = Stopwatch.GetTimestamp();
                    var model = detached?.Copy(edge) ?? edge;
                    segments.AddRange(Discretize(model, i, "Original", originals, id,
                        trim.TrimType == BrepTrimType.Seam ? null : hiddenSources, tangencies, edge));
                    if (camera.Perspective && rememberTrim != null)
                    {
                        int recorded = -1;
                        for (int j = before; j < segments.Count; j++)
                        {
                            int curve = segments[j].Source.CurveId;
                            if (curve == recorded || !originals.ContainsKey(curve)) continue;
                            rememberTrim(curve, edge);
                            recorded = curve;
                        }
                    }
                    measure("original edge discretization", start);
                    if (segments.Count > before) trims.Add((model, id));
                }
        sample?.Invoke("boundary: original trims");
        if (!face.Plane.HasValue && !face.Mesh)
            foreach (var curve in V2Silhouettes.Read(face.Face, camera, tolerance * .05, Math.Min(angle, .001), start =>
            {
                drawing.SilhouetteCalls++;
                measure("Rhino silhouette", start);
                sample?.Invoke("boundary: silhouette compute");
            }, detachOriginals))
            {
                using (curve)
                {
                    int id = NextId();
                    var contour = Discretize(curve, i, "Silhouette", originals, id, hiddenSources).ToArray();
                    if (contour.Length == 0) continue;
                    rememberSilhouette(id, curve);
                    if (!curve.IsClosed && clips.Keeps(face.Box))
                    {
                        Attach(curve.PointAtStart, contour[0].A, contour[0].Source);
                        Attach(curve.PointAtEnd, contour[^1].B, contour[^1].Source);
                    }
                    segments.AddRange(contour);
                }
            }
        sample?.Invoke("boundary: silhouette and weld");
        var sections = clips.Crossing(face.Box).ToArray();
        if (sections.Length > 0)
        {
            using var original = face.Face.DuplicateFace(false);
            foreach (var plane in sections)
            {
                if (face.Plane is Plane planar && Math.Abs(planar.Normal * plane.Normal) > 1 - 1e-12) continue;
                Intersection.BrepPlane(original, plane, tolerance * .02, out var intersections, out _);
                foreach (var cut in intersections ?? Array.Empty<Curve>())
                    using (cut)
                        segments.AddRange(Discretize(cut, i, camera.IsSection(plane) ? "SectionCut" : "ViewClip",
                            originals, NextId(), hiddenSources));
            }
        }
        sample?.Invoke("boundary: sections");
        return segments;

        void Attach(Point3d end, Point2d projected, V2SourceSpan source)
        {
            // The corresponding 3D trim event is required before welding its 2D approximation.
            double budget = tolerance * 2;
            var hits = new List<(int Curve, Point3d Point, double Distance)>();
            foreach (var (edge, id) in trims)
                if (edge.ClosestPoint(end, out double t, budget))
                {
                    var at = edge.PointAt(t);
                    hits.Add((id, at, at.DistanceTo(end)));
                }
            if (hits.Count == 0) return;
            var hit = hits.MinBy(candidate => candidate.Distance);
            if (hits.Any(candidate => candidate.Point.DistanceTo(hit.Point) > tolerance))
                return;

            var trimPoint = camera.Project(hit.Point);
            int selected = -1;
            double along = 0, best = double.PositiveInfinity;
            Point2d join = Point2d.Unset;
            for (int j = 0; j < segments.Count; j++)
            {
                var segment = segments[j];
                if (segment.Source.CurveId != hit.Curve) continue;
                var v = segment.B - segment.A;
                if (v.SquareLength == 0) continue;
                double t = Math.Clamp(((trimPoint.X - segment.A.X) * v.X +
                    (trimPoint.Y - segment.A.Y) * v.Y) / v.SquareLength, 0, 1);
                var p = segment.A + t * v;
                double distance = p.DistanceTo(trimPoint);
                if (distance >= best) continue;
                (selected, along, best, join) = (j, t, distance, p);
            }
            if (selected < 0 || projected == join || projected.DistanceTo(join) > imageTolerance * 2.25)
                return;
            var part = segments[selected];
            if (along > 0 && along < 1)
            {
                double t = part.Source.From + along * (part.Source.To - part.Source.From);
                segments[selected] = part with { B = join, Source = part.Source with { To = t } };
                segments.Add(part with { A = join, Source = part.Source with { From = t } });
            }
            segments.Add(new V2Segment(projected, join, source));
        }
    }

    internal IEnumerable<V2Segment> Discretize(Curve model, int face, string kind, V2ProjectedCurves originals, int id,
        V2HiddenSources hiddenSources = null, V2TangentEdges tangencies = null, BrepEdge topology = null)
    {
        foreach (var piece in clips.Pieces(model, tolerance * .02))
            if (ReferenceEquals(piece, model))
            {
                if (topology != null) tangencies?.Remember(id, topology);
                foreach (var segment in ProjectPiece(model, face, kind, originals, id, hiddenSources)) yield return segment;
            }
            else
                using (piece)
                {
                    int pieceId = NextId();
                    if (topology != null) tangencies?.Remember(pieceId, topology);
                    foreach (var segment in ProjectPiece(piece, face, kind, originals, pieceId, hiddenSources)) yield return segment;
                }
    }

    private sealed class CurveScope : IDisposable
    {
        private readonly List<Curve> curves = new();

        internal Curve Copy(BrepEdge edge)
        {
            var curve = edge.DuplicateCurve();
            if (curve != null) curves.Add(curve);
            return curve;
        }

        public void Dispose()
        {
            foreach (var curve in curves) curve.Dispose();
        }
    }

    private IEnumerable<V2Segment> ProjectPiece(Curve model, int face, string kind, V2ProjectedCurves originals, int id,
        V2HiddenSources hiddenSources)
    {
        using var curve = camera.ProjectCurve(model);
        if (curve == null) yield break;
        var bounds = curve.GetBoundingBox(true).Diagonal;
        if (Math.Max(bounds.X, bounds.Y) < drawing.ImageGrid) yield break;
        if (!curve.IsClosed && curve.IsLinear(imageTolerance * .025))
        {
            if (hiddenSources != null)
            {
                if (model.IsLinear(tolerance * .01))
                    hiddenSources.Remember(id, new Line(model.PointAtStart, model.PointAtEnd), curve.Domain);
                else hiddenSources.Remember(id, model, curve.Domain, collapsed: true);
            }
            yield return new V2Segment(XY(curve.PointAtStart), XY(curve.PointAtEnd),
                new V2SourceSpan(id, face, kind, curve.Domain.Min, curve.Domain.Max));
            yield break;
        }
        // An open rim can double back in projection even though its image
        // is a straight interval. Both open and closed rims need their true extrema.
        if (TryCollapsedSpan(curve, imageTolerance, out var span))
        {
            hiddenSources?.Remember(id, model, curve.Domain, collapsed: true);
            yield return new V2Segment(XY(span.From), XY(span.To),
                new V2SourceSpan(id, face, kind, curve.Domain.Min, curve.Domain.Max));
            yield break;
        }
        originals.Capture(id, curve);
        hiddenSources?.Remember(id, model, curve.Domain);
        Polyline points;
        if (!curve.TryGetPolyline(out points))
        {
            using var polyline = curve.ToPolyline(chordTolerance, Math.Max(.35, angle), 0, 0);
            if (polyline == null || !polyline.TryGetPolyline(out points))
            {
                var exactSpans = SubdivideProjected(curve, chordTolerance * .5);
                if (exactSpans == null)
                    drawing.Issues.Add($"Could not discretize {kind} of {faces[face].Id}.");
                else
                    for (int i = 1; i < exactSpans.Count; i++)
                    {
                        var a = exactSpans[i - 1];
                        var b = exactSpans[i];
                        yield return new V2Segment(XY(a.Point), XY(b.Point),
                            new V2SourceSpan(id, face, kind, a.Parameter, b.Parameter));
                    }
                yield break;
            }
        }
        double previous = curve.Domain.Min;
        for (int i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            double parameter = i == points.Count - 1 ? curve.Domain.Max :
                curve.ClosestPoint(b, out double at) ? at : curve.Domain.ParameterAt(i / (points.Count - 1d));
            yield return new V2Segment(new Point2d(a.X, a.Y), new Point2d(b.X, b.Y),
                new V2SourceSpan(id, face, kind, previous, parameter));
            previous = parameter;
        }

        static Point2d XY(Point3d p) => new(p.X, p.Y);
    }
    private static bool TryCollapsedSpan(Curve curve, double tolerance, out Line span)
    {
        span = Line.Unset;
        var start = curve.PointAtStart;
        Span<Vector3d> stations = stackalloc Vector3d[4];
        var direction = curve.PointAtEnd - start;
        stations[3] = direction;
        for (int i = 0; i < 3; i++)
        {
            var vector = curve.PointAt(curve.Domain.ParameterAt((i + 1) * .25)) - start;
            stations[i] = vector;
            if (vector.SquareLength > direction.SquareLength) direction = vector;
        }
        if (direction.Length < tolerance * .01 || !direction.Unitize()) return false;
        double roundoff = Math.Max(Math.Max(1, start.MaximumCoordinate) * 2e-14, tolerance * 1e-6);
        foreach (var station in stations)
            if (Math.Abs(direction.X * station.Y - direction.Y * station.X) > roundoff)
                return false;
        var frame = new Plane(start, direction, Vector3d.CrossProduct(Vector3d.ZAxis, direction));
        var exact = curve.GetBoundingBox(frame);
        if (!exact.IsValid || exact.Diagonal.Y > roundoff)
            return false;
        double y = (exact.Min.Y + exact.Max.Y) * .5;
        span = new Line(frame.PointAt(exact.Min.X, y), frame.PointAt(exact.Max.X, y));
        return span.IsValid;
    }

    internal static List<(Point3d Point, double Parameter)> SubdivideProjected(Curve curve, double fit)
    {
        using var nurbs = curve.ToNurbsCurve();
        if (nurbs == null) return null;
        var samples = new List<(Point3d, double)> { (nurbs.PointAtStart, nurbs.Domain.Min) };
        // Bezier extraction addresses the raw knot slots, including zero-width slots at repeated knots.
        int degree = nurbs.Degree;
        for (int i = 0; i < nurbs.Points.Count - degree; i++)
        {
            double from = nurbs.Knots[i + degree - 1], to = nurbs.Knots[i + degree];
            if (!(to > from)) continue;
            using var span = nurbs.ConvertSpanToBezier(i);
            if (span == null || !Subdivide(span, from, to, 0)) return null;
        }
        return samples.Count > 1 ? samples : null;

        bool Subdivide(BezierCurve span, double from, double to, int depth)
        {
            var a = span.GetControlVertex3d(0);
            var b = span.GetControlVertex3d(span.ControlVertexCount - 1);
            if (!a.IsValid || !b.IsValid) return false;
            var chord = new Line(a, b);
            bool flat = true;
            for (int i = 0; i < span.ControlVertexCount; i++)
            {
                if (span.IsRational && !(span.GetControlVertex4d(i).W > 0)) return false;
                var point = span.GetControlVertex3d(i);
                flat &= point.IsValid && (chord.IsValid ? chord.DistanceTo(point, true) : point.DistanceTo(a)) <= fit;
            }
            if (flat)
            {
                samples.Add((b, to));
                return true;
            }
            if (depth >= 24 || !span.Split(.5, out var left, out var right)) return false;
            using (left)
            using (right)
            {
                double mid = from + (to - from) * .5;
                return Subdivide(left, from, mid, depth + 1) && Subdivide(right, mid, to, depth + 1);
            }
        }
    }
}
