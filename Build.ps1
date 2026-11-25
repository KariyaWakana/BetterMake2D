param(
    [string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 9/System'),
    [switch]$Verification,
    [switch]$Optimized,
    [string]$OutputPath = (Join-Path $PSScriptRoot 'build/BetterMake2D.rhp')
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or [Environment]::Version.Major -ne 10) {
    throw 'Run this script with PowerShell 7 on .NET 10, such as PowerShell 7.6.'
}
$system = (Resolve-Path -LiteralPath $RhinoSystem).Path
foreach ($name in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $system $name))
}
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$runtime = [AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator
foreach ($file in @($runtime) + @('netcore/RhinoCommon.dll', 'netcore/Rhino.UI.dll', 'Eto.dll' | ForEach-Object { Join-Path $system $_ })) {
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file))
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File)
if ($Verification) { $sources += Get-ChildItem -Path (Join-Path $PSScriptRoot 'tests/*.cs') -File }
$trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@($sources | Sort-Object Name | ForEach-Object {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        (Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8), $null, $_.FullName, [Text.Encoding]::UTF8)
})
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary).WithDeterministic($true)
if ($Optimized) {
    $options = $options.WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
}
$assembly = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($(if ($Verification) { "BetterMake2D.Verify" } else { "BetterMake2D" }), $trees, $references, $options)
$bytes = [IO.MemoryStream]::new()
$resources = $null
$worker = [IO.MemoryStream]::new()
try {
    $embedded = [Collections.Generic.List[Microsoft.CodeAnalysis.ResourceDescription]]::new()
    if (!$Verification) {
        $resources = $assembly.CreateDefaultWin32Resources($true, $true, $null, $null)
        $workerSource = Join-Path $PSScriptRoot 'worker/Program.cs'
        $workerTree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
            (Get-Content -LiteralPath $workerSource -Raw -Encoding UTF8), $null, $workerSource, [Text.Encoding]::UTF8)
        $workerOptions = $options.WithOutputKind([Microsoft.CodeAnalysis.OutputKind]::ConsoleApplication)
        $bootstrap = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('BetterMake2D.Worker',
            [Microsoft.CodeAnalysis.SyntaxTree[]]@($workerTree), $references, $workerOptions)
        $workerBuild = $bootstrap.Emit($worker)
        if (!$workerBuild.Success) { throw ($workerBuild.Diagnostics -join [Environment]::NewLine) }
        $workerBytes = $worker.ToArray()
        $workerStream = { [IO.MemoryStream]::new($workerBytes, $false) }.GetNewClosure()
        $aiPath = Join-Path $PSScriptRoot 'ExportElevationAi.jsx'
        $aiStream = { [IO.File]::OpenRead($aiPath) }.GetNewClosure()
        $embedded.Add([Microsoft.CodeAnalysis.ResourceDescription]::new('BetterMake2D.Worker.dll', $workerStream, $false))
        $embedded.Add([Microsoft.CodeAnalysis.ResourceDescription]::new('ExportElevationAi.jsx', $aiStream, $false))
    }
    $build = $assembly.Emit($bytes, $null, $null, $resources, $embedded, $null, [Threading.CancellationToken]::None)
    if (!$build.Success) { throw ($build.Diagnostics -join [Environment]::NewLine) }
    $absolute = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $absolute) -Force
    [IO.File]::WriteAllBytes($absolute, $bytes.ToArray())
    Get-Item -LiteralPath $absolute
} finally {
    if ($null -ne $resources) { $resources.Dispose() }
    $worker.Dispose()
    $bytes.Dispose()
}



