[CmdletBinding()]
param([Parameter(Mandatory)][string]$BundleDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ModelAssetEvidence.ps1')
$catalog = Get-Content (Join-Path $PSScriptRoot 'release-profiles.json') -Raw | ConvertFrom-Json -Depth 30
$profile = $catalog.profiles | Where-Object profileId -CEQ 'ram-plus.swin-large-14m'
Test-RamPlusReleaseEvidence -BundleDirectory $BundleDirectory -Profile $profile
Write-Output 'RAM++ local evidence validation passed. No manifest written and no publication authorized.'
