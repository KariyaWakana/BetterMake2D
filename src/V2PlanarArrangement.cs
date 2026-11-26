using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Rhino.Geometry;

namespace BetterMake2D;

internal readonly record struct V2SourceSpan(int CurveId, int Face, string Kind, double From, double To);
internal readonly record struct V2Segment(Point2d A, Point2d B, V2SourceSpan Source);
internal readonly record struct V2OwnedEdge(V2PlanarArrangement.Key A, V2PlanarArrangement.Key B,
    int Left, int Right, V2SourceSpan[] Sources);
internal sealed class V2SnapConvergenceException() : InvalidOperationException(
    "The snap-rounded arrangement did not stabilize after 12 rounds.");

internal sealed partial class V2PlanarArrangement
{
    internal readonly record struct Key(long X, long Y) : IComparable<Key>
    {
        public int CompareTo(Key other) => X == other.X ? Y.CompareTo(other.Y) : X.CompareTo(other.X);
    }
    private sealed record Edge(Key A, Key B, V2SourceSpan[] Sources);
    internal sealed record Cell(int[] Outer, List<int[]> Holes)
    {
        internal int Owner = -1;
        internal bool Uncertain = true;
        internal double Clearance;
    }
    private sealed record Ring(int[] Edges, double Area, Rect Box);
    private readonly Edge[] edges;
    private readonly int[] next;
    private readonly int[] incidentCell;
    private readonly int[] boundaryOwners;
    private readonly Point2d origin;
    private readonly double grid;
    internal readonly Cell[] Cells;
    internal readonly int Rounds;
    internal int EdgeCount => edges.Length;
    internal double Grid => grid;

    internal static V2PlanarArrangement Build(IReadOnlyList<V2Segment> sources, double grid, Action cancel,
        Action<string> progress = null, bool fast = false)
    {
        if (sources.Count == 0 || !(grid > 0) || !double.IsFinite(grid))
            throw new InvalidOperationException("A nonempty segment batch and a finite grid are required.");
        var edges = Canonical(sources.Select(segment => new Edge(Snap(segment.A, grid), Snap(segment.B, grid), new[] { segment.Source })));
        return Arrange(edges, grid, cancel, progress, fast);
    }

    internal static V2PlanarArrangement RebuildBoundary(IReadOnlyList<V2OwnedEdge> boundary, double grid, Action cancel)
        => Arrange(Canonical(boundary.Select(edge => new Edge(edge.A, edge.B, edge.Sources))), grid, cancel, null);

    private static V2PlanarArrangement Arrange(Edge[] edges, double grid, Action cancel, Action<string> progress,
        bool fast = false)
    {
        var origin = Point2d.Origin;
        for (int round = 1; round <= 12; round++)
        {
            cancel?.Invoke();
            var points = new HashSet<Key>[edges.Length];
            {
                var boxes = new V2ManagedBounds(edges.Length, i => Bounds(edges[i]));
                // In Fast mode a long chain of adjacent pixels can repeatedly
                // move dense proxy edges. After four rounds resolve actual
                // crossings and T-junctions only, still requiring convergence.
                var hot = !fast || round <= 4 ? new HashSet<Key>() : null;
                for (int i = 0; hot != null && i < edges.Length; i++)
                {
                    var edge = edges[i];
                    hot.Add(edge.A);
                    hot.Add(edge.B);
                }
                for (int i = 0; i < edges.Length; i++)
                {
                    if ((i & 511) == 0) cancel?.Invoke();
                    int index = i;
                    var edge = edges[index];
                    boxes.Search(Bounds(edge), j =>
                    {
                        if (j <= index) return;
                        Intersections(edge, edges[j], point =>
                        {
                            hot?.Add(point);
                            Insert(index, point);
                            Insert(j, point);
                        });
                    });
                }
                int noding = progress == null ? 0 : points.Count(group => group != null);
                int visited = 0;
                foreach (var point in hot ?? Enumerable.Empty<Key>())
                {
                    if ((visited++ & 511) == 0) cancel?.Invoke();
                    var pixel = new Rect(point.X - .5, point.Y - .5, point.X + .5, point.Y + .5);
                    boxes.Search(pixel, id =>
                    {
                        var edge = edges[id];
                        if (point != edge.A && point != edge.B && CrossesPixel(edge, point))
                            Insert(id, point);
                    });
                }
                if (progress != null)
                    progress($"Snap round {round}: {edges.Length} edges, {noding} intersection splits, " +
                        $"{points.Count(group => group != null)} total changed edges.");
            }
            if (points.All(list => list == null))
                return new V2PlanarArrangement(edges, origin, grid, round);
            var fragments = new List<Edge>(edges.Length);
            for (int i = 0; i < edges.Length; i++)
            {
                if ((i & 511) == 0) cancel?.Invoke();
                var edge = edges[i];
                if (points[i] == null)
                {
                    fragments.Add(edge);
                    continue;
                }
                var stations = points[i].Append(edge.A).Append(edge.B)
                    .OrderBy(point => Along(edge, point)).ThenBy(point => point).ToArray();
                for (int j = 1; j < stations.Length; j++)
                {
                    var a = stations[j - 1];
                    var b = stations[j];
                    if (a == b) continue;
                    double t0 = Station(edge, a), t1 = Station(edge, b);
                    fragments.Add(new Edge(a, b, edge.Sources.Select(source => source with
                    {
                        From = source.From + (source.To - source.From) * t0,
                        To = source.From + (source.To - source.From) * t1
                    }).ToArray()));
                }
            }
            cancel?.Invoke();
            edges = Canonical(fragments);

            void Insert(int index, Key point)
            {
                var edge = edges[index];
                if (point != edge.A && point != edge.B)
                    (points[index] ??= new HashSet<Key>()).Add(point);
            }
        }
        throw new V2SnapConvergenceException();

    }

    internal static Key Snap(Point2d point, double grid)
    {
        double x = point.X / grid, y = point.Y / grid;
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Max(Math.Abs(x), Math.Abs(y)) >= 1L << 50)
            throw new InvalidOperationException("The integer grid exceeds the reliable view-coordinate range.");
        return new Key((long)Math.Floor(x + .5), (long)Math.Floor(y + .5));
    }

    internal static V2PlanarArrangement FromBoundary(List<V2OwnedEdge> boundary, double grid)
    {
        var edges = new Edge[boundary.Count];
        var owners = new int[edges.Length * 2];
        for (int i = 0; i < edges.Length; i++)
        {
            var edge = boundary[i];
            edges[i] = new Edge(edge.A, edge.B, edge.Sources);
            owners[i * 2] = edge.Left;
            owners[i * 2 + 1] = edge.Right;
        }
        boundary.Clear();
        return new V2PlanarArrangement(edges, Point2d.Origin, grid, 0, owners);
    }

    private V2PlanarArrangement(Edge[] edges, Point2d origin, double grid, int rounds, int[] owners = null)
    {
        this.edges = edges;
        this.origin = origin;
        this.grid = grid;
        Rounds = rounds;
        next = new int[edges.Length * 2];
        var heads = new Dictionary<Key, int>();
        var outgoing = new int[next.Length];
        for (int side = 0; side < next.Length; side++)
        {
            var key = Start(side);
            outgoing[side] = heads.GetValueOrDefault(key, -1);
            heads[key] = side;
        }
        var list = new List<int>();
        foreach (var (key, head) in heads)
        {
            list.Clear();
            for (int side = head; side >= 0; side = outgoing[side])
                list.Add(side);
            list.Sort((a, b) => CompareDirections(key, Start(a ^ 1), Start(b ^ 1)));
            for (int i = 0; i < list.Count; i++)
                next[list[i] ^ 1] = list[(i + list.Count - 1) % list.Count];
        }
        if (owners != null)
        {
            for (int side = 0; side < owners.Length; side++)
                if (owners[side] != owners[next[side]])
                    throw new InvalidOperationException($"Stitched ownership does not close at {Start(side ^ 1)}.");
            boundaryOwners = owners;
            Cells = Array.Empty<Cell>();
            return;
        }
        incidentCell = new int[next.Length];
        Array.Fill(incidentCell, -1);
        var rings = Walk(next.Length, side => true, side => next[side]);
        Cells = Group(rings);
        for (int i = 0; i < Cells.Length; i++)
        {
            foreach (int side in Cells[i].Outer) incidentCell[side] = i;
            foreach (var hole in Cells[i].Holes)
                foreach (int side in hole) incidentCell[side] = i;
        }
    }

    internal int Owner(int side) => boundaryOwners != null ? boundaryOwners[side] :
        incidentCell[side] < 0 ? -1 : Cells[incidentCell[side]].Owner;
    internal int CellIndex(int side) => incidentCell[side];
    internal Point3d Point(Key key) => new(origin.X + key.X * grid, origin.Y + key.Y * grid, 0);
    internal BoundingBox RegionBounds(Cell cell)
    {
        var box = BoundingBox.Empty;
        foreach (int side in cell.Outer) box.Union(Point(Start(side)));
        return box;
    }
    internal Key Start(int side) => (side & 1) == 0 ? edges[side / 2].A : edges[side / 2].B;
    internal IReadOnlyList<V2SourceSpan> Sources(int side) => edges[side / 2].Sources;
    internal Curve SharedEdge(int side) => new LineCurve(Point(Start(side)), Point(Start(side ^ 1)));

    internal List<V2OwnedEdge> VisibleEdges(IReadOnlyList<int> owners = null)
    {
        var result = boundaryOwners == null ? new List<V2OwnedEdge>() : new List<V2OwnedEdge>(edges.Length);
        result.AddRange(EnumerateVisibleEdges(owners));
        return result;
    }

    internal IEnumerable<V2OwnedEdge> EnumerateVisibleEdges(IReadOnlyList<int> owners = null)
    {
        for (int i = 0; i < edges.Length; i++)
        {
            int left = owners == null ? Owner(2 * i) : owners[2 * i];
            int right = owners == null ? Owner(2 * i + 1) : owners[2 * i + 1];
            if (left != right)
            {
                var edge = edges[i];
                yield return new V2OwnedEdge(edge.A, edge.B, left, right, edge.Sources);
            }
        }
    }

    internal List<V2EdgeChain> BoundaryChains(int[] chainBySide)
    {
        var atVertex = new Dictionary<Key, (int First, int Second, int Count)>();
        var primary = new V2SourceSpan[edges.Length];
        for (int i = 0; i < edges.Length; i++)
        {
            int side = i * 2, left = Owner(side), right = Owner(side ^ 1);
            if (left == right) continue;
            Add(side);
            Add(side ^ 1);
            primary[i] = PrimarySpan(side);
        }
        var chains = new List<V2EdgeChain>();
        var sides = new int[edges.Length];
        int used = 0;
        for (int i = 0; i < edges.Length; i++)
        {
            int first = i * 2;
            if (chainBySide[first] != 0 || Owner(first) == Owner(first ^ 1)) continue;
            int prior = Continue(first ^ 1);
            while (prior >= 0 && (prior ^ 1) != i * 2)
            {
                first = prior ^ 1;
                prior = Continue(first ^ 1);
            }
            int offset = used;
            int side = first, id = chains.Count + 1;
            do
            {
                sides[used++] = side;
                chainBySide[side] = id;
                chainBySide[side ^ 1] = -id;
                side = Continue(side);
            } while (side >= 0 && chainBySide[side] == 0);
            chains.Add(new V2EdgeChain(Owner(first), Owner(first ^ 1), this, sides, offset, used - offset, Span(first)));
        }
        return chains;

        void Add(int side)
        {
            var vertex = Start(side);
            var incoming = atVertex.GetValueOrDefault(vertex);
            atVertex[vertex] = incoming.Count < 2
                ? (side, incoming.First, incoming.Count + 1)
                : (incoming.First, incoming.Second, incoming.Count + 1);
        }

        V2SourceSpan Span(int side)
        {
            var span = primary[side / 2];
            return (side & 1) == 0 ? span : span with { From = span.To, To = span.From };
        }

        int Continue(int side)
        {
            var junction = atVertex[Start(side ^ 1)];
            if (junction.Count != 2) return -1;
            int adjacent = junction.First == (side ^ 1) ? junction.Second : junction.First;
            var a = Span(side);
            var b = Span(adjacent);
            if (a.Kind != b.Kind || Owner(side) != Owner(adjacent) || Owner(side ^ 1) != Owner(adjacent ^ 1))
                return -1;
            if (a.CurveId == b.CurveId) return adjacent;
            var start = Start(side);
            var join = Start(adjacent);
            var end = Start(adjacent ^ 1);
            Int128 ax = (Int128)join.X - start.X, ay = (Int128)join.Y - start.Y;
            Int128 bx = (Int128)end.X - join.X, by = (Int128)end.Y - join.Y;
            return ax * by == ay * bx && ax * bx + ay * by > 0 ? adjacent : -1;
        }
    }

    internal V2SourceSpan PrimarySpan(int side)
    {
        var sources = edges[side / 2].Sources;
        int left = Owner(side), right = Owner(side ^ 1);
        // Prefer a visible incident face when several source edges coincide.
        var best = sources[0];
        for (int i = 1; i < sources.Length; i++)
            if (Priority(sources[i]).CompareTo(Priority(best)) < 0) best = sources[i];
        return (side & 1) == 0 ? best : best with { From = best.To, To = best.From };

        (int, int, int, int) Priority(V2SourceSpan span) =>
            (span.Kind == "Mask" ? 1 : 0, span.Face == left || span.Face == right ? 0 : 1,
                span.Kind == "SectionCut" ? 0 : span.Kind == "Contact" ? 2 : 1, span.CurveId);
    }

    internal IEnumerable<string> InspectBoundary(int face)
    {
        var neighbors = new Dictionary<Key, int>();
        foreach (var edge in edges)
        {
            neighbors[edge.A] = neighbors.GetValueOrDefault(edge.A) + 1;
            neighbors[edge.B] = neighbors.GetValueOrDefault(edge.B) + 1;
        }
        foreach (var group in edges.SelectMany(edge => edge.Sources.Where(span => span.Face == face)
            .Select(span => (edge, span))).GroupBy(hit => (hit.span.CurveId, hit.span.Kind)))
        {
            var points = group.SelectMany(hit => new[] { hit.edge.A, hit.edge.B }).Distinct().ToArray();
            var open = points.Where(point => neighbors[point] == 1).Select(point => Point(point));
            var bounds = new BoundingBox(points.Select(Point));
            yield return $"{group.Key.Kind} {group.Key.CurveId} bounds {bounds}; loose endpoints: {string.Join("; ", open)}";
        }
    }

    internal bool Contains(Cell cell, Point2d point)
    {
        double x = (point.X - origin.X) / grid, y = (point.Y - origin.Y) / grid;
        return Inside(cell.Outer) && !cell.Holes.Any(Inside);

        bool Inside(int[] ring)
        {
            bool inside = false;
            foreach (int side in ring)
            {
                var a = Start(side);
                var b = Start(side ^ 1);
                if ((a.Y > y) != (b.Y > y) && x < a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y))
                    inside = !inside;
            }
            return inside;
        }
    }

    internal IEnumerable<(int Owner, Cell Region)> Regions(bool includeBackground = false)
    {
        int[] successor = new int[next.Length];
        bool IsBoundary(int side) => (includeBackground || Owner(side) >= 0) && Owner(side) != Owner(side ^ 1);
        for (int side = 0; side < next.Length; side++)
        {
            if (!IsBoundary(side)) continue;
            int following = next[side];
            while (Owner(following) == Owner(following ^ 1))
            {
                following = next[following ^ 1];
                if (following == next[side])
                    throw new InvalidOperationException("A DCEL vertex has no outgoing region boundary.");
            }
            successor[side] = following;
        }
        var rings = Walk(next.Length, IsBoundary, side => successor[side]);
        foreach (var group in rings.GroupBy(ring => Owner(ring.Edges[0])))
            foreach (var cell in Group(group.ToArray()))
                yield return (group.Key, cell);
    }

    internal bool TryInterior(Cell cell, out Point2d point)
    {
        point = Point2d.Unset;
        var rings = new[] { cell.Outer }.Concat(cell.Holes).ToArray();
        var keys = rings.SelectMany(ring => ring).Select(Start).ToArray();
        var levels = keys.Select(key => key.Y).Distinct().OrderBy(y => y).ToArray();
        if (levels.Length < 2) return false;
        var gaps = Enumerable.Range(1, levels.Length - 1).OrderByDescending(i => levels[i] - levels[i - 1]);
        double best = 0;
        foreach (int level in gaps.Take(24))
        {
            double y = (levels[level] + levels[level - 1]) * .5;
            var crossings = new List<double>();
            foreach (var ring in rings)
                foreach (int side in ring)
                {
                    var a = Start(side);
                    var b = Start(side ^ 1);
                    if ((a.Y > y) != (b.Y > y))
                        crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            crossings.Sort();
            for (int i = 1; i < crossings.Count; i += 2)
            {
                double x = (crossings[i - 1] + crossings[i]) * .5;
                double clearance = (crossings[i] - crossings[i - 1]) * .5;
                if (clearance <= best) continue;
                foreach (int side in rings.SelectMany(ring => ring))
                    clearance = Math.Min(clearance, Distance(x, y, Start(side), Start(side ^ 1)));
                if (clearance > best)
                {
                    best = clearance;
                    point = new Point2d(origin.X + x * grid, origin.Y + y * grid);
                }
            }
        }
        cell.Clearance = best * grid;
        return best > 1e-4 && point.IsValid;
    }

    private Cell[] Group(IReadOnlyList<Ring> rings)
    {
        var positive = rings.Where(ring => ring.Area > 0).ToArray();
        var cells = positive.Select(ring => new Cell(ring.Edges, new List<int[]>())).ToArray();
        var index = new V2ManagedBounds(positive.Length, i => positive[i].Box);
        foreach (var ring in rings.Where(ring => ring.Area < 0))
        {
            var a = Start(ring.Edges[0]);
            var b = Start(ring.Edges[0] ^ 1);
            long x2 = a.X + b.X, y2 = a.Y + b.Y;
            var p = new Rect(x2 * .5, y2 * .5, x2 * .5, y2 * .5);
            int owner = -1;
            double smallest = double.PositiveInfinity;
            index.Search(p, id =>
            {
                var outer = positive[id];
                if ((outer.Area < smallest || outer.Area == smallest && id < owner) &&
                    InsideLeftOf(outer.Edges, a, b, x2, y2))
                    (owner, smallest) = (id, outer.Area);
            });
            if (owner >= 0) cells[owner].Holes.Add(ring.Edges);
        }
        return cells;
    }

    private bool InsideLeftOf(int[] ring, Key from, Key to, long x2, long y2)
    {
        bool inside = false;
        foreach (int side in ring)
        {
            var a = Start(side);
            var b = Start(side ^ 1);
            if (a == from && b == to) return true;
            if (a == to && b == from) return false;
            if ((a.Y * 2 > y2) == (b.Y * 2 > y2)) continue;
            Int128 turn = (Int128)(b.X - a.X) * (y2 - a.Y * 2) -
                (Int128)(b.Y - a.Y) * (x2 - a.X * 2);
            if (turn > 0 == b.Y > a.Y)
                inside = !inside;
        }
        return inside;
    }

    private Ring[] Walk(int count, Func<int, bool> enabled, Func<int, int> following)
    {
        var seen = new bool[count];
        var rings = new List<Ring>();
        for (int i = 0; i < count; i++)
        {
            if (seen[i] || !enabled(i)) continue;
            var ring = new List<int>();
            int current = i;
            do
            {
                if (seen[current] || !enabled(current))
                    throw new InvalidOperationException("A DCEL boundary walk did not return to its origin.");
                seen[current] = true;
                ring.Add(current);
                current = following(current);
            } while (current != i);
            AddSimpleRings(ring, rings);
        }
        return rings.ToArray();
    }

    private void AddSimpleRings(List<int> walk, List<Ring> rings)
    {
        var stack = new List<int>();
        var vertices = new Dictionary<Key, int>();
        foreach (int side in walk)
        {
            var from = Start(side);
            vertices[from] = stack.Count;
            stack.Add(side);
            if (!vertices.TryGetValue(Start(side ^ 1), out int first)) continue;
            var ring = stack.Skip(first).ToArray();
            foreach (int retired in ring) vertices.Remove(Start(retired));
            stack.RemoveRange(first, ring.Length);
            if (ring.Length < 3) continue;
            var anchor = Start(ring[0]);
            Int128 signed = 0;
            foreach (int item in ring)
            {
                var a = Start(item);
                var b = Start(item ^ 1);
                signed += (Int128)(a.X - anchor.X) * (b.Y - anchor.Y) -
                    (Int128)(a.Y - anchor.Y) * (b.X - anchor.X);
            }
            if (signed == 0) continue;
            var keys = ring.Select(Start).ToArray();
            rings.Add(new Ring(ring, (double)signed, new Rect(keys.Min(key => key.X), keys.Min(key => key.Y),
                keys.Max(key => key.X), keys.Max(key => key.Y))));
        }
    }

    private static int CompareDirections(Key origin, Key a, Key b)
    {
        bool topA = a.Y > origin.Y || a.Y == origin.Y && a.X > origin.X;
        bool topB = b.Y > origin.Y || b.Y == origin.Y && b.X > origin.X;
        if (topA != topB) return topA ? -1 : 1;
        int turn = Orientation(origin, a, b);
        return turn == 0 ? a.CompareTo(b) : -turn;
    }

    private static Edge[] Canonical(IEnumerable<Edge> input)
    {
        var merged = new Dictionary<(Key, Key), Edge>();
        foreach (var edge in input)
        {
            int order = edge.A.CompareTo(edge.B);
            if (order == 0) continue;
            var key = order < 0 ? (edge.A, edge.B) : (edge.B, edge.A);
            var sorted = order < 0 ? edge : new Edge(edge.B, edge.A,
                edge.Sources.Select(source => source with { From = source.To, To = source.From }).ToArray());
            if (!merged.TryGetValue(key, out var current))
            {
                merged.Add(key, sorted);
                continue;
            }
            if (sorted.Sources.Length == 1 && Array.IndexOf(current.Sources, sorted.Sources[0]) >= 0)
                continue;
            merged[key] = current with { Sources = current.Sources.Concat(sorted.Sources).Distinct().ToArray() };
        }
        return merged.Values.ToArray();
    }

    private static Rect Bounds(Edge edge) => new(Math.Min(edge.A.X, edge.B.X), Math.Min(edge.A.Y, edge.B.Y),
        Math.Max(edge.A.X, edge.B.X), Math.Max(edge.A.Y, edge.B.Y));

    private static double Station(Edge edge, Key point)
    {
        long dx = edge.B.X - edge.A.X, dy = edge.B.Y - edge.A.Y;
        return Math.Clamp((double)Along(edge, point) /
            ((double)dx * dx + (double)dy * dy), 0, 1);
    }

    private static Int128 Along(Edge edge, Key point) =>
        (Int128)(point.X - edge.A.X) * (edge.B.X - edge.A.X) +
        (Int128)(point.Y - edge.A.Y) * (edge.B.Y - edge.A.Y);

    private static double Distance(double x, double y, Key a, Key b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        double px = x - a.X - t * dx, py = y - a.Y - t * dy;
        return Math.Sqrt(px * px + py * py);
    }

    private static bool CrossesPixel(Edge edge, Key center)
    {
        (long N, long D) low = (0, 1), high = (1, 1);
        bool openLow = false, openHigh = false;
        return Clip(edge.A.X - center.X, edge.B.X - center.X) &&
            Clip(edge.A.Y - center.Y, edge.B.Y - center.Y);

        bool Clip(long a, long b)
        {
            if (a == b) return a == 0;
            long sign = Math.Sign(b - a), den = Math.Abs((b - a) * 2);
            var first = (-1 - a * 2 * sign, den);
            var last = (1 - a * 2 * sign, den);
            int lower = Compare(first, low), upper = Compare(last, high);
            if (lower > 0) (low, openLow) = (first, sign < 0);
            else if (lower == 0) openLow |= sign < 0;
            if (upper < 0) (high, openHigh) = (last, sign > 0);
            else if (upper == 0) openHigh |= sign > 0;
            int order = Compare(low, high);
            return order < 0 || order == 0 && !openLow && !openHigh;
        }

        static int Compare((long N, long D) a, (long N, long D) b) =>
            ((Int128)a.N * b.D).CompareTo((Int128)b.N * a.D);
    }

    private static int Orientation(Key a, Key b, Key c) =>
        ((Int128)(b.X - a.X) * (c.Y - a.Y) - (Int128)(b.Y - a.Y) * (c.X - a.X)).CompareTo(0);

    private static void Intersections(Edge first, Edge second, Action<Key> add)
    {
        int a = Orientation(first.A, first.B, second.A), b = Orientation(first.A, first.B, second.B);
        int c = Orientation(second.A, second.B, first.A), d = Orientation(second.A, second.B, first.B);
        if (a == 0 && Contains(first, second.A)) add(second.A);
        if (b == 0 && Contains(first, second.B)) add(second.B);
        if (c == 0 && Contains(second, first.A)) add(first.A);
        if (d == 0 && Contains(second, first.B)) add(first.B);
        if (a * b >= 0 || c * d >= 0) return;
        long dx = first.B.X - first.A.X, dy = first.B.Y - first.A.Y;
        long ex = second.B.X - second.A.X, ey = second.B.Y - second.A.Y;
        Int128 denominator = (Int128)dx * ey - (Int128)dy * ex;
        Int128 numerator = (Int128)(second.A.X - first.A.X) * ey - (Int128)(second.A.Y - first.A.Y) * ex;
        try
        {
            add(new Key(Snap(first.A.X, dx), Snap(first.A.Y, dy)));
        }
        catch (OverflowException)
        {
            add(new Key(Wide(first.A.X, dx), Wide(first.A.Y, dy)));
        }

        long Snap(long origin, long delta)
        {
            Int128 n = checked(origin * denominator + delta * numerator);
            Int128 q = n / denominator, r = n % denominator;
            int direction = Int128.Sign(r) * Int128.Sign(denominator);
            Int128 distance = Int128.Abs(r) * 2 - Int128.Abs(denominator);
            if (distance > 0 || distance == 0 && direction > 0)
                q += direction;
            return checked((long)q);
        }

        long Wide(long origin, long delta)
        {
            BigInteger den = (BigInteger)denominator;
            BigInteger n = origin * den + delta * (BigInteger)numerator;
            BigInteger q = BigInteger.DivRem(n, den, out var r);
            int direction = r.Sign * den.Sign;
            var distance = BigInteger.Abs(r) * 2 - BigInteger.Abs(den);
            if (distance.Sign > 0 || distance.IsZero && direction > 0)
                q += direction;
            return (long)q;
        }

        static bool Contains(Edge edge, Key point) => point.X >= Math.Min(edge.A.X, edge.B.X) &&
            point.X <= Math.Max(edge.A.X, edge.B.X) && point.Y >= Math.Min(edge.A.Y, edge.B.Y) &&
            point.Y <= Math.Max(edge.A.Y, edge.B.Y);
    }
}
