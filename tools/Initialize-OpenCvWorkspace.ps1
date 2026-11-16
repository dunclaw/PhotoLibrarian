[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$WorkspaceRoot,
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$workspace = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
$repository = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    $lockKey = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($workspace.ToLowerInvariant()))).Replace('-', '')
}
finally { $hasher.Dispose() }
$lock = [Threading.Mutex]::new($false, "Global\PhotoLibrarian-OpenCvWorkspace-$lockKey")
$lockHeld = $false
try {
    $lockHeld = $lock.WaitOne()
    New-Item $workspace -ItemType Directory -Force | Out-Null
    $ownerPath = Join-Path $workspace 'workspace.txt'
    if (Test-Path $ownerPath) {
        $owner = [IO.File]::ReadAllText($ownerPath)
        if (-not [string]::Equals($owner, $repository, [StringComparison]::OrdinalIgnoreCase)) {
            throw "OpenCV cache path collision: '$workspace' belongs to '$owner', not '$repository'. Set OpenCvCacheBase to a different directory."
        }
    }
    else {
        if (@(Get-ChildItem -LiteralPath $workspace -Force).Count -ne 0) {
            throw "OpenCV cache directory '$workspace' is not empty and has no workspace owner."
        }
        [IO.File]::WriteAllText($ownerPath, $repository)
    }
}
finally {
    if ($lockHeld) { $lock.ReleaseMutex() }
    $lock.Dispose()
}
