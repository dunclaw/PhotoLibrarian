[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EnvironmentDirectory,

    [string]$PackageCache
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $EnvironmentDirectory) {
    throw 'EnvironmentDirectory must be a new directory; existing environments are never overwritten.'
}
$version = & py -3.12 -c 'import platform; print(platform.python_version(), platform.system(), platform.machine())'
if ($LASTEXITCODE -ne 0 -or $version -ne '3.12.0 Windows AMD64') {
    throw 'Install CPython 3.12.0 for Windows x64 before creating the locked export environment.'
}
$environment = [IO.Path]::GetFullPath($EnvironmentDirectory)
$packageArguments = @()
if ($PackageCache) {
    $cache = (Resolve-Path -LiteralPath $PackageCache).Path
    $packageArguments = @('--no-index', '--find-links', $cache, '--no-cache-dir')
}
& py -3.12 -m venv $environment
if ($LASTEXITCODE -ne 0) { throw 'Creating the export environment failed.' }
$python = Join-Path $environment 'Scripts\python.exe'

# fairscale is source-only; install its locked build backend before preparing it.
& $python -m pip install --require-hashes @packageArguments `
    -r (Join-Path $PSScriptRoot 'requirements-ram-plus-bootstrap.txt')
if ($LASTEXITCODE -ne 0) { throw 'Installing locked build dependencies failed.' }
& $python -m pip install --require-hashes --no-build-isolation @packageArguments `
    -r (Join-Path $PSScriptRoot 'requirements-ram-plus-export.txt')
if ($LASTEXITCODE -ne 0) { throw 'Installing the hash-locked export environment failed.' }
& $python -m pip check
if ($LASTEXITCODE -ne 0) { throw 'The installed export dependency graph is inconsistent.' }
& $python -c "import sys; sys.path.insert(0, sys.argv[1]); import export_ram_plus; export_ram_plus.verify_environment(); print('Locked RAM++ export environment verified.')" $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Verifying the installed export environment failed.' }
Write-Output "Export interpreter: $python"
