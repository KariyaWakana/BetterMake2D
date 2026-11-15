param(
    [string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 9/System'),
    [string]$OutputPath = (Join-Path $PSScriptRoot 'build/P2VisibleLines.rhp')
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Core' -or [Environment]::Version.Major -ne 10) {
    throw '请用运行在 .NET 10 上的 PowerShell 7 执行此脚本，例如 PowerShell 7.6。'
}
$system = (Resolve-Path -LiteralPath $RhinoSystem).Path
$rhinoCommon = Join-Path $system 'RhinoCommon.dll'
if (!(Test-Path -LiteralPath $rhinoCommon -PathType Leaf)) {
    throw "找不到 RhinoCommon.dll。请用 -RhinoSystem 指定 Rhino 9 的 System 文件夹。"
}

foreach ($name in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $system $name))
}
$references = [Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$runtime = [AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator
foreach ($file in @($runtime) + @($rhinoCommon)) {
    $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($file))
}
$trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@(foreach ($file in @(
    'AssemblyInfo.cs', 'FeatureLinePolicy.cs', 'HiddenLineOutput.cs', 'VisibleFill.cs', 'VisibleLinesPlugin.cs'
)) {
    $path = Join-Path $PSScriptRoot $file
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        (Get-Content -LiteralPath $path -Raw -Encoding UTF8),
        $null, $path, [Text.Encoding]::UTF8)
})
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithDeterministic($true)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
    'P2VisibleLines', $trees, $references, $options)
$iconPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../assets/icon.ico')).Path
$readIcon = { [IO.File]::OpenRead($iconPath) }.GetNewClosure()
$resources = [Microsoft.CodeAnalysis.ResourceDescription[]]@(
    [Microsoft.CodeAnalysis.ResourceDescription]::new(
        'Project2.VisibleLines.Icon.ico', [Func[IO.Stream]]$readIcon, $true)
)
$bytes = [IO.MemoryStream]::new()
try {
    $build = $compilation.Emit($bytes, $null, $null, $null, $resources)
    if (!$build.Success) { throw ($build.Diagnostics -join [Environment]::NewLine) }
    $absolute = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $absolute) -Force
    [IO.File]::WriteAllBytes($absolute, $bytes.ToArray())
    Get-Item -LiteralPath $absolute
} finally {
    $bytes.Dispose()
}
