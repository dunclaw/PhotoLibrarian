param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [switch]$SkipBuild,
    [switch]$Preview,
    [switch]$ViewerPreview,
    [switch]$Slideshow,
    [switch]$ViewerContextMenu,
    [switch]$MotionPhoto,
    [switch]$EditHistory,
    [switch]$Comparison,
    [switch]$EditorComparison,
    [string]$MotionPhotoValidationPath
)

$ErrorActionPreference = 'Stop'
if ($EditorComparison -and ($Comparison -or $EditHistory -or $Slideshow -or $ViewerContextMenu -or $MotionPhoto -or $ViewerPreview)) {
    throw '-EditorComparison cannot be combined with another suite.'
}
if ($Comparison -and ($EditHistory -or $Slideshow -or $ViewerContextMenu -or $MotionPhoto -or $ViewerPreview)) {
    throw '-Comparison cannot be combined with another suite.'
}
if ($EditHistory -and ($Slideshow -or $ViewerContextMenu -or $MotionPhoto -or $ViewerPreview)) {
    throw '-EditHistory cannot be combined with another suite.'
}
if ($ViewerPreview -and ($Preview -or $Slideshow -or $ViewerContextMenu -or $MotionPhoto)) {
    throw '-ViewerPreview cannot be combined with another suite or preview.'
}
if ($MotionPhotoValidationPath -and -not $MotionPhoto) {
    throw '-MotionPhotoValidationPath requires -MotionPhoto.'
}
if ($MotionPhotoValidationPath) {
    $MotionPhotoValidationPath = (Resolve-Path -LiteralPath $MotionPhotoValidationPath).Path
    if ($MotionPhotoValidationPath.Contains('"')) { throw 'Validation path must not contain quotes.' }
}
$project = Join-Path $PSScriptRoot '..\src\PhotoLibrarian.AccessibilityTests\PhotoLibrarian.AccessibilityTests.csproj'
Get-Command dotnet, winapp -ErrorAction Stop | Out-Null

if (-not $SkipBuild) {
    & dotnet build $project "-p:Platform=$Platform" -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Accessibility test host build failed.' }
}
$output = (& dotnet msbuild $project "-p:Platform=$Platform" -getProperty:TargetDir).Trim()
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $output)) {
    throw 'Could not locate accessibility test host output.'
}

Push-Location $output
try {
    & winapp manifest generate . --package-name PhotoLibrarian.AccessibilityTests `
        --publisher-name 'CN=PhotoLibrarianTests' --executable .\PhotoLibrarian.AccessibilityTests.exe --if-exists Skip
    if ($LASTEXITCODE -ne 0) { throw 'Could not generate test-only package identity.' }
    $resultPath = Join-Path $output 'AppX\accessibility-results.json'
    if ($Slideshow) { $resultPath = Join-Path $output 'AppX\slideshow-results.json' }
    if ($MotionPhoto) { $resultPath = Join-Path $output 'AppX\motion-photo-results.json' }
    if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath }
    $arguments = @('run', '.', '--manifest', '.\Package.appxmanifest',
        '--exe', 'PhotoLibrarian.AccessibilityTests.exe', '--unregister-on-exit')
    if ($MotionPhoto) {
        $testArguments = if ($Preview) { '--motion-photo --preview' } else { '--motion-photo' }
        if ($MotionPhotoValidationPath) {
            $testArguments += ' --motion-photo-sample "' + $MotionPhotoValidationPath + '"'
        }
        $arguments += @('--args', $testArguments)
    }
    elseif ($EditorComparison) {
        $arguments += @('--args', $(if ($Preview) { '--editor-comparison --preview' } else { '--editor-comparison' }))
    }
    elseif ($Comparison) {
        $arguments += @('--args', $(if ($Preview) { '--comparison --preview' } else { '--comparison' }))
    }
    elseif ($EditHistory) {
        $arguments += @('--args', $(if ($Preview) { '--edit-history --preview' } else { '--edit-history' }))
    }
    elseif ($Slideshow) { $arguments += @('--args', '--slideshow') }
    elseif ($Preview) { $arguments += @('--args', '--preview') }
    elseif ($ViewerContextMenu) { $arguments += @('--args', '--viewer-context-menu') }
    elseif ($ViewerPreview) { $arguments += @('--args', '--viewer-preview') }
    & winapp @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Accessibility test host failed to run.' }
    if ((-not $Preview -or $MotionPhoto) -and -not $ViewerPreview) {
        if (-not (Test-Path $resultPath)) { throw 'Accessibility test host did not produce results.' }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $result.passed | ForEach-Object { Write-Host "PASS: $_" }
        if ($result.failure) { throw $result.failure }
        $suite = if ($EditorComparison) { 'editor before/after' } elseif ($Comparison) { 'compare and survey' } elseif ($Slideshow) { 'slideshow' } elseif ($MotionPhoto) { 'motion-photo lifecycle' } elseif ($ViewerContextMenu) { 'viewer context menu' } elseif ($EditHistory) { 'unified edit history' } else { 'grid and viewer command accessibility' }
        Write-Host "Passed $($result.passed.Count) $suite checks."
    }
}
finally {
    Pop-Location
}
