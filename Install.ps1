param(
    [string]$RhinoSystem,
    [string]$PackagePath,
    [ValidateSet('Main', 'UI')][string]$Component = 'Main'
)

$ErrorActionPreference = 'Stop'
if (Get-Process -Name Rhino -ErrorAction SilentlyContinue) {
    throw 'Save your Rhino documents and close Rhino 9 before installing or upgrading BetterMake2D.'
}
if (!$RhinoSystem) {
    $RhinoSystem = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\McNeel\Rhinoceros\9.0\Install' -Name Path -ErrorAction SilentlyContinue).Path
    if (!$RhinoSystem) { $RhinoSystem = Join-Path $env:ProgramFiles 'Rhino 9\System' }
}
$label = 'BetterMake2D'
$packageName = 'bettermake2d'
if ($Component -eq 'UI') {
    $label = 'BetterMake2D UI'
    $packageName = 'bettermake2d-ui'
}
if (!$PackagePath) {
    $PackagePath = Join-Path $PSScriptRoot "$packageName-1.6.0-rh9_0-win.yak"
}
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$yak = Join-Path $RhinoSystem 'Yak.exe'
$legacyUi = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'McNeel/Rhinoceros/packages/9.0/bettermake2d/1.6.0/BetterMake2D.UI.rhp'
if (Test-Path -LiteralPath $legacyUi -PathType Leaf) {
    throw "The older combined BetterMake2D package is still installed. In PowerShell, run & `"$yak`" uninstall bettermake2d. Then install the main package and the optional UI package separately."
}
& $yak install $package
if ($LASTEXITCODE -ne 0) { throw "Yak installation failed ($LASTEXITCODE)." }
Write-Output "$label installed. Restart Rhino 9 to use it."
