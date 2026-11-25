from pathlib import Path
from math import ceil
from System import Array
from System.Drawing import Bitmap, Brushes, Color, Font, Graphics, Pen, PointF, RectangleF, SolidBrush
from System.Drawing.Drawing2D import FillMode, GraphicsPath, SmoothingMode
from Rhino.FileIO import File3dm
from Rhino.Geometry import AreaMassProperties, Brep, Curve, Interval, Plane
from Rhino.Geometry.Intersect import Intersection


def compare_and_render(root):
    folder = root / "BetterMake2D" / "examples" / "实模_20261116"
    ref_path = root / "output" / "近岸总平_v13_20261111" / "Rhino_顶视闭合分区与线稿_v13.3dm"
    target_path = folder / "总平隔离模型_顶视_实验.3dm"
    old = File3dm.Read(str(ref_path))
    new = File3dm.Read(str(target_path))
    try:
        expected = [(item.Attributes.Name, item.Geometry) for item in old.Objects
                    if item.Attributes.Name in ("platform edge", "service seam 5", "service seam 21", "R35000 exposed")]
        lines = [item.Geometry for item in new.Objects if isinstance(item.Geometry, Curve)]
        fills = [item for item in new.Objects
                 if new.AllLayers.FindIndex(item.Attributes.LayerIndex).Name == "Closed 2D fill regions"]
        platform = [item.Geometry for item in fills
                    if (item.Attributes.GetUserString("SourceObject") or "").startswith("平台 |")
                    and item.Attributes.GetUserString("SourceFace") == "8"]
        area = sum(AreaMassProperties.Compute(shape).Area for shape in platform) / 1e6
        report = ["Acceptance: v13 architectural geometry; no site terrain or graphic styling.",
                  f"Landing area: {area:.8f} m²; reference 15.26974100 m².",
                  f"Editable curves: {len(lines)}; planar fill regions: {len(fills)}."]
        for label, curve in expected:
            spans = []
            for result in lines:
                hits = Intersection.CurveCurve(curve, result, 0.05, 0.05)
                if hits:
                    for hit in hits:
                        if hit.IsOverlap:
                            spans.append((hit.OverlapA.Min, hit.OverlapA.Max))
                    hits.Dispose()
            intervals = []
            for lo, hi in sorted(spans):
                if intervals and intervals[-1][1] >= lo:
                    intervals[-1] = (intervals[-1][0], max(intervals[-1][1], hi))
                else:
                    intervals.append((lo, hi))
            length = sum(curve.GetLength(Interval(lo, hi)) for lo, hi in intervals if hi > lo)
            report.append(f"{label}: matching {length:.4f} mm / {curve.GetLength():.4f} mm.")
        radii = [arc.Radius for curve in lines for ok, arc in [curve.TryGetArc(0.05)]
                 if ok and abs(arc.Radius - 35000) < 0.05]
        report.append(f"Native arc radius R35000: {len(radii)} segments verified.")
        (folder / "总平_v13_几何逐条核验.txt").write_text("\n".join(report) + "\n", encoding="utf-8")
        print("\n".join(report))

        bitmap = Bitmap(1520, 1020)
        graphic = Graphics.FromImage(bitmap)
        try:
            graphic.SmoothingMode = SmoothingMode.AntiAlias
            graphic.Clear(Color.FromArgb(247, 247, 244))
            ink = Color.FromArgb(37, 48, 54)
            line = Pen(ink, 1.5)
            mark = Pen(Color.FromArgb(192, 93, 52), 2.1)
            panel = SolidBrush(Color.White)
            blue = SolidBrush(Color.FromArgb(191, 214, 218))
            water = SolidBrush(Color.FromArgb(135, 182, 189))
            accent = SolidBrush(Color.FromArgb(218, 171, 140))
            title = Font("Microsoft YaHei", 20.0)
            label = Font("Microsoft YaHei", 12.0)
            note = Font("Microsoft YaHei", 10.0)
            graphic.DrawString("BetterMake2D ｜ 近岸总平几何核验", title, Brushes.Black, 35., 24.)
            graphic.DrawString("毫米模型 · 正投影 · 归档 v13 同坐标 · 橙线为复核对象", note, Brushes.DimGray, 37., 64.)
            boxes = [RectangleF(37, 135, 704, 450), RectangleF(778, 135, 704, 450),
                     RectangleF(37, 665, 704, 285), RectangleF(778, 665, 704, 285)]
            names = ["归档 v13 ｜ 建筑线稿", "新算法 ｜ 可编辑线稿与闭合面",
                     "环侧接驳平台 ｜ 15.27 m²", "服务体块 ｜ 两道横边与 R35 原弧"]
            views = [(-64000, -54000, 53500, 56500)] * 2 + [
                (17700, -44400, 30900, -35200), (-25500, 12500, 26500, 48700)]
            old_lines = [item.Geometry for item in old.Objects if isinstance(item.Geometry, Curve) and
                         not (item.Attributes.Name or "").startswith("platform fill")]
            for index, box in enumerate(boxes):
                graphic.DrawString(names[index], label, Brushes.Black, box.X, box.Y - 31.)
                graphic.FillRectangle(panel, box)
                graphic.SetClip(box)
                x0, y0, x1, y1 = views[index]
                scale = min((box.Width - 24) / (x1 - x0), (box.Height - 24) / (y1 - y0))
                xoff = box.X + (box.Width - (x1 - x0) * scale) / 2
                yoff = box.Y + (box.Height - (y1 - y0) * scale) / 2

                def points(curve):
                    count = min(1200, max(3, int(ceil(curve.GetLength() * scale / 1.8))))
                    if curve.IsLinear(0.01):
                        xyz = (curve.PointAtStart, curve.PointAtEnd)
                    else:
                        values = curve.DivideByCount(count, True)
                        xyz = (curve.PointAt(t) for t in values) if values else (curve.PointAtStart, curve.PointAtEnd)
                    return Array[PointF]([PointF(xoff + (p.X - x0) * scale, yoff + (y1 - p.Y) * scale) for p in xyz])

                if index != 0:
                    for obj in fills:
                        path = GraphicsPath(FillMode.Alternate)
                        for loop in obj.Geometry.Loops:
                            boundary = loop.To3dCurve()
                            pts = points(boundary)
                            if len(pts) >= 3:
                                path.AddPolygon(pts)
                            boundary.Dispose()
                        tag = obj.Attributes.GetUserString("SourceObject") or ""
                        brush = accent if tag.startswith("平台 |") else water if "潮间环" in tag else blue
                        graphic.FillPath(brush, path)
                        path.Dispose()
                for curve in old_lines if index == 0 else lines:
                    graphic.DrawLines(line, points(curve))
                if index in (0, 2, 3):
                    for name, curve in expected:
                        if index == 0 or (name == "platform edge") == (index == 2):
                            graphic.DrawLines(mark, points(curve))
                graphic.ResetClip()
            graphic.DrawString("闭合面来自裁切后的三维 Brep；这里只显示建筑隔离模型。", note,
                               Brushes.DimGray, 37., 974.)
            bitmap.Save(str(folder / "总平_v13_前后对照.png"))
        finally:
            graphic.Dispose()
            bitmap.Dispose()
    finally:
        old.Dispose()
        new.Dispose()
