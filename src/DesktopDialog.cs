using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.ApplicationSettings;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace BetterMake2D;

internal sealed class DesktopLanguage(bool chinese)
{
    internal static DesktopLanguage Current()
    {
        int language = AppearanceSettings.LanguageIdentifier;
        return new DesktopLanguage((language & 0x3ff) == 4);
    }
    internal string Text(string zh, string en) => chinese ? zh : en;
}

internal sealed class DesktopDialog : Dialog
{
    private readonly RhinoDoc doc;
    private readonly DesktopLanguage language;
    private RhinoObject[] visible;
    private readonly DesktopOptions options;
    private readonly Label estimate = new() { Wrap = WrapMode.Word };
    private readonly Label selection = new();
    private readonly Label layerLabel = new() { Wrap = WrapMode.Word };
    private readonly Label error = new() { Wrap = WrapMode.Word };
    private readonly Label picked = new() { Wrap = WrapMode.Word };
    private readonly Dictionary<Guid, int> faceCounts = new();
    private readonly RadioButtonList scope, position, fill;
    private readonly DropDown viewPicker;
    private readonly DesktopViewChoice[] viewChoices;
    private readonly Action refresh;
    private Action refreshPaper;
    private DesktopViewChoice acceptedView;
    private readonly CheckBox occlusion, crop, clipping, lines, tangent, sections, hidden, backs, fills, insert, select, group, export, model, svg, ai, hiz;
    private readonly TextBox parent, folder, prefix, scale;
    internal RhinoObject[] Selected { get; private set; }
    internal DesktopOptions Options => options;
    internal DesktopViewChoice ChosenView => acceptedView ?? viewChoices[Math.Max(0, viewPicker.SelectedIndex)];
    internal Point3d LocationPicked { get; private set; } = Point3d.Unset;
    internal bool Accepted { get; private set; }

    internal DesktopDialog(RhinoDoc doc, RhinoViewport view, DesktopOptions options, DesktopLanguage language)
    {
        this.doc = doc;
        this.options = options;
        this.language = language;
        Selected = DesktopData.Selected(doc);
        Title = "BetterMake2D";
        MinimumSize = new Size(535, 490);
        ClientSize = new Size(610, 650);
        Resizable = true;
        this.UseRhinoStyle();

        string stamp = DateTime.Now.ToString("MMddHHmm");
        string Stem(string name) => "BetterMake2D_" + SafeName(name) + "_" + stamp;
        string stem = Stem(view.Name);
        viewChoices = new[] { view }.Concat(doc.Views.GetViewList(ViewTypeFilter.Model | ViewTypeFilter.Page)
                .Select(item => item.ActiveViewport).Where(item => item != null)).DistinctBy(item => item.Id)
            .Select(item => new DesktopViewChoice(item.Name, item))
            .Concat(doc.NamedViews.Where(item => item != null).Select(item => new DesktopViewChoice(item.Name, view, item)))
            .ToArray();
        viewPicker = new DropDown
        {
            DataStore = viewChoices.Select((choice, index) =>
            {
                using var camera = choice.ReadCamera();
                string projection = ProjectionName(camera.IsParallelProjection);
                string status = index == 0 ? T(" · 当前", ", active") : choice.Saved != null ? T(" · 命名视图", ", named view") : "";
                return T($"{choice.Name}（{projection}{status}）", $"{choice.Name} ({projection}{status})");
            }).ToArray(),
            SelectedIndex = 0
        };
        var clipHint = new Label { Wrap = WrapMode.Word, TextColor = SystemColors.DisabledText };
        int planeCount = 0;
        scope = Radios(options.All || Selected.Length == 0 ? 1 : 0,
            T("仅所选物件", "Selected objects only"), T("场景中全部物件", "All objects in scene"));
        scope.Orientation = Orientation.Horizontal;
        scope.ToolTip = T("不含已隐藏及关闭图层上的物件。", "Excludes hidden objects and objects on off layers.");
        occlusion = Check(T("计算未选物件的遮挡", "Include occlusion from unselected objects"), options.AutoOcclusion);
        occlusion.ToolTip = T("仅输出所选物件，但计算时仍会被场景中其他未隐藏物件挡住。",
            "Outputs only selected objects while allowing other visible objects to occlude them.");
        crop = Check(T("仅保留工作视窗画幅内", "Keep only inside viewport frame"), false);
        bool settingDefaultFrame = false;
        crop.ToolTip = T("只保留当前工作视窗矩形框内的画面，自动裁掉框外多余几何。",
            "Keeps the view inside the rectangular viewport frame and crops geometry outside it.");
        clipping = Check(T("应用截平面", "Apply clipping planes"), options.Clip);
        var pickObjects = Button(T("重新选择…", "Reselect…"), () =>
            this.PushPickButton((_, _) => PickObjects()));
        var selectionRow = new TableLayout
        {
            Spacing = new Size(8, 0),
            Rows = { new TableRow(new TableCell(selection), new TableCell(pickObjects), new TableCell(null, true)) }
        };
        var accuracy = Radios(options.Accuracy == V2Accuracy.Fast ? 1 : 0,
            T("精确（NURBS）", "Exact (NURBS)"), T("快速（精细网格，超大场景推荐）", "Fast (Fine mesh, for large scenes)"));
        accuracy.Orientation = Orientation.Horizontal;
        var objects = Section(T("视图与物件", "View & Objects"),
            Row(new Label { Text = T("视图", "View") }, viewPicker),
            Row(new Label { Text = T("物件", "Objects") }, scope), selectionRow, occlusion,
            Row(crop, clipping), clipHint, estimate, Row(new Label { Text = T("求解精度", "Accuracy") }, accuracy));

        lines = Check(T("可见线", "Visible lines"), options.Lines);
        tangent = Check(T("正切边缘", "Tangent edges"), options.Tangent);
        sections = Check(T("截平面交线", "Clipping plane intersections"), options.Cuts);
        hidden = Check(T("隐藏线", "Hidden lines"), options.Hidden);
        backs = Check(T("包含物件自身背面", "Include objects’ own back-facing edges"), options.BackFaces);
        backs.ToolTip = T("关闭时仅画出被其他物件挡住的线；勾选后连物件自身背朝镜头的背面棱线也一并画出。",
            "When off, draws only lines occluded by other objects. When on, also draws each object’s own back-facing edges.");
        var chooseLayers = Button(T("图层筛选…", "Filter layers…"), PickLayers);
        var hiddenSettings = Compact(backs, Row(chooseLayers, layerLabel));
        fills = Check(T("生成闭合填色", "Create closed fills"), options.Fills);
        fill = Radios(0, T("纯色填充 (Hatch)", "Solid fill (Hatch)"), T("平面曲面", "Planar surface"), T("两者", "Both"));
        fill.Orientation = Orientation.Horizontal;
        fill.SelectedIndex = Enum.IsDefined(options.Fill) ? (int)options.Fill : (int)DesktopFill.Hatch;
        var preview = new DesktopDrawingPreview();
        var sketch = new TableLayout { Rows = { new TableRow(preview), new TableRow { ScaleHeight = true } } };
        var lineOptions = Compact(Row(lines, tangent), Row(sections, hidden), hiddenSettings, fills, fill);
        var content = Section(T("线条与填色", "Lines & Fills"), new TableLayout
        {
            Spacing = new Size(12, 0),
            Rows = { new TableRow(new TableCell(lineOptions, true), new TableCell(sketch)) }
        });

        insert = Check(T("放入当前文件", "Place in current file"), options.Insert || !options.Export);
        parent = new TextBox { Text = string.IsNullOrWhiteSpace(options.Parent) ? stem : options.Parent };
        position = Radios((int)options.Placement, T("世界原点", "World origin"),
            T("空白区域", "Open area"), T("手动点选", "Pick manually"));
        position.Orientation = Orientation.Horizontal;
        var pickPoint = Button(T("指定插入点…", "Pick point…"), () =>
            this.PushPickButton((_, _) =>
            {
                if (RhinoGet.GetPoint(T("指定二维图面左下角", "Pick the lower-left insertion point"),
                    false, out var point) == Rhino.Commands.Result.Success)
                {
                    LocationPicked = point;
                    picked.Text = string.Format(CultureInfo.CurrentCulture, "{0:0.##}, {1:0.##}, {2:0.##}",
                        point.X, point.Y, point.Z);
                    refresh?.Invoke();
                }
            }));
        var placementControls = Row(pickPoint, picked);
        select = Check(T("完成后选中", "Select when complete"), options.Select);
        group = Check(T("群组输出", "Group output"), options.Group);
        var docSettings = Compact(Row(new Label { Text = T("图层名称", "Layer name") }, parent),
            Row(new Label { Text = T("放置位置", "Placement") }, position), placementControls, Row(group, select));
        docSettings.Padding = new Padding(22, 0, 0, 0);

        export = Check(T("另存为独立文件", "Save as separate files"), options.Export);
        model = Check(".3dm", options.Model);
        svg = Check(".svg", options.Svg);
        ai = Check(".ai", options.Ai);
        folder = new TextBox { Text = options.Folder };
        var browse = Button(T("浏览…", "Browse…"), () =>
        {
            using var dialog = new SelectFolderDialog { Title = T("选择输出文件夹", "Choose output folder") };
            if (Directory.Exists(folder.Text)) dialog.Directory = folder.Text;
            if (dialog.ShowDialog(this) == DialogResult.Ok) folder.Text = dialog.Directory;
        });
        prefix = new TextBox { Text = string.IsNullOrWhiteSpace(options.Prefix) ? stem : options.Prefix };
        scale = new TextBox { Text = options.Scale.ToString("G", CultureInfo.CurrentCulture), Width = 100 };
        var presets = new DropDown { DataStore = new[] { T("常用比例", "Presets"), "1:100", "1:200", "1:500", "1:1000" },
            SelectedIndex = 0 };
        presets.SelectedIndexChanged += (_, _) =>
        {
            if (presets.SelectedIndex > 0) scale.Text = new[] { "", "100", "200", "500", "1000" }[presets.SelectedIndex];
        };
        var paperSize = new Label { Wrap = WrapMode.Word };
        var paperWarning = new Label { Wrap = WrapMode.Word };
        var allowLarge = Check(T("仍按原比例导出（特大画布）", "Keep the original scale (large canvas)"), false);
        PaperExtent? estimatedPaper = null;
        PaperBand paperBand = PaperBand.Normal;
        double recommended = 0;
        var recommend = Button("", () =>
        {
            if (recommended > 0) scale.Text = recommended.ToString("G8", CultureInfo.CurrentCulture);
        });
        var paperPanel = Compact(paperSize, paperWarning,
            new TableLayout { Rows = { new TableRow(new TableCell(recommend), new TableCell(null, true)) } }, allowLarge);
        var fileSettings = Compact(Row(model, svg, ai), Row(new Label { Text = T("目录", "Folder") }, folder, browse),
            Row(new Label { Text = T("文件名前缀", "File name prefix") }, prefix),
            Row(new Label { Text = T("比例 1 :", "Scale 1 :") }, scale, presets), paperPanel);
        fileSettings.Padding = new Padding(22, 0, 0, 0);
        var output = Section(T("输出设置", "Output"), insert, docSettings, export, fileSettings);

        hiz = Check(T("保守深度预筛", "Conservative depth prefilter"), options.HiZ);
        var advanced = new Expander
        {
            Header = new Label { Text = T("高级选项", "Advanced options") },
            Content = Stack(null, hiz)
        };
        var cancel = Button(T("取消", "Cancel"), Close);
        var start = new Button { Text = T("开始生成", "Generate") };
        start.Click += (_, _) => Validate();
        DefaultButton = start;
        AbortButton = cancel;
        var body = new TableLayout { Spacing = new Size(0, 5) };
        var groups = new Control[] { objects, content, output, advanced };
        foreach (var item in groups)
            body.Rows.Add(new TableRow(new TableCell(item, true)));
        var scroll = new Scrollable { Border = BorderType.None, ExpandContentWidth = true, ExpandContentHeight = false, Content = body };
        var footer = new TableLayout
        {
            Spacing = new Size(8, 0),
            Rows = { new TableRow(new TableCell(error, true), new TableCell(cancel), new TableCell(start)) }
        };
        var layout = new TableLayout
        {
            Padding = new Padding(12), Spacing = new Size(0, 8),
            Rows =
            {
                new TableRow(new TableCell(scroll, true)) { ScaleHeight = true },
                new TableRow(new TableCell(footer, true))
            }
        };
        Content = layout;
        scroll.SizeChanged += (_, _) => AlignFooter();
        body.SizeChanged += (_, _) => AlignFooter();
        output.SizeChanged += (_, _) => Application.Instance.AsyncInvoke(FitOutputs);
        Shown += (_, _) => Application.Instance.AsyncInvoke(FitOutputs);
        refresh = Update;
        refreshPaper = InvalidatePaper;
        scope.SelectedIndexChanged += (_, _) => { UpdateEstimate(); InvalidatePaper(); };
        viewPicker.SelectedIndexChanged += (_, _) => RefreshView();
        occlusion.CheckedChanged += (_, _) => UpdateEstimate();
        clipping.CheckedChanged += (_, _) => InvalidatePaper();
        crop.CheckedChanged += (_, _) =>
        {
            if (settingDefaultFrame) return;
            options.FramePreference = On(crop);
            InvalidatePaper();
        };
        lines.CheckedChanged += (_, _) => Update();
        hidden.CheckedChanged += (_, _) => Update();
        tangent.CheckedChanged += (_, _) => Update();
        backs.CheckedChanged += (_, _) => Update();
        fills.CheckedChanged += (_, _) => Update();
        fill.SelectedIndexChanged += (_, _) => Update();
        sections.CheckedChanged += (_, _) => Update();
        insert.CheckedChanged += (_, _) => Update();
        export.CheckedChanged += (_, _) => Update();
        svg.CheckedChanged += (_, _) => Update();
        ai.CheckedChanged += (_, _) => Update();
        scale.TextChanged += (_, _) => Update();
        position.SelectedIndexChanged += (_, _) => Update();
        RefreshView();

        void FitOutputs()
        {
            if (!Visible || groups.Any(item => item.Height <= 0)) return;
            var area = Screen.WorkingArea;
            int chrome = Height - ClientSize.Height;
            int minimum = MinimumSize.Height - chrome;
            int available = Math.Max(minimum, (int)area.Height - chrome - 24);
            int preferred = groups.Sum(item => item.Height) + (groups.Length - 1) * body.Spacing.Height +
                footer.Height + layout.Padding.Vertical + layout.Spacing.Height;
            int height = Math.Clamp(preferred, minimum, available);
            if (ClientSize.Height != height) ClientSize = new Size(ClientSize.Width, height);
            if (Location.Y + Height > area.Bottom)
                Location = new Eto.Drawing.Point(Location.X, (int)Math.Max(area.Top, area.Bottom - Height));
            AlignFooter();
        }

        void AlignFooter()
        {
            int width = scroll.VisibleRect.Width;
            if (width <= 0) return;
            int gutter = Math.Max(0, scroll.Width - width);
            if (footer.Padding.Right != gutter) footer.Padding = new Padding(0, 0, gutter, 0);
        }

        void RefreshView()
        {
            var choice = ChosenView;
            using var camera = choice.ReadCamera();
            settingDefaultFrame = true;
            crop.Checked = options.FramePreference ?? !camera.IsParallelProjection;
            settingDefaultFrame = false;
            planeCount = choice.ReadClips(doc).Length;
            clipping.Text = T($"应用截平面（当前 {planeCount} 个）", $"Apply clipping planes ({planeCount} active)");
            clipHint.Text = planeCount == 0
                ? T("未检测到活动截平面，可在视窗中用 ClippingPlane 添加", "No active clipping planes. Use ClippingPlane to add one in the viewport.")
                : T("将裁掉截平面前方遮挡并可生成截平面交线", "Clips obstructions in front of the planes and can draw their intersections.");
            viewPicker.ToolTip = choice.Saved == null ? null : T(
                "使用命名视图保存的相机和截平面。对象可见性取自打开窗口时的活动视口。",
                "Uses the named view's saved camera and clipping planes. Object visibility follows the initial viewport.");
            visible = DesktopData.Visible(doc, choice.Scope);
            string nextStem = Stem(choice.Name);
            if (parent.Text == stem) parent.Text = nextStem;
            if (prefix.Text == stem) prefix.Text = nextStem;
            stem = nextStem;
            UpdateEstimate();
            InvalidatePaper();
        }

        void Update()
        {
            selectionRow.Visible = occlusion.Visible = scope.SelectedIndex == 0;
            clipping.Enabled = planeCount > 0;
            sections.Enabled = planeCount > 0 && On(clipping);
            hiddenSettings.Visible = On(hidden);
            fill.Enabled = On(fills);
            docSettings.Visible = On(insert);
            fileSettings.Visible = On(export);
            placementControls.Visible = position.SelectedIndex == (int)DesktopPlacement.Pick;
            preview.SetOptions(new DesktopPreviewOptions(On(lines), On(tangent), sections.Enabled && On(sections), On(hidden),
                On(backs), On(fills), (DesktopFill)fill.SelectedIndex));
            UpdatePaper();
            error.Text = ReadinessReason();
            start.Enabled = error.Text.Length == 0;
        }

        string ReadinessReason()
        {
            if (scope.SelectedIndex == 0 ? Selected.Length == 0 : visible.Length == 0)
                return T("当前范围没有可出图物件。", "No drawable objects in this scope.");
            if (!On(insert) && !On(export))
                return T("请选择一种输出方式。", "Choose an output method.");
            if (!(On(lines) || On(tangent) || On(fills) || On(hidden) || sections.Enabled && On(sections)))
                return T("请选择至少一种线条或填色。", "Choose at least one line or fill type.");
            if (paperBand == PaperBand.OverLimit)
                return T("请调大比例尺分母。", "Choose a larger scale denominator.");
            return "";
        }

        bool TryScale(out double value)
        {
            string text = scale.Text.Trim().Replace('：', ':');
            if (text.StartsWith("1:", StringComparison.Ordinal)) text = text[2..];
            return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
                double.IsFinite(value) && value > 0;
        }

        void InvalidatePaper()
        {
            estimatedPaper = null;
            allowLarge.Checked = false;
            Update();
        }

        void UpdatePaper()
        {
            paperPanel.Visible = On(export) && (On(svg) || On(ai));
            paperBand = PaperBand.Normal;
            if (!paperPanel.Visible)
            {
                allowLarge.Checked = false;
                return;
            }
            bool validScale = TryScale(out double value);
            if (validScale && estimatedPaper == null)
            {
                try
                {
                    estimatedPaper = DesktopData.EstimatePaper(doc, ChosenView, scope.SelectedIndex == 1 ? visible : Selected,
                        On(crop), clipping.Enabled && On(clipping));
                }
                catch (InvalidDataException) { estimatedPaper = default(PaperExtent); }
                catch (InvalidOperationException) { estimatedPaper = default(PaperExtent); }
            }
            if (!validScale || estimatedPaper is not { IsValid: true } dimensions)
            {
                paperSize.Text = T("预计纸面尺寸暂不可用。", "Estimated paper size is unavailable.");
                paperWarning.Visible = recommend.Visible = allowLarge.Visible = false;
                allowLarge.Checked = false;
                return;
            }
            var paper = dimensions.OnPaper(value);
            paperBand = PaperSizing.Band(paper);
            paperSize.Text = T($"预计纸面尺寸：{paper.Width:F1} × {paper.Height:F1} mm",
                $"Estimated paper size: {paper.Width:F1} × {paper.Height:F1} mm");
            paperWarning.Visible = recommend.Visible = paperBand != PaperBand.Normal;
            allowLarge.Visible = paperBand == PaperBand.Large;
            if (!allowLarge.Visible) allowLarge.Checked = false;
            if (paperBand == PaperBand.Normal) return;
            recommended = PaperSizing.Recommend(dimensions, value);
            string number = PaperSizing.ScaleText(recommended);
            recommend.Text = T($"改用推荐比例 1:{number}", $"Use recommended scale 1:{number}");
            bool dark = Rhino.Runtime.HostUtils.RunningInDarkMode;
            (paperWarning.Text, paperWarning.TextColor) = paperBand switch
            {
                PaperBand.Small => (T("纸面图幅偏小（< 100 mm），0.15 mm 线宽可能显得过粗。",
                    "Small drawing (< 100 mm). A 0.15 mm stroke may look heavy."), dark ? Color.FromArgb(242, 208, 98) : Color.FromArgb(122, 92, 9)),
                PaperBand.OverLimit => (T("超过 Illustrator 特大画布硬上限（57,795 mm），必须调大比例尺分母。",
                    "Exceeds the Illustrator large-canvas limit (57,795 mm). Increase the scale denominator."),
                    dark ? Color.FromArgb(255, 143, 145) : Color.FromArgb(176, 37, 43)),
                _ => (paper.LongEdge > PaperSizing.StandardLimit
                        ? T("已超出 Illustrator 标准画布上限（5,779.5 mm），导入可能触发特大画布（10×）或改写画板。",
                            "Exceeds Illustrator’s standard canvas (5,779.5 mm). Import may trigger a 10× large canvas or change the artboard.")
                        : T("接近 Illustrator 标准画布上限（5,779.5 mm），超限导入可能触发特大画布或改写画板。",
                            "Near Illustrator’s standard canvas limit (5,779.5 mm). Oversize imports may change canvas or artboard."),
                    dark ? Color.FromArgb(255, 182, 113) : Color.FromArgb(157, 74, 20))
            };
        }

        void Validate()
        {
            Update();
            if (!start.Enabled) return;
            if (On(insert) && (!ModelComponent.IsValidComponentName(parent.Text) || parent.Text.Contains("::")))
            { error.Text = T("请输入有效的父图层名称。", "Enter a valid parent layer name."); return; }
            if (On(insert) && position.SelectedIndex == (int)DesktopPlacement.Pick && !LocationPicked.IsValid)
            { error.Text = T("请先指定插入点。", "Pick the insertion point before generating."); return; }
            bool scaleOk = TryScale(out double paperScale);
            if (On(export))
            {
                if (!(On(model) || On(svg) || On(ai)) || !Directory.Exists(folder.Text) ||
                    string.IsNullOrWhiteSpace(prefix.Text) || prefix.Text != SafeName(prefix.Text) ||
                    prefix.Text.TrimEnd('.', ' ') != prefix.Text || ((On(svg) || On(ai)) && !scaleOk))
                { error.Text = T("请检查导出格式、文件夹、文件名和比例。", "Check the export formats, folder, file name and scale."); return; }
                foreach (string ext in new[] { On(model) ? ".3dm" : null, On(svg) ? ".svg" : null, On(ai) ? ".ai" : null })
                    if (ext != null && File.Exists(Path.Combine(folder.Text, prefix.Text + ext)))
                    { error.Text = T("该文件已存在，请更换前缀。", "An output file already exists. Change the prefix."); return; }
            }
            options.All = scope.SelectedIndex == 1;
            options.Accuracy = (V2Accuracy)accuracy.SelectedIndex;
            options.AutoOcclusion = On(occlusion);
            options.Frame = On(crop);
            options.Clip = On(clipping);
            options.Lines = On(lines);
            options.Tangent = On(tangent);
            options.Cuts = On(sections);
            options.Hidden = On(hidden);
            options.BackFaces = On(backs);
            options.Fills = On(fills);
            options.Fill = (DesktopFill)fill.SelectedIndex;
            options.Insert = On(insert); options.Parent = parent.Text;
            options.Placement = (DesktopPlacement)position.SelectedIndex;
            options.Select = On(select); options.Group = On(group);
            options.Export = On(export); options.Model = On(model); options.Svg = On(svg); options.Ai = On(ai);
            options.Folder = folder.Text; options.Prefix = prefix.Text; options.Scale = scaleOk ? paperScale : 1000;
            options.AllowLargeCanvas = On(allowLarge);
            options.HiZ = On(hiz);
            acceptedView = ChosenView;
            Accepted = true;
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && viewChoices != null)
            foreach (var choice in viewChoices) choice.Saved?.Dispose();
        base.Dispose(disposing);
    }

    private void PickObjects()
    {
        using var picker = new GetObject { GeometryFilter = DesktopData.Supported, SubObjectSelect = false };
        picker.SetCommandPrompt(T("选择要出图的物件", "Select objects to draw"));
        picker.EnablePreSelect(false, true);
        if (picker.GetMultiple(1, 0) != GetResult.Object) return;
        Selected = Enumerable.Range(0, picker.ObjectCount).Select(i => picker.Object(i).Object()).ToArray();
        scope.SelectedIndex = 0;
        UpdateEstimate();
        refreshPaper();
    }

    private void PickLayers()
    {
        using var dialog = new Dialog { Title = T("隐藏线源图层", "Hidden-line source layers"), ClientSize = new Size(490, 420) };
        dialog.UseRhinoStyle();
        var checks = doc.Layers.Where(item => !item.IsDeleted).OrderBy(item => item.FullPath)
            .Select(item => Check(item.FullPath, options.HiddenLayers.Contains(item.FullPath))).ToArray();
        var all = Check(T("全部图层", "All layers"), options.HiddenLayers.Length == 0);
        var list = new Scrollable { Content = Stack(null, checks), Border = BorderType.None, Enabled = !On(all) };
        all.CheckedChanged += (_, _) => list.Enabled = !On(all);
        bool accepted = false;
        var ok = Button(T("确定", "OK"), () => { accepted = true; dialog.Close(); });
        var cancel = Button(T("取消", "Cancel"), dialog.Close);
        dialog.DefaultButton = ok; dialog.AbortButton = cancel;
        dialog.Content = new TableLayout
        {
            Padding = new Padding(12), Spacing = new Size(0, 8),
            Rows = { new TableRow(all), new TableRow(list) { ScaleHeight = true }, new TableRow(null, cancel, ok) }
        };
        dialog.ShowModal(this);
        if (!accepted) return;
        options.HiddenLayers = On(all) ? Array.Empty<string>() : checks.Where(On).Select(check => check.Text).ToArray();
        UpdateEstimate();
    }

    private void UpdateEstimate()
    {
        selection.Text = T($"已选 {Selected.Length:N0} 个", $"{Selected.Length:N0} selected");
        bool all = scope.SelectedIndex == 1;
        bool withOcclusion = !all && On(occlusion);
        var supports = all ? visible : withOcclusion ? Selected.Concat(visible).DistinctBy(obj => obj.Id).ToArray() : Selected;
        long faces = supports.Sum(obj => (long)DesktopData.CountFaces(obj, faceCounts));
        estimate.Text = all
            ? T($"共计算 {supports.Length:N0} 个物件（约 {faces:N0} 个面）", $"{supports.Length:N0} objects in calculation (about {faces:N0} faces)")
            : withOcclusion
                ? T($"已选 {Selected.Length:N0} 个物件（含遮挡参考共 {supports.Length:N0} 个，约 {faces:N0} 个面）",
                    $"{Selected.Length:N0} selected objects ({supports.Length:N0} including occluders, about {faces:N0} faces)")
                : T($"已选 {Selected.Length:N0} 个物件（约 {faces:N0} 个面）", $"{Selected.Length:N0} selected objects (about {faces:N0} faces)");
        layerLabel.Text = options.HiddenLayers.Length == 0 ? T("全部图层", "All layers") :
            T($"已限定 {options.HiddenLayers.Length} 个图层", $"{options.HiddenLayers.Length} layer filters");
    }

    private string T(string zh, string en) => language.Text(zh, en);
    private string ProjectionName(bool parallel) => parallel ? T("平行", "Parallel") : T("透视", "Perspective");
    private static bool On(CheckBox box) => box.Checked == true;
    private static CheckBox Check(string text, bool value) => new() { Text = text, Checked = value };
    private static RadioButtonList Radios(int value, params string[] entries) => new()
        { DataStore = entries, SelectedIndex = value, Orientation = Orientation.Vertical };
    private static Button Button(string title, Action action)
    {
        var button = new Button { Text = title };
        button.Click += (_, _) => action();
        return button;
    }

    private static GroupBox Section(string heading, params Control[] controls) =>
        new() { Text = heading, Content = Compact(controls), Padding = new Padding(6) };
    private static StackLayout Compact(params Control[] controls)
    {
        var layout = Stack(null, controls);
        layout.Spacing = 4;
        return layout;
    }
    internal static StackLayout Stack(string heading, params Control[] controls)
    {
        var layout = new StackLayout { Spacing = 7, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (heading != null) layout.Items.Add(new Label { Text = heading });
        foreach (var control in controls) layout.Items.Add(control);
        return layout;
    }

    internal static TableLayout Row(params Control[] controls) => new()
    {
        Spacing = new Size(8, 0),
        Rows = { new TableRow(controls.Select(control => new TableCell(control, control is TextBox or DropDown)).ToArray()) }
    };

    private static string SafeName(string name) => string.Concat(name.Select(c =>
        Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
