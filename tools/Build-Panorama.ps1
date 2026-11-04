[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'x86')]
    [string]$Architecture = 'x64',
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$Target = 'PhotoLibrarian.Panorama'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Get-SourceHash([string]$Path, [string]$Algorithm) {
    $hasher = [Security.Cryptography.HashAlgorithm]::Create($Algorithm)
    try {
        return [BitConverter]::ToString($hasher.ComputeHash([IO.File]::ReadAllBytes($Path))).Replace('-', '').ToLowerInvariant()
    }
    finally { $hasher.Dispose() }
}
$repoRoot = Split-Path $PSScriptRoot -Parent
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
try {
    $repoHash = [BitConverter]::ToString(
        $hashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot))).Replace('-', '').Substring(0, 12)
}
finally {
    $hashAlgorithm.Dispose()
}
$buildRoot = Join-Path $env:TEMP "PhotoPanorama\$repoHash\$Architecture"
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$archive = Join-Path $repoRoot 'src\PhotoLibrarian.Core\obj\PanoramaDownloads\opencv-4.12.0.tar.gz'
$expectedHash = '8ac63ddd61e22cc0eaeafee4f30ae6e1cab05fc4929e2cea29070203b9ca8dfead12cc0fd7c4a87b65c1e20ec6b9ab4865a1b83fad33d114fc0708fdf107c51b'
New-Item $buildRoot, $outputRoot, (Split-Path $archive) -ItemType Directory -Force | Out-Null
$nativeFiles = @(
    $PSCommandPath,
    (Join-Path $repoRoot 'native\PhotoLibrarian.Panorama\CMakeLists.txt'),
    (Join-Path $repoRoot 'native\PhotoLibrarian.Panorama\Panorama.cpp')
)
$fingerprint = ($nativeFiles | ForEach-Object { Get-SourceHash $_ 'SHA256' }) -join ''
$stampPath = Join-Path $outputRoot 'build.sha256'
if ($Target -eq 'PhotoLibrarian.Panorama' -and
    (Test-Path (Join-Path $outputRoot 'PhotoLibrarian.Panorama.dll')) -and
    (Test-Path (Join-Path $outputRoot 'ThirdPartyNotices.Panorama.txt')) -and
    (Test-Path $stampPath) -and (Get-Content $stampPath -Raw).Trim() -eq $fingerprint) {
    return
}
if (-not (Test-Path $archive)) {
    $temporaryArchive = "$archive.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        & "$env:SystemRoot\System32\curl.exe" --fail --location --silent --show-error `
            'https://github.com/opencv/opencv/archive/refs/tags/4.12.0.tar.gz' --output $temporaryArchive
        if ($LASTEXITCODE -ne 0) { throw 'Could not download the pinned OpenCV source archive.' }
        if ((Get-SourceHash $temporaryArchive 'SHA512') -ne $expectedHash) {
            throw 'The downloaded OpenCV archive failed checksum verification.'
        }
        Move-Item $temporaryArchive $archive -Force
    }
    finally {
        if (Test-Path $temporaryArchive) { Remove-Item $temporaryArchive }
    }
}
if ((Get-SourceHash $archive 'SHA512') -ne $expectedHash) {
    throw 'The pinned OpenCV archive failed checksum verification.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vsInstall = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if (-not $vsInstall) { throw 'Visual Studio C++ build tools are required for the panorama module.' }
$cmake = Join-Path $vsInstall 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $vsInstall 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$vsDevCmd = Join-Path $vsInstall 'Common7\Tools\VsDevCmd.bat'
$targetArch = switch ($Architecture) { 'x64' { 'amd64' } 'ARM64' { 'arm64' } 'x86' { 'x86' } }
$processor = switch ($Architecture) { 'x64' { 'AMD64' } 'ARM64' { 'ARM64' } 'x86' { 'x86' } }
$commandFile = Join-Path $buildRoot 'build-panorama.cmd'
$commands = @(
    "call `"$vsDevCmd`" -arch=$targetArch -host_arch=amd64",
    'if errorlevel 1 exit /b %errorlevel%',
    "`"$cmake`" -S `"$repoRoot\native\PhotoLibrarian.Panorama`" -B `"$buildRoot`" -G Ninja -DCMAKE_MAKE_PROGRAM=`"$ninja`" -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=Windows -DCMAKE_SYSTEM_PROCESSOR=$processor -DOPENCV_ARCHIVE=`"$archive`" -DCMAKE_RUNTIME_OUTPUT_DIRECTORY=`"$outputRoot`"",
    'if errorlevel 1 exit /b %errorlevel%',
    "`"$cmake`" --build `"$buildRoot`" --target $Target --parallel 4",
    'if errorlevel 1 exit /b %errorlevel%'
)
Set-Content $commandFile ("@echo off`r`n" + ($commands -join "`r`n")) -Encoding Ascii
& $env:ComSpec /d /c "`"$commandFile`""
if ($LASTEXITCODE -ne 0) { throw "The native panorama build failed with exit code $LASTEXITCODE." }
if ($Target -ne 'PhotoLibrarian.Panorama') { return }

$sourceRoot = Join-Path $buildRoot '_deps\opencv-4.12.0'
$notices = [Collections.Generic.List[string]]::new()
$notices.Add("PhotoLibrarian.Panorama includes statically linked OpenCV 4.12.0 (Apache-2.0), FLANN (BSD), SoftFloat (BSD), fdlibm (permissive Sun license), and zlib (zlib license).`r`nSource: https://github.com/opencv/opencv/tree/4.12.0`r`nOnly core, imgproc, features2d, calib3d, flann, and stitching modules are built. Non-free algorithms, external media codecs, GPU backends, Python, and network integrations are not enabled.`r`n")
foreach ($license in @('COPYRIGHT', 'LICENSE', '3rdparty\zlib\LICENSE')) {
    $notices.Add((Get-Content (Join-Path $sourceRoot $license) -Raw))
}
$seen = [Collections.Generic.HashSet[string]]::new()
foreach ($module in @('core', 'imgproc', 'features2d', 'calib3d', 'flann', 'stitching')) {
    foreach ($file in Get-ChildItem (Join-Path $sourceRoot "modules\$module") -Recurse -File -Include '*.h', '*.hpp', '*.cpp', '*.inl') {
        $text = Get-Content $file.FullName -Raw
        $prefix = [regex]::Match($text, '\A\s*((?:(?://[^\r\n]*(?:\r?\n|$))|(?:/\*[\s\S]*?\*/)|\s)+)').Value.Trim()
        if ($prefix -match 'Redistribution and use|Permission.*(?:use|copy)|Software License Agreement' -and $seen.Add($prefix)) {
            $notices.Add("`r`n--- $($file.FullName.Substring($sourceRoot.Length + 1)) ---`r`n$prefix")
        }
    }
}
Set-Content (Join-Path $outputRoot 'ThirdPartyNotices.Panorama.txt') ($notices -join "`r`n") -Encoding UTF8
Set-Content $stampPath $fingerprint -Encoding Ascii
