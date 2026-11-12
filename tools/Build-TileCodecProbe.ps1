[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'x86')]
    [string]$Architecture = 'x64',
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceRoot = Join-Path $repoRoot 'native\PhotoLibrarian.TileCodecProbe'
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$buildRoot = Join-Path $outputRoot 'build'
$sources = @($PSCommandPath) + @(Get-ChildItem $sourceRoot -File | Select-Object -ExpandProperty FullName)
$fingerprint = ($sources | ForEach-Object {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($_))).Replace('-', '') }
    finally { $hasher.Dispose() }
}) -join ''
$stamp = Join-Path $outputRoot 'build.sha256'
if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $fingerprint -and
    (Test-Path (Join-Path $outputRoot 'PhotoLibrarian.TileCodecProbe.dll')) -and
    (Test-Path (Join-Path $outputRoot 'PhotoLibrarian.CodecMemoryProbe.exe'))) { return }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$install = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if (-not $install) { throw 'Visual Studio C++ build tools are required for the tile feasibility probe.' }
$cmake = Join-Path $install 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $install 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$devcmd = Join-Path $install 'Common7\Tools\VsDevCmd.bat'
$target = switch ($Architecture) { 'x64' { 'amd64' } 'ARM64' { 'arm64' } 'x86' { 'x86' } }
New-Item $buildRoot -ItemType Directory -Force | Out-Null
$commandFile = Join-Path $buildRoot 'build.cmd'
$commands = @(
    "call `"$devcmd`" -arch=$target -host_arch=amd64",
    'if errorlevel 1 exit /b %errorlevel%',
    "`"$cmake`" -S `"$sourceRoot`" -B `"$buildRoot`" -G Ninja -DCMAKE_MAKE_PROGRAM=`"$ninja`" -DCMAKE_BUILD_TYPE=Release -DCMAKE_RUNTIME_OUTPUT_DIRECTORY=`"$outputRoot`"",
    'if errorlevel 1 exit /b %errorlevel%',
    "`"$cmake`" --build `"$buildRoot`" --parallel 2",
    'if errorlevel 1 exit /b %errorlevel%'
)
Set-Content $commandFile ("@echo off`r`n" + ($commands -join "`r`n")) -Encoding Ascii
& $env:ComSpec /d /c "`"$commandFile`""
if ($LASTEXITCODE -ne 0) { throw "Tile codec probe build failed: $LASTEXITCODE" }
Set-Content $stamp $fingerprint -Encoding Ascii
