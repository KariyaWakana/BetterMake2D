using System;

namespace BetterMake2D;

// Local, read-only XY index for the integer arrangement. It never enters Rhino.
internal sealed class V2ManagedBounds
{
    private readonly Rect[] bounds;
    private readonly int[] order;
    private readonly Node[] nodes;
    private int used;

    private readonly record struct Node(Rect Box, int First, int Count, int Left, int Right);

    internal V2ManagedBounds(int count, Func<int, Rect> read)
    {
        bounds = new Rect[count];
        order = new int[count];
        nodes = new Node[Math.Max(1, count / 2 + 1)];
        for (int i = 0; i < count; i++)
        {
            bounds[i] = read(i);
            order[i] = i;
        }
        if (count > 0) Build(0, count);
    }

    internal void Search(Rect box, Action<int> visit)
    {
        if (used != 0) Search(0, box, visit);
    }

    private void Search(int index, Rect query, Action<int> visit)
    {
        var node = nodes[index];
        if (!Overlaps(node.Box, query)) return;
        if (node.Count == 0)
        {
            Search(node.Left, query, visit);
            Search(node.Right, query, visit);
            return;
        }
        for (int i = node.First; i < node.First + node.Count; i++)
        {
            int id = order[i];
            if (Overlaps(bounds[id], query)) visit(id);
        }
    }

    private int Build(int first, int count)
    {
        int index = used++;
        var box = bounds[order[first]];
        double x0 = Center(box.Left, box.Right), x1 = x0;
        double y0 = Center(box.Bottom, box.Top), y1 = y0;
        int end = first + count;
        for (int i = first + 1; i < end; i++)
        {
            var b = bounds[order[i]];
            box = new Rect(Math.Min(box.Left, b.Left), Math.Min(box.Bottom, b.Bottom),
                Math.Max(box.Right, b.Right), Math.Max(box.Top, b.Top));
            double x = Center(b.Left, b.Right), y = Center(b.Bottom, b.Top);
            x0 = Math.Min(x0, x); x1 = Math.Max(x1, x);
            y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
        }
        if (count <= 12)
        {
            nodes[index] = new Node(box, first, count, 0, 0);
            return index;
        }
        bool alongX = x1 - x0 >= y1 - y0;
        double split = alongX ? Center(x0, x1) : Center(y0, y1);
        int middle = first;
        for (int i = first; i < end; i++)
        {
            var b = bounds[order[i]];
            if ((alongX ? Center(b.Left, b.Right) : Center(b.Bottom, b.Top)) >= split) continue;
            (order[middle], order[i]) = (order[i], order[middle]);
            middle++;
        }
        // A midpoint partition can become badly imbalanced on sparse outliers.
        int minimum = Math.Max(4, count / 8);
        if (middle - first < minimum || end - middle < minimum)
            middle = first + count / 2;
        int left = Build(first, middle - first), right = Build(middle, end - middle);
        nodes[index] = new Node(box, 0, 0, left, right);
        return index;
    }

    private static double Center(double a, double b) => a + (b - a) * .5;

    private static bool Overlaps(Rect a, Rect b) => a.Left <= b.Right && b.Left <= a.Right &&
        a.Bottom <= b.Top && b.Bottom <= a.Top;
}
