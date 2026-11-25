using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace BetterMake2D;

// Reads retained-face checkpoints as V2 benchmark inputs.
internal static class OriginalFaceStages
{
    private const string FrameKey = "BetterMake2D.ParallelView";
    internal static T WithSavedFaces<T>(string path,
        Func<File3dm, List<Input>, Dictionary<string, int[]>, Interval, Transform, T> consume)
    {
        using var model = Open(path);
        string supportPath = OriginalFaceCheckpoint.SupportPath(path);
        using var supports = File.Exists(supportPath) ? Open(supportPath) : null;
        var owned = new List<Brep>();
        try
        {
            var toView = ViewTransform(SelectView(model, null));
            var plan = OriginalFaceCheckpoint.Read(model, supports, toView, owned);
            return consume(model, plan.Inputs, plan.Faces, plan.Depth, toView);
        }
        finally { foreach (var brep in owned) brep.Dispose(); }
    }

    private static File3dm Open(string path) => File3dm.Read(path) ?? throw new IOException($"Cannot read {path}.");

    internal static ViewInfo SelectView(File3dm model, string requested)
    {
        string name = requested ?? model.Strings.GetValue(FrameKey);
        var views = model.AllViews.Where(view => view != null).ToArray();
        var chosen = string.IsNullOrEmpty(name) ? views.SingleOrDefault() :
            views.SingleOrDefault(view => view.Name == name) ??
            model.AllNamedViews.SingleOrDefault(view => view?.Name == name);
        using var viewport = chosen?.Viewport;
        if (viewport == null || !viewport.IsParallelProjection)
            throw new InvalidDataException("Select a saved parallel model view.");
        return chosen;
    }

    private static Transform ViewTransform(ViewInfo info)
    {
        using var view = info.Viewport;
        var plane = new Plane(Point3d.Origin, view.CameraX, view.CameraY);
        if (!plane.IsValid)
            throw new InvalidDataException("The saved parallel view axes are invalid.");
        return Transform.PlaneToPlane(plane, Plane.WorldXY);
    }
}
