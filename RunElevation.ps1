[CmdletBinding(DefaultParameterSetName = 'Model')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Model')][string]$InputModel,
    [Parameter(Mandatory, ParameterSetName = 'Model')][string]$ViewName,
    [string]$V2Focus,
    [string]$V2Baseline,
    [ValidateRange(0, 10000000)][int]$V2TileSegments = 250000,
    [ValidateRange(0, 8)][int]$V2MinimumTileDepth = 0,
    [switch]$V2OverlapTiles,
    [switch]$FastMesh,
    [switch]$NoHiZ,
    [switch]$V2FixedAudit,
    [switch]$HiddenLines,
    [switch]$HiddenBackFaces,
    [switch]$TangentEdges,
    [string]$HiddenLayerPath,
    [string[]]$LinkedLayersOn,
    [string[]]$LinkedLayersOff,
    [string]$ViewArchive,
    [string]$ViewWindow,
    [Nullable[double]]$NearClip,
    [Nullable[double]]$FarClip,
    [string]$DepthClipLayer,
    [Parameter(Mandatory, ParameterSetName = 'SavedDrawing')][string]$SavedDrawingDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Checkpoint')][string]$SavedFaceCheckpoint,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 9/System'),
    [ValidateRange(0.01, 1000000)][double]$PaperScale = 1000,
    [switch]$AllowLargeCanvas,
    [switch]$Svg,
    [switch]$Ai
)

$ErrorActionPreference = 'Stop'
$saved = $PSCmdlet.ParameterSetName -eq 'SavedDrawing'
$raw = $PSCmdlet.ParameterSetName -eq 'Model'
$scopedView = $ViewWindow -or $null -ne $NearClip -or $null -ne $FarClip
if (($ViewArchive -or $scopedView -or $DepthClipLayer) -and !$raw) {
    throw 'View archive and clip options require -InputModel.'
}
if ($DepthClipLayer -and $null -eq $NearClip -and $null -eq $FarClip) {
    throw 'DepthClipLayer requires NearClip or FarClip.'
}
if (($null -ne $NearClip -and (![double]::IsFinite($NearClip) -or $NearClip -lt 0)) -or
    ($null -ne $FarClip -and (![double]::IsFinite($FarClip) -or $FarClip -le 0)) -or
    ($null -ne $NearClip -and $null -ne $FarClip -and $NearClip -ge $FarClip)) {
    throw 'Select increasing nonnegative clip distances in model units.'
}
if ($ViewArchive) { $ViewArchive = (Resolve-Path -LiteralPath $ViewArchive).Path }
if (($HiddenBackFaces -or $HiddenLayerPath) -and !$HiddenLines) { throw 'Hidden line filters require -HiddenLines.' }
if (($LinkedLayersOn -or $LinkedLayersOff) -and !$raw) {
    throw 'Linked layer overrides require -InputModel.'
}
if (@($LinkedLayersOn | Where-Object { $_ -and $_ -cin $LinkedLayersOff }).Count -gt 0) {
    throw 'A linked layer can be enabled or disabled once.'
}
if ($V2TileSegments -eq 0 -and $V2MinimumTileDepth -gt 0) { throw 'Forced tile depth requires a positive segment limit.' }
if ($saved) {
    if (!$Svg -and !$Ai) { throw 'Select -Svg or -Ai for an existing drawing.' }
    $drawingDir = (Resolve-Path -LiteralPath $SavedDrawingDirectory).Path
    $drawingFile = (Resolve-Path -LiteralPath (Join-Path $drawingDir '主立面_二维图面.3dm')).Path
    $inputFile = $drawingFile
} elseif ($raw) {
    $inputFile = (Resolve-Path -LiteralPath $InputModel).Path
    if ([IO.Path]::GetExtension($inputFile) -ine '.3dm') { throw 'InputModel must be a Rhino .3dm file.' }
    if ([string]::IsNullOrWhiteSpace($ViewName)) { throw 'Use the exact name of a saved parallel Rhino view.' }
} else {
    $faceFile = (Resolve-Path -LiteralPath $SavedFaceCheckpoint).Path
    $inputFile = $faceFile
}
$system = (Resolve-Path -LiteralPath $RhinoSystem).Path
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw "Choose a new output directory: $root" }
[void][IO.Directory]::CreateDirectory($root)
$assembly = Join-Path $root 'build/BetterMake2D.Verify.dll'
& (Join-Path $PSScriptRoot 'Build.ps1') -RhinoSystem $system -Verification -Optimized -OutputPath $assembly | Out-Null
$runner = Join-Path $PSScriptRoot 'tests/RunPipelineVerification.ps1'
$env:BMA2D_ELEVATION_RUN_REQUEST = Join-Path $root '当前阶段.json'
$stages = Join-Path $root '运行耗时.tsv'
$tab = [char]9
@('stage','wall_seconds','peak_working_mib','peak_private_mib','exit') -join $tab |
    Set-Content -LiteralPath $stages -Encoding utf8
$inputs = @{ input = $inputFile; mode = $PSCmdlet.ParameterSetName; view = $ViewName
    sha256 = (Get-FileHash -LiteralPath $inputFile).Hash
    assemblySha256 = (Get-FileHash -LiteralPath $assembly).Hash; paperScale = $PaperScale; allowLargeCanvas = [bool]$AllowLargeCanvas }
if (!$raw -and $faceFile) {
    $support = [IO.Path]::ChangeExtension($faceFile, '.支撑.3dm')
    if (Test-Path -LiteralPath $support -PathType Leaf) {
        $inputs.faceSupport = $support
        $inputs.faceSupportSha256 = (Get-FileHash -LiteralPath $support).Hash
    }
}
if (!$saved) {
    $inputs.mode = 'V2'
    $inputs.focusOriginal = $V2Focus
    $inputs.tileSegments = $V2TileSegments
    $inputs.overlapTiles = [bool]$V2OverlapTiles
    $inputs.accuracy = if ($FastMesh) { 'Fast' } else { 'Exact' }
    $inputs.hiZ = !$NoHiZ
    $inputs.fixedAudit = [bool]$V2FixedAudit
    $inputs.hiddenLines = [bool]$HiddenLines
    $inputs.tangentEdges = [bool]$TangentEdges
    $inputs.hiddenBackFaces = [bool]$HiddenBackFaces
    $inputs.hiddenLayer = $HiddenLayerPath
    $inputs.linkedLayersOn = $LinkedLayersOn
    $inputs.linkedLayersOff = $LinkedLayersOff
    $inputs.viewArchive = $ViewArchive
    if ($ViewArchive) { $inputs.viewArchiveSha256 = (Get-FileHash -LiteralPath $ViewArchive).Hash }
    $inputs.viewWindow = $ViewWindow
    $inputs.nearClip = $NearClip
    $inputs.farClip = $FarClip
    $inputs.depthClipLayer = $DepthClipLayer
    $inputs.minimumTileDepth = $V2MinimumTileDepth
    $inputs.baseline = if ($V2Baseline) { (Resolve-Path -LiteralPath $V2Baseline).Path }
    if ($inputs.baseline) { $inputs.baselineSha256 = (Get-FileHash -LiteralPath $inputs.baseline).Hash }
}
$inputs | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root '输入记录.json') -Encoding utf8
$inner = @'
$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $env:BMA2D_ELEVATION_RUN_REQUEST -Raw | ConvertFrom-Json -AsHashtable
$parameters = $request.Parameters
& $request.Runner @parameters
'@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
$hostExe = (Get-Process -Id $PID).Path

function RunStage([string]$name, [string]$folder, [hashtable]$parameters) {
    [void][IO.Directory]::CreateDirectory($folder)
    $parameters.AssemblyPath = $assembly
    $parameters.RhinoSystem = $system
    $parameters.OutputFolder = $folder
    @{ Runner = $runner; Parameters = $parameters } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $env:BMA2D_ELEVATION_RUN_REQUEST -Encoding utf8
    $standardOutput = Join-Path $folder 'stdout.txt'
    $standardError = Join-Path $folder 'stderr.txt'
    $worker = Start-Process -FilePath $hostExe -ArgumentList "-NoLogo -NoProfile -EncodedCommand $encoded" -WindowStyle Hidden -PassThru -RedirectStandardOutput $standardOutput -RedirectStandardError $standardError
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $peakWorking = [long]0
    $peakPrivate = [long]0
    $stopReason = $null
    $lastProbe = -10.0
    $lastProgressTime = 0.0
    $lastProgress = ''
    $lastProgressBytes = [long]0
    $progressFile = Join-Path $folder '进度.txt'
    try {
        Write-Output "Stage $name started, RhinoCore PID $($worker.Id)."
        while (!$worker.WaitForExit(500)) {
            $worker.Refresh()
            if ($worker.HasExited) { break }
            $peakWorking = [Math]::Max($peakWorking, $worker.PeakWorkingSet64)
            $peakPrivate = [Math]::Max($peakPrivate, $worker.PrivateMemorySize64)
            if ([Math]::Max($peakWorking, $peakPrivate) -ge 4608MB) {
                $stopReason = 'RhinoCore reached 4608 MiB.'
                break
            }
            if ($name -eq '06-v2' -and $watch.Elapsed.TotalSeconds - $lastProbe -ge 10) {
                $lastProbe = $watch.Elapsed.TotalSeconds
                $progressStamp = Get-Item -LiteralPath $progressFile -ErrorAction SilentlyContinue
                if ($progressStamp -and $progressStamp.Length -ne $lastProgressBytes) {
                    $lastProgressBytes = $progressStamp.Length
                    $lastProgress = Get-Content -LiteralPath $progressFile -Tail 1 -ErrorAction SilentlyContinue
                    $lastProgressTime = $lastProbe
                }
                if ($lastProbe - $lastProgressTime -gt 480) {
                    $stopReason = "The V2 solve has had no progress update for eight minutes."
                    break
                }
                '{0:F1}s CPU {1:F1}s peak {2:F1} MiB  {3}' -f $lastProbe, $worker.TotalProcessorTime.TotalSeconds,
                    ($peakWorking/1MB), $lastProgress |
                    Add-Content -LiteralPath (Join-Path $folder '运行监控.txt') -Encoding utf8
            }
        }
    }
    finally {
        if (!$worker.HasExited) {
            if ($stopReason) { $stopReason | Set-Content -LiteralPath (Join-Path $folder '停止说明.txt') -Encoding utf8 }
            Stop-Process -Id $worker.Id
            $worker.WaitForExit()
        }
        @($name, $watch.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture),
            [Math]::Round($peakWorking/1MB,1), [Math]::Round($peakPrivate/1MB,1), $worker.ExitCode) -join $tab |
            Add-Content -LiteralPath $stages -Encoding utf8
    }
    if ($worker.ExitCode -ne 0) {
        if ($stopReason) { throw "Stage $name stopped. $stopReason Logs: $folder" }
        throw "Stage $name failed. Logs: $folder"
    }
}

if (!$saved) {
    $drawingDir = Join-Path $root '06_立面'
    $drawingFile = Join-Path $drawingDir '主立面_二维图面.3dm'
    $env:BMA2D_V2_SOURCE = $inputFile
    $env:BMA2D_V2_VIEW = $ViewName
    $env:BMA2D_V2_FOCUS = $V2Focus
    $env:BMA2D_V2_BASELINE = $inputs.baseline
    $env:BMA2D_V2_TILE_SEGMENTS = [string]$V2TileSegments
    $env:BMA2D_V2_MIN_TILE_DEPTH = [string]$V2MinimumTileDepth
    $env:BMA2D_V2_OVERLAP_TILES = if ($V2OverlapTiles) { '1' } else { '0' }
    $env:BMA2D_V2_FAST_MESH = if ($FastMesh) { '1' } else { $null }
    $env:BMA2D_V2_NO_HIZ = if ($NoHiZ) { '1' } else { $null }
    $env:BMA2D_V2_FIXED_AUDIT = if ($V2FixedAudit) { '1' } else { $null }
    $env:BMA2D_V2_HIDDEN = if ($HiddenBackFaces) { 'backs' } elseif ($HiddenLines) { 'occluded' } else { $null }
    $env:BMA2D_V2_TANGENT = if ($TangentEdges) { '1' } else { $null }
    $env:BMA2D_V2_HIDDEN_LAYER = $HiddenLayerPath
    $env:BMA2D_V2_LINKS_ON = $LinkedLayersOn -join "`n"
    $env:BMA2D_V2_LINKS_OFF = $LinkedLayersOff -join "`n"
    $env:BMA2D_V2_VIEW_ARCHIVE = $ViewArchive
    $env:BMA2D_V2_VIEW_WINDOW = $ViewWindow
    $env:BMA2D_V2_NEAR_CLIP = if ($null -ne $NearClip) { ([double]$NearClip).ToString([Globalization.CultureInfo]::InvariantCulture) }
    $env:BMA2D_V2_FAR_CLIP = if ($null -ne $FarClip) { ([double]$FarClip).ToString([Globalization.CultureInfo]::InvariantCulture) }
    $env:BMA2D_V2_DEPTH_CLIP_LAYER = $DepthClipLayer
    $env:BMA2D_V2_OUTPUT = $drawingDir
    $env:BMA2D_V2_STAGE = 'solve'
    RunStage '06-v2' $drawingDir @{ Suite = @('VerifyV2DrawingArchive') }
    Get-Content -LiteralPath (Join-Path $drawingDir 'stdout.txt')
    if (!(Test-Path -LiteralPath $drawingFile -PathType Leaf)) { throw "No drawing was written in $drawingDir." }
    $env:BMA2D_V2_STAGE = 'audit'
    $auditStageDir = Join-Path $root '06_独立审计进程'
    RunStage '06-v2-audit' $auditStageDir @{ Suite = @('VerifyV2DrawingArchive') }
    Get-Content -LiteralPath (Join-Path $auditStageDir 'stdout.txt')
    $env:BMA2D_V2_STAGE = $null
}
Write-Output "Rhino drawing: $drawingFile"
if ($Svg -or $Ai) {
    $svgDir = Join-Path $root '07_SVG'
    $env:BMA2D_SVG_DRAWING = $drawingFile
    $env:BMA2D_SVG_OUTPUT = $svgDir
    $env:BMA2D_SVG_SCALE = $PaperScale.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BMA2D_SVG_ALLOW_LARGE = if ($AllowLargeCanvas) { '1' } else { $null }
    try { RunStage '07' $svgDir @{ Suite = @('VerifyStage6SvgExport') } }
    finally {
        $svgReport = Join-Path $svgDir '验证记录.txt'
        if (Test-Path -LiteralPath $svgReport) {
            Select-String -LiteralPath $svgReport -Pattern 'Paper size [^"\r\n]+' |
                ForEach-Object { $_.Matches[0].Value } | Select-Object -Unique |
                ForEach-Object { Write-Warning $_ }
        }
    }
    Write-Output "Layered SVG: $(Join-Path $svgDir '立面_分图层.svg')"
}
if ($Ai) {
    $aiDir = Join-Path $root '08_AI'
    [void][IO.Directory]::CreateDirectory($aiDir)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot 'ExportElevationAi.ps1') -SvgPath (Join-Path $svgDir '立面_分图层.svg') `
        -OutputPath (Join-Path $aiDir '立面_原图层.ai') -AllowLargeCanvas:$AllowLargeCanvas |
        Tee-Object -FilePath (Join-Path $aiDir '验证记录.txt')
    @('08', $watch.Elapsed.TotalSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture),
        '', '', 0) -join $tab | Add-Content -LiteralPath $stages -Encoding utf8
}
