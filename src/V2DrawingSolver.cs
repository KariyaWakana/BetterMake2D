using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace BetterMake2D;

internal sealed class V2Drawing : IDisposable
{
    internal readonly List<DraftRegion> Regions = new();
    internal readonly List<DrawnEdge> Curves = new();
    internal readonly List<string> Issues = new();
    internal readonly Dictionary<string, TimeSpan> Timing = new();
    internal int InputFaces, BackfaceCulled, HiddenFaces, Segments;
    internal int HiZFaces, HiZPixels, HiZOccluders, HiZCurvedOccluders;
    internal int PlannedTiles, ProvenTiles, ContactBoxPrunes, TileContactPrunes, TileContactBoxPrunes;
    internal long TileFaceCandidates, TileFacesHidden, ArrangementSegments;
    internal int SupportFaces, RegisteredFaces, UnregisteredCandidates;
    internal int Clusters, Backgrounds, DistributionCells, SnapRounds, Edges, Rays, NativeRays, MeshHits, Contacts;
    internal int MeshLookups, CachedMeshFaces, MeshTriangles, PlaneChecks;
    internal int BoundaryEdges, SharedChains, RestoredChains, SkippedCurveSources;
    internal int StraightChains, SmoothChains, PolylineChains, RejectedUpgrades, RecoveredRegions;
    internal int AbsorbedSlivers, FlattenedSteps, LinesBeforeJoin, ClosedLinework;
    internal int TangentChains, TangentChainsFiltered;
    internal int ContactCandidates, EmptyContacts, PointContacts, SeparatePlanes, ExactCoincidence, SilhouetteCalls;
    internal int AnalyticPlanarPairs, AnalyticContactSegments, PlanarContactFallbacks, PerspectiveDepthPrunes;
    internal int ContactTreePrunes;
    internal int ContactLanes, ParallelEmptyContacts, ParallelContactAttempts;
    internal int ContactPilotAttempts, CurvedHintEarlyExits;
    internal int CurvedRenderFaces, GeneratedHintFaces, GeneratedHintTriangles;
    internal int DepthLanes, ParallelDepthTiles;
    internal int BoundRayPrunes, BoundContactPrunes, ManagedPlaneChecks, PlanarEarlyExits, AdjacencyHits;
    internal int NativeMeshes, NativeMeshTriangles, MeshContactPairs;
    internal int ProxyFaces, ProxyTriangles;
    internal int SourceMeshTriangles, RejectedViewTriangles;
    internal int LargestClusterFaces, LargestClusterSegments, LargestClusterCells;
    internal readonly List<(int Faces, int Segments, int Cells, bool Background)> ClusterSizes = new();
    internal readonly List<V2TileReport> Tiles = new();
    internal double PlaneSeconds, NativeSeconds, ImageGrid;
    internal int LazyInitialOwners, LazyCandidates, LazyRounds, LazyRebuiltTiles, LazyPendingAtFixedPoint;
    internal long LazyInitialNeighbors, LazyDeferred2D, LazyDeferredDepth;
    internal int ContactCurveHits;
    internal long LazyPassSegments, LazyDirtyCells, LazyFrozenCells;
    internal int LazyPatchExpansions;
    internal readonly List<V2LazyContactPass> LazyContactPasses = new();
    internal string LazyContactSummary =>
        $"Lazy contacts: {LazyInitialOwners}/{LazyCandidates} Pass 1 owners; " +
        $"{LazyInitialNeighbors}/{ContactCandidates} initial owner-neighbor/AABB pairs; " +
        $"{LazyDeferred2D} XY and {LazyDeferredDepth} depth deferrals across rounds; " +
        $"{LazyRounds} native batches, {LazyPendingAtFixedPoint} pairs outside the fixed-point front, " +
        $"{LazyRebuiltTiles} tile updates and {LazyPassSegments} segments processed across passes; " +
        $"{LazyDirtyCells} dirty and {LazyFrozenCells} frozen cells, {LazyPatchExpansions} perimeter expansions.";
    internal string TileSummary =>
        $"Tile-first: {PlannedTiles} planned, {ProvenTiles} certified, {Tiles.Count} executed; " +
        $"{TileFacesHidden}/{TileFaceCandidates} face-window candidates hidden; " +
        $"{ContactBoxPrunes} global contact boxes hidden, {TileContactPrunes} tile contact prunes " +
        $"({TileContactBoxPrunes} by local contact coverage); {ArrangementSegments} segments entered arrangement.";
    public void Dispose()
    {
        foreach (var region in Regions) region.Brep.Dispose();
        foreach (var line in Curves) line.Curve.Dispose();
        Regions.Clear();
        Curves.Clear();
    }
}

internal static class V2DrawingSolver
{
    internal static V2Drawing Solve(IReadOnlyList<Input> inputs, double tolerance, double angle,
        Action cancel = null, SourceFaceId? focus = null, Action<string> progress = null,
        IReadOnlyList<Point2d> inspection = null, int tileLimit = V2TiledArrangement.DefaultSegmentLimit,
        int minimumTileDepth = 0, IReadOnlyDictionary<string, int[]> registered = null,
        Action<SourceFaceId, Point3d[][], bool> inspectRejected = null,
        Func<string, string> layerOf = null, double millimetersPerUnit = 1, bool overlapTileArrangement = false,
        V2HiddenMode hiddenMode = V2HiddenMode.None, string hiddenLayer = null, bool enableHiZ = true,
        IReadOnlyList<Plane> clipPlanes = null, IProjectionCamera projection = null, bool tangentEdges = false,
        Func<string, bool> hiddenSourceFilter = null, Func<string, string> lineLayerOf = null, bool reportTileTotal = false,
        bool renderMeshHints = true, V2Accuracy accuracy = V2Accuracy.Exact,
        bool? spillDuringBoundaryCollection = null, Action<string> profileMemory = null,
        Action<V2PlanarArrangement> inspectStitched = null, Action releaseInputs = null)
    {
        var drawing = new V2Drawing();
        drawing.SupportFaces = inputs.Sum(input => input.Brep?.Faces.Count ?? 1);
        drawing.RegisteredFaces = registered?.Values.Sum(indices => indices.Length) ?? drawing.SupportFaces;
        long stage = Stopwatch.GetTimestamp();
        var clips = projection?.Clips ?? new V2ClipPlanes(clipPlanes ?? Array.Empty<Plane>());
        var faces = ReadFaces(inputs, tolerance, focus, cancel, projection, clips, hiddenMode, drawing).ToArray();
        progress?.Invoke($"BackfaceCulled: {drawing.BackfaceCulled}/{faces.Length + drawing.BackfaceCulled} original faces screened.");
        if (faces.Length == 0) return drawing;
        IProjectionCamera camera = projection ?? new V2ParallelCamera(faces.Min(face => face.Box.Min.Z) - tolerance * 4,
            faces.Max(face => face.Box.Max.Z) + tolerance * 4, clipPlanes);
        var extent = BoundingBox.Empty;
        foreach (var face in faces) extent.Union(face.Box);
        double imageTolerance = camera.ImageTolerance(tolerance, extent, millimetersPerUnit);
        drawing.ImageGrid = imageTolerance * .01;
        double chordTolerance = imageTolerance * .05;
        if (focus is SourceFaceId focused)
        {
            var target = faces.Single(face => face.Id == focused);
            var targetBox = target.Screen.Grow(imageTolerance);
            faces = faces.Where(face => face.Id == focused ||
                !Behind(face, target, face.Screen.Grow(imageTolerance).Intersection(targetBox), tolerance, camera)).ToArray();
        }
        drawing.InputFaces = faces.Length;
        Record("original faces", stage);
        stage = Stopwatch.GetTimestamp();
        HashSet<SourceFaceId> hiZCovered = null;
        V2HiZ hiZ = null;
        if (enableHiZ)
        {
            hiZ = new V2HiZ(faces, tolerance, cancel, camera);
            hiZCovered = new HashSet<SourceFaceId>();
            foreach (var face in faces)
            {
                cancel?.Invoke();
                if (face.Id != focus && hiZ.Hides(face.Box)) hiZCovered.Add(face.Id);
            }
            drawing.HiZFaces = hiZCovered.Count;
            drawing.HiZPixels = hiZ.Written;
            drawing.HiZOccluders = hiZ.Occluders;
            drawing.HiZCurvedOccluders = hiZ.CurvedOccluders;
            Record("conservative Hi-Z", stage);
            stage = Stopwatch.GetTimestamp();
        }
        var candidates = hiZCovered is { Count: > 0 } ?
            faces.Where(face => !hiZCovered.Contains(face.Id)).ToArray() : faces;
        var visibleFaces = clips.Active ? candidates : RemoveCovered(candidates, tolerance, cancel, focus);
        drawing.HiddenFaces = drawing.InputFaces - visibleFaces.Length;
        HashSet<SourceFaceId> hiddenOnly = null;
        if (hiddenMode != V2HiddenMode.None)
        {
            var frontIds = visibleFaces.Select(face => face.Id).ToHashSet();
            hiddenOnly = faces.Where(face => !frontIds.Contains(face.Id) && HiddenLayerMatches(face.Id.Source))
                .Select(face => face.Id).ToHashSet();
            faces = faces.Where(face => frontIds.Contains(face.Id) || hiddenOnly.Contains(face.Id)).ToArray();
        }
        else faces = visibleFaces;
        drawing.UnregisteredCandidates = registered == null ? 0 : faces.Count(face =>
            !registered.TryGetValue(face.Id.Source, out var ids) || Array.BinarySearch(ids, face.Id.FaceIndex) < 0);
        progress?.Invoke($"Faces: {drawing.RegisteredFaces} registered, {drawing.SupportFaces} support, " +
            $"{faces.Length} boundary candidates ({drawing.UnregisteredCandidates} outside the registry), " +
            $"{drawing.HiddenFaces} proven covered.");
        Record("strict planar coverage", stage);
        stage = Stopwatch.GetTimestamp();
        if (accuracy == V2Accuracy.Fast)
        {
            progress?.Invoke("Fine mesh preparation started.");
            V2MeshProxy.Prepare(faces, camera, tolerance, millimetersPerUnit, cancel, progress);
            drawing.ProxyFaces = faces.Count(face => face.ProxyChord > 0);
            drawing.ProxyTriangles = faces.Where(face => face.ProxyChord > 0).Sum(face => face.Triangles.TriangleCount);
            progress?.Invoke($"Fine mesh proxies: {drawing.ProxyFaces} original faces, {drawing.ProxyTriangles} triangles.");
            Record("fine mesh preparation", stage);
            stage = Stopwatch.GetTimestamp();
        }
        drawing.NativeMeshes = faces.Count(face => face.Triangles != null);
        drawing.NativeMeshTriangles = faces.Sum(face => face.Triangles?.TriangleCount ?? 0);
        drawing.SourceMeshTriangles = faces.Sum(face => face.Triangles?.SourceTriangles ?? 0);
        drawing.RejectedViewTriangles = faces.Sum(face => face.Triangles?.RejectedViewTriangles ?? 0);
        profileMemory?.Invoke("indices: start");
        var surfaceBounds = new V2SurfaceBounds(faces, tolerance, cancel);
        profileMemory?.Invoke("indices: surface bounds");
        Record("conservative surface bounds", stage);
        stage = Stopwatch.GetTimestamp();
        using var index = new V2DepthIndex(faces, camera, tolerance, surfaceBounds, hiddenOnly, imageTolerance, renderMeshHints);
        surfaceBounds.ReleaseHints();
        profileMemory?.Invoke("indices: depth index");
        int depthLanes = faces.Length > 70000 ? 2 : 4;
        using var depthPool = new V2DepthPool(index, faces.Length >= 2048 ? Math.Min(depthLanes, Environment.ProcessorCount) : 1);
        using var planarContact = new V2PlanarContact(faces, clips, tolerance);
        profileMemory?.Invoke("indices: planar contact");
        var contacts = new V2ContactCollector(faces, drawing, camera, clips, hiZ, surfaceBounds,
            planarContact, tolerance, cancel, progress, AddTime);
        var backgrounds = Array.Empty<int>();
        var hiddenOverlay = hiddenOnly == null ? null : hiZCovered;
        var groups = focus.HasValue ? Array.Empty<int[]>() : Clusters(faces, imageTolerance, out backgrounds,
            peelBackgrounds: hiddenMode == V2HiddenMode.None && !clips.Active, cancel, hiddenOverlay);
        profileMemory?.Invoke("indices: clusters");
        // Preserve the finer topology where different projected owners can meet.
        // The paper-resolution floor applies only after proving there is no such overlap.
        if (!focus.HasValue && hiddenMode == V2HiddenMode.None && camera is V2PerspectiveCamera perspective &&
            groups.All(group => group.Length == 1))
        {
            drawing.ImageGrid = perspective.ImageGrid(imageTolerance, extent, millimetersPerUnit);
            chordTolerance = Math.Max(chordTolerance, drawing.ImageGrid * .75);
        }
        Record("indices and clusters", stage);
        drawing.Backgrounds = backgrounds.Length;
        drawing.Clusters = focus.HasValue ? 1 : groups.Length;
        progress?.Invoke($"Components: {drawing.Clusters} foreground clusters, {backgrounds.Length} backgrounds, " +
            $"maximum {(!focus.HasValue ? groups.Select(group => group.Length).DefaultIfEmpty().Max() : faces.Length)} faces.");
        if (drawing.NativeMeshes > 0)
            progress?.Invoke($"Native mesh depth: {drawing.NativeMeshes} owners, {drawing.NativeMeshTriangles} triangles " +
                $"from {drawing.SourceMeshTriangles} ({drawing.RejectedViewTriangles} discarded by the view frustum).");
        var boundaries = new V2BoundaryCollector(faces, drawing, camera, clips,
            tolerance, imageTolerance, chordTolerance, angle, cancel, Measure,
            detachOriginals: spillDuringBoundaryCollection == true ||
                spillDuringBoundaryCollection != false && faces.Length > 50000);
        double sharedGrid = drawing.ImageGrid;
        var masks = new List<V2Segment>();
        var hiddenEdges = hiddenMode == V2HiddenMode.None ? null :
            new HashSet<(V2PlanarArrangement.Key A, V2PlanarArrangement.Key B, double Grid)>();
        try
        {
            if (focus is SourceFaceId target)
            {
                int owner = Array.FindIndex(faces, face => face.Id == target);
                if (owner < 0) throw new InvalidOperationException("The requested original has no projection.");
                var members = Enumerable.Range(0, faces.Length).ToArray();
                Process(members, new HashSet<int> {owner}, owner);
            }
            else
            {
                foreach (var group in groups)
                {
                    if (group.Length == 1 && faces[group[0]].Plane.HasValue && clips.Keeps(faces[group[0]].Box) &&
                        backgrounds.All(i => !faces[i].Screen.Grow(imageTolerance)
                            .Intersection(faces[group[0]].Screen).HasArea) &&
                        Direct(group[0]))
                        continue;
                    Process(group, group.ToHashSet(), collectMask: backgrounds.Length != 0);
                }
                foreach (int owner in backgrounds)
                {
                    Process(new[] {owner}, new HashSet<int> {owner}, owner, useMasks: true);
                }
            }
            stage = Stopwatch.GetTimestamp();
            if (hiddenOverlay is { Count: > 0 } && !focus.HasValue)
            {
                EmitProvenHidden();
                Record("proven hidden line overlay", stage);
                stage = Stopwatch.GetTimestamp();
            }
            drawing.LinesBeforeJoin = drawing.Curves.Count;
            V2Linework.Join(drawing.Curves, lineLayerOf ?? layerOf, cancel);
            drawing.ClosedLinework = drawing.Curves.Count(edge => edge.Curve.IsClosed);
            Record("join styled linework", stage);
            drawing.Rays = index.Rays;
            drawing.NativeRays = index.NativeQueries;
            drawing.BoundRayPrunes = index.BoundPrunes;
            drawing.MeshHits = index.MeshHits;
            drawing.DepthLanes = depthPool.MaxLanes;
            drawing.ParallelDepthTiles = depthPool.ParallelTiles;
            drawing.CurvedRenderFaces = index.CurvedRenderFaces;
            drawing.GeneratedHintFaces = index.GeneratedHintFaces;
            drawing.GeneratedHintTriangles = index.GeneratedHintTriangles;
            drawing.MeshLookups = index.MeshLookups;
            drawing.CachedMeshFaces = index.CachedMeshFaces;
            drawing.MeshTriangles = index.TriangleCount;
            drawing.PlaneChecks = index.PlaneChecks;
            drawing.ManagedPlaneChecks = index.ManagedPlaneChecks;
            drawing.PlanarEarlyExits = index.PlanarEarlyExits;
            drawing.CurvedHintEarlyExits = index.CurvedEarlyExits;
            drawing.AdjacencyHits = index.AdjacencyHits;
            drawing.PlaneSeconds = index.PlaneTicks / (double)Stopwatch.Frequency;
            drawing.NativeSeconds = index.NativeTicks / (double)Stopwatch.Frequency;
            progress?.Invoke(drawing.TileSummary);
            if (drawing.LazyCandidates > 0)
                progress?.Invoke(drawing.LazyContactSummary);
            return drawing;
        }
        catch
        {
            drawing.Dispose();
            throw;
        }

        void EmitProvenHidden()
        {
            var foreground = drawing.Curves.Where(edge => !edge.IsHidden).ToArray();
            using var borders = new RTree();
            for (int i = 0; i < foreground.Length; i++) borders.Insert(foreground[i].Curve.GetBoundingBox(true), i);
            using var originals = new V2ProjectedCurves();
            using var sources = new V2HiddenSources();
            var ids = new Dictionary<(string Source, int Edge), int>();
            var rims = new Dictionary<int, Line>();
            var stations = new List<double>();
            for (int i = 0; i < faces.Length; i++)
            {
                if (!hiddenOverlay.Contains(faces[i].Id)) continue;
                cancel?.Invoke();
                foreach (var span in boundaries.Read(i, originals, ids, rims, sources, null, (_, _) => {}))
                {
                    var a = new Point3d(span.A.X, span.A.Y, 0);
                    var b = new Point3d(span.B.X, span.B.Y, 0);
                    if (a == b) continue;
                    using var line = new LineCurve(a, b);
                    var box = line.GetBoundingBox(false);
                    box.Inflate(sharedGrid);
                    stations.Clear();
                    stations.Add(line.Domain.Min);
                    stations.Add(line.Domain.Max);
                    borders.Search(box, (_, hit) =>
                    {
                        using var intersections = Intersection.CurveCurve(line, foreground[hit.Id].Curve, sharedGrid, sharedGrid);
                        foreach (var contact in intersections ?? Enumerable.Empty<IntersectionEvent>())
                        {
                            stations.Add(contact.ParameterA);
                            if (contact.IsOverlap) stations.Add(contact.OverlapA.T1);
                        }
                    });
                    stations.Sort();
                    for (int j = 1; j < stations.Count; j++)
                    {
                        var start = line.PointAt(stations[j - 1]);
                        var end = line.PointAt(stations[j]);
                        var keyA = V2PlanarArrangement.Snap(new Point2d(start.X, start.Y), sharedGrid);
                        var keyB = V2PlanarArrangement.Snap(new Point2d(end.X, end.Y), sharedGrid);
                        if (keyA == keyB) continue;
                        if (keyA.CompareTo(keyB) > 0) (keyA, keyB) = (keyB, keyA);
                        var middle = new Point2d((start.X + end.X) * .5, (start.Y + end.Y) * .5);
                        var along = new Vector2d(end.X - start.X, end.Y - start.Y);
                        var (owner, _) = index.Classify(middle);
                        if (owner < 0 || hiddenMode == V2HiddenMode.Occluded && faces[owner].Id.Source == faces[i].Id.Source ||
                            !sources.TryPoint(span.Source, middle, along, imageTolerance * .1, out var position, camera) ||
                            hiddenMode == V2HiddenMode.Occluded && IsBackFace(faces[i].Face, position, camera.Perspective) ||
                            position.Z >= index.DepthAt(owner, middle) - tolerance * .1 ||
                            !hiddenEdges.Add((keyA, keyB, sharedGrid))) continue;
                        drawing.Curves.Add(new DrawnEdge(new LineCurve(start, end), faces[i].Id.Source,
                            faces[i].Id.FaceIndex, span.Source.Kind, faces[owner].Id.Source, sharedGrid) { IsHidden = true });
                    }
                }
            }
        }

        bool HiddenLayerMatches(string source)
        {
            if (hiddenSourceFilter != null && !hiddenSourceFilter(source)) return false;
            if (string.IsNullOrWhiteSpace(hiddenLayer)) return true;
            string layer = layerOf?.Invoke(source);
            return layer != null && (string.Equals(layer, hiddenLayer, StringComparison.Ordinal) ||
                layer.StartsWith(hiddenLayer + "::", StringComparison.Ordinal));
        }

        bool Direct(int owner)
        {
            var loops = new List<Curve>();
            var original = faces[owner];
            try
            {
                foreach (var loop in original.Face.Loops.OrderBy(loop => loop.LoopType != BrepLoopType.Outer))
                {
                    using var model = loop.To3dCurve();
                    var projected = model == null ? null : camera.ProjectCurve(model);
                    if (projected is not { IsClosed: true })
                    {
                        projected?.Dispose();
                        return false;
                    }
                    loops.Add(projected);
                }
                var regions = Brep.CreatePlanarBreps(loops, drawing.ImageGrid);
                if (regions is not { Length: 1 } || !regions[0].IsValid ||
                    regions[0].Faces.Count != 1 || regions[0].Faces[0].Loops.Count != loops.Count)
                {
                    foreach (var region in regions ?? Array.Empty<Brep>()) region.Dispose();
                    return false;
                }
                drawing.Regions.Add(new DraftRegion(regions[0], original.Id.Source, original.Id.FaceIndex));
                foreach (var loop in loops)
                    drawing.Curves.Add(new DrawnEdge(loop.DuplicateCurve(), original.Id.Source, original.Id.FaceIndex,
                        "Original", "", drawing.ImageGrid));
                return true;
            }
            finally { foreach (var loop in loops) loop.Dispose(); }
        }

        void Process(int[] members, HashSet<int> output, int? background = null, bool collectMask = false, bool useMasks = false)
        {
            cancel?.Invoke();
            long start = Stopwatch.GetTimestamp();
            double fit = useMasks ? sharedGrid : drawing.ImageGrid;
            var tileSchedule = !focus.HasValue && !useMasks && !collectMask
                ? V2TileSchedule.Create(members, faces, camera, hiZ, surfaceBounds, tolerance, fit, imageTolerance,
                    tileLimit, minimumTileDepth, hiddenMode == V2HiddenMode.None ? null :
                        id => HiddenLayerMatches(faces[id].Id.Source), cancel)
                : null;
            if (tileSchedule != null)
            {
                Record("tile visibility planning", start);
                start = Stopwatch.GetTimestamp();
                if (tileSchedule.Relevant)
                {
                    drawing.PlannedTiles += tileSchedule.Tiles.Length;
                    drawing.ProvenTiles += tileSchedule.Tiles.Count(tile => tile.ProvenOwner >= 0);
                    drawing.TileFaceCandidates += tileSchedule.FaceCandidates;
                    drawing.TileFacesHidden += tileSchedule.HiddenFaces;
                }
                else
                {
                    progress?.Invoke($"Retained density-based tiling: {tileSchedule.HiddenFaces}/{tileSchedule.FaceCandidates} " +
                        $"proven hidden face-window candidates in {tileSchedule.Tiles.Length} planned windows, no certified owner blocks.");
                    tileSchedule = null;
                }
            }
            using var originals = new V2ProjectedCurves();
            using var hiddenSources = hiddenMode == V2HiddenMode.None ? null : new V2HiddenSources();
            var meshRims = drawing.NativeMeshes == 0 ? null : new Dictionary<int, Line>();
            var edgeIds = new Dictionary<(string Source, int Edge), int>();
            using var tangencies = new V2TangentEdges(faces, camera, tolerance, angle);
            bool collectOnDisk = !focus.HasValue && (spillDuringBoundaryCollection ?? members.Length > 50000);
            using var sourceSegments = new V2SegmentBuffer(collectOnDisk);
            var segments = sourceSegments;
            using var contactContours = new V2ProjectedCurves();
            var silhouetteSegments = new Dictionary<int, List<int>>();
            var silhouetteSpans = new Dictionary<int, List<(Curve Curve, List<int> Segments)>>();
            var curvedTrimSegments = new Dictionary<int, List<int>>();
            var curvedTrims = new Dictionary<int, List<(int Curve, BrepEdge Edge, List<int> Segments)>>();
            int boundariesRead = 0;
            foreach (int member in members)
            {
                boundariesRead++;
                if (collectOnDisk && (boundariesRead & 4095) == 0)
                    progress?.Invoke($"Projected boundaries: {boundariesRead}/{members.Length} faces, {segments.Count} lossless segments.");
                if (tileSchedule != null && !tileSchedule.NeedsBoundary(member)) continue;
                var sample = (boundariesRead & 31) == 0 ? profileMemory : null;
                sample?.Invoke("boundary: face");
                foreach (var segment in boundaries.Read(member, originals, edgeIds, meshRims,
                    HiddenLayerMatches(faces[member].Id.Source) ? hiddenSources : null, tangencies, (id, model) =>
                {
                    contactContours.Capture(id, model);
                    if (!contactContours.TryGetValue(id, out var curve)) return;
                    var indexes = new List<int>();
                    silhouetteSegments.Add(id, indexes);
                    if (!silhouetteSpans.TryGetValue(member, out var list))
                        silhouetteSpans.Add(member, list = new List<(Curve, List<int>)>());
                    list.Add((curve, indexes));
                }, (id, edge) =>
                {
                    if (!curvedTrimSegments.TryGetValue(id, out var indexes))
                        curvedTrimSegments.Add(id, indexes = new List<int>());
                    if (!curvedTrims.TryGetValue(member, out var list))
                        curvedTrims.Add(member, list = new List<(int, BrepEdge, List<int>)>());
                    list.Add((id, edge, indexes));
                }, sample))
                {
                    if (segment.Source.Kind == "Silhouette" &&
                        silhouetteSegments.TryGetValue(segment.Source.CurveId, out var silhouette))
                        silhouette.Add(segments.Count);
                    if (segment.Source.Kind == "Original" &&
                        curvedTrimSegments.TryGetValue(segment.Source.CurveId, out var trim))
                        trim.Add(segments.Count);
                    segments.Add(segment);
                }
                sample?.Invoke("boundary: stored segments");
            }
            if (useMasks)
            {
                var box = faces[background.Value].Screen.Grow(imageTolerance);
                segments.AddRange(masks.Where(segment => Math.Max(segment.A.X, segment.B.X) >= box.Left &&
                    Math.Min(segment.A.X, segment.B.X) <= box.Right && Math.Max(segment.A.Y, segment.B.Y) >= box.Bottom &&
                    Math.Min(segment.A.Y, segment.B.Y) <= box.Top));
            }
            int[] contactMembers = hiZCovered is { Count: > 0 } && hiddenOnly != null
                ? members.Where(id => !hiZCovered.Contains(faces[id].Id)).ToArray() : members;
            using var deferredContacts = accuracy == V2Accuracy.Exact && !focus.HasValue && !useMasks
                ? new V2LazyContact(faces, camera, surfaceBounds, drawing, tolerance, cancel, progress) : null;
            var lazy = deferredContacts;
            contacts.Read(members, contactMembers, background, tileSchedule, AddContact, lazy);
            if (lazy is not { HasPending: true }) lazy = null;

            void AddContact(Curve model, int first, int second)
            {
                int id = boundaries.NextId();
                int start = segments.Count;
                bool otherSource = hiddenSources != null && HiddenLayerMatches(faces[second].Id.Source);
                segments.AddRange(boundaries.Discretize(model, first, "Contact", originals, id,
                    otherSource || HiddenLayerMatches(faces[first].Id.Source) ? hiddenSources : null));
                if (tileSchedule != null)
                {
                    int recorded = -1;
                    for (int i = start; i < segments.Count; i++)
                    {
                        int curve = segments[i].Source.CurveId;
                        if (curve == recorded) continue;
                        tileSchedule.RememberContact(curve, first, second);
                        recorded = curve;
                    }
                }
                if (start == segments.Count || !otherSource &&
                    !silhouetteSpans.ContainsKey(first) && !silhouetteSpans.ContainsKey(second) &&
                    !curvedTrims.ContainsKey(first) && !curvedTrims.ContainsKey(second))
                    return;
                var contact = Enumerable.Range(start, segments.Count - start).ToList();
                var box = model.GetBoundingBox(false);
                box.Inflate(tolerance);
                Weld(first);
                Weld(second);
                AnchorTrim(first);
                AnchorTrim(second);
                if (otherSource)
                    foreach (int part in contact)
                    {
                        var segment = segments[part];
                        segments.Add(segment with { Source = segment.Source with { Face = second } });
                    }

                void Weld(int owner)
                {
                    if (!silhouetteSpans.TryGetValue(owner, out var contours)) return;
                    foreach (var (contour, indexes) in contours)
                    {
                        if (!BoundingBox.Intersection(box, contour.GetBoundingBox(false)).IsValid) continue;
                        using var events = Intersection.CurveCurve(model, contour, tolerance * .1, tolerance * .1);
                        if (events == null) continue;
                        foreach (var hit in events)
                        {
                            if (!hit.IsPoint || hit.PointA.DistanceTo(hit.PointB) > tolerance * .1) continue;
                            if (!camera.Includes(hit.PointA) || !camera.Includes(hit.PointB)) continue;
                            var point = camera.Project(hit.PointB);
                            int a = Locate(contact, hit.ParameterA, point);
                            int b = Locate(indexes, hit.ParameterB, point);
                            if (a < 0 || b < 0) continue;
                            Insert(contact, a, point, hit.ParameterA);
                            Insert(indexes, b, point, hit.ParameterB);
                        }
                    }
                }

                void AnchorTrim(int owner)
                {
                    if (!curvedTrims.TryGetValue(owner, out var rims)) return;
                    Anchor(model.PointAtStart, model.Domain.Min);
                    Anchor(model.PointAtEnd, model.Domain.Max);

                    void Anchor(Point3d end, double contactParameter)
                    {
                        if (!camera.Includes(end)) return;
                        foreach (var (id, edge, indexes) in rims)
                        {
                            // Confirm the junction in 3D before splitting the image chord.
                            if (!edge.ClosestPoint(end, out double parameter, tolerance * .1) ||
                                !originals.TryGetValue(id, out var rim) || !rim.Domain.IncludesParameter(parameter))
                                continue;
                            var p = rim.PointAt(parameter);
                            var vertex = new Point2d(p.X, p.Y);
                            int a = Locate(contact, contactParameter, vertex), b = Locate(indexes, parameter, vertex);
                            if (a < 0 || b < 0) continue;
                            Insert(contact, a, vertex, contactParameter);
                            Insert(indexes, b, vertex, parameter);
                        }
                    }
                }

                int Locate(List<int> indexes, double parameter, Point2d vertex)
                {
                    int best = -1;
                    double distance = imageTolerance * .25;
                    foreach (int index in indexes)
                    {
                        var s = segments[index];
                        if (parameter < Math.Min(s.Source.From, s.Source.To) ||
                            parameter > Math.Max(s.Source.From, s.Source.To)) continue;
                        var segment = new Line(new Point3d(s.A.X, s.A.Y, 0), new Point3d(s.B.X, s.B.Y, 0));
                        double deviation = segment.DistanceTo(new Point3d(vertex.X, vertex.Y, 0), true);
                        if (deviation > distance) continue;
                        (best, distance) = (index, deviation);
                    }
                    return best;
                }

                void Insert(List<int> indexes, int index, Point2d vertex, double parameter)
                {
                    var part = segments[index];
                    if (vertex == part.A || vertex == part.B) return;
                    if (parameter == part.Source.From || parameter == part.Source.To)
                    {
                        var previous = parameter == part.Source.From ? part.A : part.B;
                        foreach (int k in indexes)
                        {
                            var adjacent = segments[k];
                            if (adjacent.A == previous) adjacent = adjacent with { A = vertex };
                            if (adjacent.B == previous) adjacent = adjacent with { B = vertex };
                            segments[k] = adjacent;
                        }
                        return;
                    }
                    int count = indexes.Count;
                    for (int i = 0; i < count; i++)
                    {
                        int duplicate = indexes[i];
                        var original = segments[duplicate];
                        if (original.A != part.A || original.B != part.B ||
                            original.Source.From != part.Source.From || original.Source.To != part.Source.To) continue;
                        segments[duplicate] = original with { B = vertex, Source = original.Source with { To = parameter } };
                        indexes.Add(segments.Count);
                        segments.Add(original with { A = vertex, Source = original.Source with { From = parameter } });
                    }
                }
            }
            if (focus.HasValue && background is int target)
            {
                var box = faces[target].Screen.Grow(imageTolerance * 4);
                var clipped = new V2SegmentBuffer();
                foreach (var segment in segments)
                    if (Crop(segment, box) is V2Segment part) clipped.Add(part);
                var corners = new[] {new Point2d(box.Left, box.Bottom), new Point2d(box.Right, box.Bottom),
                    new Point2d(box.Right, box.Top), new Point2d(box.Left, box.Top)};
                for (int i = 0; i < corners.Length; i++)
                    clipped.Add(new V2Segment(corners[i], corners[(i + 1) % corners.Length],
                        new V2SourceSpan(boundaries.NextId(), target, "Audit extent", 0, 1)));
                segments.Dispose();
                segments = clipped;
            }
            Record("boundaries and 3D contact", start);
            drawing.LargestClusterFaces = Math.Max(drawing.LargestClusterFaces, members.Length);
            progress?.Invoke($"{members.Length} candidate faces and {segments.Count} projected segments.");
            if (segments.Count == 0) return;
            int segmentCount = segments.Count;
            bool tiled = tileLimit > 0 && (tileSchedule != null || segmentCount > tileLimit || minimumTileDepth > 0);
            bool spilled = collectOnDisk || tiled &&
                segmentCount >= (tileSchedule == null && lazy == null ? 4_500_000 : 2_000_000);
            if (spilled)
            {
                segments.Spill(cancel);
                progress?.Invoke($"Stored {segmentCount} projected segments in a temporary file for bounded-memory tiling.");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
            var tiles = new List<V2TileReport>();
            var interior = meshRims == null && hiddenSources == null ? null : new List<V2OwnedEdge>();
            using var replayStore = lazy == null ? null : new V2TiledArrangement.Replay();
            var replay = tiled ? replayStore : null;
            var depthMemo = lazy != null ? new V2DepthMemo(segmentCount) : null;
            Rect? extent = lazy != null && tileSchedule == null
                ? new Rect(members.Min(id => faces[id].Screen.Left), members.Min(id => faces[id].Screen.Bottom),
                    members.Max(id => faces[id].Screen.Right), members.Max(id => faces[id].Screen.Top)) : null;
            int refinement = 0;
            long passStart = Stopwatch.GetTimestamp();
            var plan = Arrange(frontierOnly: replay != null);
            if (lazy != null)
            {
                AddTime("lazy contact pass 1 wall", Stopwatch.GetElapsedTime(passStart));
                var frontier = Frontier();
                drawing.LazyInitialOwners += frontier.Count;
                drawing.LazyCandidates += contactMembers.Length;
                drawing.LazyPassSegments += tiles.Count == 0 ? segments.Count : tiles.Sum(tile => (long)tile.Segments);
                progress?.Invoke($"Lazy contact Pass 1: {frontier.Count}/{contactMembers.Length} visible owners, {tiles.Count} tiles, no native contact.");
                passStart = Stopwatch.GetTimestamp();
                int round = 0;
                while (lazy.HasPending)
                {
                    cancel?.Invoke();
                    var pairs = lazy.Select(frontier);
                    if (pairs.Count == 0) break;
                    round++;
                    int selected = pairs.Count;
                    int native = drawing.Contacts, empty = drawing.EmptyContacts, points = drawing.PointContacts;
                    int curveHits = drawing.ContactCurveHits;
                    var nativeTime = drawing.Timing.GetValueOrDefault("native BrepBrep");
                    long started = Stopwatch.GetTimestamp();
                    segments.TrackChanges();
                    contacts.Read(members, contactMembers, background, tileSchedule, AddContact, pending: pairs);
                    AddTime("boundaries and 3D contact", Stopwatch.GetElapsedTime(started));
                    pairs = null;
                    if (!lazy.HasPending)
                    {
                        lazy.Dispose();
                        surfaceBounds.ReleaseContactTrees();
                    }
                    int rebuilt = 0;
                    if (segments.HasEdits)
                    {
                        if (!tiled && tileLimit > 0 && segments.Count > tileLimit)
                        {
                            tiled = true;
                            replay = replayStore;
                        }
                        if (tiled && segments.ResidentCount >= (spilled ? 800_000 : 2_000_000))
                        {
                            segments.Spill(cancel);
                            spilled = true;
                        }
                        plan = null;
                        plan = Arrange(frontierOnly: replay != null);
                        rebuilt = replay == null ? 1 : replay.Rebuilt == 0 ? tiles.Count : replay.Rebuilt;
                        drawing.LazyRebuiltTiles += rebuilt;
                        drawing.LazyPassSegments += replay?.RebuiltSegments ?? segments.Count;
                        drawing.LazyDirtyCells += replay?.DirtyCells ?? 0;
                        drawing.LazyFrozenCells += replay?.FrozenCells ?? 0;
                        drawing.LazyPatchExpansions += replay?.Expansions ?? 0;
                        frontier = Frontier();
                    }
                    else frontier = new V2ContactFront();
                    drawing.LazyRounds++;
                    var pass = new V2LazyContactPass(round, selected, drawing.Contacts - native,
                        drawing.EmptyContacts - empty, drawing.PointContacts - points, drawing.ContactCurveHits - curveHits,
                        (drawing.Timing.GetValueOrDefault("native BrepBrep") - nativeTime).TotalSeconds,
                        rebuilt, tiles.Count, Stopwatch.GetElapsedTime(started).TotalSeconds);
                    drawing.LazyContactPasses.Add(pass);
                    progress?.Invoke(pass.ToString());
                }
                drawing.LazyPendingAtFixedPoint += lazy.PendingCount;
                progress?.Invoke($"Lazy contact fixed point: {round} native batches, {lazy.PendingCount} inactive pairs.");
                lazy.Dispose();
                surfaceBounds.ReleaseContactTrees();
                if (replay != null)
                {
                    plan = Arrange(finalizeReplay: true);
                    replay.Tiles.Clear();
                }
                AddTime("lazy contact pass 2+ wall", Stopwatch.GetElapsedTime(passStart));
                progress?.Invoke($"Lazy contact retained {depthMemo.Hits} exact depth witnesses across passes.");
            }
            contactContours.Dispose();
            silhouetteSegments.Clear();
            silhouetteSpans.Clear();
            curvedTrimSegments.Clear();
            curvedTrims.Clear();
            segmentCount = segments.Count;
            drawing.Segments += segmentCount;
            drawing.LargestClusterSegments = Math.Max(drawing.LargestClusterSegments, segmentCount);
            drawing.SkippedCurveSources += originals.Skipped;
            segments.Dispose();

            V2ContactFront Frontier()
            {
                if (replay != null) return replay.Frontier;
                var result = new V2ContactFront();
                result.Read(plan, Math.Max(plan.Grid * 4, imageTolerance));
                return result;
            }

            V2PlanarArrangement Arrange(bool frontierOnly = false, bool finalizeReplay = false)
            {
                string issue = null;
                bool useSavedTopology = replay is {Tiles.Count: > 0};
                for (; refinement < 3; refinement++)
                {
                    try
                    {
                        double grid = fit / (1 << refinement);
                        tiles.Clear();
                        interior?.Clear();
                        long mark = Stopwatch.GetTimestamp();
                        var result = tiled
                            ? V2TiledArrangement.Build(segments, grid, imageTolerance, tileLimit, minimumTileDepth,
                                Classify, cancel, progress, tiles, overlapTileArrangement,
                                interior == null ? null : RetainInterior, interior,
                                spilled || camera.Perspective || drawing.NativeMeshTriangles > 1_000_000,
                                reportTileTotal, splitOnConvergence: accuracy == V2Accuracy.Fast || spilled,
                                fast: accuracy == V2Accuracy.Fast,
                                schedule: tileSchedule, windowScale: 1 << refinement, replay: replay,
                                changed: finalizeReplay ? null : segments, fixedExtent: extent, frontierOnly: frontierOnly)
                            : V2PlanarArrangement.Build(segments, grid, cancel, segments.Count >= 1000 ? progress : null,
                                fast: accuracy == V2Accuracy.Fast);
                        if (collectMask) sharedGrid = Math.Min(grid, sharedGrid);
                        if (tiled)
                        {
                            AddTime("integer arrangement", TimeSpan.FromSeconds(tiles.Sum(tile => tile.ArrangementSeconds)));
                            AddTime("independent cell depth", TimeSpan.FromSeconds(tiles.Sum(tile => tile.DepthSeconds)));
                            var wall = Stopwatch.GetElapsedTime(mark);
                            if (overlapTileArrangement)
                                AddTime("tile wall including overlapped arrangement and depth", wall);
                            else
                                AddTime("tile partition, clipping and stitch", wall -
                                    TimeSpan.FromSeconds(tiles.Sum(tile => tile.ArrangementSeconds + tile.DepthSeconds)));
                        }
                        else
                        {
                            Record("integer arrangement", mark);
                            mark = Stopwatch.GetTimestamp();
                            Classify(result);
                            Record("independent cell depth", mark);
                        }
                        return result;
                    }
                    catch (InvalidOperationException error) when (!useSavedTopology && error is not V2LocalPatchException)
                    {
                        replay?.Tiles.Clear();
                        issue = error.Message;
                        progress?.Invoke($"Snap grid refinement {refinement + 1}: {issue}");
                    }
                }
                throw new InvalidOperationException($"Projected arrangement failed for {members.Length} faces and {segments.Count} segments. {issue}");
            }
            int cells = tiled ? tiles.Sum(tile => tile.Cells) : plan.Cells.Length;
            drawing.DistributionCells += cells;
            drawing.LargestClusterCells = Math.Max(drawing.LargestClusterCells, cells);
            drawing.ClusterSizes.Add((members.Length, segmentCount, cells, useMasks));
            drawing.Edges += tiled ? tiles.Sum(tile => tile.Edges) : plan.EdgeCount;
            drawing.SnapRounds += tiled ? tiles.Sum(tile => tile.Rounds) : plan.Rounds;
            drawing.Tiles.AddRange(tiles);
            drawing.ArrangementSegments += tiled ? tiles.Sum(tile => (long)tile.Segments) : segmentCount;
            if (!tiled)
            {
                if (interior != null)
                    for (int side = 0; side < plan.EdgeCount * 2; side += 2)
                    {
                        int owner = plan.Owner(side);
                        var spans = plan.Sources(side);
                        if (owner >= 0 && owner == plan.Owner(side ^ 1) && RetainInterior(owner, spans))
                            interior.Add(new V2OwnedEdge(plan.Start(side), plan.Start(side ^ 1), owner, owner, spans.ToArray()));
                    }
            }
            long auxiliaryStart = Stopwatch.GetTimestamp();
            if (interior is { Count: > 0 })
            {
                progress?.Invoke($"Checking depth on {interior.Count} same-owner source edges.");
                foreach (var edge in interior)
                {
                    cancel?.Invoke();
                    var a = plan.Point(edge.A);
                    var b = plan.Point(edge.B);
                    var point = new Point2d(a.X + (b.X - a.X) * .5, a.Y + (b.Y - a.Y) * .5);
                    if (!output.Contains(edge.Left)) continue;
                    var frontFace = faces[edge.Left];
                    double front = index.DepthAt(edge.Left, point);
                    bool drawn = false;
                    if (meshRims != null && frontFace.Triangles != null)
                        foreach (var span in edge.Sources)
                        {
                            if (span.Face != edge.Left || !meshRims.TryGetValue(span.CurveId, out var rim)) continue;
                            double t = (span.From + span.To) * .5;
                            if (camera.Perspective)
                            {
                                var a2 = camera.Project(rim.From);
                                var b2 = camera.Project(rim.To);
                                var d = b2 - a2;
                                if (d.SquareLength == 0) continue;
                                double s = Math.Clamp(((point.X - a2.X) * d.X + (point.Y - a2.Y) * d.Y) / d.SquareLength, 0, 1);
                                t = s * rim.From.Z / ((1 - s) * rim.To.Z + s * rim.From.Z);
                            }
                            double depth = rim.PointAt(t).Z;
                            if (front > depth + tolerance) continue;
                            drawing.Curves.Add(new DrawnEdge(new LineCurve(a, b), frontFace.Id.Source,
                                frontFace.Id.FaceIndex, span.Kind, "", fit));
                            drawn = true;
                            break;
                        }
                    if (drawn || hiddenSources == null || !double.IsFinite(front)) continue;
                    var along = new Vector2d(b.X - a.X, b.Y - a.Y);
                    V2SourceSpan? choice = null;
                    double best = double.NegativeInfinity;
                    foreach (var span in edge.Sources)
                    {
                        if (span.Face < 0 || span.Face == edge.Left) continue;
                        var source = faces[span.Face];
                        if (!HiddenLayerMatches(source.Id.Source) ||
                            hiddenMode == V2HiddenMode.Occluded && source.Id.Source == frontFace.Id.Source ||
                            !hiddenSources.TryPoint(span, point, along, Math.Max(fit * 2.5, imageTolerance * .08), out var position, camera) ||
                            position.Z >= front - tolerance * .1 || position.Z < best ||
                            hiddenMode == V2HiddenMode.Occluded && IsBackFace(source.Face, position, camera.Perspective)) continue;
                        if (position.Z == best && choice.HasValue && choice.Value.Face <= span.Face) continue;
                        (choice, best) = (span, position.Z);
                    }
                    if (choice is V2SourceSpan hidden && hiddenEdges.Add((edge.A, edge.B, plan.Grid)))
                    {
                        var id = faces[hidden.Face].Id;
                        drawing.Curves.Add(new DrawnEdge(new LineCurve(a, b), id.Source, id.FaceIndex, hidden.Kind,
                            frontFace.Id.Source, fit) { IsHidden = true });
                    }
                }
                Record("mesh creases and hidden edge depth", auxiliaryStart);
            }
            if (inspection != null) ReportOwnership("before seam cleanup");
            depthPool.Finish();
            inspectStitched?.Invoke(plan);
            if (releaseInputs != null && collectOnDisk && groups.Length == 1 && backgrounds.Length == 0 &&
                !focus.HasValue && inspection == null && hiddenMode == V2HiddenMode.None)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                if (tangencies.Detach(plan, cancel))
                {
                    index.Dispose();
                    releaseInputs();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    progress?.Invoke("Released source geometry after final 3D queries; retained exact tangent topology.");
                }
            }
            if (!collectMask && !useMasks)
            {
                start = Stopwatch.GetTimestamp();
                var cleaned = V2Slivers.Clean(plan, imageTolerance, millimetersPerUnit, cancel, progress);
                plan = cleaned.Plan;
                drawing.AbsorbedSlivers += cleaned.Absorbed;
                drawing.FlattenedSteps += cleaned.Flattened;
                Record("absorb and align sub-tolerance seams", start);
            }
            if (plan.EdgeCount > 250000)
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            if (inspection != null) ReportOwnership("after seam cleanup");
            start = Stopwatch.GetTimestamp();
            var regions = plan.Regions().ToArray();
            progress?.Invoke($"Output topology: {regions.Length} region loops.");
            var chainBySide = new int[plan.EdgeCount * 2];
            var chains = plan.BoundaryChains(chainBySide);
            Record("walk region topology", start);
            progress?.Invoke($"Output topology: {chains.Count} shared chains.");
            try
            {
                long restoreStart = Stopwatch.GetTimestamp();
                using var bounds = new RTree();
                for (int i = 0; i < chains.Count; i++)
                {
                    var box = chains[i].Bounds;
                    box.Inflate(fit);
                    bounds.Insert(box, i);
                }
                for (int i = 0; i < chains.Count; i++)
                {
                    cancel?.Invoke();
                    var chain = chains[i];
                    if (useMasks || collectMask && (chain.Left < 0 || chain.Right < 0)) continue;
                    int id = i;
                    Restore(id);
                }
                Record("initial chain compatibility", restoreStart);
                var incident = new (int First, int Second)[chains.Count];
                Array.Fill(incident, (-1, -1));
                var fills = new List<Brep>[regions.Length];
                var retry = new SortedSet<int>();
                try
                {
                    long fillStart = Stopwatch.GetTimestamp();
                    for (int r = 0; r < regions.Length; r++)
                    {
                        cancel?.Invoke();
                        foreach (int chain in RegionChains(r))
                        {
                            ref var neighbors = ref incident[chain];
                            if (neighbors.First < 0) neighbors.First = r;
                            else if (neighbors.Second < 0) neighbors.Second = r;
                            else throw new InvalidOperationException("A public edge chain has more than two incident regions.");
                        }
                        fills[r] = BuildRegion(r);
                        if (fills[r] == null)
                            foreach (int chain in RegionChains(r))
                                if (chains[chain].Upgraded) retry.Add(chain);
                    }
                    Record("initial planar fills", fillStart);
                    long replayStart = Stopwatch.GetTimestamp();
                    var dirty = new HashSet<int>();
                    foreach (int chain in retry)
                    {
                        chains[chain].Revert();
                        foreach (int r in Neighbors(chain)) dirty.Add(r);
                    }
                    foreach (int r in dirty)
                        Replace(r, BuildRegion(r));
                    foreach (var batch in retry.Chunk(64)) Replay(batch.AsSpan());
                    Measure("fill upgrade revalidation", replayStart);
                    long recoveryStart = Stopwatch.GetTimestamp();
                    for (int r = 0; r < regions.Length; r++)
                    {
                        var (owner, region) = regions[r];
                        var id = faces[owner].Id;
                        if (fills[r] == null)
                        {
                            var rings = new[] {region.Outer}.Concat(region.Holes).ToArray();
                            var vertices = rings.SelectMany(ring => ring.Select(plan.Start)).ToArray();
                            var points = rings.Select(RingPoints).ToArray();
                            fills[r] = faces[owner].Triangles != null
                                ? V2PlanarFill.Polygon(points, fit * .01)
                                : V2PlanarFill.Recover(points, fit * .01);
                            inspectRejected?.Invoke(id, points, fills[r] != null);
                            if (fills[r] != null) drawing.RecoveredRegions++;
                            else
                                drawing.Issues.Add($"Invalid projected region for {id}; {rings.Length} rings, " +
                                    $"{vertices.Length - vertices.Distinct().Count()} shared vertices, " +
                                    $"{vertices.Length} sides; origin {plan.Point(vertices[0])}.");
                        }
                        if (fills[r] == null) continue;
                        foreach (var piece in fills[r])
                            drawing.Regions.Add(new DraftRegion(piece, id.Source, id.FaceIndex));
                        fills[r] = null;
                    }
                    Measure("exact polygon fill recovery", recoveryStart);

                    void Replay(ReadOnlySpan<int> batch)
                    {
                        if (batch.IsEmpty) return;
                        cancel?.Invoke();
                        var changed = new List<int>();
                        var affected = new HashSet<int>();
                        foreach (int chain in batch)
                        {
                            var touching = Neighbors(chain).ToArray();
                            if (touching.Any(r => fills[r] == null)) continue;
                            Restore(chain);
                            if (!chains[chain].Upgraded) continue;
                            changed.Add(chain);
                            affected.UnionWith(touching);
                        }
                        if (changed.Count == 0)
                        {
                            drawing.RejectedUpgrades += batch.Length;
                            return;
                        }
                        var replacements = affected.Select(r => (Region: r, Pieces: BuildRegion(r))).ToArray();
                        if (replacements.All(item => item.Pieces != null))
                        {
                            drawing.RejectedUpgrades += batch.Length - changed.Count;
                            foreach (var item in replacements) Replace(item.Region, item.Pieces);
                            return;
                        }
                        foreach (var item in replacements) DisposePieces(item.Pieces);
                        foreach (int chain in changed) chains[chain].Revert();
                        if (batch.Length == 1) { drawing.RejectedUpgrades++; return; }
                        int middle = batch.Length / 2;
                        Replay(batch[..middle]);
                        Replay(batch[middle..]);
                    }
                }
                finally { foreach (var pieces in fills) DisposePieces(pieces); }
                foreach (var chain in chains)
                {
                    cancel?.Invoke();
                    int owner = chain.Left >= 0 ? chain.Left : chain.Right;
                    int neighbor = chain.Right >= 0 && chain.Left >= 0 ? chain.Right : chain.Source.Face;
                    var id = faces[owner].Id;
                    if (collectMask && (chain.Left < 0 || chain.Right < 0))
                        foreach (int side in chain.Sides)
                        {
                            var a = plan.Point(plan.Start(side));
                            var b = plan.Point(plan.Start(side ^ 1));
                            masks.Add(new V2Segment(new Point2d(a.X, a.Y), new Point2d(b.X, b.Y),
                                chain.Source with { Kind = "Mask" }));
                        }
                    if (useMasks && chain.Source.Kind == "Mask") continue;
                    drawing.BoundaryEdges += chain.Sides.Length;
                    drawing.SharedChains++;
                    if (chain.Upgraded) drawing.RestoredChains++;
                    if (chain.IsStraight) drawing.StraightChains++;
                    else if (chain.Upgraded) drawing.SmoothChains++;
                    else drawing.PolylineChains++;
                    string kind = chain.Source.Kind;
                    if (tangencies.IsTangent(plan, chain))
                    {
                        drawing.TangentChains++;
                        if (!tangentEdges) { drawing.TangentChainsFiltered++; continue; }
                        kind = "Tangent";
                    }
                    drawing.Curves.Add(new DrawnEdge(chain.DuplicateGeometry(), id.Source, id.FaceIndex,
                        kind, neighbor != owner ? faces[neighbor].Id.Source : "", fit));
                    chain.Dispose();
                }

                void Restore(int id)
                {
                    var current = chains[id];
                    if (current.Left >= 0 && faces[current.Left] is { Triangles: not null, ProxyChord: 0 } ||
                        current.Right >= 0 && faces[current.Right] is { Triangles: not null, ProxyChord: 0 }) return;
                    current.Restore(originals, fit, curve =>
                    {
                    bool clear = true;
                    var range = curve.GetBoundingBox(true);
                    range.Inflate(fit);
                    bounds.Search(range, (_, hit) =>
                    {
                        if (hit.Id == id) return;
                        var neighbor = chains[hit.Id];
                        using var original = neighbor.CreateOriginalGeometry();
                        if (V2EdgeChain.Compatible(curve, original, fit * .01) &&
                            (!neighbor.Upgraded || V2EdgeChain.Compatible(curve, neighbor.RestoredGeometry, fit * .01))) return;
                        clear = false;
                        hit.Cancel = true;
                    });
                    return clear;
                    });
                }

                IEnumerable<int> Neighbors(int chain)
                {
                    var (first, second) = incident[chain];
                    if (first >= 0) yield return first;
                    if (second >= 0) yield return second;
                }

                IEnumerable<int> RegionChains(int r) => regions[r].Region.Outer
                    .Concat(regions[r].Region.Holes.SelectMany(hole => hole))
                    .Select(side => Math.Abs(chainBySide[side]) - 1).Distinct();

                void Replace(int r, List<Brep> pieces)
                {
                    DisposePieces(fills[r]);
                    fills[r] = pieces;
                }
            }
            finally { foreach (var chain in chains) chain.Dispose(); }
            Record("merge and 2D output", start);

            bool RetainInterior(int owner, IReadOnlyList<V2SourceSpan> spans) =>
                spans.Any(span => span.Kind is not ("Mask" or "Audit extent" or V2TiledArrangement.SeamKind) &&
                    (span.Face == owner && meshRims?.ContainsKey(span.CurveId) == true ||
                    span.Face != owner && hiddenSources?.Contains(span.CurveId) == true));

            void ReportOwnership(string label)
            {
                if (progress == null) return;
                foreach (var probe in inspection)
                    progress(FormattableString.Invariant($"Probe ({probe.X:R}, {probe.Y:R}) {label}: {string.Join(", ", plan.Regions().Where(item => plan.Contains(item.Region, probe)).Select(item => Label(item.Owner)))}."));
            }

            void Classify(V2PlanarArrangement local)
            {
                if (!focus.HasValue && inspection == null && depthPool.TryClassify(local, output, drawing.Issues, cancel, depthMemo)) return;
                var inspected = inspection == null ? null : new HashSet<int>();
                var inspectedPoints = inspection == null ? null : new HashSet<Point2d>();
                foreach (var cell in local.Cells)
                {
                    cancel?.Invoke();
                    if (!local.TryInterior(cell, out var point))
                    {
                        drawing.Issues.Add("No interior probe for a bounded cell.");
                        continue;
                    }
                    int neighbor = -1;
                    if (!focus.HasValue && inspection == null)
                        foreach (int side in cell.Outer)
                        {
                            int other = local.CellIndex(side ^ 1);
                            var spans = local.Sources(side);
                            if (other < 0 || spans.Count != 1 || spans[0].Kind != "Original") continue;
                            var adjacent = local.Cells[other];
                            if (adjacent.Uncertain || adjacent.Owner < 0 || !faces[adjacent.Owner].Plane.HasValue ||
                                spans[0].Face == adjacent.Owner) continue;
                            neighbor = adjacent.Owner;
                            break;
                        }
                    var (owner, doubtful) = depthMemo == null
                        ? index.Classify(point, focus.HasValue ? background.Value : -1, neighbor)
                        : depthMemo.Classify(index, point, neighbor);
                    cell.Uncertain = doubtful;
                    if (doubtful) drawing.Issues.Add($"Uncertain owner at {point}.");
                    if (output.Contains(owner)) cell.Owner = owner;
                    if (inspection != null)
                        foreach (var probe in inspection)
                        {
                            if (!local.Contains(cell, probe)) continue;
                            inspectedPoints.Add(probe);
                            var here = index.Classify(probe);
                            var interior = index.Classify(point);
                            var closest = new[] {cell.Outer}.Concat(cell.Holes).SelectMany(sides => sides)
                                .Select(side =>
                                {
                                    var edge = new Line(local.Point(local.Start(side)), local.Point(local.Start(side ^ 1)));
                                    double distance = edge.DistanceTo(new Point3d(probe.X, probe.Y, 0), true);
                                    return (side, distance);
                                }).MinBy(hit => hit.distance);
                            string kinds = string.Join(", ", local.Sources(closest.side).Select(span => $"{span.Kind} {Label(span.Face)}"));
                            progress?.Invoke(FormattableString.Invariant(
                                $"Probe ({probe.X:R}, {probe.Y:R}): {Label(here.Owner)}; cell interior ({point.X:R}, {point.Y:R}): {Label(interior.Owner)}; nearest edge {closest.distance:G6} [{kinds}]."));
                            Inspect(local, inspected, here.Owner);
                            Inspect(local, inspected, interior.Owner);
                        }
                }
                if (inspection != null)
                    foreach (var probe in inspection)
                    {
                        if (inspectedPoints.Contains(probe)) continue;
                        var here = index.Classify(probe);
                        progress?.Invoke(FormattableString.Invariant(
                            $"Probe ({probe.X:R}, {probe.Y:R}): {Label(here.Owner)}; outside bounded cells."));
                        Inspect(local, inspected, here.Owner);
                    }
            }

            void Inspect(V2PlanarArrangement local, HashSet<int> inspected, int face)
            {
                if (face < 0 || face == background || faces[face].Face == null || !inspected.Add(face)) return;
                var source = faces[face].Face;
                progress?.Invoke($"{Label(face)} cylinder={source.TryGetCylinder(out _, tolerance * .01)}; trims={source.Loops.Sum(loop => loop.Trims.Count)}.");
                foreach (string detail in local.InspectBoundary(face)) progress?.Invoke(detail);
            }

            List<Brep> BuildRegion(int r)
            {
                var region = regions[r].Region;
                var rings = new[] {region.Outer}.Concat(region.Holes).ToArray();
                if (faces[regions[r].Owner] is { Triangles: not null, ProxyChord: 0 } ||
                    faces[regions[r].Owner].ProxyChord > 0 && rings.Sum(ring => ring.Length) > 1024)
                    return V2PlanarFill.Polygon(rings.Select(ExactRingPoints).ToArray(), fit * .01);
                Curve[] loops = rings.Select(Loop).ToArray();
                try
                {
                    if (!loops.All(loop => loop is { IsValid: true, IsClosed: true })) return null;
                    var pieces = Brep.CreatePlanarBreps(loops, fit * .01);
                    if (pieces is { Length: 1 } && V2PlanarFill.Valid(pieces[0], rings.Length))
                        return new List<Brep> { pieces[0] };
                    DisposePieces(pieces);
                    return null;
                }
                finally { foreach (var loop in loops) loop?.Dispose(); }
            }

            static void DisposePieces(IEnumerable<Brep> pieces)
            {
                if (pieces != null)
                    foreach (var piece in pieces) piece.Dispose();
            }

            Point3d[] RingPoints(int[] sides) => sides.Select(side => plan.Point(plan.Start(side)))
                .Append(plan.Point(plan.Start(sides[0]))).ToArray();

            Point3d[] ExactRingPoints(int[] sides)
            {
                var points = new List<Point3d>(sides.Length + 1);
                for (int i = 0; i < sides.Length; i++)
                {
                    var a = plan.Start(sides[(i + sides.Length - 1) % sides.Length]);
                    var b = plan.Start(sides[i]);
                    var c = plan.Start(sides[(i + 1) % sides.Length]);
                    Int128 x = (Int128)b.X - a.X, y = (Int128)b.Y - a.Y;
                    Int128 dx = (Int128)c.X - b.X, dy = (Int128)c.Y - b.Y;
                    if (x * dy != y * dx || x * dx + y * dy <= 0) points.Add(plan.Point(b));
                }
                if (points.Count != 0) points.Add(points[0]);
                return points.ToArray();
            }

            Curve Loop(int[] sides)
            {
                int[] runs = sides.Select(side => chainBySide[side]).ToArray();
                if (!runs.Any(id => chains[Math.Abs(id) - 1].Upgraded))
                    return new PolylineCurve(RingPoints(sides));
                var ids = runs.Where((id, i) => id != runs[(i + runs.Length - 1) % runs.Length]).ToArray();
                if (ids.Length == 0) return Oriented(runs[0]);
                var result = new PolyCurve();
                foreach (int id in ids)
                {
                    using var part = Oriented(id);
                    if (part != null && (result.SegmentCount == 0 || result.PointAtEnd == part.PointAtStart) &&
                        result.AppendSegment(part)) continue;
                    result.Dispose();
                    return null;
                }
                using var baseline = new PolylineCurve(RingPoints(sides));
                if (result.PointAtStart == result.PointAtEnd && result.ClosedCurveOrientation() == baseline.ClosedCurveOrientation())
                    return result;
                result.Dispose();
                return null;
            }

            Curve Oriented(int id)
            {
                var copy = chains[Math.Abs(id) - 1].DuplicateGeometry();
                if (id > 0 || copy.Reverse()) return copy;
                copy.Dispose();
                return null;
            }
        }

        string Label(int owner) => owner < 0 ? "(none)" : $"{faces[owner].Id.Source}/{faces[owner].Id.FaceIndex}";

        void Record(string name, long start)
        {
            var elapsed = Measure(name, start);
            progress?.Invoke($"{name}: {elapsed.TotalSeconds:F3} s.");
        }

        TimeSpan Measure(string name, long start)
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            AddTime(name, elapsed);
            return elapsed;
        }

        void AddTime(string name, TimeSpan elapsed) =>
            drawing.Timing[name] = drawing.Timing.GetValueOrDefault(name) + elapsed;
    }

    private static bool IsBackFace(BrepFace face, Point3d position, bool perspective)
    {
        if (face == null || !face.Brep.IsSolid || !face.ClosestPoint(position, out double u, out double v)) return false;
        var normal = face.UnderlyingSurface().NormalAt(u, v);
        bool reversed = face.OrientationIsReversed ^ (face.Brep.SolidOrientation == BrepSolidOrientation.Inward);
        var direction = perspective ? Point3d.Origin - position : Vector3d.ZAxis;
        if (!direction.Unitize()) return false;
        double facing = normal * direction;
        return reversed ? facing > 1e-8 : facing < -1e-8;
    }

    internal static bool Separates(Plane plane, BoundingBox bounds, double fit)
    {
        var n = plane.Normal;
        var width = bounds.Diagonal;
        double radius = .5 * (Math.Abs(n.X) * width.X + Math.Abs(n.Y) * width.Y + Math.Abs(n.Z) * width.Z);
        return Math.Abs(plane.DistanceTo(bounds.Center)) > radius + fit * 2;
    }

    private static V2Segment? Crop(V2Segment segment, Rect box)
    {
        var delta = segment.B - segment.A;
        double min = 0, max = 1;
        if (!Clip(segment.A.X, delta.X, box.Left, box.Right) ||
            !Clip(segment.A.Y, delta.Y, box.Bottom, box.Top))
            return null;
        var source = segment.Source;
        return new V2Segment(segment.A + min * delta, segment.A + max * delta, source with
        {
            From = source.From + min * (source.To - source.From),
            To = source.From + max * (source.To - source.From)
        });

        bool Clip(double coordinate, double direction, double lower, double upper)
        {
            if (direction == 0) return coordinate >= lower && coordinate <= upper;
            double a = (lower - coordinate) / direction, b = (upper - coordinate) / direction;
            min = Math.Max(min, Math.Min(a, b));
            max = Math.Min(max, Math.Max(a, b));
            return max > min;
        }
    }

    private static V2Face[] RemoveCovered(V2Face[] faces, double fit, Action cancel, SourceFaceId? focus)
    {
        if (faces.Length == 0) return faces;
        var extent = BoundingBox.Empty;
        foreach (var face in faces) extent.Union(face.Box);
        var covers = new List<PlanarCover>();
        try
        {
            foreach (var face in faces.Where(face => face.Plane.HasValue)
                .OrderByDescending(face => Rect.From(face.Box).Area).Take(64))
            {
                var cover = PlanarCover.Create(face.Face, face.Box, fit);
                if (cover != null) covers.Add(cover);
            }
            using var coverage = new CoverageIndex(Rect.From(extent).Grow(fit), covers, fit);
            return faces.Where(face =>
            {
                cancel?.Invoke();
                return face.Id == focus || !coverage.Hides(face.Box, face.Plane);
            }).ToArray();
        }
        finally { foreach (var cover in covers) cover.Dispose(); }
    }

    private static IEnumerable<V2Face> ReadFaces(IReadOnlyList<Input> inputs, double fit, SourceFaceId? focus, Action cancel,
        IProjectionCamera camera, V2ClipPlanes clips, V2HiddenMode hiddenMode, V2Drawing drawing)
    {
        Rect Bounds(BoundingBox box) => camera?.Bounds(box) ?? Rect.From(box);
        Rect? bounds = null;
        if (focus is SourceFaceId target)
        {
            var source = inputs.Single(input => input.Source == target.Source);
            bounds = Bounds(source.Brep.Faces[target.FaceIndex].GetBoundingBox(true)).Grow(fit);
        }
        foreach (var input in inputs.OrderBy(input => input.Source, StringComparer.Ordinal))
        {
            cancel?.Invoke();
            var inputBounds = input.Bounds;
            if (clips.Active && clips.Discards(inputBounds)) continue;
            if (bounds is Rect zone && !zone.Intersection(Bounds(inputBounds).Grow(fit)).HasArea)
                continue;
            if (input.NativeMeshDepth != null || input.NativeMesh != null)
            {
                var triangles = input.NativeMeshDepth ?? new V2NativeMesh(input.NativeMesh, cancel, viewClips: camera?.ViewClips);
                var screen = Bounds(triangles.Bounds);
                if (triangles.Bounds.IsValid && screen.HasArea)
                    yield return new V2Face(new SourceFaceId(input.Source, 0), null, triangles.Bounds, null, true)
                        { Triangles = triangles, ProjectedBounds = screen, Opaque = input.Opaque };
                continue;
            }
            int outwardSign = !focus.HasValue && hiddenMode is (V2HiddenMode.None or V2HiddenMode.Occluded)
                ? V2BackfaceCulling.OutwardSign(input, inputBounds, camera, clips, fit) : 0;
            foreach (var face in input.Brep.Faces)
            {
                var box = face.GetBoundingBox(true);
                if (!box.IsValid || clips.Discards(box)) continue;
                var screen = Bounds(box);
                if (!screen.HasArea || bounds is Rect window && !window.Intersection(screen.Grow(fit)).HasArea)
                    continue;
                Plane? plane = face.TryGetPlane(out var found, fit * .01) ? found : null;
                if (plane is Plane planar)
                {
                    if (camera?.Perspective == true
                        ? Math.Abs(planar.DistanceTo(Point3d.Origin)) <= Math.Max(1, box.Max.MaximumCoordinate) * 1e-13
                        : Math.Abs(planar.Normal.Z) <= 1e-10)
                        continue;
                    // A rear cap and the trim of its curved neighbor share the
                    // near-tangent transition to other, flush geometry. Keep
                    // the complete support at this junction for exact depth
                    // and contact, even when the cap itself faces away.
                    if (outwardSign != 0 && V2BackfaceCulling.IsBackPlane(face, planar, box, outwardSign,
                        camera?.Perspective == true, fit) && !V2BackfaceCulling.SharesCurvedRim(face, fit * .01))
                    {
                        drawing.BackfaceCulled++;
                        continue;
                    }
                }
                yield return new V2Face(new SourceFaceId(input.Source, face.FaceIndex), face, box, plane, input.FromMesh)
                    { ProjectedBounds = screen, Opaque = input.Opaque };
            }
        }
    }

    private static int[][] Clusters(V2Face[] faces, double fit, out int[] backgrounds, bool peelBackgrounds, Action cancel,
        IReadOnlySet<SourceFaceId> exclude = null)
    {
        using var index = new RTree();
        var rectangles = faces.Select(face => face.Screen.Grow(fit)).ToArray();
        var assigned = new bool[faces.Length];
        int remaining = faces.Length;
        for (int i = 0; i < faces.Length; i++)
        {
            if (exclude?.Contains(faces[i].Id) == true) { assigned[i] = true; remaining--; }
            else index.Insert(rectangles[i].Box, i);
        }
        backgrounds = peelBackgrounds ? Enumerable.Range(0, faces.Length).Where(i => !assigned[i] && CanPeel(i)).ToArray()
            : Array.Empty<int>();
        foreach (int i in backgrounds) Assign(i);
        var components = new List<int[]>();
        var found = new List<int>();
        for (int i = 0; i < faces.Length; i++)
        {
            if ((i & 511) == 0) cancel?.Invoke();
            if (assigned[i]) continue;
            Assign(i);
            var members = new List<int> {i};
            for (int j = 0; j < members.Count && remaining > 0; j++)
            {
                if ((j & 511) == 0) cancel?.Invoke();
                var box = rectangles[members[j]];
                found.Clear();
                index.Search(box.Box, (_, hit) =>
                {
                    if (box.Intersection(rectangles[hit.Id]).HasArea) found.Add(hit.Id);
                });
                found.Sort();
                foreach (int neighbor in found)
                {
                    Assign(neighbor);
                    members.Add(neighbor);
                }
            }
            components.Add(members.ToArray());
        }
        return components.ToArray();

        void Assign(int i)
        {
            index.Remove(rectangles[i].Box, i);
            assigned[i] = true;
            remaining--;
        }

        bool CanPeel(int i)
        {
            if ((i & 511) == 0) cancel?.Invoke();
            if (!faces[i].Plane.HasValue) return false;
            bool foundNeighbor = false, proved = true;
            var rectangle = rectangles[i];
            PlanarCover cover = null;
            // A containment proof keeps a foreground mask off the outer trim and any holes.
            try
            {
                index.Search(rectangle.Box, (_, hit) =>
                {
                    if (i == hit.Id) return;
                    var neighbor = rectangles[hit.Id];
                    var overlap = rectangle.Intersection(neighbor);
                    if (!overlap.HasArea) return;
                    foundNeighbor = true;
                    if (overlap == neighbor && Behind(faces[i], faces[hit.Id], overlap, fit))
                    {
                        cover ??= PlanarCover.Create(faces[i].Face, faces[i].Box, fit);
                        if (cover?.Covers(neighbor.Grow(fit)) == true) return;
                    }
                    proved = false;
                    hit.Cancel = true;
                });
                return foundNeighbor && proved;
            }
            finally { cover?.Dispose(); }
        }
    }

    internal static bool Behind(V2Face a, V2Face b, Rect overlap, double fit, IProjectionCamera camera = null)
    {
        if (a.Box.Max.Z + fit < b.Box.Min.Z)
            return true;
        if (a.Plane is not Plane p || b.Plane is not Plane q)
            return false;
        if (camera?.Perspective == true)
            return V2PlanarDepth.StrictlyBehind(p, q, overlap, fit, camera);
        if (Math.Abs(p.Normal.Z) < .01 || Math.Abs(q.Normal.Z) < .01) return false;
        double margin = fit * (2 + 1 / Math.Abs(p.Normal.Z) + 1 / Math.Abs(q.Normal.Z));
        return Lower(overlap.Left, overlap.Bottom) && Lower(overlap.Left, overlap.Top) &&
            Lower(overlap.Right, overlap.Bottom) && Lower(overlap.Right, overlap.Top);
        bool Lower(double x, double y) => Depth(q, x, y) - Depth(p, x, y) > margin;
        static double Depth(Plane plane, double x, double y) => plane.Origin.Z -
            (plane.Normal.X * (x - plane.Origin.X) + plane.Normal.Y * (y - plane.Origin.Y)) / plane.Normal.Z;
    }
}
