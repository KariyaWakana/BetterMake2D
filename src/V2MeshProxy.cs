using System;
using System.Diagnostics;
using Rhino.Geometry;

namespace BetterMake2D;

internal enum V2Accuracy { Exact, Fast }

internal static class V2MeshProxy
{
    private const long ProxyBudget = 1152L * 1024 * 1024;
    private const long ProcessBudget = 3400L * 1024 * 1024;

    internal static void Prepare(V2Face[] faces, IProjectionCamera camera, double tolerance,
        double millimetersPerUnit, Action cancel, Action<string> progress = null)
    {
        var extent = BoundingBox.Empty;
        foreach (var face in faces) extent.Union(face.Box);
        var frame = camera.Bounds(extent);
        double imageError = Math.Max(frame.Right - frame.Left, frame.Top - frame.Bottom) / 100_000;
        var originals = (V2Face[])faces.Clone();
        using var process = Process.GetCurrentProcess();
        for (int pass = 0; pass < 4; pass++)
        {
            double spacing = Math.Pow(4, pass);
            int gridMax = 64 >> pass;
            long bytes = 0;
            int meshed = 0;
            bool fits = true;
            for (int i = 0; i < faces.Length; i++)
            {
                var face = originals[i];
                if (face.Face == null || face.Plane.HasValue) continue;
                cancel?.Invoke();
                double scale = camera.ScreenMargin(face.Box, 1 / millimetersPerUnit) * millimetersPerUnit;
                var diagonal = face.Box.Diagonal;
                double detail = Math.Min(diagonal.X, Math.Min(diagonal.Y, diagonal.Z)) * .015;
                double chord = Math.Max(tolerance * .2, Math.Min(tolerance * 32, Math.Min(detail, imageError / scale)));
                chord *= spacing;
                faces[i] = PrepareFace(face, camera, chord, gridMax, cancel);
                bytes += faces[i].Triangles.StorageBytes;
                if (bytes <= ProxyBudget && (++meshed % 128 != 0 || MemoryAvailable())) continue;
                fits = false;
                break;
            }
            if (fits)
            {
                progress?.Invoke($"Fine mesh storage: {bytes / 1048576.0:F1} MiB, chord factor {spacing:G}.");
                return;
            }
            Array.Copy(originals, faces, faces.Length);
            if (pass < 3)
            {
                progress?.Invoke($"Fine mesh capacity reached after {meshed} proxies ({bytes / 1048576.0:F1} MiB). " +
                    $"Retrying with chord factor {spacing * 4:G} and a bounded sampling grid.");
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
        }
        throw new InvalidOperationException("The Fast mesh proxies exceeded their memory budget at the coarsest supported resolution.");

        bool MemoryAvailable()
        {
            process.Refresh();
            if (Math.Max(process.PrivateMemorySize64, process.WorkingSet64) < ProcessBudget) return true;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            process.Refresh();
            return Math.Max(process.PrivateMemorySize64, process.WorkingSet64) < ProcessBudget;
        }
    }

    private static V2Face PrepareFace(V2Face face, IProjectionCamera camera, double chord, int gridMax, Action cancel)
    {
        var parameters = new MeshingParameters
        {
            Tolerance = chord, MinimumTolerance = chord * .1, RefineGrid = gridMax == 64,
            GridMinCount = 8, GridMaxCount = gridMax, DoublePrecision = true
        };
        // On a memory retry, avoid subdivision driven by very long, narrow trim strips.
        if (!parameters.RefineGrid) parameters.GridAspectRatio = 0;
        using var original = face.Face.DuplicateFace(false);
        var meshes = Mesh.CreateFromBrep(original, parameters);
        try
        {
            if (meshes is not { Length: > 0 } || meshes[0] == null)
                throw new InvalidOperationException($"Could not mesh {face.Id} in Fast mode.");
            using var joined = meshes.Length > 1 ? new Mesh() : null;
            var mesh = meshes[0];
            if (joined != null)
            {
                joined.Vertices.UseDoublePrecisionVertices = true;
                foreach (var part in meshes)
                {
                    if (part != null) joined.Append(part);
                }
                mesh = joined;
            }
            var proxy = new V2NativeMesh(mesh, cancel, viewClips: camera.ViewClips);
            if (proxy.TriangleCount == 0 && proxy.RejectedViewTriangles == 0)
                throw new InvalidOperationException($"Fast mode produced no valid triangles for {face.Id}.");
            var screen = proxy.ScreenBounds(camera);
            return proxy.Bounds.IsValid && screen.HasArea
                ? face with { Triangles = proxy, ProxyChord = chord, Box = proxy.Bounds, ProjectedBounds = screen }
                : face with { Triangles = proxy, ProxyChord = chord };
        }
        finally { foreach (var part in meshes ?? Array.Empty<Mesh>()) part?.Dispose(); }
    }
}
