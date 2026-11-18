#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Original','Checkpoint')][string]$Suite)
$ErrorActionPreference='Stop'
$clock=[Diagnostics.Stopwatch]::StartNew()
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1') -Force
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Hosted contract runner requires an ephemeral GitHub-hosted runner.'}
$settings=@{
    Original=@{file='Test-NativeCI.Contract.ps1';leaf='contract'}
    Checkpoint=@{file='Test-NativeCI.Checkpoint.ps1';leaf='checkpoint-contract'}
}[$Suite]
$boundary=Join-Path $env:GITHUB_WORKSPACE '.dev/native-migration/native-ci'
$id="$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
$control=New-NativeCIArtifactRoot $env:GITHUB_WORKSPACE (Join-Path $boundary "contract-control-$Suite-$id")
$child=Join-Path $boundary "$($settings.leaf)-$id"
$remaining=[int][Math]::Floor(165-$clock.Elapsed.TotalSeconds)
if($remaining -lt 10){throw 'Contract invocation budget exhausted before start.'}
$childArguments=@('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot $settings.file),'-ArtifactRoot',$child)
$null=Invoke-NativeCIProcess -FilePath (Get-Process -Id $PID).Path -Arguments $childArguments -WorkingDirectory $env:GITHUB_WORKSPACE -LogPrefix (Join-Path $control 'contract') -TimeoutSeconds $remaining
