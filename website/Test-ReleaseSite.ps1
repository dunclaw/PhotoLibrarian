$ErrorActionPreference = 'Stop'

$siteRoot = $PSScriptRoot
$htmlPath = Join-Path $siteRoot 'index.html'
$privacyPath = Join-Path $siteRoot 'privacy.html'
$cssPath = Join-Path $siteRoot 'styles.css'
$configPath = Join-Path $siteRoot 'staticwebapp.config.json'

foreach ($requiredPath in @($htmlPath, $privacyPath, $cssPath, $configPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release-site file is missing: $requiredPath"
    }
}

$html = Get-Content -LiteralPath $htmlPath -Raw
$privacy = Get-Content -LiteralPath $privacyPath -Raw
$css = Get-Content -LiteralPath $cssPath -Raw
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json

foreach ($requiredText in @(
    '<html lang="en">',
    '<meta name="viewport"',
    '<link rel="stylesheet" href="styles.css">',
    '<h1 id="hero-title">',
    'Coming soon to Microsoft Store',
    'The public Store listing is not available yet',
    'href="privacy.html"',
    'https://github.com/dunclaw/PhotoLibrarian'
)) {
    if (-not $html.Contains($requiredText)) {
        throw "Release page is missing required content: $requiredText"
    }
}

foreach ($requiredText in @(
    '<html lang="en">',
    '<title>Privacy policy',
    'Last updated November 15, 2026',
    'does not upload your photo or video content',
    'Optional model downloads',
    'does not use advertising, analytics scripts, or tracking cookies',
    'href="index.html"'
)) {
    if (-not $privacy.Contains($requiredText)) {
        throw "Privacy page is missing required content: $requiredText"
    }
}

if (($html + $privacy) -match '(?i)<script|applicationinsights|google-analytics|googletagmanager|clarity\.ms') {
    throw 'The release pages must not load scripts or third-party tracking.'
}

if ($css -match '(?i)@import|https?://') {
    throw 'The release page must not load external styles or resources.'
}

if ($config.globalHeaders.'Content-Security-Policy' -notmatch "script-src 'none'") {
    throw 'The static site Content Security Policy must disable scripts.'
}

Write-Output 'Release site checks passed.'
