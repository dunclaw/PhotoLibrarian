$ErrorActionPreference = 'Stop'

$siteRoot = $PSScriptRoot
$htmlPath = Join-Path $siteRoot 'index.html'
$cssPath = Join-Path $siteRoot 'styles.css'
$configPath = Join-Path $siteRoot 'staticwebapp.config.json'

foreach ($requiredPath in @($htmlPath, $cssPath, $configPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release-site file is missing: $requiredPath"
    }
}

$html = Get-Content -LiteralPath $htmlPath -Raw
$css = Get-Content -LiteralPath $cssPath -Raw
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

foreach ($requiredText in @(
    '<html lang="en">',
    '<meta name="viewport"',
    '<link rel="stylesheet" href="styles.css">',
    '<h1 id="hero-title">',
    'Coming soon to Microsoft Store',
    'The public Store listing is not available yet',
    'https://github.com/dunclaw/PhotoLibrarian'
)) {
    if (-not $html.Contains($requiredText)) {
        throw "Release page is missing required content: $requiredText"
    }
}

if ($html -match '(?i)<script|applicationinsights|google-analytics|googletagmanager|clarity\.ms') {
    throw 'The release page must not load scripts or third-party tracking.'
}

if ($css -match '(?i)@import|https?://') {
    throw 'The release page must not load external styles or resources.'
}

if ($config.globalHeaders.'Content-Security-Policy' -notmatch "script-src 'none'") {
    throw 'The static site Content Security Policy must disable scripts.'
}

Write-Output 'Release site checks passed.'
