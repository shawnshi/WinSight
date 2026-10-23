#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactRoot,[string]$BeforeRoot,
    [Parameter(Mandatory)][ValidateSet('baseline','final-output','partial-output','rolling','hosted')][string]$Group)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Checkpoint.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Outputs.psm1') -Force
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=New-NativeCIArtifactRoot $repository $ArtifactRoot
$pwsh=(Get-Process -Id $PID).Path
$tests=[Collections.Generic.List[object]]::new()
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
function Reject([scriptblock]$Action,[string]$Pattern){$message=$null;try{& $Action}catch{$message=$_.Exception.Message};Check ($null -ne $message -and $message -match $Pattern) "Expected '$Pattern', actual '$message'"}
$expectedCount=@{baseline=$(if($BeforeRoot){2}else{1});'final-output'=6;'partial-output'=6;rolling=2;hosted=4}[$Group]
function Test([string]$Name,[scriptblock]$Action){
    $kind = switch -Wildcard ($Name) {
        'NM-N3R-04/05 new and touched*' {'baseline';break}
        'NM-N3R-04 frozen BEFORE*' {'baseline';break}
        'NM-N3R-04 passed *' {'final-output';break}
        'NM-N3R-04 incomplete *' {'partial-output';break}
        'NM-N3R-04 intact completed*' {'rolling';break}
        'NM-N3R-04 controlled later*' {'rolling';break}
        'NM-N3R-05 *' {'hosted';break}
        default {throw "Unclassified N3 oracle: $Name"}
    }
    if($kind -ne $Group){return}
    try{& $Action;$tests.Add([pscustomobject]@{name=$Name;state='Passed'});Write-Output "PASS $Name"}
    catch{$tests.Add([pscustomobject]@{name=$Name;state='Failed';error=$_.Exception.Message});throw}
}
function New-Fixture([string]$Name,[string]$State='passed',[string]$Mode='Offline') {
    $root=Join-Path $out $Name
    foreach($folder in @('candidate','logs','workspace/project/obj')){$null=New-Item -ItemType Directory -Path (Join-Path $root $folder) -Force}
    [IO.File]::WriteAllText((Join-Path $root 'candidate/synthetic.dll'),'NOT A NATIVE BINARY: fixture only')
    [IO.File]::WriteAllText((Join-Path $root 'workspace/project/obj/project.assets.json'),'synthetic restore dependency')
    $prefix=Join-Path $root 'logs/accepted'
    foreach($stream in @('stdout','stderr')){[IO.File]::WriteAllText("$prefix.$stream.log",'accepted fixture log')}
    $file=Get-Item -LiteralPath (Join-Path $root 'candidate/synthetic.dll')
    $inventory=[ordered]@{schema=1;modelFree=$true;installerReady=$false;releaseApproved=$false;files=@([ordered]@{path='synthetic.dll';bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash})}
    Write-NativeCIAtomicJson (Join-Path $root 'candidate-inventory.json') $inventory
    $gates=@(Get-NativeCIRequiredGates|ForEach-Object{[pscustomobject]@{name=$_;requiredForCI=$true;state='Passed'}})
    if($State -eq 'incomplete'){$gates[-1].state='NotRun'}
    $command=[ordered]@{gate='ProductionPublish';state='Passed';executable='fixture-only-never-executed';arguments=@();identity='fixture';logPrefix=$prefix;logHashes=@{stdout=(Get-FileHash -LiteralPath "$prefix.stdout.log" -Algorithm SHA256).Hash;stderr=(Get-FileHash -LiteralPath "$prefix.stderr.log" -Algorithm SHA256).Hash}}
    $report=[pscustomobject]@{schema=3;state=$State;mode='Build';dependencyMode=$Mode;runtime='win-x64';artifactRoot=$root;releaseApproved=$false;fullReleaseAcceptance=$false;gates=$gates;commands=@($command);inputHashes=@();suiteInventory=@();retainedOutputs=(Get-NativeCIRetainedOutputs $root)}
    Save-NativeCICheckpoint $root $report
    $parameters=@{artifactRoot=$root;dependencyMode=$Mode;nuget=(Join-Path $out 'public-nuget');cargo=(Join-Path $out 'public-cargo');cliHome=$env:DOTNET_CLI_HOME;toolSearchPathHash=(Get-NativeCIDigest $env:PATH);protocol=2}
    Write-NativeCIAtomicJson (Join-Path $root 'checkpoint-inputs.json') (Get-NativeCISourceBinding $repository $parameters)
    return $root
}
function ControllerArguments([string]$Root,[string]$Mode='Offline') {
    @('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot 'Invoke-NativeCICheckpoint.ps1'),'-ArtifactRoot',$Root,'-NuGetPackageRoot',(Join-Path $out 'public-nuget'),'-CargoCacheRoot',(Join-Path $out 'public-cargo'),'-DependencyMode',$Mode,'-Resume')
}
try {
    Test 'NM-N3R-04/05 new and touched PowerShell sources parse' {
        foreach($file in @((Join-Path $repository 'scripts/Test-NativeCI.ps1')) + @(Get-ChildItem -LiteralPath $PSScriptRoot -File|Where-Object Extension -IN @('.ps1','.psm1')|ForEach-Object FullName)){$t=$null;$e=$null;$null=[Management.Automation.Language.Parser]::ParseFile($file,[ref]$t,[ref]$e);Check ($e.Count -eq 0) "Parser failure $file"}
    }
    if($BeforeRoot) {
        Test 'NM-N3R-04 frozen BEFORE actually accepts deleted/mutated final and partial outputs' {
            $probe=Join-Path $out 'before-probe.ps1'
            [IO.File]::WriteAllText($probe,@'
param($Before,$Root)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $Before 'scripts/native-ci/NativeCI.Checkpoint.psm1') -Force
New-Item -ItemType Directory -Path $Root|Out-Null
$payload=Join-Path $Root 'candidate.dll'
$report=[pscustomobject]@{schema=2;state='passed';releaseApproved=$false;fullReleaseAcceptance=$false;commands=@();gates=@([pscustomobject]@{name='ArtifactInventory';requiredForCI=$true;state='Passed'})}
$observed=@()
foreach($state in @('passed','incomplete')){foreach($change in @('delete','mutate')){
[IO.File]::WriteAllText($payload,'original');$report.state=$state;Save-NativeCICheckpoint $Root $report
if($change -eq 'delete'){[IO.File]::Delete($payload)}else{[IO.File]::WriteAllText($payload,'mutated')}
$r=Read-NativeCICheckpoint $Root;$observed+=[pscustomobject]@{state=$state;change=$change;incorrectlyAccepted=$r.state -eq $state}
}}
$observed|ConvertTo-Json
'@)
            $prefix=Join-Path $out 'before-probe'
            $null=Invoke-NativeCIProcess $pwsh @('-NoProfile','-NonInteractive','-File',$probe,$BeforeRoot,(Join-Path $out 'before-fixture')) $repository $prefix
            $observed=Get-Content -LiteralPath "$prefix.stdout.log" -Raw|ConvertFrom-Json
            Check (@($observed|Where-Object{-not $_.incorrectlyAccepted}).Count -eq 0 -and $observed.Count -eq 4) 'Frozen defect baseline was not reproduced'
        }
    }
    foreach($state in @('passed','incomplete')) {
        foreach($target in @('candidate/synthetic.dll','candidate-inventory.json','workspace/project/obj/project.assets.json')) {
            foreach($change in @('delete','mutate')) {
                Test "NM-N3R-04 $state $change $target rejects actual controller resume without rebuild" {
                    $name=($state+'-'+$target.Replace('/','-').Replace('.','-')+'-'+$change)
                    $root=New-Fixture $name $state
                    $baseline=Read-NativeCICheckpoint $root
                    $envelopeHash=(Get-FileHash -LiteralPath (Join-Path $root 'checkpoint-state.json') -Algorithm SHA256).Hash
                    $bindingHash=(Get-FileHash -LiteralPath (Join-Path $root 'checkpoint-inputs.json') -Algorithm SHA256).Hash
                    $file=Join-Path $root $target
                    if($change -eq 'delete'){[IO.File]::Delete($file)}else{[IO.File]::WriteAllText($file,'mutated actual retained bytes')}
                    Reject {Invoke-NativeCIProcess $pwsh (ControllerArguments $root) $repository (Join-Path $out "$name-controller")} 'exited 1'
                    Check ((Get-FileHash -LiteralPath (Join-Path $root 'checkpoint-state.json') -Algorithm SHA256).Hash -eq $envelopeHash) 'Accepted checkpoint bindings were rewritten'
                    Check ((Get-FileHash -LiteralPath (Join-Path $root 'checkpoint-inputs.json') -Algorithm SHA256).Hash -eq $bindingHash) 'Accepted source/parameter binding was rewritten'
                    foreach($stream in @('stdout','stderr')){Check ((Get-FileHash -LiteralPath "$($baseline.commands[0].logPrefix).$stream.log" -Algorithm SHA256).Hash -eq $baseline.commands[0].logHashes.$stream) 'Accepted command logs changed'}
                    Check (@(Get-ChildItem -LiteralPath $root -Filter 'invocation-*.stdout.log').Count -eq 0) 'Worker/rebuild was launched after output drift'
                }
            }
        }
    }
    Test 'NM-N3R-04 intact completed candidate is revalidated and accepted without worker' {
        $root=New-Fixture 'intact'
        $null=Invoke-NativeCIProcess $pwsh (ControllerArguments $root) $repository (Join-Path $out 'intact-controller')
        Check (@(Get-ChildItem -LiteralPath $root -Filter 'invocation-*.stdout.log').Count -eq 0) 'Completed gates unexpectedly repeated'
    }
    Test 'NM-N3R-04 controlled later commands may replace intermediate assets and add candidate outputs' {
        $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'scripts/Test-NativeCI.ps1'),[ref]$tokens,[ref]$errors)
        foreach($name in @('Save-Report','Invoke-Gate','Run')){$node=$ast.Find({param($a)$a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name},$true);. ([scriptblock]::Create($node.Extent.Text))}
        $root=Join-Path $out 'rolling';$null=New-Item -ItemType Directory -Path (Join-Path $root 'logs') -Force
        $fixture=Join-Path $out 'rolling-command.ps1'
        [IO.File]::WriteAllText($fixture,'param($Root,$Value) $null=New-Item -ItemType Directory -Path "$Root/workspace/p/obj","$Root/candidate" -Force;[IO.File]::WriteAllText("$Root/workspace/p/obj/project.assets.json",$Value);[IO.File]::WriteAllText("$Root/candidate/$Value.dll",$Value)')
        $CheckpointWorker=$true;$Mode='Build';$DependencyMode='Offline';$workspace=$root;$inventory=@();$processEnvironment=@{};$state='incomplete'
        $inputs=[Collections.Generic.List[object]]::new();$commands=[Collections.Generic.List[object]]::new()
        $gates=@([pscustomobject]@{name='fixture';requiredForCI=$true;state='Pending';detail='fixture'})
        $script:currentGate=$gates[0];$script:commandCursor=0;$script:newCommands=0;$script:invocationClock=[Diagnostics.Stopwatch]::StartNew();$script:retainedOutputs=Get-NativeCIRetainedOutputs $root
        $savedOut=$out;$out=$root
        try {
            $null=Run $pwsh @('-NoProfile','-NonInteractive','-File',$fixture,$root,'first') $root
            $r=Read-NativeCICheckpoint $root
            $script:commandCursor=0;$script:newCommands=0
            $null=Run $pwsh @('-NoProfile','-NonInteractive','-File',$fixture,$root,'first') $root
            $null=Run $pwsh @('-NoProfile','-NonInteractive','-File',$fixture,$root,'second') $root
            $r=Read-NativeCICheckpoint $root
            Check ($r.commands.Count -eq 2 -and [IO.File]::ReadAllText((Join-Path $root 'workspace/p/obj/project.assets.json')) -eq 'second') 'Legitimate controlled output replacement rejected/repeated'
        }
        finally{$out=$savedOut}
    }
    Test 'NM-N3R-05 direct PublicHosted cannot bypass whole-invocation controller' {
        $root=Join-Path $out 'hosted-bypass'
        foreach($folder in @('public-nuget','public-cargo')){$null=New-Item -ItemType Directory -Path (Join-Path $out $folder) -Force}
        $launchArguments=@('-NoProfile','-NonInteractive','-File',(Join-Path $repository 'scripts/Test-NativeCI.ps1'),'-DependencyMode','PublicHosted','-ArtifactRoot',$root,'-NuGetPackageRoot',(Join-Path $out 'public-nuget'),'-CargoCacheRoot',(Join-Path $out 'public-cargo'))
        Reject {Invoke-NativeCIProcess -FilePath $pwsh -Arguments $launchArguments -WorkingDirectory $repository -LogPrefix (Join-Path $out 'hosted-bypass') -Environment @{GITHUB_ACTIONS='true';RUNNER_ENVIRONMENT='github-hosted';DOTNET_CLI_HOME='C:/Users/FixtureUser'}} 'exited 1'
        $r=Get-Content -LiteralPath (Join-Path $root 'native-ci-result.json') -Raw|ConvertFrom-Json
        Check ($r.commands.Count -eq 0 -and $r.state -eq 'failed' -and $r.gates[0].detail -match 'Every Build invocation') 'Hosted bypass ran a tool or failed for the wrong reason'
    }
    Test 'NM-N3R-05 hosted final gate rejects incomplete checkpoint without advancing' {
        $root=New-Fixture 'hosted-incomplete' 'incomplete' 'PublicHosted'
        Reject {Invoke-NativeCIProcess -FilePath $pwsh -Arguments ((ControllerArguments $root 'PublicHosted')+@('-RequireComplete')) -WorkingDirectory $repository -LogPrefix (Join-Path $out 'hosted-final') -Environment @{GITHUB_ACTIONS='true';RUNNER_ENVIRONMENT='github-hosted'}} 'exited 1'
        Check (@(Get-ChildItem -LiteralPath $root -Filter 'invocation-*.stdout.log').Count -eq 0) 'Final validation started new work'
        $r=Read-NativeCICheckpoint $root;Check ($r.state -eq 'incomplete' -and -not $r.fullReleaseAcceptance) 'Incomplete state was changed into pass'
    }
    Test 'NM-N3R-05 workflow has 44 finite bounded advances followed by mandatory completion' {
        $yaml=[IO.File]::ReadAllText((Join-Path $repository '.github/workflows/native.yml'))
        Check ([regex]::Matches($yaml,'run: ./scripts/native-ci/Advance-NativeCIHosted.ps1').Count -eq 44) 'Finite command allowance changed'
        Check ($yaml -notmatch 'run:.*Test-NativeCI.ps1.*PublicHosted|continue-on-error|\bwhile\b') 'Workflow contains long direct/bypass/polling execution'
        $complete=$yaml.IndexOf('-Resume -RequireComplete');$candidate=$yaml.IndexOf('name: Retain model-free candidate')
        Check ($complete -gt 0 -and $complete -lt $candidate) 'Mandatory final gate is not before candidate upload'
        $tokens=$null;$errors=$null;$hostAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Advance-NativeCIHosted.ps1'),[ref]$tokens,[ref]$errors)
        Check ($hostAst.FindAll({param($a)$a -is [Management.Automation.Language.LoopStatementAst]},$true).Count -eq 1) 'Unexpected hosted batching loop (only fixed input-field validation is allowed)'
        Check ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Advance-NativeCIHosted.ps1')).Contains('INCOMPLETE native-validation checkpoint')) 'Incomplete state transport is mislabeled'
    }
    Test 'NM-N3R-05 real wrapper forwards every group and rejects missing or incomplete evidence' {
        & (Join-Path $PSScriptRoot 'Test-NativeCIContract.Entry.ps1') -ArtifactRoot (Join-Path $out 'entry-checks')
        $entry=Get-Content -LiteralPath (Join-Path $out 'entry-checks/entrypoint-result.json') -Raw|ConvertFrom-Json
        Check ($entry.state -eq 'passed' -and @($entry.tests).Count -eq 10 -and -not $entry.productBuildExecuted) 'Wrapper entry integration contract failed'
    }
    Check ($tests.Count -eq $expectedCount) 'Selected finite N3 group did not complete every required oracle'
}
finally {
    Write-NativeCIAtomicJson (Join-Path $out 'n3-regression-result.json') ([ordered]@{schema=1;group=$Group;expectedTests=$expectedCount;state=$(if(@($tests|Where-Object state -EQ Failed).Count){'failed'}elseif($tests.Count -ne $expectedCount){'incomplete'}else{'passed'});tests=$tests.ToArray();productBuildExecuted=$false;remoteActionsExecuted=$false;fullReleaseAcceptance=$false})
}
