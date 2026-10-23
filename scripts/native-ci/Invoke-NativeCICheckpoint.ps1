#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$NuGetPackageRoot,
    [Parameter(Mandatory)][string]$CargoCacheRoot,
    [ValidateSet('Offline','PublicHosted')][string]$DependencyMode = 'Offline',
    [switch]$Resume,
    [switch]$RequireComplete
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$clock = [Diagnostics.Stopwatch]::StartNew()
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Checkpoint.psm1') -Force
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($DependencyMode -eq 'PublicHosted' -and ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted')) { throw 'PublicHosted requires an ephemeral GitHub-hosted runner.' }
if ($RequireComplete -and -not $Resume) { throw 'RequireComplete is a resume-only validation gate; it never starts work.' }
if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot)) { throw 'ArtifactRoot must be absolute.' }
$root = Assert-NativeCIPlainPath $ArtifactRoot
$boundary = Join-Path $repository '.dev/native-migration/native-ci'
if (-not $root.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Checkpoint root is outside the CI artifact boundary.' }
if ($Resume) {
    if (-not (Test-Path -LiteralPath (Join-Path $root 'checkpoint-inputs.json') -PathType Leaf)) { throw 'No initialized checkpoint at this root; resume refused.' }
}
else { $root = New-NativeCIArtifactRoot $repository $root }
# The synchronous controller, not a detached job, owns both the lock and worker lifetime.
$lock = [IO.File]::Open((Join-Path $root 'checkpoint.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$id = [Guid]::NewGuid().ToString('N')
$code = 1
try {
    $parameters = @{artifactRoot=$root;dependencyMode=$DependencyMode;nuget=$NuGetPackageRoot;cargo=$CargoCacheRoot;cliHome=$env:DOTNET_CLI_HOME;toolSearchPathHash=(Get-NativeCIDigest $env:PATH);protocol=2}
    $actual = Get-NativeCISourceBinding $repository $parameters -Clock $clock
    if ($Resume) {
        $expected = Get-Content -LiteralPath (Join-Path $root 'checkpoint-inputs.json') -Raw | ConvertFrom-Json
        Assert-NativeCIBinding $expected $actual
        $previous = Read-NativeCICheckpoint $root -Clock $clock
        Assert-NativeCIProductionCheckpoint $previous $root -DependencyMode $DependencyMode
        if ($previous.state -eq 'passed') { $code = 0; Write-Output 'All required bounded native-validation gates already passed; no gates repeated; fullReleaseAcceptance=false.'; exit 0 }
        if ($RequireComplete) { throw 'Required final native-validation gate is incomplete; no command started.' }
    }
    else { Write-NativeCIAtomicJson (Join-Path $root 'checkpoint-inputs.json') $actual }
    # Reserve twenty seconds for receipt/source verification and lock release within the 170-second caller budget.
    $remaining = [int][Math]::Floor(145 - $clock.Elapsed.TotalSeconds)
    if ($remaining -lt 15) { throw 'Invocation budget exhausted during binding validation; no worker started.' }
    $worker = Join-Path $repository 'scripts/Test-NativeCI.ps1'
    $arguments = @('-NoProfile','-NonInteractive','-File',$worker,'-CheckpointWorker','-ArtifactRoot',$root,'-NuGetPackageRoot',$NuGetPackageRoot,'-CargoCacheRoot',$CargoCacheRoot,'-DependencyMode',$DependencyMode)
    $result = Invoke-NativeCIProcess -FilePath (Get-Process -Id $PID).Path -Arguments $arguments -WorkingDirectory $repository -LogPrefix (Join-Path $root "invocation-$id") -Environment @{WINSIGHT_CI_CHECKPOINT_ROOT=$root} -TimeoutSeconds $remaining -AllowedExitCodes @(0,2)
    $report = Read-NativeCICheckpoint $root -Clock $clock
    Assert-NativeCIProductionCheckpoint $report $root -DependencyMode $DependencyMode
    # Recheck source drift occurring during the invocation before accepting its receipt.
    Assert-NativeCIBinding $actual (Get-NativeCISourceBinding $repository $parameters -Clock $clock)
    if (($result.exitCode -eq 0 -and $report.state -ne 'passed') -or ($result.exitCode -eq 2 -and $report.state -ne 'incomplete')) { throw 'Worker exit/checkpoint state conflict; no success accepted.' }
    $code = $result.exitCode
    Write-Output "Checkpoint state=$($report.state), exit=$code, completedCommands=$($report.commands.Count); fullReleaseAcceptance=false."
}
catch { Write-Error -ErrorAction Continue $_; $code = 1 }
finally {
    try { Write-NativeCIAtomicJson (Join-Path $root "invocation-$id.json") ([ordered]@{schema=1;exitCode=$code;state=$(if ($code -eq 2) {'incomplete'} elseif ($code -eq 0) {'passed'} else {'failed'});elapsedSeconds=$clock.Elapsed.TotalSeconds;fullReleaseAcceptance=$false}) }
    finally { $lock.Dispose() }
}
exit $code
