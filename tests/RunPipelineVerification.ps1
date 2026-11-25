param(
    [Parameter(Mandatory)][string]$AssemblyPath,
    [Parameter(Mandatory)][string]$OutputFolder,
    [Parameter(Mandatory)][string]$RhinoSystem,
    [Parameter(Mandatory)][string[]]$Suite
)

$ErrorActionPreference = 'Stop'
$system = (Resolve-Path -LiteralPath $RhinoSystem).Path
$assemblyFile = (Resolve-Path -LiteralPath $AssemblyPath).Path
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFolder)
[void][IO.Directory]::CreateDirectory($output)
$report = Join-Path $output '验证记录.txt'

$env:OPENBLAS_NUM_THREADS = '1'
$env:OMP_NUM_THREADS = '1'
$env:Path = "$system;$system/netcore;$env:Path"
$lock = [Threading.Mutex]::new($false, 'Local\BetterMake2D_HeadlessValidation')
$acquired = $false
$core = $null
$failed = $false
$locationPushed = $false

function Write-Report([string]$Line) {
    $Line | Add-Content -LiteralPath $report -Encoding utf8
    Write-Output $Line
}

try {
    try { $acquired = $lock.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Another BetterMake2D verification process owns the RhinoCore slot.' }
    Set-Content -LiteralPath $report -Encoding utf8 -Value "Process $PID; OPENBLAS_NUM_THREADS=$env:OPENBLAS_NUM_THREADS"
    Push-Location -LiteralPath $system
    $locationPushed = $true
    [void][Reflection.Assembly]::LoadFrom((Join-Path $system 'netcore/RhinoCommon.dll'))
    $core = [Rhino.Runtime.InProcess.RhinoCore]::new([string[]]@('/nosplash', '/notemplate'),
        [Rhino.Runtime.InProcess.WindowStyle]::NoWindow, [IntPtr]::Zero)
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($assemblyFile))
    foreach ($name in $Suite) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try {
            $method = $assembly.GetType("BetterMake2D.$name", $true).GetMethod('Run')
            Write-Report "$name`n$($method.Invoke($null, $null))"
        } catch {
            $failed = $true
            Write-Report "$name FAIL: $($_.Exception)"
        }
        $process = [Diagnostics.Process]::GetCurrentProcess()
        try {
            Write-Report ('elapsed {0:F2} ms; working set {1:F1} MiB; private {2:F1} MiB; peak working set {3:F1} MiB' -f
                $sw.Elapsed.TotalMilliseconds, ($process.WorkingSet64 / 1MB), ($process.PrivateMemorySize64 / 1MB), ($process.PeakWorkingSet64 / 1MB))
        } finally { $process.Dispose() }
    }
} finally {
    if ($core) { $core.Dispose() }
    if ($acquired) { $lock.ReleaseMutex() }
    $lock.Dispose()
    if ($locationPushed) { Pop-Location }
}
if ($failed) { throw "Verification did not pass. See $report." }
