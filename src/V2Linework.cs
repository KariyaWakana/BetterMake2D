using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;

namespace BetterMake2D;

internal static class V2Linework
{
    internal static void Join(List<DrawnEdge> edges, Func<string, string> layerOf, Action cancel)
    {
        var vertices = new Dictionary<Point3d, List<int>>();
        var peer = new int[edges.Count * 2];
        Array.Fill(peer, -1);
        var styles = edges.Select(edge => (Layer: layerOf?.Invoke(edge.Source) ?? edge.Source,
            Boundary: edge.IsHidden ? "Hidden" : edge.Boundary, edge.IsHidden)).ToArray();
        for (int i = 0; i < edges.Count; i++)
        {
            if (edges[i].Curve.IsClosed) continue;
            Add(edges[i].Curve.PointAtStart, i * 2);
            Add(edges[i].Curve.PointAtEnd, i * 2 + 1);
        }
        foreach (var ends in vertices.Values)
        {
            if (ends.Count < 2) continue;
            if (ends.Count == 2)
            {
                if (styles[ends[0] / 2] == styles[ends[1] / 2]) Pair(ends[0], ends[1]);
                continue;
            }
            var pairs = new List<(int A, int B, int Source, double Turn)>();
            for (int i = 0; i < ends.Count; i++)
                for (int j = i + 1; j < ends.Count; j++)
                {
                    int a = ends[i], b = ends[j];
                    if (styles[a / 2] != styles[b / 2]) continue;
                    int source = Continuity(edges[a / 2], edges[b / 2]);
                    double turn = -(Tangent(a) * Tangent(b));
                    if (source == 0 && turn < .9999) continue;
                    pairs.Add((a, b, source, turn));
                }
            foreach (var pair in pairs.OrderByDescending(pair => pair.Source).ThenByDescending(pair => pair.Turn)
                .ThenBy(pair => pair.A).ThenBy(pair => pair.B))
                if (peer[pair.A] < 0 && peer[pair.B] < 0) Pair(pair.A, pair.B);
        }

        var used = new bool[edges.Count];
        var joined = new List<DrawnEdge>();
        var retired = new List<int>();
        var created = new List<Curve>();
        try
        {
            for (int i = 0; i < peer.Length; i++)
                if (peer[i] < 0) Walk(i);
            for (int i = 0; i < edges.Count; i++)
                Walk(i * 2);
        }
        catch
        {
            foreach (var curve in created) curve.Dispose();
            throw;
        }
        foreach (int index in retired) edges[index].Curve.Dispose();
        edges.Clear();
        edges.AddRange(joined);

        void Walk(int start)
        {
            if (used[start / 2]) return;
            cancel?.Invoke();
            var route = new List<int>();
            int next = start;
            while (next >= 0 && !used[next / 2])
            {
                used[next / 2] = true;
                route.Add(next);
                next = peer[next ^ 1];
            }
            if (route.Count == 1)
            {
                joined.Add(edges[start / 2]);
                return;
            }
            var path = new PolyCurve();
            bool valid = true;
            foreach (int end in route)
            {
                using var part = edges[end / 2].Curve.DuplicateCurve();
                if ((end & 1) != 0 && !part.Reverse() ||
                    path.SegmentCount != 0 && path.PointAtEnd != part.PointAtStart ||
                    !path.AppendSegment(part))
                {
                    valid = false;
                    break;
                }
            }
            if (!valid || !path.IsValid || next == start && !path.IsClosed)
            {
                path.Dispose();
                foreach (int end in route) joined.Add(edges[end / 2]);
                return;
            }
            created.Add(path);
            var members = route.Select(end => edges[end / 2]).ToArray();
            var sources = members.Select(edge => new SourceFaceId(edge.Source, edge.FaceIndex)).Distinct().ToArray();
            joined.Add(members[0] with
            {
                Curve = path,
                JoinedSources = sources.Length > 1 ? string.Join(",", sources.Select(id => $"{id.Source}/{id.FaceIndex}")) : null
            });
            retired.AddRange(route.Select(end => end / 2));
        }

        void Add(Point3d point, int end)
        {
            if (!vertices.TryGetValue(point, out var ends)) vertices.Add(point, ends = new List<int>(2));
            ends.Add(end);
        }

        void Pair(int a, int b) => (peer[a], peer[b]) = (b, a);

        Vector3d Tangent(int end)
        {
            var curve = edges[end / 2].Curve;
            var tangent = (end & 1) == 0 ? curve.TangentAtStart : -curve.TangentAtEnd;
            return tangent.Unitize() ? tangent : Vector3d.Zero;
        }
    }

    private static int Continuity(DrawnEdge a, DrawnEdge b)
    {
        if (a.Source == b.Source && a.FaceIndex == b.FaceIndex) return 4;
        if (a.Source == b.Source) return 3;
        if (a.Source == b.Occluder || b.Source == a.Occluder ||
            a.Occluder.Length > 0 && a.Occluder == b.Occluder) return 2;
        int slash = a.Source.LastIndexOf('/');
        return slash > 0 && slash == b.Source.LastIndexOf('/') &&
            a.Source.AsSpan(0, slash).SequenceEqual(b.Source.AsSpan(0, slash)) ? 1 : 0;
    }
}
