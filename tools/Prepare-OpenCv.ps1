[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64', 'Win32')]
    [string]$Architecture = 'x64',
    [Parameter(Mandatory = $true)]
    [string]$BuildDirectory,
    [Parameter(Mandatory = $true)]
    [string]$WorkspaceRoot,
    [Parameter(Mandatory = $true)]
    [string]$VisualStudioDirectory,
    [Parameter(Mandatory = $true)]
    [string]$VisualStudioVersion,
    [Parameter(Mandatory = $true)]
    [string]$PlatformToolset,
    [Parameter(Mandatory = $true)]
    [string]$VCToolsVersion
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
$lockHasher = [Security.Cryptography.SHA256]::Create()
try {
    $lockKey = [BitConverter]::ToString($lockHasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToLowerInvariant())))
}
finally { $lockHasher.Dispose() }
$sourceLock = [Threading.Mutex]::new($false, "Local\PhotoLibrarian-NativeSources-$lockKey")
$lockHeld = $false
try {
    $lockHeld = $sourceLock.WaitOne()
    $dependencies = ([xml](Get-Content (Join-Path $repoRoot 'native\Dependencies.props') -Raw)).Project.PropertyGroup
    $buildRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($BuildDirectory)
    $outputRoot = $buildRoot
    $archive = Join-Path $WorkspaceRoot "sources\opencv-$($dependencies.OpenCvVersion).tar.gz"
    $expectedHash = $dependencies.OpenCvSha512
    New-Item $buildRoot, $outputRoot, (Split-Path $archive) -ItemType Directory -Force | Out-Null
    $nativeFiles = @(
        $PSCommandPath,
        (Join-Path $repoRoot 'native\Dependencies.props'),
        (Join-Path $repoRoot 'native\PhotoLibrarian.Panorama\CMakeLists.txt'),
        (Join-Path $repoRoot 'native\OpenCv.vcxproj')
    )
    $fingerprint = (($nativeFiles | ForEach-Object { Get-SourceHash $_ 'SHA256' }) -join '') +
        "|$VisualStudioDirectory|$VisualStudioVersion|$PlatformToolset|$VCToolsVersion|$Architecture"
    $stampPath = Join-Path $outputRoot 'build.sha256'
    if ((Test-Path (Join-Path $buildRoot 'opencv\modules\stitching\opencv_stitching.vcxproj')) -and
        (Test-Path (Join-Path $outputRoot 'ThirdPartyNotices.Panorama.txt')) -and
        (Test-Path $stampPath) -and (Get-Content $stampPath -Raw).Trim() -eq $fingerprint) {
        return
    }
    if (-not (Test-Path $archive)) {
        $temporaryArchive = "$archive.$([Guid]::NewGuid().ToString('N')).tmp"
        try {
            & "$env:SystemRoot\System32\curl.exe" --fail --location --silent --show-error `
                $dependencies.OpenCvUrl --output $temporaryArchive
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
    $cmake = Join-Path $VisualStudioDirectory 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    if (-not (Test-Path $cmake)) { throw 'Install the Visual Studio C++ CMake tools to configure upstream OpenCV.' }
    $major = ([version]$VisualStudioVersion).Major
    $generator = switch ($major) {
        17 { 'Visual Studio 17 2022' }
        18 { 'Visual Studio 18 2026' }
        default { throw "Unsupported OpenCV Visual Studio generator version: $VisualStudioVersion." }
    }
    $processor = switch ($Architecture) { 'x64' { 'AMD64' } 'ARM64' { 'ARM64' } 'Win32' { 'x86' } }
    # Only upstream configuration/code generation uses CMake. MSBuild compiles the generated VC projects.
    & $cmake -S "$repoRoot\native\PhotoLibrarian.Panorama" -B $buildRoot `
        -G $generator -A $Architecture -T "$PlatformToolset,version=$VCToolsVersion" `
        -DCMAKE_SYSTEM_NAME=Windows "-DCMAKE_SYSTEM_PROCESSOR=$processor" -UCPU_BASELINE -UCPU_DISPATCH `
        "-DCMAKE_GENERATOR_INSTANCE=$VisualStudioDirectory" "-DOPENCV_ARCHIVE=$archive" `
        "-DOPENCV_VERSION=$($dependencies.OpenCvVersion)" "-DOPENCV_SHA512=$expectedHash"
    if ($LASTEXITCODE -ne 0) { throw "OpenCV configuration failed with exit code $LASTEXITCODE." }

    $sourceRoot = Join-Path $buildRoot "_deps\opencv-$($dependencies.OpenCvVersion)"
    $notices = [Collections.Generic.List[string]]::new()
    $notices.Add("PhotoLibrarian.Panorama includes statically linked OpenCV $($dependencies.OpenCvVersion) (Apache-2.0), FLANN (BSD), SoftFloat (BSD), fdlibm (permissive Sun license), and zlib (zlib license).`r`nSource: https://github.com/opencv/opencv/tree/$($dependencies.OpenCvVersion)`r`nOnly core, imgproc, features2d, calib3d, flann, and stitching modules are built. Non-free algorithms, external media codecs, GPU backends, Python, and network integrations are not enabled.`r`n")
    foreach ($license in @('COPYRIGHT', 'LICENSE', '3rdparty\zlib\LICENSE')) {
        $notices.Add((Get-Content (Join-Path $sourceRoot $license) -Raw))
    }
    $seen = [Collections.Generic.HashSet[string]]::new()
    foreach ($directory in @('modules\core', 'modules\imgproc', 'modules\features2d', 'modules\calib3d', 'modules\flann', 'modules\stitching', 'hal\carotene')) {
        foreach ($file in Get-ChildItem (Join-Path $sourceRoot $directory) -Recurse -File -Include '*.h', '*.hpp', '*.cpp', '*.inl') {
            $text = Get-Content $file.FullName -Raw
            $prefix = [regex]::Match($text, '\A\s*((?:(?://[^\r\n]*(?:\r?\n|$))|(?:/\*[\s\S]*?\*/)|\s)+)').Value.Trim()
            if ($prefix -match 'Redistribution and use|Permission.*(?:use|copy)|Software License Agreement' -and $seen.Add($prefix)) {
                $notices.Add("`r`n--- $($file.FullName.Substring($sourceRoot.Length + 1)) ---`r`n$prefix")
            }
        }
    }
    Set-Content (Join-Path $outputRoot 'ThirdPartyNotices.Panorama.txt') ($notices -join "`r`n") -Encoding UTF8
    Set-Content $stampPath $fingerprint -Encoding Ascii
}
finally {
    if ($lockHeld) { $sourceLock.ReleaseMutex() }
    $sourceLock.Dispose()
}
