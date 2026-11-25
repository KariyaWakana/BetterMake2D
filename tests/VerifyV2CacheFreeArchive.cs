using System;
using System.IO;
using System.Linq;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

public static class VerifyV2CacheFreeArchive
{
    public static string Run()
    {
        string input = Environment.GetEnvironmentVariable("BMA2D_ARCHIVE_INPUT") ?? throw new ArgumentException("Input");
        string output = Environment.GetEnvironmentVariable("BMA2D_ARCHIVE_CACHE_FREE") ?? throw new ArgumentException("Output");
        if (Path.GetFullPath(input) == Path.GetFullPath(output) || File.Exists(output))
            throw new InvalidOperationException("Use a new, independent archive path.");
        using var source = File3dm.Read(input) ?? throw new IOException(input);
        var options = new File3dmWriteOptions { Version = 8, SaveAnalysisMeshes = false, SaveRenderMeshes = false };
        if (!source.Write(output, options)) throw new IOException($"Unable to write {output}.");
        using var saved = File3dm.Read(output) ?? throw new IOException(output);
        if (source.Objects.Count != saved.Objects.Count || source.Settings.ModelUnitSystem != saved.Settings.ModelUnitSystem ||
            source.Settings.ModelAbsoluteTolerance != saved.Settings.ModelAbsoluteTolerance ||
            !source.AllNamedViews.Select(view => view.Name).SequenceEqual(saved.AllNamedViews.Select(view => view.Name)))
            throw new InvalidOperationException("Archive roundtrip changed object count, units, tolerance, or views.");
        int render = 0;
        foreach (var item in source.Objects)
        {
            var copy = saved.Objects.FindId(item.Id);
            if (copy == null || copy.Attributes.LayerIndex != item.Attributes.LayerIndex ||
                !GeometryBase.GeometryEquals(item.Geometry, copy.Geometry))
                throw new InvalidOperationException($"Archive roundtrip changed original geometry or layer at {item.Id}.");
            if (copy.Geometry is Brep brep)
                render += brep.Faces.Count(face => face.GetMesh(MeshType.Render) != null);
        }
        return $"PASS {saved.Objects.Count} identical originals by ID and geometry, source layers and named views retained. " +
            $"{render} Brep faces retain a render cache. {new FileInfo(input).Length} → {new FileInfo(output).Length} bytes.";
    }
}
