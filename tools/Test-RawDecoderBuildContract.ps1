[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path $PSScriptRoot -Parent
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "RawDecoderContract-$([Guid]::NewGuid().ToString('N'))"
New-Item $temporaryRoot -ItemType Directory | Out-Null
try {
    $tokens = $null
    $parseErrors = $null
    $scriptAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'Build-RawDecoder.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -ne 0) { throw 'Could not parse Build-RawDecoder.ps1.' }
    $statements = @($scriptAst.EndBlock.Statements)

    function Get-ContractStatements([string]$Prefix, [int]$Count) {
        for ($index = 0; $index -lt $statements.Count; $index++) {
            if ($statements[$index].Extent.Text.StartsWith($Prefix)) {
                return ($statements[$index..($index + $Count - 1)] |
                    ForEach-Object { $_.Extent.Text }) -join "`r`n"
            }
        }
        throw "Native build contract statement not found: $Prefix"
    }

    # Execute the production batch generation, invocation and exit guard, not a duplicate.
    $batchContract = Get-ContractStatements '$buildCommand =' 4
    $gitContract = Get-ContractStatements '$checkedOutCommit =' 3
    $discoveryContract = Get-ContractStatements '$vsInstall =' 3
    $artifactContract = Get-ContractStatements 'if (-not (Test-Path $nativeDll))' 1
    $fixtureSource = Join-Path $temporaryRoot 'NativeFixture.cs'
    $fixtureExe = Join-Path $temporaryRoot 'NativeFixture.exe'
    Set-Content $fixtureSource @'
// Copyright (c) Duncan Lawler. All Rights Reserved.
using System;
using System.IO;
internal static class NativeFixture
{
    private static int Main(string[] args)
    {
        string stage = args.Length > 0 && args[0] == "--build" ? "build" : "configure";
        File.AppendAllText(Environment.GetEnvironmentVariable("RAW_TEST_TRACE"), stage + "\r\n");
        Console.WriteLine("error : recoverable mirror failure; fallback succeeded");
        Console.Error.WriteLine("error : recoverable stderr diagnostic");
        return Environment.GetEnvironmentVariable("RAW_TEST_STAGE") == stage
            ? int.Parse(Environment.GetEnvironmentVariable("RAW_TEST_EXIT_CODE")) : 0;
    }
}
'@ -Encoding Ascii
    $compilerScript = Join-Path $temporaryRoot 'CompileFixture.ps1'
    Set-Content $compilerScript @'
param($Source, $Output)
$ErrorActionPreference = 'Stop'
Add-Type -Path $Source -OutputAssembly $Output -OutputType ConsoleApplication
'@ -Encoding Ascii
    # Windows PowerShell produces a standalone .NET Framework console fixture.
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $compilerScript $fixtureSource $fixtureExe
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile the native exit-code fixture.' }

    $setupScript = Join-Path $temporaryRoot 'SetupFixture.bat'
    Set-Content $setupScript @'
@echo off
echo setup>>"%RAW_TEST_TRACE%"
if "%RAW_TEST_STAGE%"=="setup" exit /b %RAW_TEST_EXIT_CODE%
exit /b 0
'@ -Encoding Ascii
    $probeScript = Join-Path $temporaryRoot 'Probe.ps1'
    $probeHeader = @'
param($Root)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$cmake = Join-Path $Root 'NativeFixture.exe'
$vsDevCmd = Join-Path $Root 'SetupFixture.bat'
$targetArch = 'amd64'
$buildRoot = $Root
$commandFile = Join-Path $Root 'build-raw-decoder.cmd'
$quotedConfigureArguments = @()
if ($env:RAW_TEST_STAGE -eq 'script') { throw 'Unexpected script failure fixture.' }
if ($env:RAW_TEST_STAGE -eq 'exit') { exit 23 }
if ($env:RAW_TEST_STAGE -eq 'git') {
    function git { $global:LASTEXITCODE = 7; return 'expected-commit' }
    $vcpkgRoot = $Root
    $vcpkgCommit = 'expected-commit'
'@
    $probeDiscovery = @'
}
if ($env:RAW_TEST_STAGE -eq 'discovery') {
    function Invoke-DiscoveryFixture { $global:LASTEXITCODE = 7; return 'existing-toolset' }
    $vswhere = 'Invoke-DiscoveryFixture'
'@
    $probeFooter = @'
}
'@
    $probeArtifact = @'
if ($env:RAW_TEST_STAGE -eq 'artifact') {
    $nativeDll = Join-Path $Root 'missing.dll'
'@
    Set-Content $probeScript (
        $probeHeader + "`r`n" + $gitContract + "`r`n" +
        $probeDiscovery + "`r`n" + $discoveryContract + "`r`n" + $probeFooter +
        $batchContract + "`r`n" + $probeArtifact + "`r`n" + $artifactContract +
        "`r`n}`r`nWrite-Output 'Native contract completed.'") -Encoding Ascii

    [xml]$coreProject = Get-Content (Join-Path $repoRoot 'src\PhotoLibrarian.Core\PhotoLibrarian.Core.csproj') -Raw
    $productionExec = $coreProject.SelectSingleNode('/Project/Target[@Name="BuildRawDecoderNative"]/Exec')
    if ($null -eq $productionExec) { throw 'RAW native build Exec was not found.' }
    $projectPath = Join-Path $temporaryRoot 'Contract.proj'
    $tracePath = Join-Path $temporaryRoot 'trace.txt'
    $cases = @(
        @{ Stage = 'success'; Code = 0; Trace = 'setup,configure,build'; Succeeds = $true },
        @{ Stage = 'setup'; Code = 7; Trace = 'setup'; Succeeds = $false },
        @{ Stage = 'setup'; Code = -7; Trace = 'setup'; Succeeds = $false },
        @{ Stage = 'configure'; Code = 7; Trace = 'setup,configure'; Succeeds = $false },
        @{ Stage = 'configure'; Code = -7; Trace = 'setup,configure'; Succeeds = $false },
        @{ Stage = 'build'; Code = 7; Trace = 'setup,configure,build'; Succeeds = $false },
        @{ Stage = 'build'; Code = -7; Trace = 'setup,configure,build'; Succeeds = $false },
        @{ Stage = 'script'; Code = 0; Trace = ''; Succeeds = $false },
        @{ Stage = 'exit'; Code = 23; Trace = ''; Succeeds = $false },
        @{ Stage = 'git'; Code = 0; Trace = ''; Succeeds = $false },
        @{ Stage = 'discovery'; Code = 0; Trace = ''; Succeeds = $false },
        @{ Stage = 'artifact'; Code = 0; Trace = 'setup,configure,build'; Succeeds = $false }
    )
    foreach ($hostName in @('powershell.exe', 'pwsh.exe')) {
        $hostPath = (Get-Command $hostName -ErrorAction Stop).Source
        foreach ($case in $cases) {
            Set-Content $tracePath '' -Encoding Ascii
            $env:RAW_TEST_TRACE = $tracePath
            $env:RAW_TEST_STAGE = $case.Stage
            $env:RAW_TEST_EXIT_CODE = [string]$case.Code
            [xml]$project = '<Project><Target Name="Probe" /></Project>'
            $exec = $project.ImportNode($productionExec, $true)
            $exec.SetAttribute('Command', "`"$hostPath`" -NoProfile -ExecutionPolicy Bypass -File `"$probeScript`" -Root `"$temporaryRoot`"")
            $null = $project.Project.Target.AppendChild($exec)
            $project.Save($projectPath)
            $output = & dotnet msbuild $projectPath -target:Probe -nologo -verbosity:minimal 2>&1
            $exitCode = $LASTEXITCODE
            $trace = ((Get-Content $tracePath) | Where-Object { $_ }) -join ','
            if (($exitCode -eq 0) -ne $case.Succeeds -or $trace -ne $case.Trace) {
                throw "$hostName $($case.Stage)/$($case.Code): exit=$exitCode, trace=$trace`n$($output -join "`n")"
            }
            if ($case.Succeeds -and
                (($output -join "`n") -notmatch 'recoverable mirror failure' -or
                 ($output -join "`n") -notmatch 'recoverable stderr diagnostic' -or
                 ($output -join "`n") -notmatch 'Native contract completed')) {
                throw 'Successful native output was not preserved.'
            }
            Write-Output "PASS $hostName $($case.Stage)/$($case.Code)"
        }
        # Control: the original Exec behavior must reproduce the false MSB3077 failure.
        $env:RAW_TEST_STAGE = 'success'
        $exec.RemoveAttribute('IgnoreStandardErrorWarningFormat')
        $project.Save($projectPath)
        $output = & dotnet msbuild $projectPath -target:Probe -nologo -verbosity:minimal 2>&1
        if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'MSB3077') {
            throw "Original Exec behavior did not reproduce MSB3077.`n$($output -join "`n")"
        }
        Write-Output "PASS $hostName original Exec reproduces MSB3077"
    }
}
finally {
    Remove-Item Env:\RAW_TEST_TRACE, Env:\RAW_TEST_STAGE, Env:\RAW_TEST_EXIT_CODE -ErrorAction SilentlyContinue
    Remove-Item $temporaryRoot -Recurse -Force
}

# The final control intentionally failed; do not leak its exit code to the CI shell.
exit 0
