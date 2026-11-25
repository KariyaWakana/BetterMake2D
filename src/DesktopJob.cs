using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Commands;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.PlugIns;
using Rhino.UI;
using RhinoObject = Rhino.DocObjects.RhinoObject;

namespace BetterMake2D;

internal static class DesktopInteractive
{
    internal static Result Run(RhinoDoc doc, BetterMake2DPlugin plugin)
    {
        var language = DesktopLanguage.Current();
        string T(string zh, string en) => language.Text(zh, en);
        if (DesktopJob.Running)
        {
            RhinoApp.WriteLine(T("已有 BetterMake2D 任务正在运行。", "A BetterMake2D job is already running."));
            return Result.Nothing;
        }
        var view = doc.Views.ActiveView?.ActiveViewport;
        if (view == null) return Result.Nothing;
        try
        {
            using var dialog = new DesktopDialog(doc, view, DesktopData.LoadSettings(plugin.Settings), language);
            dialog.ShowSemiModal(doc, RhinoEtoApp.MainWindowForDocument(doc));
            if (!dialog.Accepted) return Result.Cancel;
            var options = dialog.Options;
            DesktopData.StoreSettings(plugin.Settings, options);
            return DesktopJob.Start(doc, dialog.ChosenView, options, dialog.Selected, dialog.LocationPicked, language);
        }
        catch (Exception error)
        {
            RhinoApp.WriteLine(T("无法启动 BetterMake2D。", "Could not start BetterMake2D.") + " " + error.Message);
            return Result.Failure;
        }
    }
}

internal sealed class DesktopJob
{
    private sealed record PaperReview(PaperExtent Extent, TaskCompletionSource<PaperExportSettings> Answer);
    private sealed class WorkerMemoryLimitException() : InvalidOperationException("The drawing worker reached 4,608 MiB.");
    private static DesktopJob current;
    internal static bool Running => current != null;

    private readonly string workspace;
    private readonly uint serial;
    private readonly DesktopOptions options;
    private readonly Point3d insertion;
    private readonly DesktopLanguage language;
    private readonly ConcurrentQueue<string> messages = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Form window;
    private readonly Label phase = new() { Wrap = WrapMode.Word };
    private readonly Label time = new();
    private readonly ProgressBar bar = new() { Indeterminate = true };
    private readonly Button stopButton, zoom, open;
    private readonly UITimer timer = new() { Interval = .2 };
    private Task<DesktopResult> task;
    private BoundingBox bounds = BoundingBox.Empty;
    private readonly List<(string Pending, string Final)> pending = new();
    private int tileTotal;
    private long lastSnapshotRefresh;
    private volatile bool solving;
    private bool done;
    private PaperReview paperReview;
    private double deliveredScale;

    internal static Result Start(RhinoDoc doc, DesktopViewChoice view, DesktopOptions options,
        RhinoObject[] selected, Point3d point, DesktopLanguage language)
    {
        if (Running) throw new InvalidOperationException("BetterMake2D is already running.");
        string path = Path.Combine(Path.GetTempPath(), "BetterMake2D-" + Guid.NewGuid().ToString("N"));
        var job = new DesktopJob(doc, options, point, language, path);
        File3dm snapshot = null;
        current = job;
        try
        {
            job.window.Show(doc);
            job.timer.Start();
            job.RefreshSnapshot();
            snapshot = DesktopData.Snapshot(doc, view, options, selected, out var camera, job.SnapshotProgress);
            if (snapshot.Objects.Count == 0)
            {
                RhinoApp.WriteLine(job.T("当前范围没有可出图的几何。", "No eligible geometry in the current scope."));
                return Result.Nothing;
            }
            job.bar.Indeterminate = true;
            job.phase.Text = job.T("正在保存场景快照…", "Saving the scene snapshot…");
            job.RefreshSnapshot();
            Directory.CreateDirectory(path);
            job.StartWorker(snapshot, camera);
            snapshot = null;
            return Result.Success;
        }
        catch (OperationCanceledException)
        {
            RhinoApp.WriteLine(job.T("已取消出图快照。", "Drawing snapshot cancelled."));
            return Result.Cancel;
        }
        finally
        {
            snapshot?.Dispose();
            if (job.task == null)
            {
                job.timer.Stop();
                job.done = true;
                current = null;
                Clean(path);
                job.stop.Dispose();
                job.window.Close();
            }
        }
    }

    private DesktopJob(RhinoDoc doc, DesktopOptions options, Point3d point, DesktopLanguage language, string path)
    {
        this.options = options;
        this.language = language;
        workspace = path;
        serial = doc.RuntimeSerialNumber;
        insertion = options.Placement == DesktopPlacement.Pick ? point : Point3d.Origin;
        window = new Form
        {
            Title = "BetterMake2D", ClientSize = new Size(400, 165),
            ShowInTaskbar = false, Resizable = false, WindowStyle = WindowStyle.Utility
        };
        window.UseRhinoStyle();
        var parent = RhinoEtoApp.MainWindowForDocument(doc);
        if (parent != null)
            window.Location = new Eto.Drawing.Point(parent.Location.X + Math.Max(0, parent.Size.Width - 435),
                parent.Location.Y + 78);
        stopButton = Button(T("取消", "Cancel"), () =>
        {
            if (done) window.Close();
            else RequestCancel();
        });
        zoom = Button(T("缩放至结果", "Zoom to results"), Zoom);
        open = Button(T("打开输出文件夹", "Open output folder"), () =>
            Process.Start(new ProcessStartInfo(options.Folder) { UseShellExecute = true }));
        zoom.Visible = open.Visible = false;
        var layout = DesktopDialog.Stack(null, phase, bar, time, DesktopDialog.Row(stopButton, zoom, open));
        layout.Padding = new Padding(14);
        window.Content = layout;
        phase.Text = T("正在准备出图快照…", "Preparing the drawing snapshot…");
        window.Closing += (_, e) =>
        {
            if (done) return;
            e.Cancel = true;
            RequestCancel();
        };
        timer.Elapsed += (_, _) => Tick();
    }

    private void SnapshotProgress(int completed, int total)
    {
        stop.Token.ThrowIfCancellationRequested();
        bool first = total > 0 && bar.Indeterminate;
        bool last = total > 0 && completed == total && bar.Value != total;
        if (!first && !last && Stopwatch.GetElapsedTime(lastSnapshotRefresh).TotalMilliseconds < 50) return;
        if (total > 0)
        {
            bar.MaxValue = total;
            bar.Value = completed;
            bar.Indeterminate = false;
            phase.Text = T($"正在准备出图快照（{completed:N0} / {total:N0} 个物件）…",
                $"Preparing the drawing snapshot ({completed:N0} / {total:N0} objects)…");
        }
        RefreshSnapshot();
    }

    private void RefreshSnapshot()
    {
        RhinoApp.Wait();
        stop.Token.ThrowIfCancellationRequested();
        if (RhinoDoc.FromRuntimeSerialNumber(serial) == null)
            throw new OperationCanceledException();
        lastSnapshotRefresh = Stopwatch.GetTimestamp();
    }

    private void StartWorker(File3dm snapshot, DesktopView camera)
    {
        task = Task.Run(async () =>
        {
            // The archive owns detached copies. The writer never reads the live Rhino document.
            using (snapshot)
            {
                stop.Token.ThrowIfCancellationRequested();
                var archiveOptions = new File3dmWriteOptions { Version = 8 };
                archiveOptions.EnableRenderMeshes(Rhino.DocObjects.ObjectType.Brep | Rhino.DocObjects.ObjectType.Extrusion, true);
                if (!snapshot.Write(Path.Combine(workspace, "scene.3dm"), archiveOptions))
                    throw new IOException("Could not save scene snapshot.");
            }
            stop.Token.ThrowIfCancellationRequested();
            File.WriteAllText(Path.Combine(workspace, "request.json"), JsonSerializer.Serialize(new DesktopRequest(options, camera)));
            string plugin = typeof(DesktopWorker).Assembly.Location;
            string system = Path.GetDirectoryName(Path.GetDirectoryName(typeof(PlugIn).Assembly.Location));
            var process = WorkerStart(workspace, system, plugin);
            stop.Token.ThrowIfCancellationRequested();
            solving = true;
            messages.Enqueue("startup");
            return await Execute(process).ConfigureAwait(false);
        });
        _ = task.ContinueWith(_ =>
        {
            try { RhinoApp.InvokeOnUiThread((Action)Tick); }
            catch (InvalidOperationException) { }
        }, TaskScheduler.Default);
    }

    internal static ProcessStartInfo WorkerStart(string workspace, string system, string plugin)
    {
        string worker = Path.Combine(workspace, "BetterMake2D.Worker.dll");
        Extract("BetterMake2D.Worker.dll", worker);
        string root = new DirectoryInfo(Path.TrimEndingDirectorySeparator(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()))
            .Parent?.Parent?.Parent?.FullName;
        string dotnet = new[] { root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet") }
            .Where(path => path != null).Select(path => Path.Combine(path, "dotnet.exe")).FirstOrDefault(File.Exists);
        if (dotnet == null) throw new FileNotFoundException("Could not find the Rhino .NET 10 host.");
        var start = StartInfo(dotnet, system, "exec", "--runtimeconfig",
            Path.Combine(system, "netcore", "dotnetstart.10.runtimeconfig.json"), worker, system, plugin, workspace);
        start.Environment["PATH"] = system + ";" + Path.Combine(system, "netcore") + ";" + Environment.GetEnvironmentVariable("PATH");
        start.Environment["OPENBLAS_NUM_THREADS"] = "1";
        start.Environment["OMP_NUM_THREADS"] = "1";
        return start;
    }

    private async Task<DesktopResult> Execute(ProcessStartInfo worker)
    {
        await RunProcess(worker, stop.Token, monitorMemory: true).ConfigureAwait(false);
        stop.Token.ThrowIfCancellationRequested();
        var result = JsonSerializer.Deserialize<DesktopResult>(File.ReadAllText(Path.Combine(workspace, "result.json")));
        if (result.Regions + result.Lines == 0 || !options.Export) return result;
        if (options.Svg || options.Ai)
        {
            deliveredScale = options.Scale;
            if (!File.Exists(Path.Combine(workspace, "drawing.svg")))
            {
                var answer = new TaskCompletionSource<PaperExportSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref paperReview, new PaperReview(result.VectorExtent, answer));
                var settings = await answer.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                if (settings == null) throw new OperationCanceledException();
                deliveredScale = settings.Scale;
                File.WriteAllText(Path.Combine(workspace, "paper.json"), JsonSerializer.Serialize(settings));
                worker.ArgumentList.Add("svg");
                await RunProcess(worker, stop.Token, monitorMemory: true).ConfigureAwait(false);
                worker.ArgumentList.RemoveAt(worker.ArgumentList.Count - 1);
            }
        }
        if (options.Ai)
        {
            messages.Enqueue("ai");
            worker.ArgumentList.Add("ai");
            // Allow Illustrator's existing export-and-reopen cleanup to finish if cancelled.
            await RunProcess(worker, CancellationToken.None, monitorMemory: false).ConfigureAwait(false);
        }
        foreach (var (enabled, file, extension) in new[] { (options.Model, "presentation.3dm", ".3dm"),
            (options.Svg, "drawing.svg", ".svg"), (options.Ai, "drawing.ai", ".ai") })
        {
            if (!enabled) continue;
            stop.Token.ThrowIfCancellationRequested();
            string stage = Path.Combine(options.Folder, ".BetterMake2D-" + Guid.NewGuid().ToString("N") + ".pending");
            string target = Path.Combine(options.Folder, options.Prefix + extension);
            pending.Add((stage, target));
            using var input = File.OpenRead(Path.Combine(workspace, file));
            using var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write);
            await input.CopyToAsync(output, stop.Token).ConfigureAwait(false);
        }
        return result;
    }

    private async Task RunProcess(ProcessStartInfo start, CancellationToken cancel, bool monitorMemory)
    {
        using var process = new Process { StartInfo = start };
        var errors = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is string row && row.StartsWith("P|", StringComparison.Ordinal)) messages.Enqueue(row[2..]);
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) errors.Enqueue(e.Data); };
        cancel.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Could not start the background process.");
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.HasExited)
            {
                await Task.Delay(400, cancel).ConfigureAwait(false);
                process.Refresh();
                if (monitorMemory && !process.HasExited && Math.Max(process.PeakWorkingSet64, process.PrivateMemorySize64) > 4608L * 1024 * 1024)
                    throw new WorkerMemoryLimitException();
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors.Take(10)).Trim() is { Length: > 0 } detail
                    ? detail : $"The background process exited with code {process.ExitCode}.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            process.WaitForExit();
        }
    }

    private void Tick()
    {
        if (done) return;
        time.Text = T($"已用 {clock.Elapsed:mm\\:ss}", $"Elapsed {clock.Elapsed:mm\\:ss}");
        if (task == null) return;
        while (messages.TryDequeue(out var row))
            if (!stop.IsCancellationRequested) UpdatePhase(row);
        if (!stop.IsCancellationRequested && RhinoApp.InCommand == 0 && Interlocked.Exchange(ref paperReview, null) is PaperReview review)
        {
            try { review.Answer.TrySetResult(ConfirmPaper(review.Extent)); }
            catch (Exception error) { review.Answer.TrySetException(error); }
        }
        if (!task.IsCompleted || (!stop.IsCancellationRequested && RhinoApp.InCommand > 0)) return;
        timer.Stop();
        done = true;
        clock.Stop();
        current = null;
        var published = new List<string>();
        bool closeCancelledSnapshot = stop.IsCancellationRequested && !solving;
        bool completed = false;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            var result = task.GetAwaiter().GetResult();
            if (result.Regions + result.Lines == 0)
                phase.Text = T("当前范围没有可见输出。", "No visible output in the current scope.");
            else
            {
                var doc = RhinoDoc.FromRuntimeSerialNumber(serial);
                if (options.Insert && doc == null) throw new InvalidOperationException("The source document was closed.");
                foreach (var (stage, target) in pending)
                {
                    File.Move(stage, target);
                    published.Add(target);
                }
                if (options.Insert)
                    bounds = DesktopOutput.Insert(doc, Path.Combine(workspace, "presentation.3dm"), options, insertion, out _);
                if (deliveredScale > 0 && result.VectorExtent.IsValid)
                {
                    var paper = result.VectorExtent.OnPaper(deliveredScale);
                    RhinoApp.WriteLine(T($"纸面图幅 {paper.Width:F1} × {paper.Height:F1} mm，比例 1:{PaperSizing.ScaleText(deliveredScale)}。",
                        $"Paper size {paper.Width:F1} × {paper.Height:F1} mm at 1:{PaperSizing.ScaleText(deliveredScale)}."));
                    if (PaperSizing.Band(paper) == PaperBand.Small)
                    {
                        string scale = PaperSizing.ScaleText(PaperSizing.Recommend(result.VectorExtent, deliveredScale));
                        RhinoApp.WriteLine(T($"图幅长边小于 100 mm，建议改用 1:{scale}。", $"Long edge under 100 mm. Recommended scale 1:{scale}."));
                    }
                }
                phase.Text = T($"完成 · {result.Regions:N0} 个闭合区 · {result.Lines:N0} 条线",
                    $"Complete · {result.Regions:N0} closed regions · {result.Lines:N0} lines");
                if (result.HatchFallbacks > 0)
                    RhinoApp.WriteLine(T($"{result.HatchFallbacks:N0} 个闭合区保留为平面曲面，以保留全部边界和孔洞。",
                        $"{result.HatchFallbacks:N0} closed regions were kept as planar surfaces to preserve their boundaries and holes."));
                zoom.Visible = bounds.IsValid;
                open.Visible = published.Count != 0;
            }
            completed = true;
        }
        catch (OperationCanceledException) { phase.Text = T("已取消。", "Cancelled."); }
        catch (Exception error)
        {
            foreach (string path in published) TryDelete(path);
            string title = error is WorkerMemoryLimitException
                ? T("出图已停止：达到 4,608 MiB 内存上限。", "Drawing stopped at the 4,608 MiB memory limit.")
                : T("出图失败。详见 Rhino 命令历史。", "Drawing failed. See Rhino command history.");
            string advice = options.Frame
                ? T("缩小工作视窗内的出图范围，或添加活动截平面。", "Tighten the viewport framing or add an active clipping plane.")
                : T("开启「仅保留工作视窗画幅内」，或添加活动截平面。", "Enable ‘Keep only inside viewport frame’ or add an active clipping plane.");
            phase.Text = title + "\n" + advice;
            RhinoApp.WriteLine(error.ToString());
        }
        finally
        {
            foreach (var (stage, _) in pending) TryDelete(stage);
            Clean(workspace);
            stop.Dispose();
            if (closeCancelledSnapshot)
            {
                RhinoApp.WriteLine(T("已取消出图快照。", "Drawing snapshot cancelled."));
                window.Close();
            }
            else
            {
                stopButton.Enabled = true;
                stopButton.Text = T("关闭", "Close");
                bar.Indeterminate = false;
                if (completed) bar.Value = bar.MaxValue;
            }
        }
    }

    private PaperExportSettings ConfirmPaper(PaperExtent model)
    {
        var paper = model.OnPaper(options.Scale);
        bool hard = paper.LongEdge > PaperSizing.LargeLimit;
        double recommended = PaperSizing.Recommend(model, options.Scale);
        string targetScale = PaperSizing.ScaleText(recommended);
        PaperExportSettings result = null;
        using var dialog = new Dialog { Title = T("复核纸面尺寸", "Review paper size"), ClientSize = new Size(475, 170) };
        dialog.UseRhinoStyle();
        var note = new Label
        {
            Wrap = WrapMode.Word,
            Text = T($"实际纸面尺寸 {paper.Width:F1} × {paper.Height:F1} mm。", $"Actual paper size {paper.Width:F1} × {paper.Height:F1} mm.") + "\n" +
                (hard ? T("已超过 Illustrator 特大画布上限 57,795 mm，必须调大比例尺分母。",
                    "This exceeds the Illustrator large-canvas limit of 57,795 mm. Increase the scale denominator.")
                      : T("已超过 Illustrator 标准画布上限 5,779.5 mm。保留原比例可能启用特大画布或改写画板。",
                    "This exceeds Illustrator’s standard canvas limit of 5,779.5 mm. Keeping this scale may trigger a large canvas or change the artboard."))
        };
        var accept = Button(T($"按推荐比例 1:{targetScale} 导出", $"Export at 1:{targetScale}"), () =>
        {
            result = new PaperExportSettings(recommended, false);
            dialog.Close();
        });
        var force = Button(T("仍按原比例导出", "Keep original scale"), () =>
        {
            result = new PaperExportSettings(options.Scale, true);
            dialog.Close();
        });
        force.Visible = !hard;
        var cancel = Button(T("取消", "Cancel"), dialog.Close);
        dialog.DefaultButton = accept;
        dialog.AbortButton = cancel;
        var layout = DesktopDialog.Stack(null, note, DesktopDialog.Row(cancel, force, accept));
        layout.Padding = new Padding(14);
        dialog.Content = layout;
        dialog.ShowModal(window);
        return result;
    }

    private void UpdatePhase(string message)
    {
        if (message.StartsWith("Tile plan ", StringComparison.Ordinal) && int.TryParse(message[10..], out tileTotal))
        {
            bar.MaxValue = Math.Max(1, tileTotal);
            bar.Value = 0;
            bar.Indeterminate = false;
            phase.Text = T($"正在排列与定标 · 0 / {tileTotal}", $"Arranging and classifying · 0 / {tileTotal}");
            return;
        }
        var tile = Regex.Match(message, @"^Tile (\d+):");
        if (tile.Success && int.TryParse(tile.Groups[1].Value, out int step))
        {
            if (step > tileTotal) bar.MaxValue = tileTotal = step;
            bar.Value = Math.Clamp(step, 0, bar.MaxValue);
            phase.Text = T($"正在排列与定标 · {step} / {tileTotal}", $"Arranging and classifying · {step} / {tileTotal}");
            return;
        }
        if (message.StartsWith("Fine mesh preparation", StringComparison.Ordinal))
            phase.Text = T("正在生成精细曲面网格", "Generating fine surface meshes");
        else if (message.StartsWith("3D contact search", StringComparison.Ordinal) || message.StartsWith("Components:", StringComparison.Ordinal))
            phase.Text = T("正在提取边界与真实穿插线", "Extracting boundaries and intersections");
        else if (message.Contains("projected segments") || message.StartsWith("Snap ", StringComparison.Ordinal))
            phase.Text = T("正在排列与定标", "Arranging and classifying");
        else if (message.StartsWith("Stitched ", StringComparison.Ordinal))
            phase.Text = T("正在拼接图块与闭合区域", "Joining tiles and closing regions");
        else if (message is "startup" or "read" or "solve" or "output" or "ai")
        {
            bar.Indeterminate = true;
            phase.Text = message switch
            {
                "read" => T("正在读入几何快照", "Reading the geometry snapshot"),
                "solve" => T("正在筛查遮挡", "Checking occlusion"),
                "output" => T("正在生成线稿与填色", "Writing linework and fills"),
                "ai" => T("正在导出和复核 Illustrator 文件", "Exporting and checking the Illustrator file"),
                _ => T("正在启动后台求解器", "Starting the geometry worker")
            };
        }
    }

    private void RequestCancel()
    {
        stop.Cancel();
        stopButton.Enabled = false;
        phase.Text = T("正在取消并清理临时文件", "Cancelling and cleaning temporary files");
    }

    private void Zoom()
    {
        var doc = RhinoDoc.FromRuntimeSerialNumber(serial);
        if (doc?.Views.ActiveView is not { } view || !bounds.IsValid) return;
        view.ActiveViewport.ZoomBoundingBox(bounds);
        view.Redraw();
    }

    private string T(string zh, string en) => language.Text(zh, en);
    private static Button Button(string label, Action action)
    {
        var button = new Button { Text = label };
        button.Click += (_, _) => action();
        return button;
    }

    private static ProcessStartInfo StartInfo(string program, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static void Extract(string resource, string file)
    {
        using var input = typeof(DesktopJob).Assembly.GetManifestResourceStream(resource) ??
            throw new IOException("Missing embedded resource " + resource);
        using var output = File.Create(file);
        input.CopyTo(output);
    }

    internal static void Clean(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
