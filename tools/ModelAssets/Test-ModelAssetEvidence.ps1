[CmdletBinding()]
param([Parameter(Mandatory)][string]$VerifiedBundleDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ModelAssetEvidence.ps1')
$catalog = Get-Content (Join-Path $PSScriptRoot 'release-profiles.json') -Raw | ConvertFrom-Json -Depth 30
$originalProfile = $catalog.profiles | Where-Object profileId -CEQ 'ram-plus.swin-large-14m'
$root = Join-Path ([IO.Path]::GetTempPath()) "model-evidence-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$count = 0

function Write-FixtureJson {
    param($Value, [string]$Name)
    $path = Join-Path $root $Name
    $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

function Reset-Fixture {
    $script:profile = $originalProfile | ConvertTo-Json -Depth 40 | ConvertFrom-Json -Depth 40
    foreach ($name in @($profile.provenanceFile) + @($profile.notices) + @($profile.reviewEvidence.reports.fileName)) {
        Copy-Item -LiteralPath (Join-Path $VerifiedBundleDirectory $name) -Destination (Join-Path $root $name) -Force
    }
    foreach ($asset in $profile.assets) {
        $path = Join-Path $root $asset.fileName
        [IO.File]::WriteAllText($path, "small regression fixture: $($asset.fileName)")
        $asset.sizeBytes = (Get-Item -LiteralPath $path).Length
        $asset.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $script:provenance = Get-Content (Join-Path $root $profile.provenanceFile) -Raw | ConvertFrom-Json -Depth 40
    $provenance.modelSha256 = $profile.assets[0].sha256
    $provenance.modelSizeBytes = $profile.assets[0].sizeBytes
    $provenance.labelsSha256 = $profile.assets[1].sha256
    $provenance.labelsSizeBytes = $profile.assets[1].sizeBytes
    $profile.reviewEvidence.provenanceSha256 = Write-FixtureJson $provenance $profile.provenanceFile
    foreach ($pin in $profile.reviewEvidence.reports) {
        $report = Get-Content (Join-Path $root $pin.fileName) -Raw | ConvertFrom-Json -Depth 40
        $report.modelSha256 = $provenance.modelSha256
        $report.modelSizeBytes = $provenance.modelSizeBytes
        $report.labelsSha256 = $provenance.labelsSha256
        $pin.sha256 = Write-FixtureJson $report $pin.fileName
    }
}

function Expect-Rejection {
    param([string]$Name, [scriptblock]$Mutation)
    Reset-Fixture
    & $Mutation
    $rejected = $false
    try { Test-RamPlusReleaseEvidence $root $profile }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Regression unexpectedly accepted: $Name" }
    $script:count++
    Write-Output "PASS rejection: $Name"
}

function Change-Report {
    param([scriptblock]$Mutation, [int]$Index = 0)
    $pin = $profile.reviewEvidence.reports[$Index]
    $report = Get-Content (Join-Path $root $pin.fileName) -Raw | ConvertFrom-Json -Depth 40
    & $Mutation $report
    $pin.sha256 = Write-FixtureJson $report $pin.fileName
}

try {
    Reset-Fixture
    Test-RamPlusReleaseEvidence $root $profile
    Assert-ModelEvidenceReceipt $profile.reviewEvidence $profile
    $count += 2
    Expect-Rejection 'missing report' { Remove-Item -LiteralPath (Join-Path $root 'parity-report.json') }
    Expect-Rejection 'tampered report' { Add-Content (Join-Path $root 'parity-report.json') ' ' }
    Expect-Rejection 'tampered provenance' { Add-Content (Join-Path $root $profile.provenanceFile) ' ' }
    Expect-Rejection 'missing provenance' { Remove-Item -LiteralPath (Join-Path $root $profile.provenanceFile) }
    Expect-Rejection 'artifact bytes' { Add-Content (Join-Path $root $profile.assets[0].fileName) 'changed' }
    Expect-Rejection 'notice bytes' { Add-Content (Join-Path $root 'MODIFICATIONS.txt') 'changed' }
    Expect-Rejection 'provenance contract' {
        $provenance.input.shape = @(1,3,224,224)
        $profile.reviewEvidence.provenanceSha256 = Write-FixtureJson $provenance $profile.provenanceFile
    }
    Expect-Rejection 'provenance identity' {
        $provenance.sourceRevision = 'incorrect'
        $profile.reviewEvidence.provenanceSha256 = Write-FixtureJson $provenance $profile.provenanceFile
    }
    Expect-Rejection 'report artifact identity' { Change-Report { param($r) $r.modelSha256 = '0' * 64 } }
    Expect-Rejection 'report validator identity' { Change-Report { param($r) $r.validatorSha256 = '0' * 64 } }
    Expect-Rejection 'report checkpoint identity' { Change-Report { param($r) $r.checkpointSha256 = '0' * 64 } }
    Expect-Rejection 'report provider' { Change-Report { param($r) $r.executionProvider = 'unknown' } }
    Expect-Rejection 'summary failure' { Change-Report { param($r) $r.passed = $false } }
    Expect-Rejection 'numeric tolerance' { Change-Report { param($r) $r.rawTolerance.atol = 0.1 } }
    Expect-Rejection 'individual mask failure' { Change-Report { param($r) $r.images[0].targetMismatchCount = 1 } }
    Expect-Rejection 'individual nonfinite failure' { Change-Report { param($r) $r.images[0].finite = $false } }
    Expect-Rejection 'different provider inputs' { Change-Report { param($r) $r.images[0].imageSha256 = '0' * 64 } 1 }
    Reset-Fixture
    $receipt = $profile.reviewEvidence | ConvertTo-Json -Depth 20 | ConvertFrom-Json -Depth 20
    $receipt.provenanceSha256 = '0' * 64
    $rejected = $false
    try { Assert-ModelEvidenceReceipt $receipt $profile } catch { $rejected = $true }
    if (-not $rejected) { throw 'Tampered cloud receipt accepted.' }
    $count++
    $rejected = $false
    try { Assert-ModelEvidenceReceipt $null $profile } catch { $rejected = $true }
    if (-not $rejected) { throw 'Missing cloud receipt accepted.' }
    $count++
    Write-Output "$count evidence regressions passed. No production output or publication gate changed."
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
