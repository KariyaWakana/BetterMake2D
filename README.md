# P2VisibleLines

![P2VisibleLines icon](assets/icon.png)

用于 Rhino 9 的平行投影出图插件。把原模型投影为可编辑的二维曲线，适用于平面、立面和平行轴测。

| 命令 | 输出 |
| --- | --- |
| `_P2VisibleLines` | 剪影、截线及可见折边。可选生成闭合试填与边界。 |
| `_P2HiddenLines` | 遮挡边，放在独立的浅灰虚线图层。可与可见线叠合。 |

## 安装

Windows 用户先退出 Rhino 9，完整解压安装包后双击 `Install.cmd`，再启动 Rhino 9。安装脚本使用 Rhino 自带的 Yak 包管理器，将本地 `.yak` 安装到当前用户。也可在 `_PlugInManager` 里选择 `P2VisibleLines.rhp` 手动加载。

安装程序会在当前用户的标准工具栏里找到原版 Make2D，在它后面添加点赞按钮。左键运行可见线，右键运行隐藏线。在「出图」页另有一个独立的隐藏线按钮。工具栏使用亮色与暗色两套单色 SVG，跟随 Rhino 的界面主题。修改前会在同一目录备份现有 .rui 文件。

如果你从未启动过 Rhino 9，请先打开并退出一次，再运行安装脚本。没有原版 Make2D 按钮的自定义布局，可以在 Toolbar 中使用随插件加载的「P2 出图」工具栏。

项目针对 Rhino 9 的 .NET 10 运行时。以前手动安装过同名版本时，请在 Rhino 插件管理器确认使用 0.7.1；如有旧版本占用，先退出 Rhino，再运行安装程序。

[Rhino 包安装说明](https://developer.rhino3d.com/guides/yak/installing-and-managing-packages/)

## 出图

1. 在 Rhino 独立出图文件中打开已确认的**平行视图**。选中要出图的实体、曲面、挤出、块实例、网格和会遮挡它们的构件。截平面要作用于当前视口。
2. 运行 `_P2VisibleLines`，按提示选取和回车，在空处指定插入点。二维曲线保留模型单位。
3. 运行 `_P2HiddenLines`。使用同一视图、同一组对象、同一 `SmoothTransitions` 设置和同一插入点。两套线都用可见投影包围盒的左下角配准。可在两个命令里键入同一个坐标，例如 `w220,0,0`。
4. 在图层面板用名称前缀 `P2VisibleLines-` 和 `P2HiddenLines-` 分别管理输出。虚线使用 Rhino 图层线型，可按纸面比例在 Rhino 或 Illustrator 里调节显示间距。

每次运行的选项从默认值开始。两个命令都有 `SmoothTransitions`，默认关闭；打开可保留圆角等光滑过渡边。用于试填的选项在 `_P2VisibleLines` 中。

| 选项 | 默认 | 作用 |
| --- | --- | --- |
| `ClosedFill` | No | 生成临时 Hatch 与闭合边界，根据可见构件判断归属。 |
| `RefineDetails` | No | 开启填色后，对首轮选取中的局部构件再次投影，补充原始边界。 |
| `DetailContacts` | No | 开启填色和局部细化后，追加二次选取构件的实体交线作为试填输入。 |

`ClosedFills` 是试填；默认关闭的 `FillBoundaries` 图层保留外轮廓及内孔边界。对照原模型检查漏线和重线，确认哪些区块可以独立选中后，再按纸面比例导入 Illustrator。复杂接头建议按局部细化，注意保留遮挡对象。

## 试用

打开 [QuickStart.3dm](examples/QuickStart.3dm)，在 Top 视图选中带圆孔的板和抬高的方块。用两个命令出线到同一插入点。方块遮住的板边应显示为虚线。开启 `ClosedFill` 的试填中，板上的圆孔保持空白。

## 从源码构建

使用 Windows Rhino 9 和运行在 .NET 10 上的 PowerShell 7，例如 PowerShell 7.6。将下方路径替换为本机的 Rhino 9 安装目录。

```powershell
pwsh -File "./src/Build.ps1" -RhinoSystem "D:\Program Files\Rhino 9\System"
pwsh -File "./installer/BuildPackage.ps1" -RhinoSystem "D:\Program Files\Rhino 9\System"
```

`src/build/P2VisibleLines.rhp` 是插件文件。`artifacts/` 中生成 Windows Yak 安装包和配套的 `Install.cmd`。构建会把不同尺寸的 ICO 嵌入插件，图标的可编辑源文件位于 `assets/icon.svg`。
