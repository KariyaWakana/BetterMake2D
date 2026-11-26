using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

internal readonly record struct V2TileSegmentEdit(V2Segment? Before, V2Segment? After);

internal sealed class V2LocalPatchException(string message) : InvalidOperationException(message);

internal sealed partial class V2PlanarArrangement
{
    internal readonly record struct Patch(V2PlanarArrangement Plan, V2PlanarArrangement Changed,
        int DirtyCells, int FrozenCells, int InputEdges, int Expansions, double DepthSeconds);

    internal Patch Repair(V2TileWindow window, V2TileSegmentEdit[] edits,
        Action<V2PlanarArrangement> classify, Action cancel)
    {
        var dirty = new bool[Cells.Length];
        var boxes = Cells.Select(CellBox).ToArray();
        var cellIndex = new V2ManagedBounds(boxes.Length, i => boxes[i]);
        var removed = new Dictionary<(int Curve, int Face, string Kind), List<(double Min, double Max)>>();
        var added = new List<Edge>();
        foreach (var (before, after) in edits)
        {
            cancel?.Invoke();
            if (before is V2Segment old)
            {
                Touch(old);
                var span = old.Source;
                var kind = (span.CurveId, span.Face, span.Kind);
                if (!removed.TryGetValue(kind, out var ranges)) removed.Add(kind, ranges = new());
                ranges.Add((Math.Min(span.From, span.To), Math.Max(span.From, span.To)));
            }
            if (after is V2Segment newSegment)
            {
                Touch(newSegment);
                added.Add(AnEdge(newSegment));
            }
        }

        var unattached = new Dictionary<int, int>();
        for (int i = 0; i < edges.Length; i++)
        {
            if ((i & 8191) == 0) cancel?.Invoke();
            if (incidentCell[i * 2] >= 0 || incidentCell[i * 2 + 1] >= 0) continue;
            var edge = edges[i];
            var point = new Point2d((edge.A.X + edge.B.X) * (.5 * grid), (edge.A.Y + edge.B.Y) * (.5 * grid));
            double x = (edge.A.X + edge.B.X) * .5, y = (edge.A.Y + edge.B.Y) * .5;
            cellIndex.Search(new Rect(x, y, x, y), id =>
            {
                if (Contains(Cells[id], point)) unattached.TryAdd(i, id);
            });
        }

        for (int expansions = 0; ; expansions++)
        {
            cancel?.Invoke();
            var inputs = new List<Edge>(added);
            var border = new List<(int Index, int DirtySide, int CleanCell)>();
            var replace = new bool[edges.Length];
            for (int i = 0; i < edges.Length; i++)
            {
                if ((i & 8191) == 0) cancel?.Invoke();
                int left = incidentCell[i * 2], right = incidentCell[i * 2 + 1];
                bool leftDirty = Dirty(left), rightDirty = Dirty(right);
                if (!leftDirty && !rightDirty && !(unattached.TryGetValue(i, out int owner) && Dirty(owner)))
                    continue;
                var edge = edges[i];
                bool perimeter = leftDirty != rightDirty;
                int clean = leftDirty ? right : left;
                if (perimeter) border.Add((i, leftDirty ? 0 : 1, clean));
                replace[i] = !perimeter || clean < 0;
                var sources = removed.Count == 0 ? edge.Sources : edge.Sources.Where(Keep).ToArray();
                if (perimeter || sources.Length > 0)
                    inputs.Add(ReferenceEquals(sources, edge.Sources) ? edge : edge with { Sources = sources });
            }

            if (border.Count == 0)
                throw new V2LocalPatchException("Contact edits have no bounded integer patch inside their tile.");
            V2PlanarArrangement arranged;
            try { arranged = Arrange(Canonical(inputs), grid, cancel, null); }
            catch (V2SnapConvergenceException)
            {
                throw new V2LocalPatchException("The local contact patch did not converge on its shared grid.");
            }
            var perimeterIndex = new Dictionary<(Key, Key), int>();
            for (int i = 0; i < border.Count; i++)
            {
                var edge = edges[border[i].Index];
                perimeterIndex.Add((edge.A, edge.B), i);
            }
            var outerIndex = new V2ManagedBounds(border.Count, i => Bounds(edges[border[i].Index]));
            var coverage = new long[border.Count];
            var borderMatch = new int[arranged.EdgeCount];
            Array.Fill(borderMatch, -1);
            for (int i = 0; i < arranged.EdgeCount; i++)
            {
                if ((i & 8191) == 0) cancel?.Invoke();
                var edge = arranged.edges[i];
                if (perimeterIndex.TryGetValue((edge.A, edge.B), out int matched))
                {
                    if (border[matched].CleanCell < 0 || SameSources(edge.Sources, edges[border[matched].Index].Sources))
                        Mark(i, matched);
                }
                else if (V2TiledArrangement.OnSeam(window, edge.A, edge.B).HasValue)
                {
                    outerIndex.Search(Bounds(edge), j =>
                    {
                        var old = edges[border[j].Index];
                        if (border[j].CleanCell < 0 && OnFrame(old) && On(old, edge.A) && On(old, edge.B))
                            Mark(i, j);
                    });
                }
            }

            bool expanded = false;
            for (int i = 0; i < border.Count; i++)
            {
                var (index, _, neighbor) = border[i];
                if (coverage[i] == Measure(edges[index])) continue;
                if (neighbor < 0)
                    throw new V2LocalPatchException("Local snap rounding changed the coverage of an outer tile seam.");
                dirty[neighbor] = true;
                expanded = true;
            }
            if (expanded) continue;

            var parents = Enumerable.Range(0, arranged.Cells.Length).ToArray();
            for (int i = 0; i < arranged.EdgeCount; i++)
            {
                if (borderMatch[i] >= 0) continue;
                int a = arranged.incidentCell[i * 2], b = arranged.incidentCell[i * 2 + 1];
                if (a >= 0 && b >= 0) parents[Root(a)] = Root(b);
            }
            var inside = new bool[arranged.Cells.Length];
            var outside = new bool[arranged.Cells.Length];
            for (int i = 0; i < arranged.EdgeCount; i++)
            {
                if (borderMatch[i] < 0) continue;
                var boundary = border[borderMatch[i]];
                int side = (i * 2) + boundary.DirtySide;
                int interior = arranged.incidentCell[side], exterior = arranged.incidentCell[side ^ 1];
                if (interior < 0)
                {
                    if (boundary.CleanCell < 0)
                        throw new V2LocalPatchException("A preserved tile seam has no interior cell.");
                    dirty[boundary.CleanCell] = true;
                    expanded = true;
                    continue;
                }
                inside[Root(interior)] = true;
                if (exterior >= 0) outside[Root(exterior)] = true;
            }
            if (expanded) continue;
            if (inside.Where((value, i) => value && outside[i]).Any())
            {
                for (int i = 0; i < arranged.EdgeCount; i++)
                {
                    if (borderMatch[i] < 0) continue;
                    var boundary = border[borderMatch[i]];
                    if (boundary.CleanCell < 0) continue;
                    int exterior = arranged.incidentCell[(i * 2 + boundary.DirtySide) ^ 1];
                    if (exterior < 0 || !inside[Root(exterior)]) continue;
                    dirty[boundary.CleanCell] = true;
                    expanded = true;
                }
                if (expanded) continue;
                throw new V2LocalPatchException("The repaired cells cross their frozen interface.");
            }

            var newCells = arranged.Cells.Where((_, i) => inside[Root(i)]).ToArray();
            var changed = new V2PlanarArrangement(arranged.edges, newCells, grid, arranged.Rounds);
            long depthStart = System.Diagnostics.Stopwatch.GetTimestamp();
            classify(changed);
            double depth = System.Diagnostics.Stopwatch.GetElapsedTime(depthStart).TotalSeconds;

            var mergedEdges = new List<Edge>(edges.Length);
            var previousId = new int[edges.Length];
            for (int i = 0; i < edges.Length; i++)
            {
                if (replace[i]) continue;
                previousId[i] = mergedEdges.Count;
                mergedEdges.Add(edges[i]);
            }
            var patchId = new int[arranged.EdgeCount];
            Array.Fill(patchId, -1);
            for (int i = 0; i < arranged.EdgeCount; i++)
            {
                var edge = arranged.edges[i];
                int match = borderMatch[i];
                if (match >= 0 && border[match].CleanCell >= 0)
                {
                    patchId[i] = previousId[border[match].Index];
                    continue;
                }
                if (changed.incidentCell[i * 2] < 0 && changed.incidentCell[i * 2 + 1] < 0 &&
                    !InDirtyCell(edge)) continue;
                patchId[i] = mergedEdges.Count;
                mergedEdges.Add(edge);
            }
            int dirtyCount = dirty.Count(value => value);
            var mergedCells = new List<Cell>(Cells.Length - dirtyCount + changed.Cells.Length);
            for (int i = 0; i < Cells.Length; i++)
                if (!dirty[i]) mergedCells.Add(Remap(Cells[i], previousId));
            foreach (var cell in changed.Cells) mergedCells.Add(Remap(cell, patchId));
            var merged = new V2PlanarArrangement(mergedEdges.ToArray(), mergedCells.ToArray(), grid, arranged.Rounds);
            return new Patch(merged, changed, dirtyCount, Cells.Length - dirtyCount, inputs.Count, expansions, depth);

            void Mark(int edge, int perimeter)
            {
                if (borderMatch[edge] >= 0) return;
                borderMatch[edge] = perimeter;
                coverage[perimeter] += Measure(arranged.edges[edge]);
            }
            int Root(int cell)
            {
                while (parents[cell] != cell)
                {
                    parents[cell] = parents[parents[cell]];
                    cell = parents[cell];
                }
                return cell;
            }
        }

        bool Dirty(int cell) => cell >= 0 && dirty[cell];
        void Touch(V2Segment segment) => cellIndex.Search(Bounds(AnEdge(segment)).Grow(2), i => dirty[i] = true);
        Edge AnEdge(V2Segment segment) => new(Snap(segment.A, grid), Snap(segment.B, grid), new[] {segment.Source});
        bool OnFrame(Edge edge) => V2TiledArrangement.OnSeam(window, edge.A, edge.B).HasValue;
        bool InDirtyCell(Edge edge)
        {
            bool found = false;
            double x = (edge.A.X + edge.B.X) * .5, y = (edge.A.Y + edge.B.Y) * .5;
            var point = new Point2d(x * grid, y * grid);
            cellIndex.Search(new Rect(x, y, x, y), i => found |= dirty[i] && Contains(Cells[i], point));
            return found;
        }
        bool Keep(V2SourceSpan span)
        {
            if (!removed.TryGetValue((span.CurveId, span.Face, span.Kind), out var ranges)) return true;
            double lo = Math.Min(span.From, span.To), hi = Math.Max(span.From, span.To);
            double allowance = Math.Max(1, Math.Max(Math.Abs(lo), Math.Abs(hi))) * 2e-14;
            return !ranges.Any(range => lo >= range.Min - allowance && hi <= range.Max + allowance);
        }
        Rect CellBox(Cell cell)
        {
            var box = Bounds(edges[cell.Outer[0] / 2]);
            foreach (int side in cell.Outer)
            {
                var point = Start(side);
                box = new Rect(Math.Min(box.Left, point.X), Math.Min(box.Bottom, point.Y),
                    Math.Max(box.Right, point.X), Math.Max(box.Top, point.Y));
            }
            return box;
        }
    }

    private static long Measure(Edge edge) =>
        Math.Max(Math.Abs(edge.A.X - edge.B.X), Math.Abs(edge.A.Y - edge.B.Y));

    private static bool On(Edge edge, Key point) => point.X >= Math.Min(edge.A.X, edge.B.X) &&
        point.X <= Math.Max(edge.A.X, edge.B.X) && point.Y >= Math.Min(edge.A.Y, edge.B.Y) &&
        point.Y <= Math.Max(edge.A.Y, edge.B.Y) &&
        ((Int128)edge.B.X - edge.A.X) * (point.Y - edge.A.Y) ==
        ((Int128)edge.B.Y - edge.A.Y) * (point.X - edge.A.X);

    private static bool SameSources(V2SourceSpan[] a, V2SourceSpan[] b) =>
        a.AsSpan().SequenceEqual(b) || a.Length == b.Length && a.All(b.Contains);

    private static Cell Remap(Cell cell, int[] edgeIds)
    {
        int[] Ring(int[] sides)
        {
            var result = new int[sides.Length];
            for (int i = 0; i < sides.Length; i++)
            {
                int side = sides[i];
                int edge = edgeIds[side / 2];
                if (edge < 0) throw new V2LocalPatchException("A repaired ring lost its shared half-edge.");
                result[i] = edge * 2 + (side & 1);
            }
            return result;
        }
        return new Cell(Ring(cell.Outer), cell.Holes.Select(Ring).ToList())
            {Owner = cell.Owner, Uncertain = cell.Uncertain, Clearance = cell.Clearance};
    }
}
