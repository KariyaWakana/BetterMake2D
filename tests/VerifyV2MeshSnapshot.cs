using System;
using System.IO;
using System.Linq;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2MeshSnapshot
{
    public static string Run()
    {
        var folder = System.Environment.GetEnvironmentVariable("BMA2D_DESKTOP_TEST")
            ?? throw new ArgumentException("Set BMA2D_DESKTOP_TEST.");
        Directory.CreateDirectory(folder);
        using var cylinder = new Cylinder(new Circle(new Point3d(0, 0, -12), 2), 5).ToBrep(false, false);
        var meshes = Mesh.CreateFromBrep(cylinder, MeshingParameters.FastRenderMesh);
        try
        {
            Require(meshes is { Length: 1 } && cylinder.Faces[0].SetMesh(MeshType.Render, meshes[0]), "attach");
            using var copy = (Brep)cylinder.Duplicate();
            Require(Count(copy) == 1, "Duplicate");
            var pose = Transform.Rotation(.41, Vector3d.YAxis, Point3d.Origin);
            Require(copy.Transform(pose) && Count(copy) == 1, "Transform");
            string path = Path.Combine(folder, "mesh-roundtrip.3dm");
            string legacy = Path.Combine(folder, "mesh-default-write.3dm");
            using (var snapshot = new File3dm())
            {
                var layer = snapshot.AllLayers.AddLayer("Mesh archive", System.Drawing.Color.Gray);
                using var attributes = new ObjectAttributes { LayerIndex = layer };
                Require(snapshot.Objects.Add(copy, attributes) != Guid.Empty, "File3dm add");
                Require(snapshot.Write(legacy, new File3dmWriteOptions { Version = 8 }), "default File3dm write");
                var settings = new File3dmWriteOptions { Version = 8 };
                settings.EnableRenderMeshes(ObjectType.Brep | ObjectType.Extrusion, true);
                Require(snapshot.Write(path, settings), "File3dm write");
            }
            using var reopened = File3dm.Read(path) ?? throw new IOException("Could not reopen mesh archive: " + path);
            var archived = reopened.Objects.Select(obj => obj?.Geometry).OfType<Brep>().Single();
            Require(Count(archived) == 1, "File3dm reopen");
            using var legacyArchive = File3dm.Read(legacy) ?? throw new IOException("Could not reopen default archive.");
            int legacyCount = legacyArchive.Objects.Select(obj => obj?.Geometry).OfType<Brep>().Sum(Count);
            var read = ProjectionPipeline.ReadObjects(new[] { new ProjectionObject(archived, "roundtrip", Transform.Identity) });
            try { Require(Count(read.Single().Brep) == 1, "Worker read"); }
            finally { foreach (var item in read) item.Dispose(); }

            using var missing = new Cylinder(new Circle(new Point3d(0, 0, -12), 2), 5).ToBrep(false, false);
            var target = missing.Faces[0];
            var itemFace = new V2Face(new SourceFaceId("generated", 0), target, target.GetBoundingBox(true), null, false);
            var camera = new V2ParallelCamera(-30, 0);
            var surface = new V2SurfaceBounds(new[] { itemFace }, .001, null);
            using var depth = new V2DepthIndex(new[] { itemFace }, camera, .001, surface);
            Require(depth.GeneratedHintFaces == 1 && depth.GeneratedHintTriangles > 0, "generated chord hints");
            return $"PASS render mesh survived geometry duplication, rigid transform, File3dm write/reopen and worker intake. " +
                $"Default write retained {legacyCount} render meshes. " +
                $"{depth.GeneratedHintTriangles} lightweight triangles generated on a missing-cache NURBS face.";
        }
        finally { foreach (var mesh in meshes ?? Array.Empty<Mesh>()) mesh.Dispose(); }
    }

    public static string TraceArchive()
    {
        string path = System.Environment.GetEnvironmentVariable("BMA2D_MESH_TRACE_SCENE")
            ?? throw new ArgumentException("Set BMA2D_MESH_TRACE_SCENE.");
        using var file = File3dm.Read(path) ?? throw new IOException(path);
        int curved = 0, mesh = 0, duplicated = 0, extrusions = 0, storedExtrusions = 0, converted = 0;
        foreach (var obj in file.Objects)
        {
            if (obj.Geometry is Brep brep)
            {
                foreach (var face in brep.Faces)
                    if (!face.TryGetPlane(out _, file.Settings.ModelAbsoluteTolerance * .01))
                    {
                        curved++;
                        if (face.GetMesh(MeshType.Render) != null) mesh++;
                    }
                using var copy = brep.DuplicateBrep();
                if (!copy.Transform(Transform.Identity)) throw new InvalidOperationException("Identity placement failed.");
                duplicated += copy.Faces.Count(face => !face.TryGetPlane(out _, file.Settings.ModelAbsoluteTolerance * .01) &&
                    face.GetMesh(MeshType.Render) != null);
            }
            else if (obj.Geometry is Extrusion extrusion)
            {
                extrusions++;
                if (extrusion.GetMesh(MeshType.Render) == null) continue;
                storedExtrusions++;
                using var copy = extrusion.ToBrep(true);
                converted += Count(copy);
            }
        }
        return $"Archived curved Brep faces {curved}, with render mesh {mesh}, after duplicate and identity transform {duplicated}. " +
            $"Extrusions {extrusions}, stored render meshes {storedExtrusions}, converted Brep faces with mesh {converted}.";
    }

    private static int Count(Brep brep) => brep.Faces.Count(face => face.GetMesh(MeshType.Render) != null);
    private static void Require(bool condition, string step)
    {
        if (!condition) throw new InvalidOperationException("Mesh provenance check failed at " + step);
    }
}

public static class VerifyV2MeshArchive
{
    public static string Run() => VerifyV2MeshSnapshot.TraceArchive();
}
