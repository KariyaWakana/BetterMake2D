using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class OriginalFaceCheckpoint
{
    private const string Marker = "BetterMake2D.OriginalFaces.v1";
    private const string PairKey = "BetterMake2D.SupportPair";
    internal const string SourceKey = "BetterMake2D.Source";
    private const string IndexKey = "BetterMake2D.OriginalFaceIndices";
    private const string SupportIdKey = "BetterMake2D.SupportObject";

    internal static (List<Input> Inputs, Dictionary<string, int[]> Faces, Interval Depth) Read(File3dm saved,
        File3dm supports, Transform toView, List<Brep> owned)
    {
        var header = saved.Strings.GetValue(Marker)?.Split(',');
        if (header is not { Length: 2 } || !Number(header[0], out double back) || !Number(header[1], out double front))
            throw new InvalidDataException("This is not a BetterMake2D original-face model.");
        var inputs = new List<Input>();
        var faces = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var item in saved.Objects)
        {
            if (item == null)
                continue;
            var attrs = item.Attributes;
            string source = attrs.GetUserString(SourceKey);
            if (source == null || item.Geometry is not Brep kept)
                throw new InvalidDataException("An object in the saved model has no original face identity.");
            Brep brep = kept;
            string support = attrs.GetUserString(SupportIdKey);
            if (support != null)
            {
                if (supports == null || saved.Strings.GetValue(PairKey) != supports.Strings.GetValue(PairKey) ||
                    !Guid.TryParse(support, out Guid id) || supports.Objects.FindId(id) is not { Geometry: Brep original } member ||
                    member.Attributes.GetUserString(SourceKey) != source)
                    throw new InvalidDataException($"The referenced original support for {source} is absent.");
                brep = original;
            }
            else if (!toView.IsIdentity)
            {
                brep = kept.DuplicateBrep();
                owned.Add(brep);
                if (!brep.Transform(toView))
                    throw new InvalidDataException($"Could not align the saved geometry for {source}.");
            }
            string[] tokens = attrs.GetUserString(IndexKey)?.Split(',') ?? Array.Empty<string>();
            var indices = new int[tokens.Length];
            int previous = -1;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out int index) ||
                    index <= previous || index >= brep.Faces.Count)
                    throw new InvalidDataException($"A saved face index is invalid for {source}.");
                indices[i] = previous = index;
            }
            if (indices.Length == 0 || indices.Length != kept.Faces.Count || !faces.TryAdd(source, indices))
                throw new InvalidDataException($"The saved face selection is invalid for {source}.");
            inputs.Add(new Input(brep, source, attrs.GetUserString("BetterMake2D.SourceMesh") == "1"));
        }
        var depth = new Interval(back, front);
        if (inputs.Count != 0 && (!depth.IsIncreasing || !double.IsFinite(back) || !double.IsFinite(front)))
            throw new InvalidDataException("The original scene depth interval is invalid.");
        return (inputs, faces, depth);

        static bool Number(string value, out double number) =>
            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    internal static string SupportPath(string path) => Path.ChangeExtension(path, ".支撑.3dm");
}
