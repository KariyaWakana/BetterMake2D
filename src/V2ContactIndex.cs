using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace BetterMake2D;

internal sealed class V2ContactIndex
{
    private readonly record struct Node(BoundingBox Box, int First, int Count, int Left, int Right, int MaxId);
    private readonly V2Face[] faces;
    private readonly int[] order;
    private readonly List<Node> nodes = new();

    internal V2ContactIndex(V2Face[] faces, int[] members)
    {
        this.faces = faces;
        order = (int[])members.Clone();
        if (order.Length != 0) Build(0, order.Length);
    }

    internal void Search(int owner, List<int> result)
    {
        result.Clear();
        if (nodes.Count != 0) Search(0, owner, faces[owner].Box, result);
    }

    private void Search(int id, int owner, BoundingBox box, List<int> result)
    {
        var node = nodes[id];
        if (node.MaxId <= owner || !Overlap(node.Box, box)) return;
        if (node.Count == 0)
        {
            Search(node.Left, owner, box, result);
            Search(node.Right, owner, box, result);
            return;
        }
        for (int i = node.First; i < node.First + node.Count; i++)
        {
            int candidate = order[i];
            if (candidate > owner && Overlap(faces[candidate].Box, box)) result.Add(candidate);
        }
    }

    private int Build(int first, int count)
    {
        int node = nodes.Count;
        nodes.Add(default);
        var box = BoundingBox.Empty;
        var centers = BoundingBox.Empty;
        int max = -1;
        int end = first + count;
        for (int i = first; i < end; i++)
        {
            var item = faces[order[i]].Box;
            box.Union(item);
            centers.Union(item.Center);
            max = Math.Max(max, order[i]);
        }
        if (count <= 16)
        {
            nodes[node] = new Node(box, first, count, 0, 0, max);
            return node;
        }
        var span = centers.Diagonal;
        int axis = span.X >= span.Y ? span.X >= span.Z ? 0 : 2 : span.Y >= span.Z ? 1 : 2;
        double middle = centers.Center[axis];
        int pivot = first;
        for (int i = first; i < end; i++)
        {
            if (faces[order[i]].Box.Center[axis] >= middle) continue;
            (order[i], order[pivot]) = (order[pivot], order[i]);
            pivot++;
        }
        int minimum = Math.Max(4, count / 8);
        if (pivot - first < minimum || end - pivot < minimum) pivot = first + count / 2;
        int left = Build(first, pivot - first), right = Build(pivot, end - pivot);
        nodes[node] = new Node(box, 0, 0, left, right, max);
        return node;
    }

    private static bool Overlap(BoundingBox a, BoundingBox b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X &&
        a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y &&
        a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
}
