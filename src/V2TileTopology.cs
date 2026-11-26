using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

internal sealed class V2TileTopology : IDisposable
{
    private const int BufferSize = 1 << 20;
    private FileStream file;

    internal long Save(V2PlanarArrangement arrangement)
    {
        file ??= new FileStream(Path.Combine(Path.GetTempPath(), $"bma2d-tile-{Guid.NewGuid():N}.bin"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize, FileOptions.DeleteOnClose);
        long offset = file.Seek(0, SeekOrigin.End);
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        arrangement.WriteTopology(writer);
        return offset;
    }

    internal V2PlanarArrangement Load(long offset, double grid)
    {
        file.Position = offset;
        using var reader = new BinaryReader(file, System.Text.Encoding.UTF8, leaveOpen: true);
        return V2PlanarArrangement.ReadTopology(reader, grid);
    }

    public void Dispose() => file?.Dispose();
}

internal sealed partial class V2PlanarArrangement
{
    private V2PlanarArrangement(Edge[] edges, Cell[] cells, double grid, int rounds)
    {
        this.edges = edges;
        this.grid = grid;
        origin = Point2d.Origin;
        Rounds = rounds;
        Cells = cells;
        incidentCell = new int[edges.Length * 2];
        Array.Fill(incidentCell, -1);
        for (int i = 0; i < cells.Length; i++)
        {
            foreach (int side in cells[i].Outer) incidentCell[side] = i;
            foreach (var hole in cells[i].Holes)
                foreach (int side in hole) incidentCell[side] = i;
        }
    }

    internal void WriteTopology(BinaryWriter writer)
    {
        writer.Write(Rounds);
        var kinds = edges.SelectMany(edge => edge.Sources).Select(source => source.Kind)
            .Distinct(StringComparer.Ordinal).ToArray();
        var kindIds = kinds.Select((kind, i) => (kind, i)).ToDictionary(item => item.kind, item => item.i);
        writer.Write(kinds.Length);
        foreach (string kind in kinds) writer.Write(kind);
        writer.Write(edges.Length);
        foreach (var edge in edges)
        {
            writer.Write(edge.A.X);
            writer.Write(edge.A.Y);
            writer.Write(edge.B.X);
            writer.Write(edge.B.Y);
            writer.Write(edge.Sources.Length);
            foreach (var source in edge.Sources)
            {
                writer.Write(source.CurveId);
                writer.Write(source.Face);
                writer.Write(kindIds[source.Kind]);
                writer.Write(source.From);
                writer.Write(source.To);
            }
        }
        writer.Write(Cells.Length);
        foreach (var cell in Cells)
        {
            writer.Write(cell.Owner);
            writer.Write(cell.Uncertain);
            writer.Write(cell.Clearance);
            Ring(cell.Outer);
            writer.Write(cell.Holes.Count);
            foreach (var hole in cell.Holes) Ring(hole);
        }

        void Ring(int[] sides)
        {
            writer.Write(sides.Length);
            foreach (int side in sides) writer.Write(side);
        }
    }

    internal static V2PlanarArrangement ReadTopology(BinaryReader reader, double grid)
    {
        int rounds = reader.ReadInt32();
        var kinds = new string[reader.ReadInt32()];
        for (int i = 0; i < kinds.Length; i++) kinds[i] = reader.ReadString();
        var edges = new Edge[reader.ReadInt32()];
        for (int i = 0; i < edges.Length; i++)
        {
            var a = new Key(reader.ReadInt64(), reader.ReadInt64());
            var b = new Key(reader.ReadInt64(), reader.ReadInt64());
            var spans = new V2SourceSpan[reader.ReadInt32()];
            for (int j = 0; j < spans.Length; j++)
                spans[j] = new(reader.ReadInt32(), reader.ReadInt32(), kinds[reader.ReadInt32()],
                    reader.ReadDouble(), reader.ReadDouble());
            edges[i] = new Edge(a, b, spans);
        }
        var cells = new Cell[reader.ReadInt32()];
        for (int i = 0; i < cells.Length; i++)
        {
            int owner = reader.ReadInt32();
            bool uncertain = reader.ReadBoolean();
            double clearance = reader.ReadDouble();
            var outer = Ring();
            int count = reader.ReadInt32();
            var holes = new List<int[]>(count);
            for (int j = 0; j < count; j++) holes.Add(Ring());
            cells[i] = new Cell(outer, holes) {Owner = owner, Uncertain = uncertain, Clearance = clearance};
        }
        return new V2PlanarArrangement(edges, cells, grid, rounds);

        int[] Ring()
        {
            var sides = new int[reader.ReadInt32()];
            for (int i = 0; i < sides.Length; i++) sides[i] = reader.ReadInt32();
            return sides;
        }
    }
}
