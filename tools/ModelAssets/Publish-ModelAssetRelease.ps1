[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('tinyclip.vit-40m-32.int8', 'ram-plus.swin-large-14m')]
    [string]$ProfileId,

    [Parameter(Mandatory)]
    [string]$BundleDirectory,

    [Parameter(Mandatory)]
    [string]$StorageAccountName
)

$ErrorActionPreference = 'Stop'

if ($StorageAccountName -notmatch '^[a-z0-9]{3,24}$') {
    throw 'StorageAccountName must contain 3-24 lowercase letters or digits.'
}

$catalogPath = Join-Path $PSScriptRoot 'release-profiles.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw |
    ConvertFrom-Json -Depth 20
$profile = @($catalog.profiles | Where-Object profileId -eq $ProfileId)
if ($profile.Count -ne 1) {
    throw "Expected exactly one release profile for '$ProfileId'."
}
$profile = $profile[0]
if (-not $profile.publicationReady) {
    throw "Publication is blocked for '$ProfileId': $($profile.blockedReason)"
}

$bundle = (Resolve-Path -LiteralPath $BundleDirectory).Path
$manifestPath = Join-Path $bundle 'model-assets-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Missing model-assets-manifest.json. Run New-ModelAssetRelease.ps1 first."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json -Depth 30
. (Join-Path $PSScriptRoot 'ModelAssetEvidence.ps1')
if ($ProfileId -eq 'ram-plus.swin-large-14m') {
    Assert-ModelEvidenceReceipt -Receipt $manifest.reviewEvidence -Profile $profile
}
if ($manifest.schemaVersion -ne 1 -or $manifest.profileId -ne $ProfileId) {
    throw 'Manifest schema or profile ID does not match the requested release.'
}
if ($manifest.manifestUri -ne $profile.manifestUri) {
    throw 'Manifest URL does not match the immutable release catalog.'
}
if ($manifest.buildRevision -notmatch '^[0-9a-f]{40}$') {
    throw 'Manifest is missing a valid immutable build revision.'
}
if ($manifest.version -ne $profile.version -or
    $manifest.pipelineVersion -ne $profile.pipelineVersion -or
    $manifest.license -ne $profile.license) {
    throw 'Manifest version, pipeline, or license does not match the release catalog.'
}
if ($null -eq $manifest.sourceRevisions -or
    $null -eq $manifest.assets -or
    $null -eq $manifest.legalNotices) {
    throw 'Manifest is missing source revisions, assets, or legal notices.'
}
foreach ($revision in $profile.sourceRevisions.PSObject.Properties) {
    $manifestRevision =
        $manifest.sourceRevisions.PSObject.Properties[$revision.Name]
    if ($null -eq $manifestRevision -or
        $manifestRevision.Value -ne $revision.Value) {
        throw "Manifest source revision mismatch for '$($revision.Name)'."
    }
}
if ($manifest.sourceRevisions.PSObject.Properties.Count -ne
    $profile.sourceRevisions.PSObject.Properties.Count) {
    throw 'Manifest contains an unexpected source revision set.'
}
if (@($manifest.assets).Count -ne $profile.assets.Count) {
    throw 'Manifest asset count does not match the release catalog.'
}
foreach ($expectedAsset in $profile.assets) {
    $entry = @($manifest.assets | Where-Object fileName -eq $expectedAsset.fileName)
    if ($entry.Count -ne 1 -or
        [long]$entry[0].sizeBytes -ne [long]$expectedAsset.sizeBytes -or
        $entry[0].sha256 -ne $expectedAsset.sha256) {
        throw "Manifest asset metadata does not match '$($expectedAsset.fileName)'."
    }
}
if (@($manifest.legalNotices).Count -ne $profile.notices.Count) {
    throw 'Manifest legal notice list does not match the release catalog.'
}
foreach ($requiredNotice in $profile.notices) {
    $notice = @($manifest.legalNotices | Where-Object fileName -eq $requiredNotice)
    if ($notice.Count -ne 1) {
        throw "Manifest is missing required legal notice '$requiredNotice'."
    }
    if ($ProfileId -eq 'ram-plus.swin-large-14m') {
        $pinnedNotice = $profile.reviewEvidence.noticeSha256.PSObject.Properties[$requiredNotice]
        if ($null -eq $pinnedNotice -or $notice[0].sha256 -ne $pinnedNotice.Value) {
            throw "Manifest notice fingerprint is not reviewed: $requiredNotice"
        }
    }
    if ($requiredNotice -ne [IO.Path]::GetFileName($requiredNotice)) {
        throw "Unsafe legal notice path '$requiredNotice'."
    }
}

$expectedRoot = "https://$StorageAccountName.blob.core.windows.net/model-assets"
$profilePath = [Uri]::EscapeDataString($manifest.profileId)
$versionPath = [Uri]::EscapeDataString($manifest.version)
$releasePrefix = "$expectedRoot/releases/$profilePath/$versionPath/"
$expectedManifestUri = "$expectedRoot/manifests/$profilePath/$versionPath.json"
if ($manifest.manifestUri -ne $expectedManifestUri) {
    throw 'Storage account does not match the immutable manifest URL in the release catalog.'
}

function Publish-ImmutableBlob {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$BlobName,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string]$ContentType
    )

    $existsOutput = & az storage blob exists `
        --auth-mode login `
        --account-name $StorageAccountName `
        --container-name model-assets `
        --name $BlobName `
        --query exists `
        --output tsv
    if ($LASTEXITCODE -ne 0) {
        throw "Could not check whether '$BlobName' already exists."
    }
    if ($existsOutput -eq 'true') {
        $existingHash = & az storage blob show `
            --auth-mode login `
            --account-name $StorageAccountName `
            --container-name model-assets `
            --name $BlobName `
            --query metadata.sha256 `
            --output tsv
        if ($LASTEXITCODE -ne 0 -or $existingHash -ne $Sha256) {
            throw "Refusing to overwrite immutable blob '$BlobName'."
        }
        Write-Output "Already published with matching SHA-256: $BlobName"
        return
    }

    & az storage blob upload `
        --auth-mode login `
        --account-name $StorageAccountName `
        --container-name model-assets `
        --name $BlobName `
        --file $Path `
        --overwrite false `
        --content-type $ContentType `
        --content-cache-control 'public, max-age=31536000, immutable' `
        --metadata "sha256=$Sha256" `
        --only-show-errors `
        --output none
    if ($LASTEXITCODE -ne 0) {
        throw "Upload failed for '$BlobName'. The immutable release may be partially uploaded; rerun to verify and continue."
    }
}

$files = @()
foreach ($asset in $manifest.assets) {
    $expectedUrl = "$releasePrefix$([Uri]::EscapeDataString($asset.fileName))"
    if ($asset.url -ne $expectedUrl) {
        throw "Asset URL does not exactly match the versioned release path: $($asset.fileName)"
    }
    $assetPath = Join-Path $bundle $asset.fileName
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
        throw "Release bundle is missing '$($asset.fileName)'."
    }
    $file = Get-Item -LiteralPath $assetPath
    $hash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash
    if ([long]$file.Length -ne [long]$asset.sizeBytes -or $hash -ne $asset.sha256) {
        throw "Release asset '$($asset.fileName)' differs from the manifest."
    }
    $files += [pscustomobject]@{
        Path = $assetPath
        BlobName = "releases/$profilePath/$versionPath/$($asset.fileName)"
        Sha256 = $hash
        ContentType = 'application/octet-stream'
    }
}
foreach ($notice in $manifest.legalNotices) {
    $expectedUrl = "$releasePrefix$([Uri]::EscapeDataString($notice.fileName))"
    if ($notice.url -ne $expectedUrl) {
        throw "Notice URL does not exactly match the versioned release path: $($notice.fileName)"
    }
    $noticePath = Join-Path $bundle $notice.fileName
    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf)) {
        throw "Release bundle is missing legal notice '$($notice.fileName)'."
    }
    $file = Get-Item -LiteralPath $noticePath
    $hash = (Get-FileHash -LiteralPath $noticePath -Algorithm SHA256).Hash
    if ([long]$file.Length -ne [long]$notice.sizeBytes -or $hash -ne $notice.sha256) {
        throw "Legal notice '$($notice.fileName)' differs from the manifest."
    }
    $files += [pscustomobject]@{
        Path = $noticePath
        BlobName = "releases/$profilePath/$versionPath/$($notice.fileName)"
        Sha256 = $hash
        ContentType = 'text/plain; charset=utf-8'
    }
}

$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
$files += [pscustomobject]@{
    Path = $manifestPath
    BlobName = "manifests/$profilePath/$versionPath.json"
    Sha256 = $manifestHash
    ContentType = 'application/json'
}

foreach ($file in $files) {
    Publish-ImmutableBlob `
        -Path $file.Path `
        -BlobName $file.BlobName `
        -Sha256 $file.Sha256 `
        -ContentType $file.ContentType
}
Write-Output "Published verified immutable release $($manifest.profileId) $($manifest.version)."
