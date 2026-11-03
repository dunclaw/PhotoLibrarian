param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [switch]$SkipBuild,
    [switch]$Preview,
    [switch]$ViewerContextMenu
)

$ErrorActionPreference = 'Stop'
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
    if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath }
    $arguments = @('run', '.', '--manifest', '.\Package.appxmanifest',
        '--exe', 'PhotoLibrarian.AccessibilityTests.exe', '--unregister-on-exit')
    if ($Preview) { $arguments += @('--args', '--preview') }
    elseif ($ViewerContextMenu) { $arguments += @('--args', '--viewer-context-menu') }
    & winapp @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Accessibility test host failed to run.' }
    if (-not $Preview) {
        if (-not (Test-Path $resultPath)) { throw 'Accessibility test host did not produce results.' }
        $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
        $result.passed | ForEach-Object { Write-Host "PASS: $_" }
        if ($result.failure) { throw $result.failure }
        Write-Host "Passed $($result.passed.Count) accessibility checks."
    }
}
finally {
    Pop-Location
}
