[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'x86')]
    [string]$Architecture = 'x64',

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$vcpkgCommit = 'd186940b2f0c4e8e5b4ff804af57d2a26fbfa091'
$vcpkgToolSha256 = '2a4ae1467b9e797964c60c919d057b9e5dc1ec03f4b9632c1150026db25201f2'
$libRawSourceHash = 'a9453f8cff55302926c11c47ba7c8d04d81023bbbcad145551d2c0cc601de7eea8218d00a073e6b9ece845d3420897a2272a34fabf55f5b821183eaa7f66b3ef'
$repoRoot = Split-Path $PSScriptRoot -Parent
$nativeRoot = Join-Path $repoRoot 'native\PhotoLibrarian.RawDecoder'
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$repoHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
try {
    $repoHash = [BitConverter]::ToString(
        $repoHashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot))).Replace('-', '').Substring(0, 12).ToLowerInvariant()
}
finally {
    $repoHashAlgorithm.Dispose()
}
$buildRoot = Join-Path $env:TEMP "PhotoRaw\$repoHash\$Architecture"
$installedRoot = Join-Path $buildRoot 'vcpkg_installed'
$triplet = switch ($Architecture) {
    'x64' { 'x64-windows-static' }
    'ARM64' { 'arm64-windows-static' }
    'x86' { 'x86-windows-static' }
}
$targetArch = switch ($Architecture) {
    'x64' { 'amd64' }
    'ARM64' { 'arm64' }
    'x86' { 'x86' }
}

New-Item $outputRoot -ItemType Directory -Force | Out-Null
$buildToolsRoot = Join-Path $env:LOCALAPPDATA 'PhotoLibrarian\BuildTools'
$vcpkgRoot = Join-Path $buildToolsRoot "vcpkg-$vcpkgCommit"
New-Item $buildToolsRoot -ItemType Directory -Force | Out-Null

if (-not (Test-Path (Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat'))) {
    if (Test-Path $vcpkgRoot) {
        throw "The pinned vcpkg directory is incomplete: $vcpkgRoot"
    }

    & git init --quiet $vcpkgRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Could not initialize the pinned vcpkg checkout: $LASTEXITCODE"
    }
    & git -C $vcpkgRoot remote add origin https://github.com/microsoft/vcpkg.git
    if ($LASTEXITCODE -ne 0) {
        throw "Could not configure the vcpkg source remote: $LASTEXITCODE"
    }
    & git -C $vcpkgRoot fetch --quiet --depth 1 origin $vcpkgCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Could not fetch the pinned vcpkg commit $vcpkgCommit."
    }
    & git -C $vcpkgRoot checkout --quiet --detach FETCH_HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check out the pinned vcpkg commit $vcpkgCommit."
    }
}

$checkedOutCommit = (& git -C $vcpkgRoot rev-parse HEAD).Trim()
if ($checkedOutCommit -ne $vcpkgCommit) {
    throw "Expected vcpkg $vcpkgCommit but found $checkedOutCommit at $vcpkgRoot."
}

$bootstrap = Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat'
& $env:ComSpec /d /c "`"$bootstrap`" -disableMetrics"
if ($LASTEXITCODE -ne 0) {
    throw "Bootstrapping pinned vcpkg failed with exit code $LASTEXITCODE."
}

$vcpkgToolHashAlgorithm = [System.Security.Cryptography.SHA256]::Create()
try {
    $actualVcpkgToolSha256 = [BitConverter]::ToString(
        $vcpkgToolHashAlgorithm.ComputeHash(
            [IO.File]::ReadAllBytes((Join-Path $vcpkgRoot 'vcpkg.exe')))).Replace('-', '').ToLowerInvariant()
}
finally {
    $vcpkgToolHashAlgorithm.Dispose()
}
if ($actualVcpkgToolSha256 -ne $vcpkgToolSha256) {
    throw "Pinned vcpkg tool SHA-256 mismatch: expected $vcpkgToolSha256; found $actualVcpkgToolSha256."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw 'Visual Studio Installer vswhere.exe was not found. Install the Visual C++ build tools.'
}

$vsInstall = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
if (-not $vsInstall) {
    throw 'A Visual Studio C++ toolset was not found. Install the Desktop development with C++ workload.'
}

$vsDevCmd = Join-Path $vsInstall 'Common7\Tools\VsDevCmd.bat'
$cmake = Join-Path $vsInstall 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if (-not (Test-Path $vsDevCmd) -or -not (Test-Path $cmake)) {
    throw 'Visual Studio C++ build tools and CMake are required to build the pinned RAW decoder.'
}

$commandFile = Join-Path $buildRoot 'build-raw-decoder.cmd'
New-Item $buildRoot -ItemType Directory -Force | Out-Null
$configureArguments = @(
    '-S', $nativeRoot,
    '-B', $buildRoot,
    '-G', 'NMake Makefiles',
    '-DCMAKE_BUILD_TYPE=Release',
    "-DCMAKE_TOOLCHAIN_FILE=$(Join-Path $vcpkgRoot 'scripts\buildsystems\vcpkg.cmake')",
    "-DVCPKG_TARGET_TRIPLET=$triplet",
    "-DVCPKG_INSTALLED_DIR=$installedRoot",
    "-DVCPKG_MANIFEST_DIR=$nativeRoot",
    "-DCMAKE_RUNTIME_OUTPUT_DIRECTORY=$outputRoot"
)
$quotedConfigureArguments = $configureArguments | ForEach-Object { '"' + $_.Replace('"', '""') + '"' }
$buildCommand = @(
    "call `"$vsDevCmd`" -arch=$targetArch -host_arch=amd64",
    "if errorlevel 1 exit /b %errorlevel%",
    "`"$cmake`" $($quotedConfigureArguments -join ' ')",
    "if errorlevel 1 exit /b %errorlevel%",
    "`"$cmake`" --build `"$buildRoot`" --target PhotoLibrarian.RawDecoder",
    "if errorlevel 1 exit /b %errorlevel%"
) -join "`r`n"
Set-Content -Path $commandFile -Value "@echo off`r`n$buildCommand`r`n" -Encoding Ascii
& $env:ComSpec /d /c "`"$commandFile`""
if ($LASTEXITCODE -ne 0) {
    throw "The LibRaw native build failed with exit code $LASTEXITCODE."
}

$nativeDll = Join-Path $outputRoot 'PhotoLibrarian.RawDecoder.dll'
if (-not (Test-Path $nativeDll)) {
    throw "The native build did not produce the decoder DLL: $nativeDll"
}

$libRawSource = Join-Path $buildRoot '_deps\libraw\LibRaw-0.22.3'
$licenseSource = Join-Path $libRawSource 'LICENSE.CDDL'
$copyrightSource = Join-Path $libRawSource 'COPYRIGHT'
$zlibLicenseSource = Join-Path $installedRoot "$triplet\share\zlib\copyright"
foreach ($requiredFile in @($licenseSource, $copyrightSource, $zlibLicenseSource)) {
    if (-not (Test-Path $requiredFile)) {
        throw "A required third-party license or source file is missing: $requiredFile"
    }
}

Copy-Item $licenseSource (Join-Path $outputRoot 'LibRaw-LICENSE.CDDL.txt') -Force
Copy-Item $copyrightSource (Join-Path $outputRoot 'LibRaw-COPYRIGHT.txt') -Force
Copy-Item $zlibLicenseSource (Join-Path $outputRoot 'zlib-LICENSE.txt') -Force

$dcbSource = Join-Path $libRawSource 'src\demosaic\dcb_demosaic.cpp'
$x3fSource = Join-Path $libRawSource 'src\x3f\x3f_parse_process.cpp'
foreach ($licenseEntry in @(
        @{
            Source = $dcbSource
            Destination = 'LibRaw-DCB-FBDD-BSD-LICENSE.txt'
            Name = 'DCB demosaic and FBDD denoise'
        },
        @{
            Source = $x3fSource
            Destination = 'LibRaw-X3F-BSD-LICENSE.txt'
            Name = 'X3F support'
        })) {
    $header = [regex]::Match(
        [IO.File]::ReadAllText($licenseEntry.Source),
        '\A/\*(.*?)\*/',
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $header.Success) {
        throw "The upstream license header is missing from $($licenseEntry.Source)."
    }

    Set-Content `
        -Path (Join-Path $outputRoot $licenseEntry.Destination) `
        -Value "$($licenseEntry.Name) upstream license and notices`r`n$($header.Groups[1].Value.Trim())" `
        -Encoding utf8
}

$adobeDngLicense = @'
Adobe DNG SDK 1.4 MIT License

Copyright (c) 2005 Adobe Systems Incorporated

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
Set-Content `
    -Path (Join-Path $outputRoot 'LibRaw-Adobe-DNG-SDK-MIT-LICENSE.txt') `
    -Value $adobeDngLicense `
    -Encoding utf8

$sourceArchive = Join-Path $outputRoot 'LibRaw-0.22.3-source.tar.gz'
if (-not (Test-Path $sourceArchive)) {
    Invoke-WebRequest `
        -Uri 'https://github.com/LibRaw/LibRaw/archive/refs/tags/0.22.3.tar.gz' `
        -OutFile $sourceArchive
}
$sha512Algorithm = [System.Security.Cryptography.SHA512]::Create()
try {
    $actualHash = [BitConverter]::ToString(
        $sha512Algorithm.ComputeHash([IO.File]::ReadAllBytes($sourceArchive))).Replace('-', '').ToLowerInvariant()
}
finally {
    $sha512Algorithm.Dispose()
}
if ($actualHash -ne $libRawSourceHash) {
    throw "LibRaw source archive SHA-512 mismatch: expected $libRawSourceHash; found $actualHash."
}

$dllSize = (Get-Item $nativeDll).Length
$notices = @"
PhotoLibrarian RAW decoder third-party notices

Native bundle architecture: $Architecture
LibRaw 0.22.3: CDDL-1.0; static-linked into PhotoLibrarian.RawDecoder.dll.
Source archive included at ThirdPartySources\LibRaw-0.22.3-source.tar.gz
(SHA-512 $libRawSourceHash).
LibRaw CDDL text, upstream copyright notices, and third-party BSD/MIT notices
are included alongside this file. X3F support is disabled in this build.
zlib is statically linked; its license is included alongside this file.

LibRaw build options: OpenMP, LCMS, Jasper, JPEG/DNG-lossy, RawSpeed, examples,
dcraw debug, X3F tools, and 6by9rpi support disabled. DNG deflate support is
enabled through zlib. The static decoder DLL is $dllSize bytes.

The LibRaw-cmake build helper is fetched at commit
eb98e4325aef2ce85d2eb031c2ff18640ca616d3 (SHA-512
63e68a4d30286ec3aa97168d46b7a1199268099ae27b61abcc92e93ec30e48d364086227983a1d724415e5f4da44d905422f30192453b95f31040e5f8469c3f9).
The helper is build-time only and is not included in the application.
vcpkg is pinned to commit $vcpkgCommit; it is a build-time dependency manager
and is not included in the application. Its 2026-09-26 tool binary is pinned
by SHA-256 $vcpkgToolSha256.
"@
Set-Content -Path (Join-Path $outputRoot 'ThirdPartyNotices.RawDecoder.txt') -Value $notices -Encoding utf8

Write-Output "Built $Architecture RAW decoder: $nativeDll ($dllSize bytes)"
