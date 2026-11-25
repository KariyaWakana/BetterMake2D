param(
    [string]$RhinoSystem = (Join-Path $env:ProgramFiles 'Rhino 9/System'),
    [string]$PluginPath = (Join-Path $PSScriptRoot '../build/BetterMake2D.rhp'),
    [string]$LogPath = (Join-Path $PSScriptRoot '../build/Rhino9插件加载验证.txt')
)

$ErrorActionPreference = 'Stop'
$system = (Resolve-Path -LiteralPath $RhinoSystem).Path
$path = (Resolve-Path -LiteralPath $PluginPath).Path
$log = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($log))
[IO.File]::WriteAllText($log, '')

function Write-Check([string]$Line) {
    $Line | Tee-Object -FilePath $log -Append
}

$expectedPlugin = [guid]'4A30881F-DC1F-4FA2-80E6-F5E233255108'
$expectedCommand = [guid]'5D721950-ED9D-4C3B-B2A8-54FB27615EE0'
$oldPath = $env:Path
$mutex = [Threading.Mutex]::new($false, 'Local\BetterMake2D_HeadlessValidation')
$acquired = $false
$pushed = $false
$core = $null
try {
    try { $acquired = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (!$acquired) { throw 'Another BetterMake2D verification process owns the RhinoCore slot.' }

    Write-Check "RhinoCore plug-in load, $(Get-Date -Format o)"
    Write-Check "Requested path $path"
    Write-Check "SHA256 $((Get-FileHash -LiteralPath $path).Hash)"
    Write-Check "Host .NET $([Environment]::Version)"
    $env:Path = "$system;$system/netcore;$oldPath"
    Push-Location -LiteralPath $system
    $pushed = $true
    [void][Reflection.Assembly]::LoadFrom((Join-Path $system 'netcore/RhinoCommon.dll'))
    $core = [Rhino.Runtime.InProcess.RhinoCore]::new([string[]]@('/nosplash', '/notemplate',
        '/scheme=BetterMake2DLoadVerification'), [Rhino.Runtime.InProcess.WindowStyle]::NoWindow, [IntPtr]::Zero)
    Write-Check "Rhino version $([Rhino.RhinoApp]::Version)"
    Write-Check "RhinoCommon $([Rhino.PlugIns.PlugIn].Assembly.FullName)"

    $loaded = $false
    $protected = $false
    $exists = [Rhino.PlugIns.PlugIn]::PlugInExists($expectedPlugin, [ref]$loaded, [ref]$protected)
    Write-Check "Before loading: registered=$exists loaded=$loaded loadProtected=$protected"
    if ($loaded) { throw 'A BetterMake2D build was already loaded in this RhinoCore process.' }

    $id = [guid]::Empty
    $result = [Rhino.PlugIns.PlugIn]::LoadPlugIn($path, [ref]$id)
    Write-Check "PlugIn.LoadPlugIn(path, out id) returned $result, id=$id"
    if ($result -ne [Rhino.PlugIns.LoadPlugInResult]::Success -or $id -ne $expectedPlugin) {
        throw "Rhino did not freshly load the requested plug-in and ID: $result / $id"
    }

    $plugin = [Rhino.PlugIns.PlugIn]::Find($id)
    $info = [Rhino.PlugIns.PlugIn]::GetPlugInInfo($id)
    if ($null -eq $plugin -or $null -eq $info -or !$info.IsLoaded -or $plugin.Id -ne $id -or
        $info.Id -ne $id -or $info.Name -cne 'BetterMake2D') {
        throw 'Rhino did not register a loaded BetterMake2D plug-in.'
    }
    $loadedAssembly = $plugin.Assembly
    $location = [IO.Path]::GetFullPath($loadedAssembly.Location)
    if (![string]::Equals($location, $path, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Rhino loaded a different build: $location"
    }
    $fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($location)
    if ($fileInfo.FileVersion -ne $loadedAssembly.GetName().Version.ToString() -or $info.Version -ne $fileInfo.FileVersion) {
        throw 'The Rhino plug-in version, assembly version and Windows file version differ.'
    }
    $description = [Reflection.CustomAttributeExtensions]::GetCustomAttribute(
        $loadedAssembly, [Reflection.AssemblyDescriptionAttribute]).Description
    if ([string]::IsNullOrWhiteSpace($description) -or $plugin.Description -cne $description -or
        $info.Description -cne $description) {
        throw 'The Rhino plug-in details do not match the assembly description.'
    }
    Write-Check "Registered plug-in $($info.Name), IsLoaded=$($info.IsLoaded), version=$($info.Version)"
    Write-Check "Rhino plug-in details description $($info.Description)"
    $details = [Reflection.CustomAttributeExtensions]::GetCustomAttributes(
        $loadedAssembly, [Rhino.PlugIns.PlugInDescriptionAttribute])
    foreach ($detail in $details) {
        $property = $info.PSObject.Properties[$detail.DescriptionType.ToString()]
        if ($null -eq $property) { continue }
        if ($property.Value -cne $detail.Value) {
            throw "The Rhino plug-in details field $($property.Name) does not match its assembly attribute."
        }
        Write-Check "Rhino plug-in details $($property.Name) $($property.Value)"
    }
    Write-Check "Loaded assembly $($loadedAssembly.FullName)"
    Write-Check "Loaded path $location"
    Write-Check "Windows file version $($fileInfo.FileVersion), product version $($fileInfo.ProductVersion)"
    $pluginType = $plugin.GetType()
    Write-Check "Plug-in type $($pluginType.FullName), sealed=$($pluginType.IsSealed), publicParameterless=$($null -ne $pluginType.GetConstructor([type[]]@()))"
    foreach ($entry in $loadedAssembly.GetCustomAttributesData()) {
        if ($entry.AttributeType.Name -match '^(Guid|AssemblyFileVersion|AssemblyCompany|AssemblyProduct|PlugInDescription|TargetFramework)Attribute$') {
            $values = $entry.ConstructorArguments | ForEach-Object { $_.Value }
            Write-Check "$($entry.AttributeType.Name) $($values -join ' / ')"
        }
    }

    $commandId = [Rhino.Commands.Command]::LookupCommandId('BetterMake2D', $true)
    $names = [Rhino.Commands.Command]::GetCommandNames($true, $true)
    $commands = @($plugin.GetCommands() | Where-Object { $_.EnglishName -ceq 'BetterMake2D' })
    if ($commandId -ne $expectedCommand -or $names -cnotcontains 'BetterMake2D' -or $commands.Count -ne 1) {
        throw "Rhino did not register the BetterMake2D command on the loaded plug-in: $commandId"
    }
    Write-Check "Registered English command _BetterMake2D, id=$commandId, present in the loaded command list and owned by $($info.Name)"
    Write-Check 'PASS Rhino loaded the requested RHP and registered its command without exceptions.'
} catch {
    Write-Check "FAIL $($_.Exception.ToString())"
    if ($null -ne $core) {
        $history = [Rhino.RhinoApp]::CommandHistoryWindowText
        if ($history) { Write-Check (($history -split '\r?\n' | Select-Object -Last 24) -join [Environment]::NewLine) }
    }
    throw
} finally {
    try { if ($null -ne $core) { $core.Dispose() } }
    finally {
        if ($pushed) { Pop-Location }
        $env:Path = $oldPath
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
