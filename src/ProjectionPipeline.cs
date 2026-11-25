using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace BetterMake2D;

// Source contains the original object and its full instance path. ToView includes placement.
internal sealed record ProjectionObject(GeometryBase Geometry, string Source, Transform ToView)
{
    internal bool Opaque { get; init; } = true;
}

internal readonly record struct SourceFaceId(string Source, int FaceIndex);

internal static class ProjectionPipeline
{
    internal static List<Input> ReadObjects(IReadOnlyList<ProjectionObject> objects, Action checkCancel = null,
        bool compactMeshes = false, V2ClipPlanes viewClips = null)
    {
        var inputs = new List<Input>(objects.Count);
        try
        {
            foreach (var obj in objects)
            {
                checkCancel?.Invoke();
                if (obj.Geometry is Mesh originalMesh)
                {
                    if (compactMeshes)
                    {
                        var packed = new V2NativeMesh(originalMesh, checkCancel, obj.ToView, viewClips);
                        if (packed.TriangleCount > 0)
                            inputs.Add(new Input(null, obj.Source) { NativeMeshDepth = packed, Opaque = obj.Opaque });
                        continue;
                    }
                    var mesh = originalMesh.DuplicateMesh();
                    inputs.Add(new Input(null, obj.Source) { NativeMesh = mesh, Opaque = obj.Opaque });
                    if (!mesh.Transform(obj.ToView))
                        throw new InvalidOperationException($"Could not transform mesh {obj.Source} to the view frame.");
                    continue;
                }
                var copy = obj.Geometry switch
                {
                    Brep source => source.DuplicateBrep(),
                    Extrusion extrusion => extrusion.ToBrep(true),
                    Surface surface => surface.ToBrep(),
                    _ => throw new InvalidOperationException($"Unsupported geometry at {obj.Source}: {obj.Geometry.ObjectType}.")
                };
                if (copy == null)
                    throw new InvalidOperationException($"Could not read {obj.Source} as a Brep.");
                inputs.Add(new Input(copy, obj.Source) { Opaque = obj.Opaque });
                if (!copy.Transform(obj.ToView))
                    throw new InvalidOperationException($"Could not transform {obj.Source} to the view frame.");
            }
            return inputs;
        }
        catch
        {
            foreach (var input in inputs)
                input.Dispose();
            throw;
        }
    }
}
