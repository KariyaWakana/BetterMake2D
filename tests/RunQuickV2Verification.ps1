param(
    [string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 9/System'),
    [string]$BridgeModel = (Join-Path $PSScriptRoot 'fixtures/BridgeThreeViews.3dm'),
    [switch]$CoreOnly,
    [string]$OutputFolder = (Join-Path $PSScriptRoot '../build/quick-verification')
)

$ErrorActionPreference = 'Stop'
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFolder)
if (Test-Path -LiteralPath $destination) { throw "Choose a new verification folder: $destination" }
[void][IO.Directory]::CreateDirectory($destination)
$assembly = Join-Path $destination 'BetterMake2D.Verify.dll'
$timer = [Diagnostics.Stopwatch]::StartNew()
& (Join-Path $PSScriptRoot '../Build.ps1') -Verification -Optimized -RhinoSystem $RhinoSystem -OutputPath $assembly | Out-Null
$build = $timer.Elapsed.TotalSeconds
$env:BMA2D_HIDDEN_TEST_OUTPUT = Join-Path $destination 'hidden'
$env:BMA2D_LINE_FINISH_OUTPUT = Join-Path $destination 'lines'
$env:BMA2D_V2_SECTION_TEST_OUTPUT = Join-Path $destination 'sections'
$env:BMA2D_V2_TANGENT_TEST_OUTPUT = Join-Path $destination 'tangent'
$env:BMA2D_DESKTOP_TEST = Join-Path $destination 'desktop'
$suite = @('VerifyV2HiZ','VerifyV2ClipPlanes','VerifyV2Perspective','VerifyV2WidePerspective','VerifyV2PlanarContact','VerifyV2SegmentStorage','VerifyV2CommandOutput','VerifyV2NestedOutput',
    'VerifyV2Drawing','VerifyV2SameSupport','VerifyV2Backfaces','VerifyV2PinchedFill','VerifyV2Terrain','VerifyV2HiddenLines','VerifyV2LineFinish','VerifyV2TangentEdges',
    'VerifyDesktopDrawing','VerifyPaperSizing','VerifyV2MeshSnapshot','VerifyV2FastMesh','VerifyV2TileFirst','VerifyV2LazyContact')
if (!$CoreOnly) {
    $env:BMA2D_V2_BRIDGE_MODEL = (Resolve-Path -LiteralPath $BridgeModel).Path
    $env:BMA2D_V2_BRIDGE_OUTPUT = Join-Path $destination 'bridge-views'
    $suite += 'VerifyV2BridgeViews'
}
& (Join-Path $PSScriptRoot 'RunPipelineVerification.ps1') -AssemblyPath $assembly -OutputFolder $destination -RhinoSystem $RhinoSystem -Suite $suite
$summary = 'Build {0:F2}s; verification {1:F2}s; wall {2:F2}s' -f $build, ($timer.Elapsed.TotalSeconds - $build), $timer.Elapsed.TotalSeconds
$summary | Tee-Object -FilePath (Join-Path $destination 'wall.txt')
