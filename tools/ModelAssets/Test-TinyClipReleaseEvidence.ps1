[CmdletBinding()]
param([Parameter(Mandatory)][string]$VerifiedBundleDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ModelAssetEvidence.ps1')
$catalog = Get-Content (Join-Path $PSScriptRoot 'release-profiles.json') -Raw | ConvertFrom-Json -Depth 30
$original = $catalog.profiles | Where-Object profileId -CEQ 'tinyclip.vit-40m-32.int8'
$root = Join-Path ([IO.Path]::GetTempPath()) "tinyclip-evidence-tests-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $root | Out-Null
$count = 0

function Reset-Fixture {
    $script:profile = $original | ConvertTo-Json -Depth 30 | ConvertFrom-Json -Depth 30
    foreach ($name in @($profile.assets.fileName) + @($profile.notices) + @($profile.provenanceFile, 'reference.json')) {
        Copy-Item -LiteralPath (Join-Path $VerifiedBundleDirectory $name) -Destination (Join-Path $root $name) -Force
    }
}

function Expect-Rejection {
    param([string]$Name, [scriptblock]$Mutation)
    Reset-Fixture
    & $Mutation
    $rejected = $false
    try { Test-TinyClipReleaseEvidence $root $profile } catch { $rejected = $true }
    if (-not $rejected) { throw "Unexpectedly accepted $Name" }
    $script:count++
    Write-Output "PASS rejection: $Name"
}

try {
    Reset-Fixture
    Test-TinyClipReleaseEvidence $root $profile
    $count++
    Expect-Rejection 'missing model' { Remove-Item -LiteralPath (Join-Path $root $profile.assets[0].fileName) }
    Expect-Rejection 'modified embeddings' { Add-Content (Join-Path $root $profile.assets[1].fileName) ' ' }
    Expect-Rejection 'missing preparation evidence' { Remove-Item -LiteralPath (Join-Path $root $profile.provenanceFile) }
    Expect-Rejection 'modified reference' { Add-Content (Join-Path $root 'reference.json') ' ' }
    Expect-Rejection 'modified notice' { Add-Content (Join-Path $root 'NOTICE.txt') ' ' }
    Expect-Rejection 'wrong vocabulary identity' { $profile.reviewEvidence.vocabularySha256 = '0' * 64 }
    Expect-Rejection 'wrong source pin' { $profile.sourceRevisions.'wkcn/TinyCLIP-ViT-40M-32-Text-19M-LAION400M' = '0' * 40 }
    Expect-Rejection 'wrong provider with matching fingerprint' {
        $path = Join-Path $root $profile.provenanceFile
        $data = Get-Content $path -Raw | ConvertFrom-Json -Depth 30
        $data.executionProvider = 'DmlExecutionProvider'
        $data | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        $profile.reviewEvidence.provenanceSha256 = (Get-FileHash $path).Hash
    }
    Write-Output "$count TinyCLIP evidence regressions passed. No preserved artifacts or publication gate changed."
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
