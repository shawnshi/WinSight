#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactRoot,[switch]$RealBaseline)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Checkpoint.psm1') -Force
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=New-NativeCIArtifactRoot $repository $ArtifactRoot
$clock=[Diagnostics.Stopwatch]::StartNew()
$wrapper=Join-Path $PSScriptRoot 'Invoke-NativeCIContract.ps1'
$common=Join-Path $PSScriptRoot 'NativeCI.Common.psm1'
$wrapperHash=(Get-FileHash $wrapper).Hash
$fixture=Join-Path $out 'fixture';$scripts=Join-Path $fixture 'scripts/native-ci'
New-Item $scripts -ItemType Directory -Force|Out-Null
Copy-Item -LiteralPath $wrapper -Destination (Join-Path $scripts 'Invoke-NativeCIContract.ps1')
Copy-Item -LiteralPath $common -Destination (Join-Path $scripts 'NativeCI.Common.psm1')
$stub=@'
param([Parameter(Mandatory)][string]$ArtifactRoot,[Parameter(Mandatory)][ValidateSet('baseline','final-output','partial-output','rolling','hosted')][string]$Group)
$ErrorActionPreference='Stop'
if(Test-Path $ArtifactRoot){throw 'Fixture root collision'}
New-Item $ArtifactRoot -ItemType Directory -Force|Out-Null
@{group=$Group;artifactRoot=$ArtifactRoot;fixtureOnly=$true}|ConvertTo-Json|Set-Content (Join-Path $ArtifactRoot 'received.json')
if($env:N3_ENTRY_FIXTURE -eq 'missing'){exit 0}
$count=@{baseline=1;'final-output'=6;'partial-output'=6;rolling=2;hosted=4}[$Group]
$actualGroup=if($env:N3_ENTRY_FIXTURE -eq 'wrong-group'){'wrong'}else{$Group}
$state=if($env:N3_ENTRY_FIXTURE -eq 'incomplete'){'incomplete'}else{'passed'}
$tests=@(1..$count|ForEach-Object{@{name="fixture-$_";state='Passed'}})
@{schema=1;group=$actualGroup;state=$state;expectedTests=$count;tests=$tests}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $ArtifactRoot 'n3-regression-result.json')
'@
[IO.File]::WriteAllText((Join-Path $scripts 'Test-NativeCI.N3.ps1'),$stub)
$results=[Collections.Generic.List[object]]::new()
$pwsh=(Get-Process -Id $PID).Path
function RunEntry([string]$Id,[string[]]$ChildArguments,[int]$Expected,[string]$StubMode='', [string]$Workspace=$fixture){
    $remaining=[Math]::Floor(110-$clock.Elapsed.TotalSeconds)
    if($remaining -lt 1){throw 'Entrypoint suite shared110s budget exhausted'}
    $envs=@{GITHUB_ACTIONS='true';RUNNER_ENVIRONMENT='github-hosted';GITHUB_WORKSPACE=$Workspace;GITHUB_RUN_ID=$Id;GITHUB_RUN_ATTEMPT='1';N3_ENTRY_FIXTURE=$StubMode}
    $script=Join-Path $Workspace 'scripts/native-ci/Invoke-NativeCIContract.ps1'
    $commandArguments=@('-NoProfile','-NonInteractive','-File',$script)+$ChildArguments
    $r=Invoke-NativeCIProcess -FilePath $pwsh -Arguments $commandArguments -WorkingDirectory $Workspace -LogPrefix (Join-Path $out ($Id+'-'+$results.Count)) -TimeoutSeconds ([int][Math]::Min(40,$remaining)) -AllowedExitCodes @($Expected) -Environment $envs
    if($r.exitCode -ne $Expected){throw 'Unexpected wrapper exit'}
    $results.Add([pscustomobject]@{name=($Id+'-'+$results.Count);arguments=$ChildArguments;expectedExit=$Expected;actualExit=$r.exitCode;fixtureOnly=($Workspace -eq $fixture)})
}
try{
    $roots=@()
    foreach($g in @('baseline','final-output','partial-output','rolling','hosted')){
        RunEntry 'positive' @('-Suite','N3','-Group',$g) 0
        $leaf=Join-Path $fixture ('.dev/native-migration/native-ci/n3-contract-'+$g+'-positive-1')
        $received=Get-Content (Join-Path $leaf 'received.json') -Raw|ConvertFrom-Json
        if($received.group -cne $g -or $received.artifactRoot -cne $leaf){throw 'Selected group/artifact root not forwarded exactly'}
        if((Get-FileHash (Join-Path $scripts 'Invoke-NativeCIContract.ps1')).Hash -cne $wrapperHash){throw 'Fixture wrapper differs from production source'}
        $roots+=$leaf
    }
    if(@($roots|Sort-Object -Unique).Count -ne 5){throw 'N3 groups share artifact roots'}
    RunEntry 'missing-group' @('-Suite','N3') 1
    RunEntry 'non-n3-group' @('-Suite','Original','-Group','baseline') 1
    foreach($mode in @('missing','incomplete','wrong-group')){RunEntry ('bad-'+$mode) @('-Suite','N3','-Group','baseline') 1 $mode}
    $yaml=[IO.File]::ReadAllText((Join-Path $repository '.github/workflows/native.yml'))
    $calls=@([regex]::Matches($yaml,'(?m)^\s*run: \./scripts/native-ci/Invoke-NativeCIContract\.ps1 -Suite N3 -Group ([a-z-]+)\s*$')|ForEach-Object{$_.Groups[1].Value})
    if(($calls -join ',') -cne 'baseline,final-output,partial-output,rolling,hosted'){throw 'Workflow does not run exactly the five finite N3 groups'}
    foreach($g in $calls){if(-not $yaml.Contains('n3-contract-'+$g+'-')){throw 'Workflow does not retain each group receipt'}}
    if($yaml -match '(?m)^\s*run: \./scripts/native-ci/Invoke-NativeCIContract\.ps1 -Suite N3\s*$'){throw 'Old groupless N3 workflow entry remains'}
    if($RealBaseline){RunEntry 'actual-baseline' @('-Suite','N3','-Group','baseline') 0 '' $repository}
    if((Get-FileHash $wrapper).Hash -cne $wrapperHash){throw 'Production wrapper changed during validation'}
    Write-NativeCIAtomicJson (Join-Path $out 'entrypoint-result.json') ([ordered]@{schema=1;state='passed';wrapperSha256=$wrapperHash;tests=$results.ToArray();groups=$calls;realBaselineExecuted=[bool]$RealBaseline;hostedEnvironmentSimulated=$true;productBuildExecuted=$false;remoteActionsExecuted=$false;fullReleaseAcceptance=$false})
    Write-Output "PASS entrypoint contract: $($results.Count) process cases, five finite workflow groups, actualBaseline=$RealBaseline"
}catch{
    Write-NativeCIAtomicJson (Join-Path $out 'entrypoint-result.json') ([ordered]@{schema=1;state='failed';error=$_.Exception.Message;tests=$results.ToArray();productBuildExecuted=$false;remoteActionsExecuted=$false})
    throw
}
