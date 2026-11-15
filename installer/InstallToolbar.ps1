param(
    [string]$RuiPath = (Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'McNeel\Rhinoceros\9.0\UI\default.rui'),
    [string]$TemplatePath = (Join-Path $PSScriptRoot 'P2VisibleLines.rui')
)

$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $RuiPath -PathType Leaf)) {
    Write-Warning 'Rhino has not created its toolbar file. Open and close Rhino 9 once, then rerun Install.cmd to add the buttons next to Make2D.'
    exit 0
}

function Read-Rui([string]$Path) {
    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path)
    try { $xml.Load($reader) } finally { $reader.Dispose() }
    if ($xml.DocumentElement.Name -ne 'RhinoUI') { throw "Unexpected RUI format: $Path" }
    return $xml
}

function Get-RuiHash([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToBase64String($sha.ComputeHash([IO.File]::ReadAllBytes($Path)))
    } finally { $sha.Dispose() }
}

function Get-ButtonId([string]$ToolbarId, [string]$ButtonId) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("$ToolbarId/$ButtonId"))
        return ([Guid]::new([byte[]]$bytes[0..15])).ToString()
    } finally { $sha.Dispose() }
}

$RuiPath = (Resolve-Path -LiteralPath $RuiPath).Path
$originalHash = (Get-RuiHash $RuiPath)
$rui = Read-Rui $RuiPath
$template = Read-Rui $TemplatePath
$macros = $rui.SelectSingleNode('/RhinoUI/macros')
$icons = $rui.SelectSingleNode('/RhinoUI/icons')
if (!$macros -or !$icons) { throw 'The current toolbar file does not have the Rhino 9 macro and icon sections.' }

$make2dMacroIds = @($macros.SelectNodes('macro_item') | Where-Object {
    $_.SelectSingleNode('script').InnerText -match '^\s*!?\s*_?Make2D\s*$'
} | ForEach-Object { $_.GetAttribute('guid') })
$templateButton = $template.SelectSingleNode('/RhinoUI/tool_bars/tool_bar/tool_bar_item')
$formerHiddenButtonId = Get-ButtonId '90fd89fc-e41f-49cc-bcf0-29e0d58017a1' '53dd3d79-5784-422f-ae0a-be17a7165aeb'
$formerHiddenIconId = 'd15ebd3e-bab3-4e44-b733-f23d6e4a2c38'
$changed = $false
$locations = @()

foreach ($bar in $rui.SelectNodes('/RhinoUI/tool_bars/tool_bar')) {
    $barId = $bar.GetAttribute('guid')
    if ($barId -eq '00211289-60a5-460b-88dc-cc667b234dbd') {
        $recentId = Get-ButtonId $barId $templateButton.GetAttribute('guid')
        $injected = $bar.SelectSingleNode("tool_bar_item[@guid='$recentId']")
        if ($injected) {
            [void]$bar.RemoveChild($injected)
            $changed = $true
        }
        continue
    }
    $anchor = $bar.SelectNodes('tool_bar_item') | Where-Object {
        $make2dMacroIds -contains $_.SelectSingleNode('left_macro_id').InnerText
    } | Select-Object -First 1
    if (!$anchor) { continue }
    $barId = $bar.GetAttribute('guid')
    $name = $bar.SelectSingleNode('text/locale_1033').InnerText
    $locations += $name
    $id = Get-ButtonId $barId $templateButton.GetAttribute('guid')
    $installedButton = $bar.SelectSingleNode("tool_bar_item[@guid='$id']")
    if ($installedButton) {
        $left = $installedButton.SelectSingleNode('left_macro_id').InnerText
        $right = $installedButton.SelectSingleNode('right_macro_id').InnerText
        $leftScript = $macros.SelectSingleNode("macro_item[@guid='$left']/script").InnerText
        $rightScript = $macros.SelectSingleNode("macro_item[@guid='$right']/script").InnerText
        if ($leftScript -ne '! _P2VisibleLines' -or $rightScript -ne '! _P2HiddenLines') { continue }
    }
    $button = $rui.ImportNode($templateButton, $true)
    $button.SetAttribute('guid', $id)
    if ($installedButton) { [void]$bar.ReplaceChild($button, $installedButton) }
    else { [void]$bar.InsertAfter($button, $anchor) }
    $changed = $true
}

foreach ($formerButton in $rui.SelectNodes("/RhinoUI/tool_bars/tool_bar/tool_bar_item[@guid='$formerHiddenButtonId']")) {
    $macroId = $formerButton.SelectSingleNode('left_macro_id').InnerText
    $macro = $macros.SelectSingleNode("macro_item[@guid='$macroId']")
    if ($macro.SelectSingleNode('script').InnerText -ne '! _P2HiddenLines') { continue }
    [void]$formerButton.ParentNode.RemoveChild($formerButton)
    if (!$rui.SelectSingleNode("//*[self::left_macro_id or self::right_macro_id or self::macro_id][text()='$macroId']")) {
        [void]$macros.RemoveChild($macro)
    }
    $changed = $true
}

if ($locations.Count -eq 0) {
    Write-Warning 'No native Make2D button was found in this layout. The separate Better Make2D toolbar is available instead.'
    exit 0
}

foreach ($section in @($macros, $icons)) {
    foreach ($node in $template.SelectNodes("/RhinoUI/$($section.Name)/*")) {
        $id = $node.GetAttribute('guid')
        $existing = $section.SelectSingleNode("*[@guid='$id']")
        if ($existing -and $existing.OuterXml -eq $node.OuterXml) { continue }
        $entry = $rui.ImportNode($node, $true)
        if ($existing) { [void]$section.ReplaceChild($entry, $existing) }
        else { [void]$section.AppendChild($entry) }
        $changed = $true
    }
}

$formerIcon = $icons.SelectSingleNode("icon[@guid='$formerHiddenIconId']")
if ($formerIcon -and !$rui.SelectSingleNode("//*[@bitmap_id='$formerHiddenIconId']")) {
    [void]$icons.RemoveChild($formerIcon)
    $changed = $true
}

if (!$changed) { Write-Output 'Better Make2D buttons are already installed.'; exit 0 }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$copy = "$RuiPath.p2-$stamp.tmp"
$backup = "$RuiPath.p2-$stamp.bak"
try {
    $rui.Save($copy)
    if ((Get-RuiHash $RuiPath) -ne $originalHash) { throw 'The toolbar file changed during installation. Close Rhino and retry.' }
    [IO.File]::Replace($copy, $RuiPath, $backup)
} finally {
    if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy }
}
Write-Output "Added Better Make2D buttons to: $($locations -join ', ')."
Write-Output "Toolbar backup: $backup"
