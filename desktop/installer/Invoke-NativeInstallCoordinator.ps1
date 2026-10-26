[CmdletBinding()]
param(
    [ValidateSet('Inspect','CreateIntent','Prepare','Commit','Finalize','Recover','ResumeFinalize','ValidateLaunch','RunOwnedShell','AbortPreparedCopy')][string]$Command,
    [ValidateSet('Apply','Remove')][string]$Operation,
    [string]$BindingPath,[string]$BindingSha256,[string]$TransactionId,[string]$StageDirectory,[string]$ManifestSha256,[string]$IntentSha256,[string]$ExpectedReceiptSha256,[string]$OuterResultSha256,
    [string]$CopyCaseRoot,[string]$CopyWitnessSha256,
    [ValidateRange(1000,160000)][int]$DeadlineMilliseconds=140000,[long]$StartTick=-1,
    [switch]$OwnerCallback,
    [ValidateSet('Preflight','Checkpoint')][string]$LiveCallback
)
$entryClock=[Diagnostics.Stopwatch]::StartNew()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false,$true)
$clockReady=$false
$effectiveTick=$StartTick
$entryBootstrapMilliseconds=0L
try {
    if(($OwnerCallback -or $LiveCallback) -and ($PSBoundParameters.Count -ne 1 -or $env:N5_PIPE)){throw 'N7.CallbackArguments: callback accepts only its closed callback selector.'}
    if($Command -ceq 'AbortPreparedCopy'){
        if(@($PSBoundParameters.Keys|Where-Object {$_ -cnotin @('Command','CopyCaseRoot','CopyWitnessSha256','DeadlineMilliseconds','StartTick')}).Count){throw 'N7.CopyArguments: copy selector cannot mix binding/native arguments.'}
    }elseif($PSBoundParameters.ContainsKey('CopyCaseRoot') -or $PSBoundParameters.ContainsKey('CopyWitnessSha256')){throw 'N7.CopyArguments: explicit copy-only command required.'}
    $module=Join-Path $PSScriptRoot 'NativeInstallCoordinator.psm1'
    $utf8=[Text.UTF8Encoding]::new($false,$true)
    function EntryHash([byte[]]$Bytes){$sha=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
    function EntryBudget {
        if($clockReady -and (Get-NativeDeadlineRemaining $effectiveTick $DeadlineMilliseconds) -le 0){throw 'N5.Deadline: entry bootstrap exhausted shared budget.'}
        if(-not $clockReady -and $entryClock.Elapsed.TotalMilliseconds -ge $DeadlineMilliseconds){throw 'N5.Deadline: clock bootstrap exhausted budget.'}
    }
    function EntryBytes([string]$Path,[int]$Limit){
        EntryBudget
        $s=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try{if($s.Length -gt $Limit){throw 'N5.Entry: byte bound.'};$bytes=New-Object byte[] ([int]$s.Length);$i=0;while($i -lt $bytes.Length){$n=$s.Read($bytes,$i,$bytes.Length-$i);if(-not $n){throw 'N5.Entry: truncated read.'};$i+=$n};,$bytes}finally{$s.Dispose()}
    }
    # Stopwatch begins before preflight. Native tick is sampled only after the
    # frozen clock implementation is checked; synchronous bootstrap IO is not preemptible.
    $jobModule=Join-Path $PSScriptRoot 'NativeDeploymentJob.psm1'
    for($p=$jobModule;$p;$p=[IO.Path]::GetDirectoryName($p)){if(([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)) -and ([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint)){throw 'N5.Entry: reparse clock bootstrap rejected.'}}
    if((EntryHash (EntryBytes $jobModule 2097152)) -cne '1f5600efadac9afc1f5afb2a7f6158643fcb25aeaf9400f58022674c69763331'){throw 'N5.Entry: frozen clock hash mismatch.'}
    Import-Module $jobModule -ErrorAction Stop
    $sample=Get-NativeTick
    $entryBootstrapMilliseconds=[long][Math]::Ceiling($entryClock.Elapsed.TotalMilliseconds)
    if($OwnerCallback -or $LiveCallback){
        if($env:N7_START -cnotmatch '^[0-9]{1,10}$' -or $env:N7_DEADLINE -cnotmatch '^[0-9]{4,6}$'){throw 'N7.CallbackContext: inherited original clock required.'}
        $effectiveTick=[long]$env:N7_START;$DeadlineMilliseconds=[int]$env:N7_DEADLINE
        if($DeadlineMilliseconds -lt 1000 -or $DeadlineMilliseconds -gt 160000){throw 'N7.CallbackContext: deadline bound.'}
    }elseif($env:N5_PIPE){
        $effectiveTick=[long]$env:N5_START;$DeadlineMilliseconds=[int]$env:N5_DEADLINE
    }elseif($StartTick -eq -1){
        $effectiveTick=($sample-$entryBootstrapMilliseconds-32+4294967296L)%4294967296L
        $PSBoundParameters['StartTick']=$effectiveTick
    }
    $clockReady=$true
    if($effectiveTick -lt 0 -or $effectiveTick -gt 4294967295L){throw 'N5.Deadline: invalid entry tick.'}
    EntryBudget
    if($OwnerCallback -or $LiveCallback){
        if($env:N7_OWNER_PIPE -cnotmatch '^WinSightN7-[0-9a-f]{32}$' -or $env:N7_MODULE_HASH -cnotmatch '^[0-9a-f]{64}$' -or $env:N7_ENTRY_HASH -cnotmatch '^[0-9a-f]{64}$'){throw 'N7.CallbackContext: existing owner session/source pins required.'}
        foreach($n in @('N5_NONCE','N5_PARENT','N5_PARENT_START','N5_REQUEST','N5_MODULE','N5_ENTRY','N5_START','N5_DEADLINE')){if([Environment]::GetEnvironmentVariable($n)){throw 'N7.CallbackArguments: mixed N5 capability rejected.'}}
        $moduleHash=$env:N7_MODULE_HASH;$entryHash=$env:N7_ENTRY_HASH
    }elseif($env:N5_PIPE){
        if($PSBoundParameters.Count -or $env:N5_MODULE -cnotmatch '^[0-9a-f]{64}$' -or $env:N5_ENTRY -cnotmatch '^[0-9a-f]{64}$'){throw 'N5.Entry: invalid internal invocation.'}
        $moduleHash=$env:N5_MODULE;$entryHash=$env:N5_ENTRY
    }elseif($Command -ceq 'AbortPreparedCopy'){
        throw 'N7.UnconfiguredPreviewSource: prepared-copy recovery requires a materialized, re-pinned private graph.'
        # Trusted fixed sibling for this additive copy entry, never a caller/env code selector.
        $moduleHash='676fb9ad9e3ecf8f2ade87578881e3046a7a0691bdca8bc0cb0089666182f081'
        $entryHash=EntryHash (EntryBytes $PSCommandPath 32768)
    }else{
        if($BindingPath -notmatch '^[A-Za-z]:\\' -or $BindingSha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'N5.Entry: explicit binding path/hash required.'}
        $bytes=EntryBytes $BindingPath 32768
        if((EntryHash $bytes) -cne $BindingSha256){throw 'N5.Entry: binding hash mismatch.'}
        $b=$utf8.GetString($bytes)|ConvertFrom-Json
        if ([Convert]::ToBase64String($utf8.GetBytes(($b|ConvertTo-Json -Depth 30))) -cne [Convert]::ToBase64String($bytes)) { throw 'N5.Serializer: canonical same-host bytes required.' }
        if ($b -is [System.Management.Automation.PSCustomObject] -and $null -ne $b.PSObject.Properties['schema'] -and ($b.schema -is [int] -or $b.schema -is [long]) -and $b.schema -eq 3) {
            if($b.sources.path -cne ($b.roots.controlRoot+'\objects\'+$b.sources.sha256+'.json')){throw 'N7.Entry: exact source object location required.'}
            for($q=$b.sources.path;$q;$q=[IO.Path]::GetDirectoryName($q)){if(([IO.File]::Exists($q) -or [IO.Directory]::Exists($q)) -and ([IO.File]::GetAttributes($q) -band [IO.FileAttributes]::ReparsePoint)){throw 'N7.Entry: reparse source object rejected.'}}
            $sourceBytes=EntryBytes $b.sources.path 32768
            if($sourceBytes.Length -ne $b.sources.bytes -or (EntryHash $sourceBytes) -cne $b.sources.sha256){throw 'N7.Entry: source object pin mismatch.'}
            $sources=$utf8.GetString($sourceBytes)|ConvertFrom-Json
            if($sources.protocol -cne 'StandardSources/1' -or @($sources.files).Count -ne 9 -or [Convert]::ToBase64String($utf8.GetBytes(($sources|ConvertTo-Json -Depth 30))) -cne [Convert]::ToBase64String($sourceBytes)){throw 'N7.Entry: canonical source9 required.'}
            foreach($row in @(@('NativeInstallCoordinator.psm1','moduleHash'),@('Invoke-NativeInstallCoordinator.ps1','entryHash'),@('NativeStandardLiveShell.psm1','adapterHash'))){
                $slot=@($sources.files|Where-Object {$_.name -ceq $row[0]});if($slot.Count -ne 1 -or $slot[0].sha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'N7.Entry: unique source slot required.'}
                $pin=EntryBytes (Join-Path $PSScriptRoot $row[0]) 2097152
                if($pin.Length -ne $slot[0].bytes -or (EntryHash $pin) -cne $slot[0].sha256){throw 'N7.Entry: exact source bytes differ.'}
                Set-Variable -Name $row[1] -Value $slot[0].sha256
            }
        }
        else{$moduleHash=$b.coordinatorSha256;$entryHash=$b.entrySha256}
        if($moduleHash -cnotmatch '^[0-9a-f]{64}$' -or $entryHash -cnotmatch '^[0-9a-f]{64}$'){throw 'N5.Entry: source pins required.'}
    }
    foreach($path in @($module,$PSCommandPath)){
        for($p=$path;$p;$p=[IO.Path]::GetDirectoryName($p)){if(([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)) -and ([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint)){throw 'N5.Entry: reparse bootstrap rejected.'}}
    }
    if((EntryHash (EntryBytes $module 2097152)) -cne $moduleHash -or (EntryHash (EntryBytes $PSCommandPath 32768)) -cne $entryHash){throw 'N5.Entry: fixed sibling pin mismatch.'}
    EntryBudget
    $loaded=Import-Module $module -PassThru -ErrorAction Stop
    EntryBudget
    if($OwnerCallback){$facts=& $loaded {Invoke-N7CallbackClient}}
    elseif($LiveCallback){$facts=& $loaded {param($Phase) Invoke-N7LiveCallbackClient $Phase} $LiveCallback}
    else{$facts=Invoke-NativeInstallCoordinator @PSBoundParameters}
    $facts|Add-Member -NotePropertyName entryAccounting -NotePropertyValue ([pscustomobject]@{startTick=$effectiveTick;deadlineMilliseconds=$DeadlineMilliseconds;clockBootstrapElapsedMilliseconds=$entryBootstrapMilliseconds;elapsedMilliseconds=(((Get-NativeTick)-$effectiveTick+4294967296L)%4294967296L);calibrationAllowanceMilliseconds=32;synchronousBootstrapPreemptible=$false}) -Force
    if(-not $LiveCallback){[Console]::Out.WriteLine(($facts|ConvertTo-Json -Depth 12))}
    exit 0
}catch{
    $failure=$_.Exception.Data['N5Failure']
    if($null -eq $failure){
        $message=$_.Exception.Message;if($message.Length -gt 2048){$message=$message.Substring(0,2048)}
        $id=[string]$_.FullyQualifiedErrorId;if($message -match '^(N[57]\.[A-Za-z]+)'){$id=$Matches[1]};if($id.Length -gt 128){$id=$id.Substring(0,128)}
        $elapsed=[long][Math]::Ceiling($entryClock.Elapsed.TotalMilliseconds);$clockErrors=@()
        if($clockReady -and $effectiveTick -ge 0 -and $effectiveTick -le 4294967295L){try{$elapsed=((Get-NativeTick)-$effectiveTick+4294967296L)%4294967296L}catch{$clockErrors=@('Native elapsed observation unavailable; elapsed is entry Stopwatch duration.')}}
        $failure=[pscustomobject]@{schema=1;status='Failed';primaryError=[pscustomobject]@{id=$id;category=[string]$_.CategoryInfo.Category;message=$message};settlement=[pscustomobject]@{workerStarted=$false;workerPid=$null;workerExited=$null;workerExitCode=$null;activeOwnedProcesses=$null;stdoutState='NotStarted';stderrState='NotStarted';readersSettled=$null;cleanupState='NotStarted';cleanupErrors=$clockErrors;elapsedMilliseconds=$elapsed;jobCreated=$false;pipeCreated=$false}}
    }
    [Console]::Error.WriteLine(($failure|ConvertTo-Json -Depth 12))
    exit 1
}
