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

function Get-ButtonId([string]$ToolbarId, [string]$ButtonId) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes("$ToolbarId/$ButtonId"))
        return ([Guid]::new([byte[]]$bytes[0..15])).ToString()
    } finally { $sha.Dispose() }
}

$RuiPath = (Resolve-Path -LiteralPath $RuiPath).Path
$originalHash = (Get-FileHash -LiteralPath $RuiPath).Hash
$rui = Read-Rui $RuiPath
$template = Read-Rui $TemplatePath
$macros = $rui.SelectSingleNode('/RhinoUI/macros')
$icons = $rui.SelectSingleNode('/RhinoUI/icons')
if (!$macros -or !$icons) { throw 'The current toolbar file does not have the Rhino 9 macro and icon sections.' }

$make2dMacroIds = @($macros.SelectNodes('macro_item') | Where-Object {
    $_.SelectSingleNode('script').InnerText -match '^\s*!?\s*_?Make2D\s*$'
} | ForEach-Object { $_.GetAttribute('guid') })
$templateButtons = @($template.SelectNodes('/RhinoUI/tool_bars/tool_bar/tool_bar_item'))
$draftingId = '90fd89fc-e41f-49cc-bcf0-29e0d58017a1'
$changed = $false
$locations = @()

foreach ($bar in $rui.SelectNodes('/RhinoUI/tool_bars/tool_bar')) {
    $anchor = $bar.SelectNodes('tool_bar_item') | Where-Object {
        $make2dMacroIds -contains $_.SelectSingleNode('left_macro_id').InnerText
    } | Select-Object -First 1
    if (!$anchor) { continue }
    $barId = $bar.GetAttribute('guid')
    $name = $bar.SelectSingleNode('text/locale_1033').InnerText
    $locations += $name
    $buttons = if ($barId -eq $draftingId -or $name -eq 'Drafting') { $templateButtons } else { $templateButtons[0] }
    foreach ($button in $buttons) {
        $id = Get-ButtonId $barId $button.GetAttribute('guid')
        $existing = $bar.SelectSingleNode("tool_bar_item[@guid='$id']")
        if (!$existing) {
            $existing = $rui.ImportNode($button, $true)
            $existing.SetAttribute('guid', $id)
            [void]$bar.InsertAfter($existing, $anchor)
            $changed = $true
        }
        $anchor = $existing
    }
}

if ($locations.Count -eq 0) {
    Write-Warning 'No native Make2D button was found in this layout. The separate P2 Make2D toolbar is available instead.'
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

if (!$changed) { Write-Output 'P2 Make2D buttons are already installed.'; exit 0 }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$copy = "$RuiPath.p2-$stamp.tmp"
$backup = "$RuiPath.p2-$stamp.bak"
try {
    $rui.Save($copy)
    if ((Get-FileHash -LiteralPath $RuiPath).Hash -ne $originalHash) { throw 'The toolbar file changed during installation. Close Rhino and retry.' }
    [IO.File]::Replace($copy, $RuiPath, $backup)
} finally {
    if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy }
}
Write-Output "Added P2 Make2D buttons to: $($locations -join ', ')."
Write-Output "Toolbar backup: $backup"
