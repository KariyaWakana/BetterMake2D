# BetterMake2D 1.6.0

Rhino 9 for Windows 插件，面向 .NET 10 运行时。V2 从选中的三维几何计算平面可见性，输出按 Rhino 原图层组织的闭合区和线稿。

**v1.6.0** 提供两个独立的 Windows 安装包。主插件提供出图命令，UI 插件提供工具栏和顶部「出图」菜单。用下文的 `BuildDesktopPackage.ps1` 构建两份 Yak。

[下载安装包](https://github.com/KariyaWakana/BetterMake2D/releases/tag/v1.6.0) · [使用指南](使用指南.md) · [V2 架构设计](v2混合架构设计.md)

## 使用

1. 退出 Rhino 9。解压主插件安装 ZIP，运行 `Install.cmd`。需要工具栏和菜单时，再解压 UI 插件安装 ZIP，运行 `InstallUI.cmd`。两份 Yak 也可以分别用 Rhino 自带的 `yak.exe` 安装。已安装过含两项组件的旧合包时，先按[使用指南](使用指南.md#二下载与安装)卸载旧合包。
2. 重开 Rhino，激活目标视口，运行 `_BetterMake2D`。装有 UI 插件时，也可左键单击原生 Make2D 右边的点赞按钮，或选择顶部「出图」菜单中的「BetterMake2D」。当前布局找不到原生按钮时，会显示独立的 BetterMake2D 工具列。
3. 在开始前确认画幅、截平面、线条和填色格式，指定插入位置或独立文件路径。默认输出实心 Hatch，亦可选择平面 Brep 或两者。三类文件可按需选用。SVG 与 AI 提供预计纸面尺寸和一键推荐比例，实际尺寸经过导出前复核。
4. 后台非模态进度窗显示阶段、图块和用时，支持取消。完成后可缩放至结果或打开输出文件夹。文档写入使用一次 Undo 记录。输出按原图层镜像到 `ClosedRegions`、`VisibleLines`、`SectionCuts`、`HiddenLines`。

设置窗跟随 Rhino 主题，保存上次使用的选项。插件内嵌后台求解启动程序与 AI 转换脚本，使用 Rhino 9 的 .NET 10 运行时。AI 导出需要本机 Illustrator。采集和写入快照、最终提交图面使用 Rhino 主线程，几何求解在另一个进程完成。

两个包都安装后，Rhino 插件管理器（`_PlugInManager`）会显示两项。

- **BetterMake2D** 提供 `_BetterMake2D` 命令和出图功能。日常启用或停用时，只切换这一项的「启用」。
- **BetterMake2D UI** 随 Rhino 启动，管理工具栏按钮和顶部「出图」菜单。请保持这一项启用，以便主插件停用后重启 Rhino，再次勾选主插件时，两个入口也能在当前会话恢复。

停用主插件时，两个入口会自动隐藏。工具栏按钮只绑定左键。单独加载 `BetterMake2D.rhp` 可使用命令入口。

`-_BetterMake2D` 通过命令行选择物件和放置位置。设置窗与命令行共用 V2 求解器。可用 `HiZ=Yes/No` 对照保守深度预筛。完整操作说明见 [使用指南](使用指南.md)。

V2 的求解精度默认是“精确（NURBS）”。“快速（精细网格，超大场景推荐）”用随工作视窗和文档容差调整的曲面网格计算轮廓、接触和深度。平面修剪和圆孔仍使用原始平面几何。快速模式会按代理内存预算调整曲面网格密度，曲面邻近的可见性和接触位置会随网格精度变化。命令宏使用 `Accuracy=Exact/Fast`，批处理使用 `RunElevation.ps1 -FastMesh`。


## 批处理出图

从 Rhino 原档的已保存平行机位计算二维闭合区域和线稿。结果在 `06_立面`，保留原图层、原对象和原面编号。同目录保存求解分步耗时、未解记录和独立原档射线对照。求解和独立审计使用依次启动的 RhinoCore 进程，支持 `-Svg` 和 `-Ai` 出口。

```powershell
pwsh -File ./RunElevation.ps1 -InputModel './模型.3dm' -ViewName '主立面' -OutputDirectory './输出/v2正立面' -RhinoSystem 'D:/Program Files/Rhino 9/System' -Svg
```

`-HiddenLines` 将虚线输出到 Rhino 的 `Hidden` 和 SVG / AI 的 `隐藏线` 图层树。`-HiddenBackFaces` 加入实体背面边，`-HiddenLayerPath '结构::桩基'` 只导出指定原图层及其子层的隐藏线。`-V2OverlapTiles` 允许当前 RhinoCore 后台预建下一块 Tile 的整数排列。

精确模式先用原边、视轮廓、解析平面接触和网格接触建立可见前沿，再按需对原始 NURBS 面求交。新交线和焊接移动的边界触及哪些单元，就在共享整数格上局部更新这些单元。其余单元复用原有边环和深度归属。前沿收敛后进入跨缝拼接。大型簇的原始投影段和图块拓扑都可流式写盘。

从含外链块的原档出图时，V2 优先读取档内保存的源路径，再查相对路径及原档目录里的同名链接文件。链接变换和来源图层随几何传入。`-LinkedLayersOn` 与 `-LinkedLayersOff` 可在本次出图中覆盖外链档的原图层显示状态，原档的保存状态和链接配置保持原样。

原档模式可用 `-ViewArchive` 从独立的 Rhino 档读取 `-ViewName` 指定的平行命名视图。`-ViewWindow 'left,bottom,right,top'` 使用以相机为原点的视图平面坐标，四个值与模型单位相同。`-NearClip` 和 `-FarClip` 是沿视向从相机起算的距离。`-DepthClipLayer` 将深度裁切限定于指定原图层及其子层，视平面窗口仍覆盖全场景。出图与独立射线审计沿用同一机位、裁切范围和外链图层设置。

通过 `-V2Baseline` 指定同原档、同机位的二维图面可以逐点比较。用 `-NoHiZ` 对照 Hi-Z 关闭时的结果和耗时。`-V2FixedAudit` 只审计全幅固定格点，用于快速检查。也可用 `-SavedFaceCheckpoint` 读取配对原面阶段档，配合 `-V2Focus '原对象ID/面编号'` 检查一张原面。批处理入口使用已保存的平行机位。更多实现细节见 [v2 混合架构设计](v2混合架构设计.md)。

已有二维图面时，可用 `-SavedDrawingDirectory` 指定包含 `主立面_二维图面.3dm` 的目录，仅执行 `-Svg` 或 `-Ai` 导出。`-PaperScale` 指定纸面比例分母，默认 1000。已有 SVG 也可单独运行 `ExportElevationAi.ps1 -SvgPath <文件> -OutputPath <文件>`。

## 1.6.0 验证结果

| 精确模式实测 | 基线 → v1.6.0 墙钟 | 原生 `BrepBrep` | 峰值工作集 / 私有内存 |
| --- | ---: | ---: | ---: |
| 正立面检查点 | 7:06 → 5:05 | 582,854 → 34,399 | 3.62 / 3.58 GiB |
| 20 mm 广角透视 | 17:03 → 7:30 | 812,401 → 87,986 | 3.69 / 3.63 GiB |
| 当期全建筑正立面 | 27:58 → 19:24 | 约 351 万 → 192,168 | 4.26 / 4.25 GiB |

前两项对照 v1.5.0，全建筑对照 v1.5.2。墙钟包含求解和写盘。三组 Rhino 回读均为 0 开环、0 无效区。正立面 226,251 点复核中，局部重排新增标记的 44 个双命中均只有预期区域命中内部。广角 3,560 条射线保留基线全部 3,539 个正确点，另有 2 点转正。

全建筑当前最耗时的是首轮排列与定标，约 8 分 31 秒。正立面含写盘仍高于约 4 分钟目标，全建筑仍高于 8–12 分钟目标。正式版保留这些实测结果作为后续性能基线。

## 构建与验证

正式插件从 `src/` 构建。`-Verification` 构建另含 `tests/` 中的 V2 回归、批处理入口和检查点读取。

使用运行在 .NET 10 上的 PowerShell 7（如 7.6）与 Rhino 9 的 System 目录。以下为发行版的优化构建方式，请按本机安装位置调整路径。

```powershell
pwsh -File ./Build.ps1 -Optimized -RhinoSystem 'D:\Program Files\Rhino 9\System'
pwsh -File ./BuildDesktopPackage.ps1 -RhinoSystem 'D:\Program Files\Rhino 9\System'
pwsh -File ./tests/VerifyPluginLoad.ps1 -RhinoSystem 'D:\Program Files\Rhino 9\System'
pwsh -File ./tests/RunQuickV2Verification.ps1 -RhinoSystem 'D:\Program Files\Rhino 9\System' -CoreOnly -OutputFolder ./build/quick-verification
```

加载验证在独立 RhinoCore 进程调用 `PlugIn.LoadPlugIn` 加载 `build/BetterMake2D.rhp`，核对插件 ID、实际加载路径和 `_BetterMake2D` 命令归属。脚本将验证结果输出到终端。

快速回归在单个 RhinoCore 内验证遮挡证明、0.05 mm 细缝、透视射线、活动截平面、镜像嵌套块、分层输出、取消回滚及惰性求交。用 `-CoreOnly` 可直接从公开仓库运行合成回归。若另有含 `Top`、`验证_侧转35度立面` 和 `验证_侧转35度俯视25度轴测` 的 Rhino 测试档，可去掉 `-CoreOnly` 并传入 `-BridgeModel <模型路径>`，运行三机位射线审计和二维回读。
