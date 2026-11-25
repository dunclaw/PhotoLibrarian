[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = 'x64',

    [string]$Version = '1.0.0.0',

    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^[1-9]\d*\.\d+\.\d+\.0$' -or
    @($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count -gt 0) {
    throw 'Use a Store package version of the form major.minor.build.0, with major >= 1 and each field <= 65535.'
}

$repoRoot = Split-Path $PSScriptRoot -Parent
$outputRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$layout = Join-Path $outputRoot "layout-$Architecture"
$packageDirectory = Join-Path $outputRoot 'packages'
if (Test-Path $layout) {
    throw "The package layout already exists: $layout. Choose a fresh output directory to avoid packaging stale files."
}

$kitsRoot = Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' 'KitsRoot10'
$makeAppx = Get-ChildItem (Join-Path $kitsRoot 'bin') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $makeAppx) {
    throw 'MakeAppx.exe was not found. Install the Windows SDK packaging tools.'
}
$makePri = Join-Path (Split-Path $makeAppx -Parent) 'makepri.exe'
if (-not (Test-Path $makePri)) {
    throw 'MakePri.exe was not found alongside MakeAppx.exe.'
}

[xml]$manifest = Get-Content (Join-Path $repoRoot 'packaging\Package.appxmanifest') -Raw
$manifest.Package.Identity.Version = $Version
$manifest.Package.Identity.ProcessorArchitecture = $Architecture.ToLowerInvariant()
$packageIdentity = $manifest.Package.Identity.Name

New-Item $packageDirectory -ItemType Directory -Force | Out-Null
$runtime = "win-$($Architecture.ToLowerInvariant())"
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'MSBuild\Current\Bin\MSBuild.exe'
if ($LASTEXITCODE -ne 0 -or -not $msbuild) { throw 'Install Visual Studio MSBuild and the Desktop development with C++ workload.' }
& $msbuild (Join-Path $repoRoot 'src\PhotoLibrarian\PhotoLibrarian.csproj') `
    /restore /t:Publish /m /p:Configuration=Release "/p:RuntimeIdentifier=$runtime" /p:SelfContained=true `
    "/p:Platform=$Architecture" /p:WindowsPackageType=MSIX /p:EnableMsixTooling=false `
    "/p:ProjectPriIndexName=$packageIdentity" `
    /p:PublishTrimmed=false /p:PublishReadyToRun=false "/p:PublishDir=$layout\"
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild publish failed with exit code $LASTEXITCODE."
}

foreach ($requiredFile in @(
    'PhotoLibrarian.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll',
    'App.xbf', 'MainWindow.xbf', 'PhotoLibrarian.pri'
)) {
    if (-not (Test-Path (Join-Path $layout $requiredFile))) {
        throw "The self-contained layout is missing $requiredFile."
    }
}

$manifest.Save((Join-Path $layout 'AppxManifest.xml'))

# Generate package-specific logo sizes from the existing application artwork.
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $layout 'PackageAssets'
New-Item $assetDirectory -ItemType Directory | Out-Null
$source = [System.Drawing.Image]::FromFile((Join-Path $repoRoot 'src\PhotoLibrarian\Assets\icons\icon-256.png'))
try {
    foreach ($asset in @(
        @{ Name = 'StoreLogo.png'; Size = 50 },
        @{ Name = 'Square44x44Logo.png'; Size = 44 },
        @{ Name = 'Square150x150Logo.png'; Size = 150 }
    )) {
        $bitmap = [System.Drawing.Bitmap]::new($asset.Size, $asset.Size)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.DrawImage($source, 0, 0, $asset.Size, $asset.Size)
                $bitmap.Save((Join-Path $assetDirectory $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $graphics.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $source.Dispose()
}

$appPri = Join-Path $layout 'PhotoLibrarian.pri'
$priDump = Join-Path $outputRoot "$Architecture-pri.xml"
& $makePri dump /if $appPri /of $priDump /dt basic /o
if ($LASTEXITCODE -ne 0) {
    throw "MakePri dump failed for $appPri with exit code $LASTEXITCODE."
}
[xml]$resourceIndex = Get-Content -LiteralPath $priDump -Raw
$mainMap = $resourceIndex.SelectSingleNode('//ResourceMap[@primary="true"]')
if (-not $mainMap -or $mainMap.GetAttribute('name') -ne $packageIdentity) {
    throw "The app resource index must use the package identity $packageIdentity."
}
foreach ($resource in @(
    'Resources/DuplicateInitialStatus',
    'Files/App.xbf', 'Files/MainWindow.xbf',
    'Files/Microsoft.UI.Xaml/Themes/themeresources.xbf'
)) {
    $uri = "ms-resource://$packageIdentity/$resource"
    if (-not $mainMap.SelectSingleNode(".//NamedResource[@uri='$uri']")) {
        throw "The app resource index is missing $resource."
    }
}

# Preserve the SDK's embedded WinUI resources for XAML activation. The app's
# explicit resource loaders still open PhotoLibrarian.pri by filename.
Copy-Item -LiteralPath $appPri -Destination (Join-Path $layout 'resources.pri')

$packagePath = Join-Path $packageDirectory "PhotoLibrarian_${Version}_$Architecture.msix"
& $makeAppx pack /d $layout /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx pack failed with exit code $LASTEXITCODE."
}
Write-Output "Unsigned Store package: $packagePath"
