[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('tinyclip.vit-40m-32.int8', 'ram-plus.swin-large-14m')]
    [string]$ProfileId,

    [Parameter(Mandatory)]
    [string]$BundleDirectory,

    [Parameter(Mandatory)]
    [string]$StorageAccountName,

    [Parameter(Mandatory)]
    [string]$BuildRevision
)

$ErrorActionPreference = 'Stop'

if ($StorageAccountName -notmatch '^[a-z0-9]{3,24}$') {
    throw 'StorageAccountName must contain 3-24 lowercase letters or digits.'
}
if ($BuildRevision -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'BuildRevision must be a full 40-character Git commit SHA.'
}

$catalogPath = Join-Path $PSScriptRoot 'release-profiles.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw |
    ConvertFrom-Json -Depth 20
if ($catalog.schemaVersion -ne 1) {
    throw "Unsupported release profile catalog schema: $($catalog.schemaVersion)"
}
$profile = @($catalog.profiles | Where-Object profileId -eq $ProfileId)
if ($profile.Count -ne 1) {
    throw "Expected exactly one release profile for '$ProfileId'."
}
$profile = $profile[0]
if (-not $profile.publicationReady) {
    throw "Publication is blocked for '$ProfileId': $($profile.blockedReason)"
}
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$headRevision = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or
    $headRevision -ne $BuildRevision.ToLowerInvariant()) {
    throw 'BuildRevision must match the current repository HEAD.'
}
$workingChanges = & git -C $repositoryRoot status --porcelain --untracked-files=all
if ($LASTEXITCODE -ne 0 -or $workingChanges.Count -gt 0) {
    throw 'Create the release from a clean repository checkout.'
}
$bundle = (Resolve-Path -LiteralPath $BundleDirectory).Path
. (Join-Path $PSScriptRoot 'ModelAssetEvidence.ps1')
if ($ProfileId -eq 'ram-plus.swin-large-14m') {
    Test-RamPlusReleaseEvidence -BundleDirectory $bundle -Profile $profile
}

function Get-VerifiedFileRecord {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [Parameter(Mandatory)][string]$ExpectedSha256,
        [Parameter(Mandatory)][long]$ExpectedSize
    )

    $path = Join-Path $bundle $FileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release bundle is missing '$FileName'."
    }

    $file = Get-Item -LiteralPath $path -Force
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Release bundle file '$FileName' must not be a reparse point."
    }
    if ($file.Length -ne $ExpectedSize) {
        throw "'$FileName' has $($file.Length) bytes; expected $ExpectedSize."
    }

    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($hash -ne $ExpectedSha256) {
        throw "'$FileName' SHA-256 does not match the application catalog."
    }

    return [pscustomobject]@{
        FileName = $FileName
        Path = $path
        SizeBytes = [long]$file.Length
        Sha256 = $hash
    }
}

$assetRecords = @(
    foreach ($asset in $profile.assets) {
        Get-VerifiedFileRecord `
            -FileName $asset.fileName `
            -ExpectedSha256 $asset.sha256 `
            -ExpectedSize ([long]$asset.sizeBytes)
    }
)

$provenancePath = Join-Path $bundle $profile.provenanceFile
if (-not (Test-Path -LiteralPath $provenancePath -PathType Leaf)) {
    throw "Release bundle is missing required provenance '$($profile.provenanceFile)'."
}
$provenance = Get-Content -LiteralPath $provenancePath -Raw |
    ConvertFrom-Json -Depth 30

if ($ProfileId -eq 'tinyclip.vit-40m-32.int8') {
    $preparedModel = @($provenance.models | Where-Object modelId -eq 'tinyclip')
    if ($preparedModel.Count -ne 1 -or
        $preparedModel[0].modelSha256 -ne $assetRecords[0].Sha256 -or
        [long]$preparedModel[0].modelSizeBytes -ne $assetRecords[0].SizeBytes) {
        throw 'TinyCLIP preparation provenance does not match the application model asset.'
    }

    $bundleMetadataPath = Join-Path $bundle 'tinyclip.json'
    $bundleMetadata = Get-Content -LiteralPath $bundleMetadataPath -Raw |
        ConvertFrom-Json -Depth 20
    foreach ($source in $profile.sourceRevisions.PSObject.Properties) {
        $match = @($bundleMetadata.sources | Where-Object {
            $_.repository -eq $source.Name -and $_.revision -eq $source.Value
        })
        if ($match.Count -eq 0) {
            throw "TinyCLIP bundle provenance is missing pinned source '$($source.Name)'."
        }
    }
}
else {
    if ($provenance.schemaVersion -ne 1 -or
        $provenance.sourceRevision -ne $profile.sourceRevisions.'recognize-anything' -or
        $provenance.checkpointRevision -ne $profile.sourceRevisions.'recognize-anything-plus-model' -or
        $provenance.modelSha256 -ne $assetRecords[0].Sha256 -or
        $provenance.labelsSha256 -ne $assetRecords[1].Sha256 -or
        [long]$provenance.labelCount -ne 4585 -or
        $null -eq $provenance.externalDataFiles -or
        @($provenance.externalDataFiles).Count -ne 0) {
        throw 'RAM++ export provenance does not match the pinned application assets.'
    }
}

$noticeRecords = @(
    foreach ($noticeName in $profile.notices) {
        $noticePath = Join-Path $bundle $noticeName
        if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
            throw "Release bundle is missing required license/notice '$noticeName'."
        }
        $notice = Get-Item -LiteralPath $noticePath -Force
        if (($notice.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release notice '$noticeName' must not be a reparse point."
        }
        [pscustomobject]@{
            FileName = $noticeName
            SizeBytes = [long]$notice.Length
            Sha256 = (Get-FileHash -LiteralPath $noticePath -Algorithm SHA256).Hash
        }
    }
)

$baseUri = "https://$StorageAccountName.blob.core.windows.net/model-assets"
$encodedProfileId = [Uri]::EscapeDataString($profile.profileId)
$encodedVersion = [Uri]::EscapeDataString($profile.version)
$releaseRoot = "$baseUri/releases/$encodedProfileId/$encodedVersion"
$manifestUri = "$baseUri/manifests/$encodedProfileId/$encodedVersion.json"
if ($manifestUri -ne $profile.manifestUri) {
    throw 'Storage account does not match the immutable manifest URL in the release catalog.'
}

$manifest = [ordered]@{
    schemaVersion = 1
    profileId = $profile.profileId
    manifestUri = $manifestUri
    version = $profile.version
    pipelineVersion = $profile.pipelineVersion
    buildRevision = $BuildRevision.ToLowerInvariant()
    license = $profile.license
    sourceRevisions = $profile.sourceRevisions
    assets = @(
        foreach ($asset in $assetRecords) {
            [ordered]@{
                fileName = $asset.FileName
                url = "$releaseRoot/$([Uri]::EscapeDataString($asset.FileName))"
                sizeBytes = $asset.SizeBytes
                sha256 = $asset.Sha256
            }
        }
    )
    legalNotices = @(
        foreach ($notice in $noticeRecords) {
            [ordered]@{
                fileName = $notice.FileName
                url = "$releaseRoot/$([Uri]::EscapeDataString($notice.FileName))"
                sizeBytes = $notice.SizeBytes
                sha256 = $notice.Sha256
            }
        }
    )
}
if ($ProfileId -eq 'ram-plus.swin-large-14m') {
    $manifest.reviewEvidence = $profile.reviewEvidence
}
$manifestPath = Join-Path $bundle 'model-assets-manifest.json'
$manifest | ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

Write-Output "Validated $($assetRecords.Count) application assets and $($noticeRecords.Count) legal notices."
Write-Output "Immutable manifest URL: $manifestUri"
