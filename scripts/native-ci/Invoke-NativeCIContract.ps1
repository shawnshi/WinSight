#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Original','Checkpoint','N3')][string]$Suite,
    [ValidateSet('baseline','final-output','partial-output','rolling','hosted')][string]$Group)
$ErrorActionPreference='Stop'
if($Suite -eq 'N3' -and [string]::IsNullOrWhiteSpace($Group)){throw 'N3 requires one explicit finite Group.'}
if($Suite -ne 'N3' -and $PSBoundParameters.ContainsKey('Group')){throw 'Group is valid only for the N3 suite.'}
$clock=[Diagnostics.Stopwatch]::StartNew()
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1') -Force
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted'){throw 'Hosted contract runner requires an ephemeral GitHub-hosted runner.'}
$settings=@{
    Original=@{file='Test-NativeCI.Contract.ps1';leaf='contract'}
    Checkpoint=@{file='Test-NativeCI.Checkpoint.ps1';leaf='checkpoint-contract'}
    N3=@{file='Test-NativeCI.N3.ps1';leaf='n3-contract'}
}[$Suite]
$boundary=Join-Path $env:GITHUB_WORKSPACE '.dev/native-migration/native-ci'
$id="$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
$scope=if($Suite -eq 'N3'){"$Suite-$Group"}else{$Suite}
$leaf=if($Suite -eq 'N3'){"$($settings.leaf)-$Group"}else{$settings.leaf}
$control=New-NativeCIArtifactRoot $env:GITHUB_WORKSPACE (Join-Path $boundary "contract-control-$scope-$id")
$child=Join-Path $boundary "$leaf-$id"
$remaining=[int][Math]::Floor(165-$clock.Elapsed.TotalSeconds)
if($remaining -lt 10){throw 'Contract invocation budget exhausted before start.'}
$childArguments=@('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot $settings.file),'-ArtifactRoot',$child)
if($Suite -eq 'N3'){$childArguments+=@('-Group',$Group)}
$null=Invoke-NativeCIProcess -FilePath (Get-Process -Id $PID).Path -Arguments $childArguments -WorkingDirectory $env:GITHUB_WORKSPACE -LogPrefix (Join-Path $control 'contract') -TimeoutSeconds $remaining
if($Suite -eq 'N3'){
    $receiptPath=Join-Path $child 'n3-regression-result.json'
    if(-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)){throw 'N3 child exited without a group result.'}
    $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
    $required=@{baseline=1;'final-output'=6;'partial-output'=6;rolling=2;hosted=4}[$Group]
    if($receipt.schema -ne 1 -or $receipt.group -cne $Group -or $receipt.state -cne 'passed' -or
        $receipt.expectedTests -ne $required -or @($receipt.tests).Count -ne $required -or
        @($receipt.tests|Where-Object state -CNE 'Passed').Count -ne 0){throw 'N3 child group result is incomplete or inconsistent.'}
}
