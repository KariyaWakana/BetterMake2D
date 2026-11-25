using System;
using Eto.Drawing;
using Eto.Forms;

namespace BetterMake2D;

internal readonly record struct DesktopPreviewOptions(bool Lines, bool Tangent, bool Section,
    bool Hidden, bool BackFaces, bool Fills, DesktopFill Fill);

internal sealed class DesktopDrawingPreview : Drawable
{
    private const int SampleWidth = 180;
    private const int SampleHeight = 130;
    private DesktopPreviewOptions options;

    internal DesktopDrawingPreview()
    {
        Size = new Size(SampleWidth, SampleHeight);
        MinimumSize = Size;
        Paint += (_, args) => DrawSample(args.Graphics, Size, options);
    }

    internal void SetOptions(DesktopPreviewOptions value)
    {
        if (value == options) return;
        options = value;
        Invalidate();
    }

    internal static void DrawSample(Graphics graphics, Size size, DesktopPreviewOptions options)
    {
        var sheet = Colors.White;
        var ink = Color.FromArgb(24, 24, 24);
        var tangentInk = Color.FromArgb(78, 78, 78);
        var hiddenInk = Color.FromArgb(117, 117, 117);
        var roofColor = Color.FromArgb(247, 247, 247);
        var frontColor = Color.FromArgb(235, 235, 235);
        var curveColor = Color.FromArgb(241, 241, 241);
        var sideColor = Color.FromArgb(223, 223, 223);
        graphics.AntiAlias = true;
        graphics.FillRectangle(sheet, new RectangleF(0, 0, size.Width, size.Height));
        graphics.DrawRectangle(Color.FromArgb(168, 168, 168), new RectangleF(.5f, .5f, size.Width - 1, size.Height - 1));

        float scale = Math.Min(size.Width / (float)SampleWidth, size.Height / (float)SampleHeight);
        graphics.SaveTransform();
        try
        {
            graphics.TranslateTransform((size.Width - SampleWidth * scale) / 2 - 11 * scale,
                (size.Height - SampleHeight * scale) / 2 + 13 * scale);
            graphics.ScaleTransform(scale);
            using var outline = new Pen(ink, 1.25f) { LineJoin = PenLineJoin.Round };
            using var tangent = new Pen(tangentInk, .75f);
            using var hidden = new Pen(hiddenInk, 1f) { DashStyle = DashStyles.Dash };
            using var back = new Pen(hiddenInk, 1f) { DashStyle = DashStyles.Dot };
            using var section = new Pen(ink, 2.4f) { LineJoin = PenLineJoin.Round };

            const float rightX = 154;
            const float columnFarX = rightX + 16;
            using var columnTop = Shape((102, 2), (118, -9), (columnFarX, 4), (rightX, 15));
            using var columnFront = Shape((102, 2), (rightX, 15), (rightX, 102), (102, 89));
            using var columnSide = Shape((rightX, 15), (columnFarX, 4), (columnFarX, 91), (rightX, 102));
            Face(columnTop, roofColor, options.Lines);
            Face(columnFront, frontColor, options.Lines);
            Face(columnSide, sideColor, options.Lines);

            var a = new PointF(37, 59);
            var b = new PointF(90, 72);
            var c = new PointF(121, 64);
            var d = new PointF(rightX, 44);
            var ab = new PointF(37, 95);
            var bb = new PointF(90, 108);
            var cb = new PointF(121, 100);
            var db = new PointF(rightX, 80);
            var arcStart = new PointF(103, 76);
            var arcEnd = new PointF(113, 69);
            var baseArcStart = new PointF(103, 112);
            var baseArcEnd = new PointF(113, 105);
            var roofBack = new PointF(87, 28);

            using var roof = new GraphicsPath();
            roof.AddLines(new[] { d, roofBack, a, b });
            roof.AddBezier(b, arcStart, arcEnd, c);
            roof.AddLine(c.X, c.Y, d.X, d.Y);
            roof.CloseFigure();
            using var wall = Shape((a.X, a.Y), (b.X, b.Y), (bb.X, bb.Y), (ab.X, ab.Y));
            using var flank = new GraphicsPath();
            flank.AddBezier(b, arcStart, arcEnd, c);
            flank.AddLine(c.X, c.Y, cb.X, cb.Y);
            flank.AddBezier(cb, baseArcEnd, baseArcStart, bb);
            flank.AddLine(bb.X, bb.Y, b.X, b.Y);
            flank.CloseFigure();
            using var side = Shape((c.X, c.Y), (d.X, d.Y), (db.X, db.Y), (cb.X, cb.Y));

            // The foreground silhouette hides the column even in the line-only sample.
            using var silhouette = new GraphicsPath();
            silhouette.AddLines(new[] { d, roofBack, a, ab, bb });
            silhouette.AddBezier(bb, baseArcStart, baseArcEnd, cb);
            silhouette.AddLine(cb.X, cb.Y, db.X, db.Y);
            silhouette.CloseFigure();
            graphics.FillPath(sheet, silhouette);

            Face(roof, roofColor);
            Face(wall, frontColor);
            Face(flank, curveColor);
            Face(side, sideColor);

            if (options.Hidden)
            {
                graphics.DrawLines(hidden, new[] { new PointF(102, 31.6f), new PointF(102, 89), new PointF(128.3f, 95.6f) });
                if (options.BackFaces)
                    graphics.DrawLines(back, new[] { new PointF(87, 31), new PointF(87, 65), new PointF(49, 84) });
            }
            if (options.Lines)
            {
                graphics.DrawPath(outline, roof);
                graphics.DrawLine(outline, a, ab);
                graphics.DrawLine(outline, d, db);
                using var plinth = new GraphicsPath();
                plinth.AddLine(ab.X, ab.Y, bb.X, bb.Y);
                plinth.AddBezier(bb, baseArcStart, baseArcEnd, cb);
                plinth.AddLine(cb.X, cb.Y, db.X, db.Y);
                graphics.DrawPath(outline, plinth);
            }

            if (options.Tangent)
            {
                graphics.DrawLine(tangent, b, bb);
                graphics.DrawLine(tangent, c, cb);
            }
            if (options.Section)
            {
                using var sectionFace = Shape((27, 95), (54, 73), (70, 77), (70, 96), (50, 111), (27, 103));
                Face(sectionFace, sideColor);
                graphics.DrawPath(section, sectionFace);
            }

            void Face(GraphicsPath path, Color color, bool stroke = false)
            {
                if (options.Fills)
                {
                    graphics.FillPath(options.Fill == DesktopFill.Brep ? Alpha(color, .55f) : color, path);
                    if (options.Fill == DesktopFill.Both)
                        graphics.DrawPath(Color.FromArgb(184, 184, 184), path);
                }
                if (stroke) graphics.DrawPath(outline, path);
            }
        }
        finally { graphics.RestoreTransform(); }
    }

    private static Color Alpha(Color color, float opacity) => new(color.R, color.G, color.B, opacity);

    private static GraphicsPath Shape(params (float X, float Y)[] vertices)
    {
        var path = new GraphicsPath();
        path.MoveTo(vertices[0].X, vertices[0].Y);
        for (int i = 1; i < vertices.Length; i++) path.LineTo(vertices[i].X, vertices[i].Y);
        path.CloseFigure();
        return path;
    }
}
