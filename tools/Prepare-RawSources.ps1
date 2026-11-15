[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$SourceDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceDirectory)
New-Item $root -ItemType Directory -Force | Out-Null

function Get-SourceHash([string]$Path) {
    $hasher = [Security.Cryptography.SHA512]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $hasher.Dispose() }
}

function Get-PinnedSource([string]$Name, [string]$Url, [string]$Hash) {
    $archive = Join-Path $root "$Name.tar.gz"
    if (-not (Test-Path $archive)) {
        $temporary = "$archive.tmp"
        try {
            & "$env:SystemRoot\System32\curl.exe" --fail --location --silent --show-error $Url --output $temporary
            if ($LASTEXITCODE -ne 0) { throw "Could not download $Name." }
            if ((Get-SourceHash $temporary) -ne $Hash) {
                throw "$Name source archive checksum mismatch."
            }
            Move-Item $temporary $archive
        }
        finally {
            if (Test-Path $temporary) { Remove-Item $temporary }
        }
    }
    if ((Get-SourceHash $archive) -ne $Hash) {
        throw "$Name source archive checksum mismatch."
    }
    $source = Join-Path $root $Name
    $stamp = Join-Path $source '.verified-source'
    if (-not (Test-Path $stamp)) {
        & "$env:SystemRoot\System32\tar.exe" -xzf $archive -C $root
        if ($LASTEXITCODE -ne 0) { throw "Could not extract $Name." }
        Set-Content $stamp $Hash -Encoding Ascii
    }
    return $source
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
    $libRawHash = $dependencies.LibRawSha512
    $libRaw = Get-PinnedSource "LibRaw-$($dependencies.LibRawVersion)" $dependencies.LibRawUrl $libRawHash
    # Same source and checksum as zlib 1.3.2#2 in the former pinned vcpkg baseline.
    $zlib = Get-PinnedSource "zlib-$($dependencies.ZlibVersion)" $dependencies.ZlibUrl $dependencies.ZlibSha512
    $notices = Join-Path $root 'notices'
    New-Item $notices -ItemType Directory -Force | Out-Null

    function Write-Notice([string]$Name, [string]$Text) {
        $path = Join-Path $notices $Name
        if (-not (Test-Path $path) -or [IO.File]::ReadAllText($path).TrimEnd() -ne $Text.TrimEnd()) {
            Set-Content $path $Text -Encoding UTF8
        }
    }

    Write-Notice 'LibRaw-LICENSE.CDDL.txt' ([IO.File]::ReadAllText((Join-Path $libRaw 'LICENSE.CDDL')))
    Write-Notice 'LibRaw-COPYRIGHT.txt' ([IO.File]::ReadAllText((Join-Path $libRaw 'COPYRIGHT')))
    Write-Notice 'zlib-LICENSE.txt' ([IO.File]::ReadAllText((Join-Path $zlib 'LICENSE')))
    foreach ($entry in @(
        @{ Source = 'src\demosaic\dcb_demosaic.cpp'; Name = 'LibRaw-DCB-FBDD-BSD-LICENSE.txt'; Title = 'DCB demosaic and FBDD denoise' },
        @{ Source = 'src\x3f\x3f_parse_process.cpp'; Name = 'LibRaw-X3F-BSD-LICENSE.txt'; Title = 'X3F support' })) {
        $header = [regex]::Match([IO.File]::ReadAllText((Join-Path $libRaw $entry.Source)),
            '\A/\*(.*?)\*/', [Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $header.Success) { throw "Missing upstream license header: $($entry.Source)." }
        Write-Notice $entry.Name "$($entry.Title) upstream license and notices`r`n$($header.Groups[1].Value.Trim())"
    }
    Write-Notice 'LibRaw-Adobe-DNG-SDK-MIT-LICENSE.txt' @'
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
    Write-Notice 'ThirdPartyNotices.RawDecoder.txt' @"
PhotoLibrarian RAW decoder third-party notices

LibRaw $($dependencies.LibRawVersion): CDDL-1.0; statically linked into PhotoLibrarian.RawDecoder.dll.
Source archive: ThirdPartySources\LibRaw-$($dependencies.LibRawVersion)-source.tar.gz
SHA-512: $libRawHash
LibRaw CDDL, copyright, BSD and Adobe DNG MIT notices are included.
zlib $($dependencies.ZlibVersion) is statically linked; its license is included.
OpenMP, LCMS, Jasper, JPEG/DNG-lossy, RawSpeed, examples, dcraw debug,
X3F tools and 6by9rpi support are disabled. DNG deflate support is enabled.
Native VC projects use the static CRT and the thread-safe raw_r source set.
The source selection matches LibRaw-cmake eb98e4325aef2ce85d2eb031c2ff18640ca616d3:
all src/*.cpp recursively, excluding *_ph.cpp placeholders.
zlib sources match vcpkg baseline d186940b2f0c4e8e5b4ff804af57d2a26fbfa091.
Neither vcpkg nor the LibRaw-cmake helper is required by this build.
"@
}
finally {
    if ($lockHeld) { $sourceLock.ReleaseMutex() }
    $sourceLock.Dispose()
}
