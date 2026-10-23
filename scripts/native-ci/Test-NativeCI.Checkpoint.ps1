#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Checkpoint.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Outputs.psm1') -Force
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out=New-NativeCIArtifactRoot $repository $ArtifactRoot
$pwsh=(Get-Process -Id $PID).Path
$tests=[Collections.Generic.List[object]]::new()
function Check([bool]$Value,[string]$Message) { if(-not $Value){throw $Message} }
function Reject([scriptblock]$Action,[string]$Pattern) {
    $message=$null;try{& $Action}catch{$message=$_.Exception.Message}
    Check ($null -ne $message -and $message -match $Pattern) "Expected '$Pattern', actual '$message'"
}
function Test([string]$Name,[scriptblock]$Action) {
    try{& $Action;$tests.Add([pscustomobject]@{name=$Name;state='Passed'});Write-Output "PASS $Name"}
    catch{$tests.Add([pscustomobject]@{name=$Name;state='Failed';error=$_.Exception.Message});throw}
}
# Execute the actual production gate/command functions against real PowerShell fixtures, not copied success logic or product compilers.
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'scripts/Test-NativeCI.ps1'),[ref]$tokens,[ref]$parseErrors)
Check ($parseErrors.Count -eq 0) 'Production script parser failed.'
foreach($name in @('Save-Report','Invoke-Gate','Run')) {
    $node=$ast.Find({param($a) $a -is [Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name},$true)
    . ([scriptblock]::Create($node.Extent.Text))
}
$append=Join-Path $out 'append.ps1';$counter=Join-Path $out 'executed.txt'
[IO.File]::WriteAllText($append,'param($Counter,$Value) [IO.File]::AppendAllText($Counter,$Value+"`n"); [Console]::WriteLine($Value)')
function Invoke-FixtureEngine([string]$Root,[switch]$Resume,[switch]$ChangeArgument) {
    $CheckpointWorker=$true;$Mode='Build';$DependencyMode='Offline';$workspace=$Root;$inventory=@();$processEnvironment=@{}
    $inputs=[Collections.Generic.List[object]]::new();$commands=[Collections.Generic.List[object]]::new();$state='incomplete'
    $gates=@('A','B')|ForEach-Object{[pscustomobject]@{name=$_;requiredForCI=$true;state='NotRun';detail='Not executed'}}
    if($Resume){$saved=Read-NativeCICheckpoint $Root;$gates=@($saved.gates);foreach($entry in $saved.commands){$commands.Add($entry)}}
    $script:commandCursor=0;$script:newCommands=0;$script:currentGate=$null
    $script:invocationClock=[Diagnostics.Stopwatch]::StartNew()
    $script:retainedOutputs=Get-NativeCIRetainedOutputs $Root
    # Save-Report resolves the fixture root from this scope, never the contract runner's result root.
    $out=$Root
    try {
        Invoke-Gate A {
            $first=if($ChangeArgument){'CHANGED'}else{'A1'}
            $null=Run $pwsh @('-NoProfile','-NonInteractive','-File',$append,'-Counter',$counter,'-Value',$first) $Root
            $null=Run $pwsh @('-NoProfile','-NonInteractive','-File',$append,'-Counter',$counter,'-Value','A2') $Root
        }
        Invoke-Gate B { $null=Run $pwsh @('-NoProfile','-NonInteractive','-File',$append,'-Counter',$counter,'-Value','B1') $Root }
        $state='passed';Save-Report
    }
    catch {
        if(-not $_.Exception.Data['NativeCICheckpoint']){throw}
        $state='incomplete';$script:currentGate.state='Pending';Save-Report
    }
}
try {
    Test 'New protocol sources parse, conditional media lock helper is restricted' {
        foreach($file in Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object{$_.Extension -in @('.ps1','.psm1')}){
            $t=$null;$e=$null;$null=[Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$t,[ref]$e);Check ($e.Count -eq 0) "Parse failure $($file.Name)"
        }
        [xml]$xml=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MediaSuiteLocks.targets') -Raw
        Check ($xml.Project.PropertyGroup.Condition -match 'WinSight.MediaBackend.Tests' -and $xml.Project.PropertyGroup.Condition -match 'WinSight.MediaProcess.Tests') 'Suite-only lock scope missing'
        Check ($xml.SelectNodes('//PropertyGroup').Count -eq 1 -and $xml.SelectNodes('//RestoreLockedMode').Count -eq 1) 'Unexpected lock injection scope'
    }
    $engine=Join-Path $out 'engine';$null=New-Item -ItemType Directory -Path (Join-Path $engine 'logs') -Force
    Test 'One-command boundary is incomplete and does not start the next command' {
        Invoke-FixtureEngine $engine
        $r=Read-NativeCICheckpoint $engine
        Check ($r.state -eq 'incomplete' -and $r.commands.Count -eq 1 -and $r.gates[0].state -eq 'Pending' -and $r.gates[1].state -eq 'NotRun') 'Checkpoint pretended to finish'
        Check ([IO.File]::ReadAllText($counter).Trim() -eq 'A1') 'Second command ran in first invocation'
    }
    $firstState=[IO.File]::ReadAllText((Join-Path $engine 'checkpoint-state.json'))
    Test 'Changed replay arguments are rejected before re-execution' {
        Reject {Invoke-FixtureEngine $engine -Resume -ChangeArgument} 'identity/order changed'
        Check ([IO.File]::ReadAllText($counter).Trim() -eq 'A1') 'Drift repeated a completed command'
        [IO.File]::WriteAllText((Join-Path $engine 'checkpoint-state.json'),$firstState)
    }
    Test 'Resume reuses partial command receipt then skips whole completed gate' {
        Invoke-FixtureEngine $engine -Resume
        $r=Read-NativeCICheckpoint $engine
        Check ($r.state -eq 'incomplete' -and $r.commands.Count -eq 2 -and $r.gates[0].state -eq 'Passed') 'Second invocation failed to checkpoint gate A'
        Invoke-FixtureEngine $engine -Resume
        $r=Read-NativeCICheckpoint $engine
        Check ($r.state -eq 'passed' -and $r.commands.Count -eq 3 -and -not $r.fullReleaseAcceptance) 'Final bounded native state incorrect'
        Invoke-FixtureEngine $engine -Resume
        Check (([IO.File]::ReadAllText($counter).Trim() -split "`n") -join ',' -eq 'A1,A2,B1') 'Completed commands/gates repeated'
    }
    Test 'Corrupt checksum, changed logs and interrupted states refuse resume' {
        $valid=[IO.File]::ReadAllText((Join-Path $engine 'checkpoint-state.json'))
        $r=Read-NativeCICheckpoint $engine
        $bad=$valid.Replace('"schema": 1','"schema": 9');[IO.File]::WriteAllText((Join-Path $engine 'checkpoint-state.json'),$bad)
        Reject {Read-NativeCICheckpoint $engine} 'checksum/schema'
        [IO.File]::WriteAllText((Join-Path $engine 'checkpoint-state.json'),$valid)
        $log="$($r.commands[0].logPrefix).stdout.log";$text=[IO.File]::ReadAllText($log);[IO.File]::AppendAllText($log,'tampered')
        Reject {Read-NativeCICheckpoint $engine} 'log missing/changed'
        [IO.File]::WriteAllText($log,$text)
        $r.state='incomplete';$r.commands[0].state='Running';Save-NativeCICheckpoint $engine $r
        Reject {Read-NativeCICheckpoint $engine} 'Interrupted/failed'
        $r.commands[0].state='Failed';Save-NativeCICheckpoint $engine $r
        Reject {Read-NativeCICheckpoint $engine} 'Interrupted/failed'
        [IO.File]::WriteAllText((Join-Path $engine 'checkpoint-state.json'),$valid)
    }
    Test 'Source addition, deletion, modification and semantic parameter drift invalidate binding' {
        $repo=Join-Path $out 'synthetic-source'
        foreach($dir in @('desktop','inference','native/winsight-tokenizer','scripts/native-ci','.github/workflows')){$null=New-Item -ItemType Directory -Path (Join-Path $repo $dir) -Force}
        foreach($file in @('scripts/Test-NativeCI.ps1','.github/workflows/native.yml','desktop/source.txt')){[IO.File]::WriteAllText((Join-Path $repo $file),'source')}
        $p=@{nuget='explicit-public';cliHome='existing-cli-home'};$expected=Get-NativeCISourceBinding $repo $p
        Assert-NativeCIBinding $expected (Get-NativeCISourceBinding $repo $p)
        $leaf=Join-Path $repo 'desktop/source.txt';[IO.File]::WriteAllText($leaf,'changed')
        Reject {Assert-NativeCIBinding $expected (Get-NativeCISourceBinding $repo $p)} 'binding changed'
        [IO.File]::Delete($leaf);Reject {Assert-NativeCIBinding $expected (Get-NativeCISourceBinding $repo $p)} 'binding changed'
        [IO.File]::WriteAllText($leaf,'source');$added=Join-Path $repo 'desktop/added.txt';[IO.File]::WriteAllText($added,'new')
        Reject {Assert-NativeCIBinding $expected (Get-NativeCISourceBinding $repo $p)} 'binding changed'
        [IO.File]::Delete($added);$p.cliHome='different-home';Reject {Assert-NativeCIBinding $expected (Get-NativeCISourceBinding $repo $p)} 'binding changed'
    }
    Test 'Controller lock contention refuses a second writer without launching worker' {
        Write-NativeCIAtomicJson (Join-Path $engine 'checkpoint-inputs.json') @{schema=1}
        $lock=[IO.File]::Open((Join-Path $engine 'checkpoint.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try { Reject {Invoke-NativeCIProcess $pwsh @('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot 'Invoke-NativeCICheckpoint.ps1'),'-Resume','-ArtifactRoot',$engine,'-NuGetPackageRoot','not-read','-CargoCacheRoot','not-read') $repository (Join-Path $out 'lock-contention')} 'exited 1' }
        finally {$lock.Dispose()}
        Check (@(Get-ChildItem -LiteralPath $engine -Filter 'invocation-*.json').Count -eq 0) 'Contending writer modified invocation state'
    }
    Test 'Synthetic or incomplete gate sets cannot impersonate completed production CI' {
        Reject {Assert-NativeCIProductionCheckpoint (Read-NativeCICheckpoint $engine) $engine} 'required production CI'
        $r=[pscustomobject]@{schema=3;retainedOutputs=(Get-NativeCIRetainedOutputs $engine);state='passed';mode='Build';runtime='win-x64';dependencyMode='Offline';artifactRoot=$engine;releaseApproved=$false;fullReleaseAcceptance=$false;commands=@();gates=@((Get-NativeCIRequiredGates)|ForEach-Object{[pscustomobject]@{name=$_;requiredForCI=$true;state='Passed'}})}
        Assert-NativeCIProductionCheckpoint $r $engine
        ($r.gates|Where-Object name -EQ MediaSyntheticTests).state='Pending'
        $valid=[IO.File]::ReadAllText((Join-Path $engine 'checkpoint-state.json'))
        Save-NativeCICheckpoint $engine $r
        Reject {Read-NativeCICheckpoint $engine} 'Incomplete required gates'
        [IO.File]::WriteAllText((Join-Path $engine 'checkpoint-state.json'),$valid)
    }
    Test 'Required media gates and real integration options are not omitted' {
        $source=[IO.File]::ReadAllText((Join-Path $repository 'scripts/Test-NativeCI.ps1'))
        foreach($value in @('MediaSyntheticRestore','MediaSyntheticTests','WinSight.NativeMedia.Tests','WinSight.MediaBackend.Tests','WinSight.MediaProcess.Tests','WinSight.MediaIndexing.Tests','WinSight.MediaMetadata.Tests','--require-integration','Required media lock missing','fullReleaseAcceptance = $false')){Check ($source.Contains($value)) "Missing protocol contract $value"}
    }
    Test 'Shared execution/drain/cleanup deadline is bounded for a real owned process tree' {
        $fixture=Join-Path $out 'deadline.ps1';$pidPath=Join-Path $out 'deadline.pid'
        [IO.File]::WriteAllText($fixture,'param($Pwsh,$PidPath) $p=Start-Process -FilePath $Pwsh -ArgumentList @(''-NoProfile'',''-NonInteractive'',''-Command'',''Start-Sleep 40'') -PassThru; [IO.File]::WriteAllText($PidPath,[string]$p.Id); [Console]::Out.Write("started"); Start-Sleep 40')
        $watch=[Diagnostics.Stopwatch]::StartNew()
        Reject {Invoke-NativeCIProcess -FilePath $pwsh -Arguments @('-NoProfile','-NonInteractive','-File',$fixture,$pwsh,$pidPath) -WorkingDirectory $out -LogPrefix (Join-Path $out 'deadline') -TimeoutSeconds 6} 'timed out'
        Check ($watch.Elapsed.TotalSeconds -lt 7) 'Process + pipe drain + cleanup obtained separate unbounded budgets'
        Check (Test-Path -LiteralPath $pidPath) 'Owned child not established; deadline oracle inconclusive'
        Check ($null -eq (Get-Process -Id ([int][IO.File]::ReadAllText($pidPath)) -ErrorAction SilentlyContinue)) 'Owned descendant survived'
    }
    Test 'Actual local controller propagates failed preflight and refuses automatic resume' {
        $failed=Join-Path $out 'real-preflight'
        $arguments=@('-NoProfile','-NonInteractive','-File',(Join-Path $PSScriptRoot 'Invoke-NativeCICheckpoint.ps1'),'-ArtifactRoot',$failed,'-NuGetPackageRoot',(Join-Path $out 'absent-public-nuget'),'-CargoCacheRoot',(Join-Path $out 'absent-public-cargo'))
        Reject {Invoke-NativeCIProcess $pwsh $arguments $repository (Join-Path $out 'preflight')} 'exited 1'
        $r=Get-Content -LiteralPath (Join-Path $failed 'native-ci-result.json') -Raw|ConvertFrom-Json
        Check ($r.state -eq 'failed' -and $r.commands.Count -eq 0) 'Preflight failure became checkpoint success or launched compiler'
        Reject {Invoke-NativeCIProcess $pwsh ($arguments + @('-Resume')) $repository (Join-Path $out 'preflight-resume')} 'exited 1'
        Check ($r.commands.Count -eq 0) 'Failed state retried product command'
    }
}
finally {
    Write-NativeCIAtomicJson (Join-Path $out 'checkpoint-contract-result.json') ([ordered]@{schema=1;state=$(if(@($tests|Where-Object state -EQ Failed).Count){'failed'}else{'passed'});tests=$tests.ToArray();productBuildExecuted=$false;legacyNineAssertionsRerun=$false;fullReleaseAcceptance=$false})
}
