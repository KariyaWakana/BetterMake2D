
using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

// Independent of the sampled Cell owners. A selected planar trim must cover
// the entire window, survive clipping, and lie ahead at its farthest corner.
internal sealed class V2ContactCover : IDisposable
{
    private readonly record struct Candidate(Rect Screen, BrepFace Face, BoundingBox ModelBox, V2PlanarDepth Far);
    private readonly Candidate[] candidates;
    private readonly PlanarCover[] covers;
    private readonly bool[] prepared;
    private readonly V2ManagedBounds index;
    private readonly IProjectionCamera camera;
    private readonly double tolerance;

    internal V2ContactCover(V2Face[] faces, IProjectionCamera camera, double tolerance)
    {
        this.camera = camera;
        this.tolerance = tolerance;
        var source = new List<Candidate>();
        foreach (var face in faces.Where(face => face.Opaque && face.Face != null && face.Plane.HasValue &&
            face.Screen.HasArea && camera.Clips.Keeps(face.Box)).OrderByDescending(face => face.Screen.Area).Take(4096))
            if (V2PlanarDepth.TryCreate(face.Plane.Value, camera, tolerance * 2, out var far))
                source.Add(new Candidate(face.Screen, face.Face, face.Box, far));
        candidates = source.ToArray();
        covers = new PlanarCover[candidates.Length];
        prepared = new bool[candidates.Length];
        index = new V2ManagedBounds(candidates.Length, id => candidates[id].Screen);
    }

    internal bool Hides(Rect window, double upper, double pad)
    {
        bool found = false;
        int tests = 0;
        var guarded = window.Grow(camera.ScreenMargin(
            new BoundingBox(camera.AtDepth(new Point2d(window.Left, window.Bottom), upper),
                camera.AtDepth(new Point2d(window.Right, window.Top), upper)), pad));
        if (!guarded.HasArea) return false;
        index.Search(new Rect((window.Left + window.Right) * .5, (window.Bottom + window.Top) * .5,
            (window.Left + window.Right) * .5, (window.Bottom + window.Top) * .5), Test);
        return found;

        void Test(int id)
        {
            if (found || tests >= 4) return;
            var candidate = candidates[id];
            if (candidate.Screen.Intersection(guarded) != guarded ||
                !candidate.Far.TryExtrema(guarded, out double lower, out _) || lower <= upper + pad) return;
            tests++;
            if (!prepared[id])
            {
                prepared[id] = true;
                covers[id] = PlanarCover.Create(candidate.Face, candidate.ModelBox, tolerance,
                    camera.Perspective ? camera : null);
            }
            found = covers[id]?.Covers(guarded) == true;
        }
    }

    public void Dispose()
    {
        foreach (var cover in covers) cover?.Dispose();
    }
}
