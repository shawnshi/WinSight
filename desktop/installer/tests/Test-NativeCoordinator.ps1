[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ModulePath,
    [Parameter(Mandatory=$true)][string]$EntryPath,
    [Parameter(Mandatory=$true)][string]$ArtifactRoot,
    [ValidateSet('All','Baseline','UnknownIntents','UnknownReceipts','UnknownFinalized','UnknownRecovered','TamperedInspect','Expired','LowDeadline','Spoof','ControlRelationships','EntryFailure','DefaultEntry')][string[]]$Case=@('All'),
    [Parameter(Mandatory=$true)][string]$HostPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$utf8=[Text.UTF8Encoding]::new($false,$true)
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant() }
function SaveJson([string]$Path,$Value) { [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),$utf8) }
function Require($Condition,[string]$Message) { if(-not $Condition){throw $Message} }
foreach($p in @($ModulePath,$EntryPath,$ArtifactRoot,$HostPath)){ Require ([IO.Path]::IsPathRooted($p)) 'Absolute public paths required.' }
Require (([Diagnostics.Process]::GetCurrentProcess().MainModule.FileName) -ieq $HostPath) 'Explicit host must be the running host.'
Require (-not (Test-Path -LiteralPath $ArtifactRoot)) 'Fresh artifact root required; existing evidence is never reset.'
$frozen='__WINSIGHT_PRIVATE_FROZEN_COORDINATOR__'
throw 'N5.UnconfiguredHistoricalQualification: materialize the frozen private test lane and all pins before use.'
Require ($ModulePath -ieq (Join-Path $frozen 'NativeInstallCoordinator.psm1')) 'Only the fixed frozen module is permitted.'
Require ($EntryPath -ieq (Join-Path $frozen 'Invoke-NativeInstallCoordinator.ps1')) 'Only the fixed frozen entry is permitted.'
$pins=[ordered]@{'NativeInstallCoordinator.psm1'='df00e0edb3834487af6bcb000722dd755cb45c9eda29efb00d33e513f2dd5bf8';'Invoke-NativeInstallCoordinator.ps1'='7484e88ddbb616f72d123e5a71d6455570f48c18e47e5f8ad635866b46a00efd';'NativeInstaller.psm1'='68e6d550820d77c8e1f84b115ff251804ebf41ffe2c397e11a419f9c92577f03';'NativeDeploymentJob.psm1'='1f5600efadac9afc1f5afb2a7f6158643fcb25aeaf9400f58022674c69763331'}
foreach($n in $pins.Keys){Require ((HashFile (Join-Path $frozen $n)) -ceq $pins[$n]) ('Frozen pin mismatch: '+$n)}
$lane=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$main=$lane
Require ($ArtifactRoot.StartsWith((Join-Path $lane '.dev\'),[StringComparison]::OrdinalIgnoreCase)) 'Artifacts must belong to this test lane .dev.'
$protected=(Get-Content -LiteralPath (Join-Path $main '.dev\native-migration\release-acceptance\n5-coordinator-base\manifest.json') -Raw | ConvertFrom-Json).files
function ProtectedSnapshot {
    $rows=@();foreach($base in @($main,$lane)){foreach($f in $protected){$p=Join-Path $base $f.path;$h='ABSENT';if([IO.File]::Exists($p)){$h=HashFile $p};$rows+= [pscustomobject]@{base=$base;path=$f.path;hash=$h}}};,$rows
}
function IndexSnapshot {
    $p=(& git -C $lane rev-parse --git-path index);Require ($LASTEXITCODE -eq 0) 'Index path probe failed.'
    $cached=@(& git -C $lane diff --cached --name-status);Require ($LASTEXITCODE -eq 0) 'Cached list probe failed.'
    $exists=[IO.File]::Exists($p);$h=$null;if($exists){$h=HashFile $p}
    [pscustomobject]@{path=$p;exists=$exists;hash=$h;cached=$cached}
}
$historyPaths=@((Join-Path $main '.dev\native-migration\release-acceptance\n5-coordinator-base\oracle-v1\Test-NativeCoordinator.ps1'),(Join-Path $main '.dev\native-migration\release-acceptance\n5-coordinator-base\oracle-v1\summary.json'),(Join-Path $lane '.dev\n5-independent-081449d1\summary.json'))
$historyBefore=@($historyPaths | ForEach-Object {[pscustomobject]@{path=$_;sha256=(HashFile $_)}})
Require ($historyBefore[0].sha256 -ceq '1d6b601ac8d8cd6e94e03d11339ae6ad0555de6190eb0dadc582f9d5335386b5') 'Original test history missing.'
Require ($historyBefore[1].sha256 -ceq '7521030b6f9bc097184d729b7708aaeecb8d99095f42b640859cdf537aa3059e') 'Original red summary missing.'
Require ($historyBefore[2].sha256 -ceq $historyBefore[1].sha256) 'Original evidence summary changed.'
$before=ProtectedSnapshot;$indexBefore=IndexSnapshot
$null=[IO.Directory]::CreateDirectory($ArtifactRoot)
SaveJson (Join-Path $ArtifactRoot 'baseline.json') ([pscustomobject]@{pins=$pins;protected=$before;index=$indexBefore;history=$historyBefore;hostEdition=$PSVersionTable.PSEdition;hostVersion=$PSVersionTable.PSVersion.ToString();testSha256=(HashFile $PSCommandPath)})
Import-Module $ModulePath -ErrorAction Stop
Import-Module (Join-Path $frozen 'NativeDeploymentJob.psm1') -ErrorAction Stop
$results=[Collections.Generic.List[object]]::new()
$clock=[Diagnostics.Stopwatch]::StartNew()
function Snapshot([string]$Root) {
    $rows=@();if([IO.Directory]::Exists($Root)) { foreach($f in @(Get-ChildItem -LiteralPath $Root -Recurse -Force | Sort-Object FullName)) { $h='DIRECTORY';if(-not $f.PSIsContainer){$h=HashFile $f.FullName};$rows+=[pscustomobject]@{path=$f.FullName.Substring($Root.Length);hash=$h} } }
    ConvertTo-Json -InputObject @($rows) -Depth 8 -Compress
}
function NewFixture([string]$Name) {
    $root=Join-Path $ArtifactRoot $Name;Require (-not (Test-Path -LiteralPath $root)) 'Case root exists.'
    $null=[IO.Directory]::CreateDirectory((Join-Path $root 'stage\app'))
    $null=[IO.Directory]::CreateDirectory((Join-Path $root 'data'))
    [IO.File]::WriteAllText((Join-Path $root 'data\keep.txt'),'untouched-data',$utf8)
    [IO.File]::WriteAllText((Join-Path $root 'stage\app\payload.txt'),'inert-native-payload',$utf8)
    $payload=Join-Path $root 'stage\app\payload.txt'
    $manifest=[pscustomobject]@{schema=1;runtime='win-x64';selfContained=$true;releaseApproved=$false;appOnlyPreview=$true;files=@([pscustomobject]@{path='app/payload.txt';bytes=(Get-Item -LiteralPath $payload).Length;sha256=(HashFile $payload)})}
    $mp=Join-Path $root 'stage\release-manifest.json';SaveJson $mp $manifest
    $shellRoot=Join-Path $root 'install\shell';$null=[IO.Directory]::CreateDirectory($shellRoot)
    $shell=Join-Path $shellRoot 'synthetic.txt';[IO.File]::WriteAllText($shell,'inert-shell-evidence-not-OS',$utf8)
    $binding=[pscustomobject]@{schema=1;protocol='N5.FileVerification/1';mode='Verification';appId='WinSight.Native.Preview';instanceId=[Guid]::NewGuid().ToString('N');installRoot=(Join-Path $root 'install');payloadRoot=(Join-Path $root 'install\payload');controlRoot=(Join-Path $root 'control');dataRoot=(Join-Path $root 'data');shellRoot=$shellRoot;engineSha256=$pins['NativeInstaller.psm1'];jobSha256=$pins['NativeDeploymentJob.psm1'];coordinatorSha256=$pins['NativeInstallCoordinator.psm1'];entrySha256=$pins['Invoke-NativeInstallCoordinator.ps1'];host=[pscustomobject]@{edition=[string]$PSVersionTable.PSEdition;version=$PSVersionTable.PSVersion.ToString();executableSha256=(HashFile $HostPath);machine=[Environment]::MachineName;userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;serializer='ConvertTo-Json/Depth30/UTF8-noBOM'};shellFiles=@([pscustomobject]@{path='synthetic.txt';bytes=(Get-Item -LiteralPath $shell).Length;sha256=(HashFile $shell)})}
    $bp=Join-Path $root 'binding.json';SaveJson $bp $binding
    [pscustomobject]@{root=$root;binding=$binding;common=@{Operation='Apply';BindingPath=$bp;BindingSha256=(HashFile $bp);DeadlineMilliseconds=20000};transaction=[Guid]::NewGuid().ToString('N');manifestHash=(HashFile $mp);calls=[Collections.Generic.List[object]]::new()}
}
function Call($F,[string]$Command,[hashtable]$Extra=@{}) {
    $a=@{};foreach($k in $F.common.Keys){$a[$k]=$F.common[$k]};$a.Command=$Command;foreach($k in $Extra.Keys){$a[$k]=$Extra[$k]}
    Require ($clock.ElapsedMilliseconds -lt 125000) 'Caller total budget: refusing additional invocation.'
    $tickBefore=Get-NativeTick;$sw=[Diagnostics.Stopwatch]::StartNew();$facts=$null;$errorText=$null;$errorId=$null;$failure=$null;$failureDepth=$null
    try{$facts=Invoke-NativeInstallCoordinator @a}catch{
        $errorText=$_.Exception.Message;$errorId=$_.FullyQualifiedErrorId;$ex=$_.Exception
        for($depth=0;$depth -lt 8 -and $null -ne $ex;$depth++){
            if($ex.Data.Contains('N5Failure')){$failure=$ex.Data['N5Failure'];$failureDepth=$depth;break};$ex=$ex.InnerException
        }
    }
    $sw.Stop();$r=[pscustomobject]@{command=$Command;parameters=$a;success=($null -eq $errorText);facts=$facts;failure=$failure;failureExceptionDepth=$failureDepth;error=$errorText;errorId=$errorId;elapsedMilliseconds=$sw.ElapsedMilliseconds;tickBefore=$tickBefore;tickAfter=(Get-NativeTick)}
    $F.calls.Add($r);$r
}
function Intent($F) { Call $F 'CreateIntent' @{TransactionId=$F.transaction;StageDirectory=(Join-Path $F.root 'stage');ManifestSha256=$F.manifestHash} }
function AssertMetadata($Facts) {
    Require ($Facts.status -ceq 'MetadataInspected' -and $Facts.auditScope -ceq 'ControlMetadataOnly') 'NM-N5-INSPECT-01: exact metadata scope missing.'
    Require ($Facts.payloadHealth -ceq 'NotAssessed' -and $Facts.shellHealth -ceq 'NotAssessed' -and $Facts.lifecycleReconciled -eq $false) 'NM-N5-INSPECT-01: unsupported health/lifecycle claim.'
    Require ($Facts.liveAdaptersAvailable -eq $false -and $Facts.shellEvidence -ceq 'VerificationOnlyNotOSCommit') 'Live adapter limitations missing.'
}
function AssertFailure($R,[string]$Guard,[string]$Stage) {
    Require (-not $R.success) ('Expected failure at '+$Guard)
    $v=$R.failure;Require ($null -ne $v) 'NM-N5-SETTLE-01: no actual N5Failure exception data/raw entry JSON.'
    Require ($v.schema -eq 1 -and $v.status -cin @('Failed','BlockedUnsettled')) 'Invalid N5Failure schema/status.'
    Require ($v.primaryError.message.Contains($Guard) -and $v.primaryError.id.Length -gt 0 -and $null -ne $v.primaryError.category) ('Wrong primary guard: '+$v.primaryError.message)
    Require ($v.primaryError.message.Length -le 2048 -and $v.primaryError.id.Length -le 128) 'Unbounded primary error.'
    $s=$v.settlement
    Require ($s.cleanupErrors -is [array] -and $s.cleanupErrors.Count -le 8 -and $s.elapsedMilliseconds -ge 0) 'Invalid cleanup error/elapsed facts.'
    if($Stage -eq 'Worker') {
        Require ($v.status -ceq 'Failed' -and $s.workerStarted -eq $true -and $s.workerPid -gt 0 -and $s.workerExited -eq $true -and $s.workerExitCode -ne 0) 'Real worker exit not demonstrated.'
        Require ($s.jobCreated -eq $true -and $s.pipeCreated -eq $true -and $s.activeOwnedProcesses -eq 0) 'Owned Job/pipe settlement not demonstrated.'
        Require ($s.stdoutState -ceq 'Eof' -and $s.stderrState -ceq 'Eof' -and $s.readersSettled -eq $true -and $s.cleanupState -ceq 'Settled' -and $s.cleanupErrors.Count -eq 0) 'Failure readers/cleanup not proved settled.'
    } else {
        Require ($s.workerStarted -eq $false -and $null -eq $s.workerPid -and $null -eq $s.workerExited -and $null -eq $s.workerExitCode) 'Prestart invented worker facts.'
        Require ($s.stdoutState -ceq 'NotStarted' -and $s.stderrState -ceq 'NotStarted' -and $null -eq $s.readersSettled) 'Prestart invented EOF/reader facts.'
        if($Stage -eq 'BeforeResources'){Require ($s.jobCreated -eq $false -and $s.pipeCreated -eq $false -and $null -eq $s.activeOwnedProcesses -and $s.cleanupState -ceq 'NotStarted') 'Pre-bootstrap invented Job facts.'}
        else{Require ($s.jobCreated -eq $true -and $s.pipeCreated -eq $true -and $s.activeOwnedProcesses -eq 0 -and $s.cleanupState -ceq 'Settled') 'Low reserve owned-resource cleanup not demonstrated.'}
    }
}
function AssertModuleClock($R) {
    $s=$R.facts.settlement;$age=($R.tickBefore-$s.startTick+4294967296L)%4294967296L
    Require ($s.deadlineMilliseconds -eq $R.parameters.DeadlineMilliseconds -and $s.bootstrapElapsedMilliseconds -ge 0) 'Module accounting fields mismatch.'
    if($R.parameters.ContainsKey('StartTick')){Require ($s.startTick -eq $R.parameters.StartTick) 'Explicit StartTick reset.'}
    else{Require ($age -le ($s.bootstrapElapsedMilliseconds+64)) 'Module default tick undercounts or uses unrelated epoch.'}
    Require ($s.elapsedMilliseconds -ge $s.bootstrapElapsedMilliseconds -and $s.elapsedMilliseconds -le ($R.elapsedMilliseconds+96)) 'Module default accounting excludes bootstrap or exceeds calibration allowance.'
}
function EntryCall($F,[string]$Command,[hashtable]$Extra=@{}) {
    Require ($clock.ElapsedMilliseconds -lt 110000) 'Caller budget: refusing additional entry process.'
    $a=@{Command=$Command;Operation='Apply';BindingPath=$F.common.BindingPath;BindingSha256=$F.common.BindingSha256};foreach($k in $Extra.Keys){$a[$k]=$Extra[$k]}
    $si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=$HostPath;$si.UseShellExecute=$false;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true;$si.StandardOutputEncoding=$utf8;$si.StandardErrorEncoding=$utf8
    $argsText='-NoProfile -NonInteractive -File "'+$EntryPath+'"';foreach($k in $a.Keys){$argsText+=' -'+$k+' "'+[string]$a[$k]+'"'};$si.Arguments=$argsText
    $p=[Diagnostics.Process]::new();$p.StartInfo=$si;$job=[IntPtr]::Zero;$started=$false;$assigned=$false;$texts=@('','');$states=@('NotStarted','NotStarted');$tasks=@();$buffers=@();$cleanup=[Collections.Generic.List[string]]::new();$nativeFacts=$null;$failure=$null;$exitCode=$null;$complete=$false;$harnessError=$null;$active=$null
    $tickBefore=Get-NativeTick;$sw=[Diagnostics.Stopwatch]::StartNew()
    try {
        $job=New-NativeDeploymentJob;$started=$p.Start();Require $started 'Entry process did not start.'
        $assigned=[WinSight.DeploymentJobApi]::AssignProcessToJobObject($job,$p.Handle);Require $assigned 'Owned entry Job assignment failed.'
        $buffers=@((New-Object char[] 2048),(New-Object char[] 2048));$streams=@($p.StandardOutput,$p.StandardError)
        $tasks=@($streams[0].ReadAsync($buffers[0],0,2048),$streams[1].ReadAsync($buffers[1],0,2048));$states=@('Pending','Pending')
        while($sw.ElapsedMilliseconds -lt 25000){
            for($j=0;$j -lt 2;$j++){if($states[$j] -eq 'Pending' -and $tasks[$j].IsCompleted){$n=$tasks[$j].GetAwaiter().GetResult();if($n -eq 0){$states[$j]='Eof'}else{$texts[$j]+=[string]::new($buffers[$j],0,$n);Require (($texts[0].Length+$texts[1].Length) -le 65536) 'Entry output exceeded bounded capture.';$tasks[$j]=$streams[$j].ReadAsync($buffers[$j],0,2048)}}}
            if($p.HasExited -and $states[0] -eq 'Eof' -and $states[1] -eq 'Eof'){$active=Get-NativeJobActiveProcesses $job;if($active -eq 0){$complete=$true;break}}
            Start-Sleep -Milliseconds 10
        }
        Require $complete 'Owned entry/stdio/tree exceeded 25s caller allowance.';$exitCode=$p.ExitCode
        if($exitCode -eq 0){$nativeFacts=$texts[0]|ConvertFrom-Json}else{$failure=$texts[1]|ConvertFrom-Json}
    }catch{$harnessError=$_.Exception.Message}
    finally {
        if($job -ne [IntPtr]::Zero){try{if((Get-NativeJobActiveProcesses $job) -gt 0){Require ([WinSight.DeploymentJobApi]::TerminateJobObject($job,124)) 'Owned entry Job termination failed.'};while($sw.ElapsedMilliseconds -lt 29000 -and (Get-NativeJobActiveProcesses $job) -gt 0){Start-Sleep -Milliseconds 10};$active=Get-NativeJobActiveProcesses $job}catch{$cleanup.Add($_.Exception.Message)}}
        if($started){try{if(-not $assigned -and -not $p.HasExited){$p.Kill()};Require ($p.WaitForExit([Math]::Max(0,29500-$sw.ElapsedMilliseconds))) 'Owned entry process not settled.';if($tasks.Count){Require ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$tasks,[Math]::Max(0,29800-$sw.ElapsedMilliseconds))) 'Owned entry reader tasks not settled.'};$p.StandardOutput.Close();$p.StandardError.Close()}catch{$cleanup.Add($_.Exception.Message)}}
        if($job -ne [IntPtr]::Zero){try{Require ([WinSight.DeploymentJobApi]::CloseHandle($job)) 'Owned entry Job close failed.'}catch{$cleanup.Add($_.Exception.Message)}};$p.Dispose()
    }
    $sw.Stop();$r=[pscustomobject]@{command=$Command;surface='RawEntryProcess';parameters=$a;success=($complete -and $exitCode -eq 0 -and $null -eq $harnessError);facts=$nativeFacts;failure=$failure;error=$harnessError;exitCode=$exitCode;rawStdout=$texts[0];rawStderr=$texts[1];elapsedMilliseconds=$sw.ElapsedMilliseconds;tickBefore=$tickBefore;tickAfter=(Get-NativeTick);harnessSettlement=[pscustomobject]@{activeOwnedProcesses=$active;stdoutState=$states[0];stderrState=$states[1];cleanupErrors=@($cleanup.ToArray())}}
    $F.calls.Add($r);Require ($null -eq $harnessError -and $cleanup.Count -eq 0 -and $active -eq 0) ('Entry harness failed: '+$harnessError+' '+($cleanup -join ';'));$r
}
$names=@('Baseline','UnknownIntents','UnknownReceipts','UnknownFinalized','UnknownRecovered','TamperedInspect','Expired','LowDeadline','Spoof','ControlRelationships','EntryFailure','DefaultEntry')
$baselineOkay=$false
foreach($name in $names) {
    if('All' -notin $Case -and $name -notin $Case){continue}
    $f=NewFixture $name;$state='NOT_RUN';$note='';$observations=[ordered]@{}
    try {
        if($name -eq 'Baseline') {
            $s=Snapshot $f.root;$i=Call $f 'Inspect';Require $i.success ('Valid Inspect failed: '+$i.error);AssertMetadata $i.facts;AssertModuleClock $i;Require ($s -ceq (Snapshot $f.root)) 'Inspect wrote to roots.'
            $explicit=Get-NativeTick;$pinned=Call $f 'Inspect' @{StartTick=$explicit};Require $pinned.success ('Explicit tick Inspect failed: '+$pinned.error);AssertModuleClock $pinned
            $c=Intent $f;Require $c.success ('Valid CreateIntent failed: '+$c.error);Require ($c.facts.status -ceq 'IntentCreated') 'Missing IntentCreated fact.'
            Require (-not [IO.Directory]::Exists($f.binding.payloadRoot)) 'CreateIntent mutated payload.'
            $baselineOkay=$true;$state='PASS';$note='Same-host public wrapper Inspect/CreateIntent valid; no payload mutation.'
        } elseif($name -like 'Unknown*') {
            if(-not $baselineOkay){throw 'DEPENDENCY: valid baseline unavailable; unknown-object branch not executed.'}
            $valid=Call $f 'Inspect';Require $valid.success ('Fresh Inspect failed: '+$valid.error)
            $area=$name.Substring(7).ToLowerInvariant();$dir=Join-Path $f.binding.controlRoot $area;$null=[IO.Directory]::CreateDirectory($dir)
            $tmp=Join-Path $f.root 'object.json';SaveJson $tmp ([pscustomobject]@{schema=999;kind='UnrecognizedIndependentEvidence';transactionId=[Guid]::NewGuid().ToString('N')})
            $h=HashFile $tmp;$object=Join-Path $dir ($h+'.json');[IO.File]::Copy($tmp,$object)
            $s=Snapshot $f.root;$i=Call $f 'Inspect';$afterInspect=Snapshot $f.root;$c=Intent $f
            $observations.objectPath=$object;$observations.objectSha256=$h;$observations.objectPreserved=((HashFile $object) -ceq $h);$observations.inspectReadonly=($s -ceq $afterInspect);$observations.afterCallsUnchanged=($s -ceq (Snapshot $f.root));$observations.intentObjects=@();$intentDir=Join-Path $f.binding.controlRoot 'intents';if([IO.Directory]::Exists($intentDir)){$observations.intentObjects=@(Get-ChildItem -LiteralPath $intentDir -Filter '*.json' -ErrorAction Stop | ForEach-Object {$_.Name})}
            if($i.success -or $c.success){$state='PRODUCT_GAP';$note='NM-N5-CONTROL-01: canonical hash-named unknown schema/kind accepted.'}
            else{AssertFailure $i 'N5.Schema: closed fields required.' 'Worker';AssertFailure $c 'N5.Schema: closed fields required.' 'Worker';Require ($observations.objectPreserved -and $observations.afterCallsUnchanged) 'Unknown evidence mutated/adopted.';$state='PASS';$note='NM-N5-CONTROL-01: unknown object rejected at schema guard, bytes/roots unchanged; actual failure settlement retained.'}
        } elseif($name -eq 'TamperedInspect') {
            if(-not $baselineOkay){throw 'DEPENDENCY: valid baseline unavailable.'}
            $c=Intent $f;Require $c.success ('Valid CreateIntent failed: '+$c.error)
            $phase=@{TransactionId=$f.transaction;IntentSha256=$c.facts.intentSha256}
            $p=Call $f 'Prepare' $phase;Require $p.success ('Valid Prepare failed: '+$p.error)
            $p=Call $f 'Commit' $phase;Require $p.success ('Valid Commit failed: '+$p.error)
            $phase.ExpectedReceiptSha256=$p.facts.receiptSha256;$p=Call $f 'Finalize' $phase;Require $p.success ('Valid Finalize failed: '+$p.error)
            $sp=Join-Path $f.binding.payloadRoot 'state.json';$sh=HashFile $sp
            $active=Join-Path $f.binding.payloadRoot ('versions\'+$f.manifestHash+'\app\payload.txt');$old=HashFile $active;[IO.File]::WriteAllText($active,'TAMPERED-inert-payload',$utf8)
            $s=Snapshot $f.root;$i=Call $f 'Inspect' @{ExpectedReceiptSha256=$phase.ExpectedReceiptSha256}
            $observations.originalPayloadSha256=$old;$observations.tamperedPayloadSha256=HashFile $active;$observations.stateBytesUnchanged=((HashFile $sp) -ceq $sh);$observations.inspectReadonly=($s -ceq (Snapshot $f.root))
            Require $i.success ('Metadata Inspect failed: '+$i.error);AssertMetadata $i.facts
            Require ($observations.stateBytesUnchanged -and $observations.inspectReadonly) 'Metadata Inspect changed roots/state.'
            $f.transaction=[Guid]::NewGuid().ToString('N');$c=Call $f 'CreateIntent' @{TransactionId=$f.transaction;StageDirectory=(Join-Path $f.root 'stage');ManifestSha256=$f.manifestHash;ExpectedReceiptSha256=$phase.ExpectedReceiptSha256}
            AssertFailure $c 'N5.Payload: changed file.' 'Worker';Require ($s -ceq (Snapshot $f.root)) 'Worker rejection changed roots.'
            $state='PASS';$note='NM-N5-INSPECT-01 exact metadata-only claims; NM-N5-SETTLE-01 real Core payload rejection with exit/Job0/EOF facts.'
        } elseif($name -eq 'Expired' -or $name -eq 'LowDeadline') {
            $s=Snapshot $f.root;$a=@{DeadlineMilliseconds=1000};if($name -eq 'Expired'){$a.StartTick=(((Get-NativeTick)-5000+4294967296L)%4294967296L)}
            $r=Call $f 'Inspect' $a;$expected=if($name -eq 'Expired'){'N5.Deadline: invalid/expired native tick.'}else{'N5.Deadline: insufficient dispatch reserve.'}
            $observations.rootsUnchanged=($s -ceq (Snapshot $f.root));$observations.failureSettlementFieldsExposed=($null -ne $r.failure)
            $stage=if($name -eq 'Expired'){'BeforeResources'}else{'DispatchPrestart'};AssertFailure $r $expected $stage
            Require $observations.rootsUnchanged 'Deadline failure changed roots.';Require ($r.elapsedMilliseconds -lt 5000) 'Deadline failure exceeded diagnostic elapsed allowance.'
            $state='PASS';$note='NM-N5-SETTLE-01: actual prestart N5Failure; no invented worker/EOF. Running-child timeout remains NOT PROVEN.'
        } elseif($name -eq 'Spoof') {
            Require ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('N5_NONCE'))) 'Pre-existing marker: refusing environment replacement.'
            $s=Snapshot $f.root
            try{[Environment]::SetEnvironmentVariable('N5_NONCE','independent-spoof');$r=Call $f 'Inspect'}finally{[Environment]::SetEnvironmentVariable('N5_NONCE',$null)}
            AssertFailure $r 'N5.Worker: stray internal environment rejected.' 'BeforeResources'
            Require ($s -ceq (Snapshot $f.root)) 'Spoof rejection changed roots.';$state='PASS';$note='Public module rejects stray internal marker at exact guard with truthful prestart facts.'
        } elseif($name -eq 'ControlRelationships') {
            if(-not $baselineOkay){throw 'DEPENDENCY: valid baseline unavailable.'}
            $c=Intent $f;Require $c.success ('Recovery setup CreateIntent failed: '+$c.error);$phase=@{TransactionId=$f.transaction;IntentSha256=$c.facts.intentSha256}
            $p=Call $f 'Prepare' $phase;Require $p.success ('Recovery setup Prepare failed: '+$p.error)
            $p=Call $f 'Recover' $phase;Require $p.success ('Valid first-install Recover failed: '+$p.error)
            $rp=Join-Path $f.binding.controlRoot ('recovered\'+$c.facts.intentSha256+'.json');$fact=Get-Content -LiteralPath $rp -Raw|ConvertFrom-Json
            Require ($fact.receiptSha256 -ceq '') 'First-install recovery must retain explicit empty receipt case.'
            $i=Call $f 'Inspect';Require $i.success ('Valid recovered fact falsely rejected: '+$i.error);AssertMetadata $i.facts
            $wrong=Join-Path $f.binding.controlRoot ('recovered\'+('f'*64)+'.json');[IO.File]::Copy($rp,$wrong);$s=Snapshot $f.root
            $i=Call $f 'Inspect';AssertFailure $i 'N5.Control: recovered filename must identify its intent.' 'Worker'
            $c=Intent $f;AssertFailure $c 'N5.Control: recovered filename must identify its intent.' 'Worker'
            Require ($s -ceq (Snapshot $f.root)) 'Wrong-relationship evidence changed.';$observations.validRecoveredFact=$fact;$observations.wrongFilename=$wrong
            $state='PASS';$note='NM-N5-CONTROL-01: real first-install empty receipt recovery accepted; copied fact under wrong intent filename blocks and preserves.'
        } elseif($name -eq 'EntryFailure') {
            if(-not $baselineOkay){throw 'DEPENDENCY: valid baseline unavailable.'}
            $i=Call $f 'Inspect';Require $i.success ('Entry negative prerequisite failed: '+$i.error)
            $dir=Join-Path $f.binding.controlRoot 'receipts';$null=[IO.Directory]::CreateDirectory($dir);$tmp=Join-Path $f.root 'unknown.json';SaveJson $tmp ([pscustomobject]@{schema=999;kind='UnknownEntryEvidence'});[IO.File]::Copy($tmp,(Join-Path $dir ((HashFile $tmp)+'.json')))
            $s=Snapshot $f.root;$r=EntryCall $f 'Inspect';Require ($r.exitCode -ne 0 -and $r.rawStdout.Length -eq 0) 'Entry failure did not use nonzero/raw stderr.'
            AssertFailure $r 'N5.Schema: closed fields required.' 'Worker';Require ($s -ceq (Snapshot $f.root)) 'Entry failure changed roots.'
            $state='PASS';$note='NM-N5-SETTLE-01: direct .NET raw stderr parses as N5Failure; Core primary error and real exit/Job0/EOF retained.'
        } elseif($name -eq 'DefaultEntry') {
            if(-not $baselineOkay){throw 'DEPENDENCY: valid baseline unavailable.'}
            $s=Snapshot $f.root;$r=EntryCall $f 'Inspect';Require $r.success ('Default entry failed: '+$r.error);Require ($r.rawStderr.Length -eq 0) 'Default entry unexpected stderr.';AssertMetadata $r.facts
            $a=$r.facts.entryAccounting;$v=$r.facts.settlement
            Require ($a.deadlineMilliseconds -eq 140000 -and $v.deadlineMilliseconds -eq $a.deadlineMilliseconds -and $v.startTick -eq $a.startTick) 'Default entry reset shared tick/deadline.'
            Require ($a.calibrationAllowanceMilliseconds -eq 32 -and $a.synchronousBootstrapPreemptible -eq $false) 'Bootstrap limitations/calibration missing.'
            Require ($a.clockBootstrapElapsedMilliseconds -ge 0 -and $v.bootstrapElapsedMilliseconds -ge 0 -and $v.elapsedMilliseconds -ge $a.clockBootstrapElapsedMilliseconds -and $a.elapsedMilliseconds -ge $v.elapsedMilliseconds) 'Earliest clock bootstrap excluded.'
            $tickTotal=($r.tickAfter-$r.tickBefore+4294967296L)%4294967296L;$tickAccounted=($r.tickAfter-$a.startTick+4294967296L)%4294967296L
            Require ($tickAccounted -le ($tickTotal+64) -and $a.elapsedMilliseconds -le ($tickAccounted+32) -and $a.elapsedMilliseconds -le ($r.elapsedMilliseconds+96)) 'Entry elapsed inconsistent with actual native tick interval.'
            $observations.externalElapsedMilliseconds=$r.elapsedMilliseconds;$observations.entryAccountedMilliseconds=$a.elapsedMilliseconds;$observations.startupAndObservationGapMilliseconds=$r.elapsedMilliseconds-$a.elapsedMilliseconds;$observations.nativeTickOuterInterval=$tickTotal;$observations.nativeTickAccountedAtObservation=$tickAccounted;$observations.limit='Host startup before earliest script instruction and observation scheduling remain outside entry accounting; synchronous bootstrap cannot be preempted.'
            Require ($s -ceq (Snapshot $f.root)) 'Default entry Inspect changed roots.';$state='PASS';$note='NM-N5-DEADLINE-01: default entry and module share actual tick/deadline; bootstrap included, outer startup/observation gap disclosed.'
        }
    } catch {
        $note=$_.Exception.Message;if($note.StartsWith('DEPENDENCY:')){$state='NOT_RUN'}else{$state='BLOCKED'}
    }
    $r=[pscustomobject]@{case=$name;status=$state;note=$note;observations=[pscustomobject]$observations;calls=@($f.calls.ToArray());root=$f.root}
    $results.Add($r);SaveJson (Join-Path $ArtifactRoot ($name+'.receipt.json')) $r
    [Console]::Out.WriteLine(($name+': '+$state+' '+$note))
}
$after=ProtectedSnapshot;$indexAfter=IndexSnapshot;$protectedSame=(($before|ConvertTo-Json -Depth 8 -Compress) -ceq ($after|ConvertTo-Json -Depth 8 -Compress));$indexSame=(($indexBefore|ConvertTo-Json -Depth 8 -Compress) -ceq ($indexAfter|ConvertTo-Json -Depth 8 -Compress))
$pinSame=$true;foreach($n in $pins.Keys){if((HashFile (Join-Path $frozen $n)) -cne $pins[$n]){$pinSame=$false}}
$historySame=$true;foreach($row in $historyBefore){if((HashFile $row.path) -cne $row.sha256){$historySame=$false}}
$manifestMismatches=@();foreach($row in $after){$ref=$protected|Where-Object path -ceq $row.path;if($row.hash -cne $ref.sha256){$manifestMismatches+=$row}}
$summary=[pscustomobject]@{schema=2;status='TARGETED_CORRECTED_SOURCE_NOT_APPROVAL';selectedCases=@($names|Where-Object{'All' -in $Case -or $_ -in $Case});results=@($results.ToArray());protectedFilesPerRoot=@($protected).Count;protectedUnchanged=$protectedSame;manifestMismatchCount=$manifestMismatches.Count;indexUnchanged=$indexSame;indexAfter=$indexAfter;candidateUnchanged=$pinSame;historyUnchanged=$historySame;elapsedMilliseconds=$clock.ElapsedMilliseconds;testSha256=(HashFile $PSCommandPath);host=$HostPath;notRunCases=@($names|Where-Object{$_ -notin @($results|ForEach-Object{$_.case})});unavailableCoverage=@('Forced Job-query/termination failure NOT PROVEN','Forced reader/pipe fault NOT PROVEN','Running-worker deadline termination NOT PROVEN','Second-host independent qualification NOT RUN')}
SaveJson (Join-Path $ArtifactRoot 'summary.json') $summary
Require ($protectedSame -and $indexSame -and $pinSame -and $historySame -and $manifestMismatches.Count -eq 0) 'Protected source/index/candidate/history drift detected.'
if(@($results | Where-Object {$_.status -in @('PRODUCT_GAP','EVIDENCE_GAP','BLOCKED','NOT_RUN')}).Count){exit 1}
exit 0
