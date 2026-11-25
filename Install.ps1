param(
    [string]$RhinoSystem,
    [string]$PackagePath = (Join-Path $PSScriptRoot 'bettermake2d-1.6.0-rh9_0-win.yak'),
    [string]$RuiPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'McNeel\Rhinoceros\9.0\UI\default.rui'),
    [switch]$ToolbarOnly,
    [switch]$RemoveToolbar
)

$ErrorActionPreference = 'Stop'
$profileRui = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'McNeel\Rhinoceros\9.0\UI\default.rui'
$RuiPath = [IO.Path]::GetFullPath($RuiPath)
if (!$RhinoSystem) {
    $RhinoSystem = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\McNeel\Rhinoceros\9.0\Install' -Name Path -ErrorAction SilentlyContinue).Path
    if (!$RhinoSystem) { $RhinoSystem = Join-Path $env:ProgramFiles 'Rhino 9\System' }
}

function Assert-RhinoClosed {
    if ($RuiPath -eq $profileRui -and (Get-Process -Name Rhino -ErrorAction SilentlyContinue)) {
        throw 'Close Rhino before modifying its toolbars. Save any open documents first.'
    }
}

function Read-Rui($Source) {
    $document = New-Object Xml.XmlDocument
    $document.PreserveWhitespace = $true
    $document.XmlResolver = $null
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $reader = [Xml.XmlReader]::Create($Source, $settings)
    try { $document.Load($reader) } finally { $reader.Dispose() }
    if ($document.DocumentElement.Name -ne 'RhinoUI') { throw 'Unrecognized Rhino toolbar format.' }
    return $document
}

function Get-ButtonId([string]$ToolbarId) {
    # Keep the ID used by the original installer so an upgrade replaces its button in place.
    $previousTemplateId = '36f53cad-5149-4c09-a1b4-88704c179d73'
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("$ToolbarId/$previousTemplateId"))
        return ([guid]::new([byte[]]$bytes[0..15])).ToString()
    } finally { $sha.Dispose() }
}

Assert-RhinoClosed
$exists = Test-Path -LiteralPath $RuiPath -PathType Leaf
if (!$exists -and $RemoveToolbar) { return }
$originalHash = $null
if ($exists) {
    $originalHash = (Get-FileHash -LiteralPath $RuiPath -Algorithm SHA256).Hash
    $rui = Read-Rui $RuiPath
} else {
    # Use the default library shipped with this Rhino installation on a new user profile.
    $uiPath = Join-Path $RhinoSystem 'netcore\Rhino.UI.dll'
    $assembly = if ($PSVersionTable.PSEdition -eq 'Desktop') {
        [Reflection.Assembly]::ReflectionOnlyLoadFrom($uiPath)
    } else { [Reflection.Assembly]::LoadFrom($uiPath) }
    $resource = $assembly.GetManifestResourceStream('Rhino.UI.Resources.rui.default.rui')
    if (!$resource) { throw 'The Rhino 9 default toolbar library was not found.' }
    try { $rui = Read-Rui $resource } finally { $resource.Dispose() }
}

$template = Read-Rui (Join-Path $PSScriptRoot 'toolbar\BetterMake2D.rui')
$templateItem = $template.SelectSingleNode('/RhinoUI/tool_bars/tool_bar/tool_bar_item')
$macros = $rui.SelectSingleNode('/RhinoUI/macros')
$icons = $rui.SelectSingleNode('/RhinoUI/icons')
if (!$macros -or !$icons) { throw 'The Rhino toolbar library is missing macros or icons.' }

$scripts = @{}
foreach ($macro in $macros.SelectNodes('macro_item')) {
    $scriptText = [string]$macro.SelectSingleNode('script').InnerText
    $scripts[$macro.GetAttribute('guid')] = $scriptText.Trim()
}

$positions = 0
$changed = $false
$recentToolbarId = '00211289-60a5-460b-88dc-cc667b234dbd'
$emptyId = [guid]::Empty.ToString()
$bars = $rui.SelectNodes('/RhinoUI/tool_bars/tool_bar')
$buttonIds = @{}
foreach ($bar in $bars) {
    $barId = $bar.GetAttribute('guid')
    $buttonIds[$barId] = Get-ButtonId $barId
}
foreach ($bar in $bars) {
    $barId = $bar.GetAttribute('guid')
    $ownedId = $buttonIds[$barId]
    if ($barId -eq $recentToolbarId) {
        # Rhino can copy a recent button with its source toolbar's original ID.
        foreach ($recent in @($bar.SelectNodes('tool_bar_item'))) {
            if ($recent.GetAttribute('guid') -in $buttonIds.Values -and
                $scripts[[string]$recent.SelectSingleNode('left_macro_id').InnerText] -in @('! _BetterMake2D', '! _P2VisibleLines')) {
                [void]$bar.RemoveChild($recent)
                $changed = $true
            }
        }
        continue
    }
    $existing = $bar.SelectSingleNode("tool_bar_item[@guid='$ownedId']")
    if ($existing) {
        $oldLeft = [string]$existing.SelectSingleNode('left_macro_id').InnerText
        $oldRight = [string]$existing.SelectSingleNode('right_macro_id').InnerText
        $leftOwned = $scripts[$oldLeft] -in @('! _BetterMake2D', '! _P2VisibleLines')
        $rightOwned = !$oldRight -or $oldRight -eq $emptyId -or
            $scripts[$oldRight] -in @('! _BetterMake2DHidden', '! _P2HiddenLines')
        if (!$leftOwned -or !$rightOwned) { continue }
    }

    $anchor = $bar.SelectNodes('tool_bar_item') | Where-Object {
        $scripts[[string]$_.SelectSingleNode('left_macro_id').InnerText] -match '^!?\s*_?Make2D$'
    } | Select-Object -First 1
    if ($RemoveToolbar -or !$anchor) {
        if ($existing) {
            [void]$bar.RemoveChild($existing)
            $changed = $true
        }
        continue
    }

    $positions++
    $button = $rui.ImportNode($templateItem, $true)
    $button.SetAttribute('guid', $ownedId)
    if ($existing -and $existing.SelectSingleNode('preceding-sibling::tool_bar_item[1]') -eq $anchor -and
        $existing.OuterXml -ceq $button.OuterXml) {
        continue
    }
    if ($existing) { [void]$bar.RemoveChild($existing) }
    [void]$bar.InsertAfter($button, $anchor)
    $changed = $true
}

if (!$RemoveToolbar) {
    if (!$positions) { throw 'The native Make2D button was not found in this Rhino toolbar layout.' }
    foreach ($section in @($macros, $icons)) {
        foreach ($node in $template.SelectNodes("/RhinoUI/$($section.Name)/*")) {
            $id = $node.GetAttribute('guid')
            $old = $section.SelectSingleNode("*[@guid='$id']")
            if ($old -and $old.OuterXml -ceq $node.OuterXml) { continue }
            $entry = $rui.ImportNode($node, $true)
            if ($old) { [void]$section.ReplaceChild($entry, $old) }
            else { [void]$section.AppendChild($entry) }
            $changed = $true
        }
    }
    if (!$ToolbarOnly) {
        $package = (Resolve-Path -LiteralPath $PackagePath).Path
        & (Join-Path $RhinoSystem 'Yak.exe') install $package
        if ($LASTEXITCODE -ne 0) { throw "Yak installation failed ($LASTEXITCODE)." }
    }
}

if ($changed) {
    $folder = Split-Path -Parent $RuiPath
    [void][IO.Directory]::CreateDirectory($folder)
    $temporary = Join-Path $folder ('.bettermake2d-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $rui.Save($temporary)
        Assert-RhinoClosed
        if ($exists) {
            if ((Get-FileHash -LiteralPath $RuiPath -Algorithm SHA256).Hash -ne $originalHash) {
                throw 'The Rhino toolbar file changed during installation. Close Rhino and retry.'
            }
            $backup = "$RuiPath.bettermake2d-$(Get-Date -Format yyyyMMdd-HHmmss-fff).bak"
            [IO.File]::Replace($temporary, $RuiPath, $backup)
            Write-Output "Toolbar backup: $backup"
        } else { [IO.File]::Move($temporary, $RuiPath) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

if ($RemoveToolbar) { Write-Output 'BetterMake2D toolbar buttons removed.' }
else { Write-Output "BetterMake2D button placed immediately after Make2D in $positions toolbar(s)." }
