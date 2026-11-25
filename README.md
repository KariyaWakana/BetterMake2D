# BetterMake2D 1.6.0

Rhino 9 for Windows 插件，面向 .NET 10 运行时。V2 从选中的三维几何计算平面可见性，输出按 Rhino 原图层组织的闭合区和线稿。

**v1.6.0 预发行版**的 Windows 安装 ZIP 会安装插件，并把点赞按钮放到 Rhino「出图」工具栏的原生 Make2D 右边。也可单独下载 RHP 或 Yak。[下载安装包](https://github.com/KariyaWakana/BetterMake2D/releases/tag/v1.6.0)

[使用指南](使用指南.md) · [V2 架构设计](v2混合架构设计.md)

## 使用

1. 退出 Rhino 9，解压 Windows 安装 ZIP，运行其中的 `Install.cmd`。重新打开 Rhino 后切换到「出图」工具栏。
2. 激活目标视口，左键单击原生 Make2D 右边的点赞按钮，或运行 `_BetterMake2D`。可预选模型对象，也可直接选择当前视口可见对象。从「视图」切换其他视口或命名视图。
3. 在开始前确认画幅、截平面、线条和填色格式，指定插入位置或独立文件路径。默认输出实心 Hatch，亦可选择平面 Brep 或两者。三类文件可按需选用。SVG 与 AI 提供预计纸面尺寸和一键推荐比例，实际尺寸经过导出前复核。
4. 后台非模态进度窗显示阶段、图块和用时，支持取消。完成后可缩放至结果或打开输出文件夹。文档写入使用一次 Undo 记录。输出按原图层镜像到 `ClosedRegions`、`VisibleLines`、`SectionCuts`、`HiddenLines`。

设置窗跟随 Rhino 主题，保存上次使用的选项。插件内嵌后台求解启动程序与 AI 转换脚本，使用 Rhino 9 的 .NET 10 运行时。AI 导出需要本机 Illustrator。采集和写入快照、最终提交图面使用 Rhino 主线程，几何求解在另一个进程完成。

点赞按钮只绑定左键。使用 Rhino 插件管理器停用插件后，如需从工具栏移除按钮，在 Rhino 关闭时运行 `Install.cmd -RemoveToolbar`。再次安装会恢复按钮。安装脚本会在原工具栏所在目录保存备份。

`-_BetterMake2D` 通过命令行选择物件和放置位置。设置窗与命令行共用 V2 求解器。可用 `HiZ=Yes/No` 对照保守深度预筛。完整操作说明见 [使用指南](使用指南.md)。

V2 的求解精度默认是“精确（NURBS）”。“快速（精细网格，超大场景推荐）”用随工作视窗和文档容差调整的曲面网格计算轮廓、接触和深度。平面修剪和圆孔仍使用原始平面几何。快速模式会按代理内存预算调整曲面网格密度，曲面邻近的可见性和接触位置会随网格精度变化。命令宏使用 `Accuracy=Exact/Fast`，批处理使用 `RunElevation.ps1 -FastMesh`。


## 批处理出图

从 Rhino 原档的已保存平行机位计算二维闭合区域和线稿。结果在 `06_立面`，保留原图层、原对象和原面编号。同目录保存求解分步耗时、未解记录和独立原档射线对照。求解和独立审计使用依次启动的 RhinoCore 进程，支持 `-Svg` 和 `-Ai` 出口。

```powershell
pwsh -File ./RunElevation.ps1 -InputModel './模型.3dm' -ViewName '主立面' -OutputDirectory './输出/v2正立面' -RhinoSystem 'D:/Program Files/Rhino 9/System' -Svg
```

`-HiddenLines` 将虚线输出到 Rhino 的 `Hidden` 和 SVG / AI 的 `隐藏线` 图层树。`-HiddenBackFaces` 加入实体背面边，`-HiddenLayerPath '结构::桩基'` 只导出指定原图层及其子层的隐藏线。`-V2OverlapTiles` 允许当前 RhinoCore 后台预建下一块 Tile 的整数排列。精确模式先用原边、轮廓、解析平面交线和网格接触定标，再由可见前沿调度原生 BrepBrep。新交线只重排受影响的图块，归属与深度证明不足的面对由补查处理。

从含外链块的原档出图时，V2 优先读取档内保存的源路径，再查相对路径及原档目录里的同名链接文件。链接变换和来源图层随几何传入。`-LinkedLayersOn` 与 `-LinkedLayersOff` 可在本次出图中覆盖外链档的原图层显示状态，原档的保存状态和链接配置保持原样。

原档模式可用 `-ViewArchive` 从独立的 Rhino 档读取 `-ViewName` 指定的平行命名视图。`-ViewWindow 'left,bottom,right,top'` 使用以相机为原点的视图平面坐标，四个值与模型单位相同。`-NearClip` 和 `-FarClip` 是沿视向从相机起算的距离。`-DepthClipLayer` 将深度裁切限定于指定原图层及其子层，视平面窗口仍覆盖全场景。出图与独立射线审计沿用同一机位、裁切范围和外链图层设置。

通过 `-V2Baseline` 指定同原档、同机位的二维图面可以逐点比较。用 `-NoHiZ` 对照 Hi-Z 关闭时的结果和耗时。`-V2FixedAudit` 只审计全幅固定格点，用于快速检查。也可用 `-SavedFaceCheckpoint` 读取配对原面阶段档，配合 `-V2Focus '原对象ID/面编号'` 检查一张原面。批处理入口使用已保存的平行机位。更多实现细节见 [v2 混合架构设计](v2混合架构设计.md)。

已有二维图面时，可用 `-SavedDrawingDirectory` 指定包含 `主立面_二维图面.3dm` 的目录，仅执行 `-Svg` 或 `-Ai` 导出。`-PaperScale` 指定纸面比例分母，默认 1000。已有 SVG 也可单独运行 `ExportElevationAi.ps1 -SvgPath <文件> -OutputPath <文件>`。

## 1.6.0 验证结果

正立面和 20 mm 广角透视的精确与快速模式均通过 Rhino 回读，0 开环、0 无效对象。广角两种模式在 3,560 条独立射线上与 v1.5.0 逐点一致。正立面精确模式在 226,251 个探点中有 12 处归属变化，且整图耗时高于 v1.5.0。

## 构建与验证

正式插件从 `src/` 构建。`-Verification` 构建另含 `tests/` 中的 V2 回归、批处理入口和检查点读取。

使用运行在 .NET 10 上的 PowerShell 7（如 7.6）与 Rhino 9 的 System 目录。以下为发行版的优化构建方式，请按本机安装位置调整路径。

```powershell
pwsh -File ./Build.ps1 -Optimized -RhinoSystem 'D:\Program Files\Rhino 9\System'
pwsh -File ./tests/VerifyPluginLoad.ps1 -RhinoSystem 'D:\Program Files\Rhino 9\System'
pwsh -File ./tests/RunQuickV2Verification.ps1 -RhinoSystem 'D:\Program Files\Rhino 9\System' -CoreOnly -OutputFolder ./build/quick-verification
```

加载验证在独立 RhinoCore 进程调用 `PlugIn.LoadPlugIn` 加载 `build/BetterMake2D.rhp`，核对插件 ID、实际加载路径和 `_BetterMake2D` 命令归属。脚本将验证结果输出到终端。

快速回归在单个 RhinoCore 内验证遮挡证明、0.05 mm 细缝、透视射线、活动截平面、镜像嵌套块、分层输出、取消回滚及惰性求交。用 `-CoreOnly` 可直接从公开仓库运行合成回归。若另有含 `Top`、`验证_侧转35度立面` 和 `验证_侧转35度俯视25度轴测` 的 Rhino 测试档，可去掉 `-CoreOnly` 并传入 `-BridgeModel <模型路径>`，运行三机位射线审计和二维回读。
