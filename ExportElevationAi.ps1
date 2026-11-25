param(
    [Parameter(Mandatory)][string]$SvgPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [switch]$AllowLargeCanvas
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SvgPath).Path
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([IO.Path]::GetExtension($destination) -ine '.ai' -or (Test-Path -LiteralPath $destination)) {
    throw 'Use a new .ai output path.'
}
$folder = [IO.Path]::GetDirectoryName($destination)
[void][IO.Directory]::CreateDirectory($folder)
$staging = Join-Path $folder ('.' + [guid]::NewGuid().ToString('N') + '.ai')
$importSvg = [IO.Path]::ChangeExtension($staging, '.svg')
$svg = [xml](Get-Content -LiteralPath $source -Raw -Encoding utf8)
$namespace = [Xml.XmlNamespaceManager]::new($svg.NameTable)
$namespace.AddNamespace('svg', 'http://www.w3.org/2000/svg')
$inkscape = 'http://www.inkscape.org/namespaces/inkscape'
$root = $svg.DocumentElement
$culture = [Globalization.CultureInfo]::InvariantCulture
if ($root.LocalName -ne 'svg' -or !$root.GetAttribute('data-model-scale') -or
    $root.GetAttribute('width') -notmatch '^([0-9.]+)mm$') {
    throw 'Expected a BetterMake2D SVG with paper dimensions in millimeters.'
}
$width = [double]::Parse($Matches[1], $culture)
if ($root.GetAttribute('height') -notmatch '^([0-9.]+)mm$') { throw 'The SVG has no physical paper height.' }
$height = [double]::Parse($Matches[1], $culture)
$scale = [double]::Parse($root.GetAttribute('data-model-scale'), $culture)
if (([double]::IsNaN($scale) -or [double]::IsInfinity($scale)) -or $scale -le 0 -or $width -lt .15 -or $height -lt .15) {
    throw 'The SVG contains an invalid scale or paper size.'
}
$longEdge = [Math]::Max($width, $height)
if ($longEdge -lt 100 -or $longEdge -gt 5500) {
    $modelEdge = ($longEdge - .15) * $scale
    $sequence = @(1, 2, 5, 10, 20, 25, 50, 75, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000,
        20000, 25000, 50000, 100000, 200000, 250000, 500000, 1000000)
    if ($longEdge -lt 100) { [array]::Reverse($sequence) }
    $pick = $sequence | Where-Object { ($modelEdge / $_ + .15) -ge 300 -and ($modelEdge / $_ + .15) -le 1200 } |
        Select-Object -First 1
    if (!$pick) { $pick = $modelEdge / (600 - .15) }
    $recommendation = if ($pick -gt 0) { " Regenerate the SVG with RunElevation.ps1 -PaperScale $($pick.ToString('G8', $culture))." } else { '' }
    $size = "$($width.ToString('F1', $culture)) x $($height.ToString('F1', $culture)) mm"
    if ($longEdge -gt 57795) { throw "Paper size $size exceeds the 57,795 mm Illustrator large-canvas limit.$recommendation" }
    if ($longEdge -gt 5779.5 -and !$AllowLargeCanvas) {
        throw "Paper size $size exceeds the 5,779.5 mm standard canvas. Pass -AllowLargeCanvas to keep its scale.$recommendation"
    }
    if ($longEdge -lt 100) { Write-Warning "Paper size $size has a long edge under 100 mm. A 0.15 mm stroke may look heavy.$recommendation" }
    else { Write-Warning "Paper size $size is near or above the standard canvas. The output artboard and scale will be checked.$recommendation" }
}
$categories = @($root.SelectNodes('svg:g', $namespace) | ForEach-Object {
    $category = $_
    $layers = @($category.SelectNodes('svg:g', $namespace) | ForEach-Object {
        $layer = $_
        $paths = $layer.SelectNodes('.//svg:path', $namespace)
        $fills = 0
        $loops = 0
        $closedLines = 0
        $roundCaps = 0
        $dashed = 0
        foreach ($path in $paths) {
            if ($path.GetAttribute('fill-rule') -eq 'evenodd') {
                $fills++
                $loops += [regex]::Matches($path.GetAttribute('d'), '\bZ\b').Count
            }
            else {
                if ($path.GetAttribute('d') -match '\bZ\b') { $closedLines++ }
                if ($path.GetAttribute('stroke-linecap') -eq 'round') { $roundCaps++ }
                if ($path.GetAttribute('stroke-dasharray') -eq '0.7 0.4') { $dashed++ }
            }
        }
        @{
            id = $layer.GetAttribute('id')
            label = $layer.GetAttribute('label', $inkscape)
            regions = $fills
            lines = $paths.Count - $fills
            loops = $loops
            closedLines = $closedLines
            roundCaps = $roundCaps
            dashed = $dashed
        }
    })
    @{ id = $category.GetAttribute('id'); label = $category.GetAttribute('label', $inkscape); layers = $layers }
})
$layers = @($categories | ForEach-Object { $_.layers })
$declaredPaths = ($layers | Measure-Object -Property { $_.regions + $_.lines } -Sum).Sum
if ($layers.Count -eq 0 -or $declaredPaths -ne $root.SelectNodes('.//svg:path', $namespace).Count -or
    @($layers | Where-Object { !$_.label -or !$_.id }).Count -gt 0) {
    throw 'The SVG has geometry outside its declared original layers.'
}
$largeCanvas = $longEdge -gt 5779.5
$payload = @{
    source = $(if ($largeCanvas) { $importSvg } else { $source })
    staging = $staging
    width = $width
    height = $height
    largeCanvas = $largeCanvas
    drawingTolerance = [math]::Max(.002, .008 / $scale)
    categories = $categories
} | ConvertTo-Json -Depth 8 -Compress
$script = Get-Content -Raw -Encoding utf8 -LiteralPath (Join-Path $PSScriptRoot 'ExportElevationAi.jsx')

try {
    if ($largeCanvas) {
        # Illustrator uses one-tenth coordinates in its native large canvas.
        $root.SetAttribute('width', ($width / 10).ToString('G17', $culture) + 'mm')
        $root.SetAttribute('height', ($height / 10).ToString('G17', $culture) + 'mm')
        $svg.Save($importSvg)
    }
    $illustrator = New-Object -ComObject Illustrator.Application
    try {
        $report = $illustrator.DoJavaScript($script.Replace('BETTERMAKE2D_INPUT', $payload))
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($illustrator)
    }
    [IO.File]::Move($staging, $destination)
    $report
    Write-Output "Layered AI: $destination"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging }
    if (Test-Path -LiteralPath $importSvg) { Remove-Item -LiteralPath $importSvg }
}

