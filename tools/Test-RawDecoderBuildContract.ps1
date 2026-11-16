[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path $PSScriptRoot -Parent
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "NativeContract-$([Guid]::NewGuid().ToString('N'))"
New-Item $temporaryRoot -ItemType Directory | Out-Null
try {
    $dependencies = ([xml](Get-Content (Join-Path $repoRoot 'native\Dependencies.props') -Raw)).Project.PropertyGroup
    $archive = Join-Path $temporaryRoot "LibRaw-$($dependencies.LibRawVersion).tar.gz"
    [IO.File]::WriteAllText($archive, 'corrupt cached source archive')
    foreach ($hostName in @('powershell.exe', 'pwsh.exe')) {
        $output = & $hostName -NoProfile -ExecutionPolicy Bypass -File `
            (Join-Path $PSScriptRoot 'Prepare-RawSources.ps1') -SourceDirectory $temporaryRoot 2>&1
        if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'checksum mismatch') {
            throw "Source corruption was not rejected by $hostName.`n$($output -join "`n")"
        }
        if (Test-Path (Join-Path $temporaryRoot "LibRaw-$($dependencies.LibRawVersion)")) {
            throw 'A failed checksum must not publish extracted sources.'
        }
        Write-Output "PASS $hostName rejects corrupt cached source before extraction"
    }
    foreach ($projectName in @('PhotoLibrarian.Core', 'PhotoLibrarian.Tests')) {
        $project = [xml](Get-Content (Join-Path $repoRoot "src\$projectName\$projectName.csproj") -Raw)
        $nativeReferences = @($project.SelectNodes('/Project/ItemGroup/ProjectReference') |
            Where-Object { $_.Include -like '*.vcxproj' })
        if ($nativeReferences.Count -ne 2 -or
            @($nativeReferences | Where-Object { $_.ReferenceOutputAssembly -ne 'false' }).Count -ne 0) {
            throw "$projectName must use two build-only native project references."
        }
        if ($project.OuterXml -match 'Build-RawDecoder|Build-TileCodecProbe|Build-Panorama') {
            throw "$projectName still invokes hidden native compilation."
        }
    }
    $nativeProjects = Get-ChildItem (Join-Path $repoRoot 'native') -Recurse -Filter '*.vcxproj' |
        Where-Object { $_.FullName -notmatch '\\obj\\' }
    foreach ($project in $nativeProjects) {
        if ([IO.File]::ReadAllText($project.FullName) -match '(?i)[a-z]:\\.*(?:cl|clang-cl)\.exe') {
            throw "Checked-in compiler path in $($project.Name)."
        }
    }
    Write-Output 'PASS managed references and native compiler-path contract'

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $msbuild = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\Current\Bin\MSBuild.exe'
    if ($LASTEXITCODE -ne 0 -or -not $msbuild) { throw 'Visual Studio C++ MSBuild is required for the native build contract.' }
    $openCvProject = Join-Path $repoRoot 'native\OpenCv.vcxproj'
    $currentTree = & $msbuild $openCvProject /nologo /p:Platform=x64 /getProperty:OpenCvBuildDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate the active OpenCV build tree.' }
    $changedTree = & $msbuild $openCvProject /nologo /p:Platform=x64 /p:VCToolsVersion=0.0.0 /getProperty:OpenCvBuildDirectory
    if ($LASTEXITCODE -ne 0 -or $changedTree -eq $currentTree) {
        throw 'A compiler version change must select a different OpenCV build tree.'
    }
    Write-Output 'PASS compiler-version changes cannot reuse the previous generated build tree'

    $featuresDirectory = Join-Path $currentTree.Trim() 'opencv\modules\features2d'
    if (Test-Path $featuresDirectory) {
        $trackingPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($name in @('opencv_features2d_AVX2', 'opencv_features2d_AVX512_SKX')) {
            $projectPath = Join-Path $featuresDirectory "$name.vcxproj"
            if (-not (Test-Path $projectPath)) { continue }
            foreach ($configuration in @('Debug', 'Release')) {
                $state = & $msbuild $projectPath /nologo /p:Platform=x64 "/p:Configuration=$configuration" /getProperty:LastBuildState
                if ($LASTEXITCODE -ne 0) { throw "Could not evaluate $name tracking state." }
                $statePath = [IO.Path]::GetFullPath([IO.Path]::Combine($featuresDirectory, $state.Trim()))
                $expectedPath = Join-Path $currentTree.Trim() "tlog\$name\$configuration\$name.lastbuildstate"
                if ($statePath -ne [IO.Path]::GetFullPath($expectedPath) -or
                    -not $trackingPaths.Add($statePath)) {
                    throw 'OpenCV tracking files must be isolated by project and configuration below the build root.'
                }
                if ($statePath.Length -ge 260) {
                    throw "OpenCV tracking state exceeds the classic Windows path limit: $statePath"
                }
            }
        }
        Write-Output 'PASS short, project/configuration-isolated OpenCV tracking paths'
    }

    $libRawRoot = Join-Path $repoRoot "native\obj\sources\LibRaw-$($dependencies.LibRawVersion)"
    if (Test-Path $libRawRoot) {
        $manifest = [xml](Get-Content (Join-Path $repoRoot 'native\LibRawSources.props') -Raw)
        $expected = @($manifest.Project.ItemGroup.ClCompile |
            ForEach-Object { $_.Include.Replace('$(LibRawSource)', '') })
        $actual = @(Get-ChildItem (Join-Path $libRawRoot 'src') -Recurse -Filter '*.cpp' |
            Where-Object { $_.Name -notlike '*_ph.cpp' } |
            ForEach-Object { $_.FullName.Substring($libRawRoot.Length + 1) })
        if (Compare-Object $expected $actual) { throw 'The native LibRaw manifest differs from the pinned upstream raw_r source set.' }
        Write-Output "PASS exact upstream LibRaw source set ($($actual.Count) files)"
    }
}
finally {
    Remove-Item $temporaryRoot -Recurse -Force
}
exit 0
