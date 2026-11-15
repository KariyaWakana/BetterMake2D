param(
    [string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 9/System'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$system = (Resolve-Path -LiteralPath $RhinoSystem).Path
$yak = Join-Path $system 'Yak.exe'
if (!(Test-Path -LiteralPath $yak -PathType Leaf)) { throw "Yak.exe not found in $system" }

$binary = & (Join-Path $root 'src/Build.ps1') -RhinoSystem $system
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$stage = New-Item -ItemType Directory -Path (Join-Path $root ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))) -Force
try {
    Copy-Item -LiteralPath $binary.FullName -Destination $stage.FullName
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'manifest.yml') -Destination $stage.FullName
    Copy-Item -LiteralPath (Join-Path $root 'assets/icon.png') -Destination $stage.FullName
    Copy-Item -LiteralPath (Join-Path $root 'toolbar/P2VisibleLines.rui') -Destination $stage.FullName
    Push-Location -LiteralPath $stage.FullName
    try {
        & $yak build --platform win | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Yak build failed' }
    } finally {
        Pop-Location
    }
    $package = @(Get-ChildItem -LiteralPath $stage.FullName -File -Filter '*-rh9*-win.yak')
    if ($package.Count -ne 1) { throw 'Expected exactly one Rhino 9 Windows package' }
    Copy-Item -LiteralPath $package[0].FullName -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $PSScriptRoot 'InstallToolbar.ps1'), (Join-Path $root 'toolbar/P2VisibleLines.rui') -Destination $output -Force
    Get-Item -LiteralPath (Join-Path $output $package[0].Name)
} finally {
    $expected = (Resolve-Path -LiteralPath (Join-Path $root 'artifacts')).Path.TrimEnd('\') + '\'
    $actual = (Resolve-Path -LiteralPath $stage.FullName).Path
    if (!$actual.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected staging path $actual" }
    Remove-Item -LiteralPath $actual -Recurse -Force
}
