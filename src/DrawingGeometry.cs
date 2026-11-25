using System;
using Rhino.Geometry;

namespace BetterMake2D;

internal sealed record Input(Brep Brep, string Source, bool FromMesh = false) : IDisposable
{
    internal Mesh NativeMesh { get; init; }
    internal V2NativeMesh NativeMeshDepth { get; init; }
    internal bool Opaque { get; init; } = true;
    internal BoundingBox Bounds => Brep?.GetBoundingBox(false) ?? NativeMeshDepth?.Bounds ?? NativeMesh.GetBoundingBox(false);

    public void Dispose()
    {
        Brep?.Dispose();
        NativeMesh?.Dispose();
    }
}

internal sealed record DraftRegion(Brep Brep, string Source, int FaceIndex);
internal sealed record DrawnEdge(Curve Curve, string Source, int FaceIndex, string Boundary, string Occluder, double Fit)
{
    internal string JoinedSources { get; init; }
    internal bool IsHidden { get; init; }
}
