[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ArtifactRoot,[ValidateSet('Typed','Owner')][string]$Case='Typed')
function Invoke-OwnerProtocolTests([string]$Root) {
    Set-StrictMode -Version Latest
    $ErrorActionPreference='Stop'
    $utf8=[Text.UTF8Encoding]::new($false,$true)
    $installer=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$lane=[IO.Path]::GetFullPath((Join-Path $installer '..\..'))
    if(-not $Root.StartsWith($lane+'\.dev\n7-a\fixtures\',[StringComparison]::Ordinal) -or (Test-Path -LiteralPath $Root)){throw 'Fresh private owner fixture root required.'}
    if($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or [IntPtr]::Size -ne 8){throw 'PS5.1 x64 required.'}
    [void][IO.Directory]::CreateDirectory($Root)
    function Check($Ok,[string]$Message){if(-not $Ok){throw $Message}}
    function Get-OwnerHash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
    function J([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),$utf8)}
    $results=[Collections.Generic.List[object]]::new();$watch=[Diagnostics.Stopwatch]::StartNew()
    $coord=Import-Module (Join-Path $installer 'NativeInstallCoordinator.psm1') -PassThru -ErrorAction Stop
    Import-Module (Join-Path $installer 'NativeDeploymentJob.psm1') -ErrorAction Stop
    $status='FAILED';$failure=$null
    try {
        foreach($file in @('NativeInstallCoordinator.psm1','tests\Test-NativeLiveProtocol.ps1')){$t=$null;$errors=$null;[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $installer $file),[ref]$t,[ref]$errors);Check ($errors.Count -eq 0) ('PS5 parser: '+$file)}
        $hostPath=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $binding=Get-Content -LiteralPath (Join-Path $lane '.dev\n7-a\fixtures\protocol-03\binding3.json') -Raw|ConvertFrom-Json
        $bp=Join-Path $Root 'binding3.json';J $bp $binding
        $fixture=Join-Path $Root 'OwnedCallbackFixture.ps1'
        $text=@'
[CmdletBinding()]
param([switch]$Sleeper)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
if($Sleeper){while($true){Start-Sleep -Milliseconds 100}}
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false,$true)
$m=Import-Module $env:N7_COORDINATOR -PassThru -ErrorAction Stop
& $m {param($S,$D) Initialize-N5Clock;$script:N5Start=$S;$script:N5Deadline=$D} ([long]$env:N7_START) ([int]$env:N7_DEADLINE)
$p=[Diagnostics.Process]::GetCurrentProcess()
try{$ticks=$p.StartTime.ToUniversalTime().Ticks}finally{$p.Dispose()}
$base=[pscustomobject]@{protocol='NativeShellCallback/1';ownerRequestSha256=$env:N7_REQUEST;nonce=$env:N7_NONCE;sourceSha256=$env:N7_SOURCE;bindingSha256=$env:N7_BINDING;intentSha256=$env:N7_INTENT;previousReceiptSha256=$(if($env:N7_PREVIOUS){$env:N7_PREVIOUS}else{$null});operation=$env:N7_OPERATION;transactionId=$env:N7_TRANSACTION;sequence=1;command='Inspect';processId=$PID;processStartTicks=$ticks;startTick=[long]$env:N7_START;deadlineMilliseconds=[int]$env:N7_DEADLINE}
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$env:N7_OWNER_PIPE,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
$all=[Collections.Generic.List[object]]::new()
function Call($Frame,[string]$Raw=$null){
    & $m {param($Pipe,$Frame,$Raw)
        if($null -ne $Raw -and $Raw.Length){$b=$script:N5Utf8.GetBytes($Raw);foreach($part in @([BitConverter]::GetBytes([int]$b.Length),$b)){$task=$Pipe.WriteAsync($part,0,$part.Length);if(-not $task.Wait((Get-N5Remaining))){throw 'fixture frame write deadline'};$null=$task.GetAwaiter().GetResult()}}
        else{Send-N5Pipe $Pipe $Frame}
        Receive-N5Pipe $Pipe
    } $pipe $Frame $Raw
}
try {
    $pipe.Connect(3000)
    if($env:N7_MODE -ceq 'Timeout'){
        $observed=Call $base;if($observed.status -cne 'OwnerObserved'){throw 'timeout fixture Inspect rejected'}
        $child=[Diagnostics.Process]::new();$si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName;$si.Arguments='-NoProfile -NonInteractive -File "'+$PSCommandPath+'" -Sleeper';$si.UseShellExecute=$false;$si.CreateNoWindow=$true;$child.StartInfo=$si
        if(-not $child.Start()){throw 'inert descendant did not start'};$null=$child.Handle
        while($true){Start-Sleep -Milliseconds 100}
    }
    foreach($name in @('nonce','sourceSha256','bindingSha256','intentSha256','previousReceiptSha256','operation','transactionId','processId','processStartTicks','startTick','deadlineMilliseconds')){
        $f=$base|ConvertTo-Json -Depth 10|ConvertFrom-Json
        switch($name){'operation'{$f.$name='Remove'} 'transactionId'{$f.$name='0'*32} 'processId'{$f.$name=1} 'processStartTicks'{$f.$name=1L} 'startTick'{$f.$name=($f.$name+1)%4294967296L} 'deadlineMilliseconds'{$f.$name++} default{$f.$name='0'*64}}
        $r=Call $f;if($r.status -cne 'Rejected'){throw ('forged '+$name+' accepted')};$all.Add([pscustomobject]@{case=$name;reply=$r})
    }
    $f=$base|ConvertTo-Json -Depth 10|ConvertFrom-Json;$f|Add-Member -NotePropertyName outerResult -NotePropertyValue ([pscustomobject]@{exitCode=0;jobCount=0;readersSettled=$true})
    $r=Call $f;if($r.status -cne 'Rejected' -or -not $r.reason.Contains('N7.Fields')){throw 'forged outer JSON accepted'};$all.Add([pscustomobject]@{case='outerResult';reply=$r})
    $r=Call $null '{"nonce":"a","nonce":"b"}';if($r.status -cne 'Rejected' -or -not $r.reason.Contains('N7.DuplicateKey')){throw 'duplicate raw frame accepted'};$all.Add([pscustomobject]@{case='duplicate';reply=$r})
    $f=$base|ConvertTo-Json -Depth 10|ConvertFrom-Json;$f.sequence=2;$r=Call $f;if($r.status -cne 'Rejected' -or -not $r.reason.Contains('N7.OwnerReplay')){throw 'skipped sequence accepted'};$all.Add([pscustomobject]@{case='skip';reply=$r})
    $r=Call $base;if($r.status -cne 'OwnerObserved'){throw 'actual owned observation rejected'};$all.Add([pscustomobject]@{case='Inspect';reply=$r})
    $r=Call $base;if($r.status -cne 'Rejected' -or -not $r.reason.Contains('N7.OwnerReplay')){throw 'one-use replay accepted'};$all.Add([pscustomobject]@{case='replay';reply=$r})
    $f=$base|ConvertTo-Json -Depth 10|ConvertFrom-Json;$f.sequence=2;$f.command='Complete';$r=Call $f;if($r.status -cne 'OwnerCompleted'){throw 'owned completion rejected'};$all.Add([pscustomobject]@{case='Complete';reply=$r})
    [Console]::Out.WriteLine(([pscustomobject]@{status='PASS_PRIVATE_OWNER_CALLBACKS';processId=$PID;results=$all.ToArray()}|ConvertTo-Json -Depth 12))
}finally{$pipe.Dispose()}
'@
        [IO.File]::WriteAllText($fixture,$text,$utf8)
        $bPins=[pscustomobject]@{coordinatorSha256=(Get-OwnerHash (Join-Path $installer 'NativeInstallCoordinator.psm1'));entrySha256=(Get-OwnerHash (Join-Path $installer 'Invoke-NativeInstallCoordinator.ps1'))}
        $adapterHash=Get-OwnerHash (Join-Path $installer 'NativeStandardLiveShell.psm1')
        $sourceHash=& $coord {param($C,$E,$A) Get-N5Hash (ConvertTo-N5Bytes ([pscustomobject]@{coordinator=$C;entry=$E;adapter=$A;job=$script:N5JobHash}))} $bPins.coordinatorSha256 $bPins.entrySha256 $adapterHash
        $plan=[pscustomobject]@{protocol='NativeShellPlan/1';purpose='InertOwnerQualification';executable=[pscustomobject]@{path=$hostPath;bytes=[long](Get-Item -LiteralPath $hostPath).Length;sha256=(Get-OwnerHash $hostPath)};fixture=[pscustomobject]@{path=$fixture;bytes=[long](Get-Item -LiteralPath $fixture).Length;sha256=(Get-OwnerHash $fixture)};mode='Observe';sourceSha256=$sourceHash;adapterSha256=$adapterHash}
        $p=@{bindingPath=$bp;bindingSha256=(Get-OwnerHash $bp);intentSha256=('a'*64);previousReceiptSha256=$null;operation='Apply';transactionId=('b'*32);StartTick=(Get-NativeTick);DeadlineMilliseconds=30000}
        $r=& $coord {param($P,$B,$Plan) Initialize-N5Clock;$script:N5Start=$P.StartTick;$script:N5Deadline=$P.DeadlineMilliseconds;$script:N5BootstrapElapsed=0;Invoke-N5Dispatch $P $B $Plan} $p $bPins $plan
        J (Join-Path $Root 'owner-observed.json') $r
        Check ($r.status -ceq 'ObservedOnly' -and $r.liveCommit -eq $false -and $r.phase -ceq 'Complete') 'Owner result wrong.'
        Check ($r.childExitCode -eq 0 -and $r.childStdoutState -ceq 'Eof' -and $r.childStderrState -ceq 'Eof') 'Actual retained child/readers not clean.'
        Check ($r.settlement.activeOwnedProcesses -eq 0 -and $r.settlement.readersSettled -eq $true -and $r.ownerSettlement.rootMemberCount -eq 0 -and $r.ownerSettlement.handlesClosed -eq $true -and $r.ownerSettlement.jobFlags -eq 8192) 'Actual root settlement not clean.'
        $child=$r.nativeOutput|ConvertFrom-Json
        Check ($child.status -ceq 'PASS_PRIVATE_OWNER_CALLBACKS' -and $child.processId -eq $r.childPid -and $child.results.Count -eq 17 -and $r.rejections.Count -eq 15) 'Physical callback assertions incomplete.'
        $results.Add([pscustomobject]@{id='RetainedOwner.Duplex17Frames';status='PASS';physical=$true})
        foreach($test in @('ForgedCapability','QueryFailureUnknown')){
            $errorRecord=$null
            try{& $coord {param($Test) if($Test -ceq 'ForgedCapability'){Assert-N7OwnerFrame ([pscustomobject]@{nonce=('a'*64)}) ([pscustomobject]@{outerResult='claimed'})}else{Get-N7JobMembers ([IntPtr]::new(-1))}} $test}catch{$errorRecord=$_}
            $reason=if($test -ceq 'ForgedCapability'){'N7.OwnerCapability'}else{'N7.JobQueryUnknown'}
            Check ($null -ne $errorRecord -and $errorRecord.Exception.Message.Contains($reason)) ($test+' did not reject with exact reason')
            $results.Add([pscustomobject]@{id=$test;status='PASS';reason=$errorRecord.Exception.Message})
        }
        # New source lifecycle test, not a replay of the parent's inherited-start oracle.
        $plan.mode='Timeout';$p.StartTick=((Get-NativeTick)-3000+4294967296L)%4294967296L;$p.DeadlineMilliseconds=20000
        $expected=$null;$elapsed=[Diagnostics.Stopwatch]::StartNew()
        try{& $coord {param($P,$B,$Plan) $script:N5Start=$P.StartTick;$script:N5Deadline=$P.DeadlineMilliseconds;Invoke-N5Dispatch $P $B $Plan} $p $bPins $plan}catch{$expected=$_}
        Check ($null -ne $expected -and $expected.Exception.Data.Contains('N5Failure')) 'Timeout did not retain structured physical failure.'
        $fail=$expected.Exception.Data['N5Failure'];J (Join-Path $Root 'owner-timeout.json') $fail
        Check ($fail.primaryError.id -ceq 'N5.Deadline' -and $fail.settlement.cleanupState -ceq 'Settled' -and $fail.settlement.activeOwnedProcesses -eq 0 -and $fail.settlement.rootMemberCount -eq 0 -and $fail.settlement.readersSettled -eq $true) 'Actual timeout tree/EOF cleanup not proved.'
        Check ($elapsed.ElapsedMilliseconds -lt 20000 -and $fail.settlement.elapsedMilliseconds -ge 9000) 'Original T0 was replaced or cleanup unbounded.'
        $results.Add([pscustomobject]@{id='Timeout.OriginalT0.TreeEOF';status='PASS';wallMilliseconds=$elapsed.ElapsedMilliseconds;originalElapsed=$fail.settlement.elapsedMilliseconds})
        $n5root=Join-Path $Root 'n5';[void][IO.Directory]::CreateDirectory((Join-Path $n5root 'install\shell'));[void][IO.Directory]::CreateDirectory((Join-Path $n5root 'stage\app'))
        $shell=Join-Path $n5root 'install\shell\synthetic.txt';[IO.File]::WriteAllText($shell,'inert-only',$utf8);$payload=Join-Path $n5root 'stage\app\payload.txt';[IO.File]::WriteAllText($payload,'inert-payload',$utf8)
        $manifest=[pscustomobject]@{schema=1;runtime='win-x64';selfContained=$true;releaseApproved=$false;appOnlyPreview=$true;files=@([pscustomobject]@{path='app/payload.txt';bytes=[long](Get-Item -LiteralPath $payload).Length;sha256=(Get-OwnerHash $payload)})};$mp=Join-Path $n5root 'stage\release-manifest.json';J $mp $manifest
        $identity=[Security.Principal.WindowsIdentity]::GetCurrent();try{$sid=$identity.User.Value}finally{$identity.Dispose()}
        $n5=[pscustomobject]@{schema=1;protocol='N5.FileVerification/1';mode='Verification';appId='WinSight.Native.Preview';instanceId=('a'*32);installRoot=($n5root+'\install');payloadRoot=($n5root+'\install\payload');controlRoot=($n5root+'\control');dataRoot=($n5root+'\data');shellRoot=($n5root+'\install\shell');engineSha256=(Get-OwnerHash (Join-Path $installer 'NativeInstaller.psm1'));jobSha256=(Get-OwnerHash (Join-Path $installer 'NativeDeploymentJob.psm1'));coordinatorSha256=$bPins.coordinatorSha256;entrySha256=$bPins.entrySha256;host=[pscustomobject]@{edition='Desktop';version=$PSVersionTable.PSVersion.ToString();executableSha256=(Get-OwnerHash $hostPath);machine=[Environment]::MachineName;userSid=$sid;serializer='ConvertTo-Json/Depth30/UTF8-noBOM'};shellFiles=@([pscustomobject]@{path='synthetic.txt';bytes=[long](Get-Item -LiteralPath $shell).Length;sha256=(Get-OwnerHash $shell)})}
        $n5bp=Join-Path $n5root 'binding.json';J $n5bp $n5
        $r=Invoke-NativeInstallCoordinator -Command Inspect -Operation Apply -BindingPath $n5bp -BindingSha256 (Get-OwnerHash $n5bp) -DeadlineMilliseconds 20000;Check ($r.status -ceq 'MetadataInspected' -and $r.settlement.activeOwnedProcesses -eq 0 -and $r.settlement.readersSettled -eq $true) 'Affected N5 Inspect regressed.';J (Join-Path $Root 'n5-inspect.json') $r
        $r=Invoke-NativeInstallCoordinator -Command CreateIntent -Operation Apply -BindingPath $n5bp -BindingSha256 (Get-OwnerHash $n5bp) -TransactionId ('b'*32) -StageDirectory (Join-Path $n5root 'stage') -ManifestSha256 (Get-OwnerHash $mp) -DeadlineMilliseconds 20000;Check ($r.status -ceq 'IntentCreated') 'N5 CreateIntent regressed.'
        $n5.mode='Production';$prod=Join-Path $n5root 'production.json';J $prod $n5;$refusal=$null
        try{Invoke-NativeInstallCoordinator -Command CreateIntent -Operation Apply -BindingPath $prod -BindingSha256 (Get-OwnerHash $prod) -TransactionId ('c'*32) -StageDirectory (Join-Path $n5root 'stage') -ManifestSha256 (Get-OwnerHash $mp) -DeadlineMilliseconds 20000}catch{$refusal=$_}
        Check ($null -ne $refusal -and $refusal.Exception.Message.Contains('N5.AdapterUnavailable: Production mutating commands are disabled.')) 'Production refusal changed.'
        $results.Add([pscustomobject]@{id='AffectedN5.Inspect.Intent.ProductionRefusal';status='PASS'})
        J (Join-Path $Root 'entry-fixtures.json') ([pscustomobject]@{entry=(Join-Path $installer 'Invoke-NativeInstallCoordinator.ps1');host=$hostPath;n5BindingPath=$n5bp;n5BindingSha256=(Get-OwnerHash $n5bp);liveBindingPath=$bp;liveBindingSha256=(Get-OwnerHash $bp)})
        $status='PASS_PRIVATE_RETAINED_OWNER_ONLY'
    }catch{$failure=$_.Exception.ToString();[Console]::Error.WriteLine($failure)}finally{
        J (Join-Path $Root 'owner-summary.json') ([pscustomobject]@{status=$status;host=$PSVersionTable.PSVersion.ToString();testSha256=(Get-OwnerHash $PSCommandPath);results=$results.ToArray();failure=$failure;elapsedMilliseconds=$watch.ElapsedMilliseconds;parentStartupOracleRerun=$false;publicLiveQualified=$false;remaining='Production graph/intent/shell resource authority/commit/CAS/recovery/descriptor and B callback client unimplemented'})
    }
    [Console]::Out.WriteLine(($status+' cases='+$results.Count+' host='+$PSVersionTable.PSVersion.ToString()))
    if($status -ceq 'FAILED'){return 1};return 0
}
if($Case -ceq 'Owner'){$code=Invoke-OwnerProtocolTests $ArtifactRoot;exit $code}
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$utf8=[Text.UTF8Encoding]::new($false,$true)
$clock=[Diagnostics.Stopwatch]::StartNew()
$installer=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lane=[IO.Path]::GetFullPath((Join-Path $installer '..\..'))
if (-not $ArtifactRoot.StartsWith($lane+'\.dev\n7-a\',[StringComparison]::Ordinal) -or [IO.Directory]::Exists($ArtifactRoot)) { throw 'Fresh component-A artifact directory required.' }
if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or [IntPtr]::Size -ne 8) { throw 'Windows PowerShell 5.1 x64 test host required.' }
[void][IO.Directory]::CreateDirectory($ArtifactRoot)
$live=Import-Module (Join-Path $installer 'NativeStandardLiveShell.psm1') -PassThru -ErrorAction Stop
Import-Module (Join-Path $installer 'NativeDeploymentJob.psm1') -ErrorAction Stop
Import-Module (Join-Path $installer 'NativeInstallCoordinator.psm1') -ErrorAction Stop
$results=[Collections.Generic.List[object]]::new()
function Require($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Save([string]$Path,$Value) { [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),$utf8) }
function HashFile([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant() }
function Clone($Value) { $Value|ConvertTo-Json -Depth 30|ConvertFrom-Json }
function Positive([string]$Name,[scriptblock]$Body) {
    if ($clock.ElapsedMilliseconds -gt 110000) { throw 'Test budget exhausted.' }
    & $Body
    $results.Add([pscustomobject]@{id=$Name;kind='positive';status='PASS'})
}
function Negative([string]$Name,[string]$Reason,[scriptblock]$Body) {
    if ($clock.ElapsedMilliseconds -gt 110000) { throw 'Test budget exhausted.' }
    $caught=$null
    try { & $Body } catch { $caught=$_ }
    Require ($null -ne $caught) ($Name+': expected rejection')
    Require ($caught.Exception.Message.Contains($Reason)) ($Name+': wrong reason '+$caught.Exception.Message)
    $results.Add([pscustomobject]@{id=$Name;kind='negative';status='PASS';reason=$Reason;actual=$caught.Exception.Message})
}
$run='__WINSIGHT_PRIVATE_RUN_ROOT__'
throw 'N7.UnconfiguredHistoricalQualification: materialize and re-pin a private graph before this qualification script is used.'
$exe=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$id=[Security.Principal.WindowsIdentity]::GetCurrent()
try { $sid=$id.User.Value } finally { $id.Dispose() }
$hostRecord=[pscustomobject]@{edition='Desktop';version=$PSVersionTable.PSVersion.ToString();executable=[pscustomobject]@{path=$exe;bytes=[long](Get-Item -LiteralPath $exe).Length;sha256=(HashFile $exe)};machine=[Environment]::MachineName;userSid=$sid;serializer='ConvertTo-Json/Depth30/UTF8-noBOM'}
function JsonPin([string]$Hash) { [pscustomobject]@{path=($run+'\control\metadata\'+$Hash+'.json');bytes=100L;sha256=$Hash} }
$b=[pscustomobject]@{schema=3;backend='InnoPerUserLive/1';purpose='InternalStandardCandidate';releaseApproved=$false;instanceId='run-01';appId='WinSight.Native.Preview';displayName='WinSight Native Preview';roots=[pscustomobject]@{taskRoot=$run;installRoot=($run+'\install');shellRoot=($run+'\install\shell');deploymentRoot=($run+'\install\deployment');dataRoot=($run+'\data');controlRoot=($run+'\control')};host=$hostRecord;sources=(JsonPin ('1'*64));package=(JsonPin ('2'*64));shellSpec=(JsonPin ('3'*64));priorReceipt=$null}
$bindingPath=Join-Path $ArtifactRoot 'binding3.json'; Save $bindingPath $b; $bindingHash=HashFile $bindingPath
$script:start=Get-NativeTick
function Decode([byte[]]$Bytes,[int]$Limit=2097152) { & $live {param($Bytes,$Start,$Limit) ConvertFrom-NLBytes $Bytes $Start 120000 $Limit} $Bytes $script:start $Limit }
try {
    foreach ($file in @('NativeStandardLiveShell.psm1','NativeInstallCoordinator.psm1','Invoke-NativeInstallCoordinator.ps1','tests\Test-NativeLiveProtocol.ps1')) {
        $tokens=$null;$errors=$null
        [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $installer $file),[ref]$tokens,[ref]$errors)
        Require ($errors.Count -eq 0) ('Parser error: '+$file+' '+($errors|Out-String))
        $results.Add([pscustomobject]@{id=('Parser:'+ $file);kind='physical-parser';status='PASS'})
    }
    Positive 'Binding.TypedMetadata' { & $live {param($B) Assert-NLBinding $B} $b }
    Positive 'Binding.PinnedMetadataReader.NotAuthority' { $actual=Read-NativeLiveBinding -BindingPath $bindingPath -BindingSha256 $bindingHash -StartTick $script:start -DeadlineMilliseconds 120000; Require ($actual.backend -ceq 'InnoPerUserLive/1') 'Binding read mismatch.' }
    Positive 'Json.CanonicalPS51' { $v=Decode ([IO.File]::ReadAllBytes($bindingPath)) 32768; Require ($v.schema -eq 3) 'Bad decode.' }
    Negative 'Json.DuplicateKeys' 'N7.DuplicateKey' { Decode ($utf8.GetBytes('{"schema":3,"schema":3}')) }
    Negative 'Json.CaseDuplicateKeys' 'N7.DuplicateKey' { Decode ($utf8.GetBytes('{"schema":3,"Schema":3}')) }
    Negative 'Json.EscapedKey' 'N7.JsonProperty' { Decode ($utf8.GetBytes('{"sch\u0065ma":3}')) }
    Negative 'Json.TrailingValue' 'N7.JsonSyntax' { Decode ($utf8.GetBytes('{} {}')) }
    Negative 'Json.Truncated' 'N7.JsonSyntax' { Decode ($utf8.GetBytes('{"schema":')) }
    Negative 'Json.Noncanonical' 'N7.Serializer' { Decode ($utf8.GetBytes('{"schema":3}')) }
    Negative 'Json.Binding32KiB' 'N7.JsonSize' { Decode (New-Object byte[] 32769) 32768 }
    Negative 'Json.Control2MiB' 'N7.JsonSize' { Decode (New-Object byte[] 2097153) }
    Negative 'Json.Record4097' 'N7.Records' { Decode ($utf8.GetBytes('['+((@('0')*4097)-join ',')+']')) }
    foreach ($name in @('schema','backend','instanceId','appId','displayName')) {
        $x=Clone $b; $x.$name=@($x.$name)
        $reason=if($name -ceq 'schema'){'N7.Integer'}else{'N7.String'}
        Negative ('Binding.Array.'+$name) $reason { & $live {param($B) Assert-NLBinding $B} $x }
    }
    Negative 'Binding.BooleanSchema' 'N7.Integer' { $x=Clone $b;$x.schema=$true;& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.FloatSchema' 'N7.Integer' { $x=Clone $b;$x.schema=3.0;& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.Schema2NotLive' 'N7.Integer' { $x=Clone $b;$x.schema=2;& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.UnknownField' 'N7.Fields' { $x=Clone $b;$x|Add-Member extra 0;& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.MissingExplicitNull' 'N7.Fields' { $x=Clone $b;$x.PSObject.Properties.Remove('priorReceipt');& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.NonNullPriorCannotAdopt' 'N7.PriorAuthorityUnavailable' { $x=Clone $b;$x.priorReceipt=JsonPin ('4'*64);$p=Join-Path $ArtifactRoot 'prior-binding.json';Save $p $x;Read-NativeLiveBinding $p (HashFile $p) $script:start 120000 }
    Negative 'Binding.OtherRun' 'N7.Roots' { $x=Clone $b;$x.roots.taskRoot+='-other';& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.OtherMachine' 'N7.HostMismatch' { $x=Clone $b;$x.host.machine+='-other';$p=Join-Path $ArtifactRoot 'other-machine.json';Save $p $x;Read-NativeLiveBinding $p (HashFile $p) $script:start 120000 }
    Negative 'Binding.InvalidSIDType' 'N7.String' { $x=Clone $b;$x.host.userSid=@($sid);& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.OtherHostVersion' 'N7.Host' { $x=Clone $b;$x.host.version='7.7.0'; & $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.TamperedHash' 'N7.PinMismatch' { Read-NativeLiveBinding $bindingPath ('0'*64) $script:start 120000 }
    Negative 'Binding.BooleanBytes' 'N7.Integer' { $x=Clone $b;$x.sources.bytes=$true;& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.ArrayPin' 'N7.Object' { $x=Clone $b;$x.package=@($x.package);& $live {param($B) Assert-NLBinding $B} $x }
    Negative 'Binding.NoncanonicalPath' 'N7.Path' { $x=Clone $b;$x.roots.taskRoot+='\..\run-01';& $live {param($B) Assert-NLBinding $B} $x }
    $record=[pscustomobject]@{path='app/models/large.onnx';bytes=5000000000L;sha256=('a'*64)}
    Positive 'FileRecord.Int64BeyondLegacyCaps' { & $live {param($R) Assert-NLFileRecord $R} $record }
    Positive 'Integer.Int64Max' { & $live {Assert-NLInteger ([long]::MaxValue)} }
    Negative 'FileRecord.BooleanBytes' 'N7.Integer' { $x=Clone $record;$x.bytes=$false;& $live {param($R) Assert-NLFileRecord $R} $x }
    Negative 'FileRecord.ParentEscape' 'N7.FileRecord' { $x=Clone $record;$x.path='app/../escape';& $live {param($R) Assert-NLFileRecord $R} $x }
    Negative 'FileRecord.DOSDevice' 'N7.FileRecord' { $x=Clone $record;$x.path='app/COM1.txt';& $live {param($R) Assert-NLFileRecord $R} $x }
    $tiny=Join-Path $ArtifactRoot 'stream.bin';$bytes=New-Object byte[] 70001;for($i=0;$i -lt $bytes.Length;$i++){$bytes[$i]=[byte]($i%251)};[IO.File]::WriteAllBytes($tiny,$bytes)
    Positive 'Stream64KiB.PhysicalTinyOracle' { $p=& $live {param($P,$S) Get-NLFilePin $P $S 120000} $tiny $script:start;Require ($p.bytes -eq 70001 -and $p.sha256 -ceq (HashFile $tiny)) 'Streaming disagrees with independent Get-FileHash.' }
    Negative 'Stream.ExpiredBudget' 'N7.Deadline' { & $live {param($P,$S) Get-NLFilePin $P $S 1000} $tiny (((Get-NativeTick)-5000+4294967296L)%4294967296L) }
    $receipt=[pscustomobject]@{protocol='StandardCommitReceipt/1';bindingSha256=$bindingHash;instanceId='run-01';appId='WinSight.Native.Preview';roots=(Clone $b.roots);host=(Clone $b.host);transactionId=('a'*32);operation='Apply';intentSha256=('b'*64);previousReceiptSha256=$null;manifestSha256=('c'*64);selection=('d'*64);journalSha256=('e'*64);previousStateSha256=$null;nextStateSha256=('f'*64);sourceManifestSha256=$b.sources.sha256;packageSha256=$b.package.sha256;shellSpecSha256=$b.shellSpec.sha256;observation=(JsonPin ('5'*64));stableDigest=('6'*64);outerResult=$null;trustedPreviousSetup=$null}
    Positive 'Receipt.TypedOnly.NotCommit' { & $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $receipt $b $bindingHash }
    Negative 'Receipt.TamperedBinding' 'N7.ReceiptPins' { $x=Clone $receipt;$x.bindingSha256='0'*64;& $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $x $b $bindingHash }
    Negative 'Receipt.TamperedSource' 'N7.ReceiptPins' { $x=Clone $receipt;$x.sourceManifestSha256='0'*64;& $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $x $b $bindingHash }
    Negative 'Receipt.TamperedSID' 'N7.ReceiptHost' { $x=Clone $receipt;$x.host.userSid='S-1-5-21-1-2-3-4';& $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $x $b $bindingHash }
    Negative 'Receipt.ArrayHash' 'N7.String' { $x=Clone $receipt;$x.bindingSha256=@($bindingHash);& $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $x $b $bindingHash }
    Negative 'Receipt.ForgedOuterApply' 'N7.ReceiptState' { $x=Clone $receipt;$x.outerResult=JsonPin ('9'*64);& $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $x $b $bindingHash }
    Negative 'Receipt.RemoveMissingOuter' 'N7.ReceiptState' { $x=Clone $receipt;$x.operation='Remove';$x.manifestSha256=$null;$x.selection=$null;$x.nextStateSha256=$null;& $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $x $b $bindingHash }
    $shared=@{BindingPath=$bindingPath;BindingSha256=$bindingHash;StartTick=$script:start;DeadlineMilliseconds=120000}
    foreach ($cmd in @('Inspect','ValidateLaunch','CreateIntent','Prepare','Commit','Finalize','Recover','ResumeFinalize')) {
        foreach ($op in @('Apply','Remove')) {
            $a=@{};foreach($k in $shared.Keys){$a[$k]=$shared[$k]};$a.Command=$cmd
            if($cmd -cnotin @('Inspect','ValidateLaunch')){$a.Operation=$op;$a.TransactionId='a'*32;if($cmd -cne 'CreateIntent'){$a.IntentSha256='b'*64};if($cmd -ceq 'CreateIntent' -and $op -ceq 'Apply'){$a.StageDirectory=Join-Path $ArtifactRoot 'stage';$a.ManifestSha256='c'*64};if($cmd -cin @('Finalize','ResumeFinalize')){$a.ExpectedReceiptSha256='d'*64};if($cmd -ceq 'Commit' -and $op -ceq 'Remove'){$a.OuterResultSha256='e'*64}}
            Positive ('Arguments.Typed.'+$cmd+'.'+$op) { & $live {param($A) Assert-NLArguments $A} $a }
            Negative ('Wire.NoLivePositive.'+$cmd+'.'+$op) 'N7.OwnedCapabilityUnavailable' { Invoke-NativeInstallCoordinator @a }
        }
    }
    foreach($pair in @(@('Inspect','Operation'),@('ValidateLaunch','TransactionId'),@('Prepare','StageDirectory'),@('CreateIntent','IntentSha256'),@('Finalize','OuterResultSha256'))) {
        $a=@{};foreach($k in $shared.Keys){$a[$k]=$shared[$k]};$a.Command=$pair[0]
        if($pair[0] -cnotin @('Inspect','ValidateLaunch')){$a.Operation='Remove';$a.TransactionId='a'*32;if($pair[0] -cne 'CreateIntent'){$a.IntentSha256='b'*64};if($pair[0] -ceq 'Finalize'){$a.ExpectedReceiptSha256='c'*64}}
        $a[$pair[1]]=if($pair[1] -ceq 'Operation'){'Apply'}elseif($pair[1] -ceq 'StageDirectory'){Join-Path $ArtifactRoot 'stage'}elseif($pair[1] -ceq 'TransactionId'){'a'*32}else{'d'*64}
        Negative ('Arguments.Combo.'+$pair[0]+'.'+$pair[1]) 'N7.Arguments' { & $live {param($A) Assert-NLArguments $A} $a }
    }
    Negative 'Arguments.BooleanDeadline' 'N7.Integer' { $a=@{};foreach($k in $shared.Keys){$a[$k]=$shared[$k]};$a.Command='Inspect';$a.DeadlineMilliseconds=$true;& $live {param($A) Assert-NLArguments $A} $a }
    Negative 'Arguments.ForgedObservationRejected' 'N7.Arguments' { $a=@{};foreach($k in $shared.Keys){$a[$k]=$shared[$k]};$a.Command='Inspect';$a.Observation=[pscustomobject]@{state='Absent';readersSettled=$true};& $live {param($A) Assert-NLArguments $A} $a }
    Negative 'PublicWire.NoObservationParameter' 'Observation' { Invoke-NativeInstallCoordinator -Command Inspect -BindingPath $bindingPath -BindingSha256 $bindingHash -Observation '{}' }
    # Exercise only affected N5 dispatch/entry seams, with inert local bytes and current source pins.
    $n5root=Join-Path $ArtifactRoot 'n5';[void][IO.Directory]::CreateDirectory((Join-Path $n5root 'install\shell'));[void][IO.Directory]::CreateDirectory((Join-Path $n5root 'stage\app'))
    $shell=Join-Path $n5root 'install\shell\synthetic.txt';[IO.File]::WriteAllText($shell,'inert-only',$utf8)
    $payload=Join-Path $n5root 'stage\app\payload.txt';[IO.File]::WriteAllText($payload,'inert-payload',$utf8)
    $manifest=[pscustomobject]@{schema=1;runtime='win-x64';selfContained=$true;releaseApproved=$false;appOnlyPreview=$true;files=@([pscustomobject]@{path='app/payload.txt';bytes=[long](Get-Item -LiteralPath $payload).Length;sha256=(HashFile $payload)})};$mp=Join-Path $n5root 'stage\release-manifest.json';Save $mp $manifest
    $n5=[pscustomobject]@{schema=1;protocol='N5.FileVerification/1';mode='Verification';appId='WinSight.Native.Preview';instanceId=('a'*32);installRoot=($n5root+'\install');payloadRoot=($n5root+'\install\payload');controlRoot=($n5root+'\control');dataRoot=($n5root+'\data');shellRoot=($n5root+'\install\shell');engineSha256=(HashFile (Join-Path $installer 'NativeInstaller.psm1'));jobSha256=(HashFile (Join-Path $installer 'NativeDeploymentJob.psm1'));coordinatorSha256=(HashFile (Join-Path $installer 'NativeInstallCoordinator.psm1'));entrySha256=(HashFile (Join-Path $installer 'Invoke-NativeInstallCoordinator.ps1'));host=[pscustomobject]@{edition='Desktop';version=$PSVersionTable.PSVersion.ToString();executableSha256=(HashFile $exe);machine=[Environment]::MachineName;userSid=$sid;serializer='ConvertTo-Json/Depth30/UTF8-noBOM'};shellFiles=@([pscustomobject]@{path='synthetic.txt';bytes=[long](Get-Item -LiteralPath $shell).Length;sha256=(HashFile $shell)})}
    $n5bp=Join-Path $n5root 'binding.json';Save $n5bp $n5
    Positive 'N5.Dispatch.Inspect.RealOwnedWorker' { $r=Invoke-NativeInstallCoordinator -Command Inspect -Operation Apply -BindingPath $n5bp -BindingSha256 (HashFile $n5bp) -DeadlineMilliseconds 20000;Require ($r.status -ceq 'MetadataInspected' -and $r.liveAdaptersAvailable -eq $false -and $r.settlement.activeOwnedProcesses -eq 0 -and $r.settlement.readersSettled -eq $true) 'N5 worker contract changed.';Save (Join-Path $ArtifactRoot 'n5-inspect.json') $r }
    Positive 'N5.Dispatch.CreateIntent.NoPayload' { $r=Invoke-NativeInstallCoordinator -Command CreateIntent -Operation Apply -BindingPath $n5bp -BindingSha256 (HashFile $n5bp) -TransactionId ('b'*32) -StageDirectory (Join-Path $n5root 'stage') -ManifestSha256 (HashFile $mp) -DeadlineMilliseconds 20000;Require ($r.status -ceq 'IntentCreated' -and -not [IO.Directory]::Exists($n5.payloadRoot)) 'N5 intent behavior changed.';Save (Join-Path $ArtifactRoot 'n5-intent.json') $r }
    $prod=Clone $n5;$prod.mode='Production';$pb=Join-Path $n5root 'production.json';Save $pb $prod
    Negative 'N5.ProductionRefusal.Unchanged' 'N5.AdapterUnavailable: Production mutating commands are disabled.' { Invoke-NativeInstallCoordinator -Command CreateIntent -Operation Apply -BindingPath $pb -BindingSha256 (HashFile $pb) -TransactionId ('c'*32) -StageDirectory (Join-Path $n5root 'stage') -ManifestSha256 (HashFile $mp) -DeadlineMilliseconds 20000 }
    Save (Join-Path $ArtifactRoot 'entry-fixtures.json') ([pscustomobject]@{entry=(Join-Path $installer 'Invoke-NativeInstallCoordinator.ps1');host=$exe;n5BindingPath=$n5bp;n5BindingSha256=(HashFile $n5bp);liveBindingPath=$bindingPath;liveBindingSha256=$bindingHash})
    $status='SOURCE_TYPED_PASS_LIVE_BLOCKED'
} catch {
    $status='FAILED';$failure=$_.Exception.Message
    [Console]::Error.WriteLine($failure)
} finally {
    $summary=[ordered]@{status=$status;testSha256=(HashFile $PSCommandPath);host=$hostRecord;results=@($results.ToArray());elapsedMilliseconds=$clock.ElapsedMilliseconds;physicalLiveCommitTested=$false;privateSnapshotsNotOSProof=$true;gap='NM-N7-OWNED-CAPABILITY-01';unimplemented=@('authenticated B setup/uninstaller capability','source/package/shell graph consumption','live authority/observation/anchor','CAS intent persistence/idempotence','precommit Recover vs postcommit ResumeFinalize','DailyLaunchDescriptor')}
    if ($status -ceq 'FAILED') {$summary.failure=$failure}
    Save (Join-Path $ArtifactRoot 'summary.json') ([pscustomobject]$summary)
    [Console]::Out.WriteLine(($status+' assertions='+$results.Count+' host='+$PSVersionTable.PSVersion.ToString()))
}
if ($status -ceq 'FAILED') {exit 1}
exit 0
