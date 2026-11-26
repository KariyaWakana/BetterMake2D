param(
    [string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 9/System'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'build')
)

$ErrorActionPreference = 'Stop'
$system = (Resolve-Path -LiteralPath $RhinoSystem).Path
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$stage = New-Item -ItemType Directory -Path (Join-Path $output ('package-' + [guid]::NewGuid().ToString('N')))
try {
    $build = Join-Path $PSScriptRoot 'Build.ps1'
    $components = @(
        @{ Name = 'bettermake2d'; Plugin = 'BetterMake2D.rhp'; Manifest = 'manifest.yml'; UiHost = $false }
        @{ Name = 'bettermake2d-ui'; Plugin = 'BetterMake2D.UI.rhp'; Manifest = 'toolbar/manifest.yml'; UiHost = $true }
    )
    $deliverables = foreach ($component in $components) {
        $folder = New-Item -ItemType Directory -Path (Join-Path $stage.FullName $component.Name)
        $plugin = Join-Path $folder.FullName $component.Plugin
        & $build -RhinoSystem $system -Optimized -UiHost:$component.UiHost -OutputPath $plugin | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $component.Manifest) -Destination (Join-Path $folder.FullName 'manifest.yml')
        Push-Location -LiteralPath $folder.FullName
        try {
            & (Join-Path $system 'Yak.exe') build --platform win | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Yak package build failed for $($component.Name)." }
        } finally { Pop-Location }
        $package = @(Get-ChildItem -LiteralPath $folder.FullName -Filter "$($component.Name)-*-rh9_0-win.yak" -File)
        if ($package.Count -ne 1) { throw "Expected one Rhino 9 Windows Yak for $($component.Name)." }
        $plugin
        $package[0].FullName
    }
    Copy-Item -LiteralPath $deliverables -Destination $output -Force
    Get-Item -LiteralPath ($deliverables | Where-Object { $_ -like '*.yak' } | ForEach-Object { Join-Path $output (Split-Path -Leaf $_) })
} finally {
    $resolvedStage = (Resolve-Path -LiteralPath $stage.FullName).Path
    if ((Split-Path -Parent $resolvedStage) -ine $output -or (Split-Path -Leaf $resolvedStage) -notmatch '^package-[0-9a-f]{32}$') {
        throw 'Unexpected package staging folder.'
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
