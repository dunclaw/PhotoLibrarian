[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [ValidateSet('ram-plus.swin-large-14m', 'tinyclip.vit-40m-32.int8')]
    [string]$ProfileId = 'ram-plus.swin-large-14m'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ModelAssetEvidence.ps1')
$catalog = Get-Content (Join-Path $PSScriptRoot 'release-profiles.json') -Raw | ConvertFrom-Json -Depth 30
$profile = $catalog.profiles | Where-Object profileId -CEQ $ProfileId
if ($ProfileId -eq 'ram-plus.swin-large-14m') {
    Test-RamPlusReleaseEvidence -BundleDirectory $BundleDirectory -Profile $profile
} else {
    Test-TinyClipReleaseEvidence -BundleDirectory $BundleDirectory -Profile $profile
}
Write-Output "$ProfileId local evidence validation passed. No manifest written and no publication authorized."
