[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$BindingPath,
    [Parameter(Mandatory=$true)][string]$BindingSha256,
    [ValidateRange(1000,160000)][int]$TimeoutMilliseconds=160000
)
$bootstrap=[Diagnostics.Stopwatch]::StartNew()
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$utf8=[Text.UTF8Encoding]::new($false,$true)
[Console]::OutputEncoding=$utf8
$locks=[Collections.Generic.List[IDisposable]]::new()
$loaded=$null;$result=$null;$primary=$null;$cleanup=[Collections.Generic.List[string]]::new()
$launchOutcome=@{startAttempted=$false;startSucceeded=$null;processId=$null;cleanupErrors=@()}
try {
    throw 'N7.UnconfiguredPreviewSource: the public source launcher is disabled until a private graph is materialized and re-pinned.'
    if($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or [IntPtr]::Size -ne 8){throw 'N7.LaunchHost: exact x64 Windows PowerShell 5.1 required.'}
    $self=[Diagnostics.Process]::GetCurrentProcess()
    try{$hostPath=$self.MainModule.FileName}finally{$self.Dispose()}
    if(-not [string]::Equals($hostPath,($env:WINDIR+'\System32\WindowsPowerShell\v1.0\powershell.exe'),[StringComparison]::OrdinalIgnoreCase)){throw 'N7.LaunchHost: system Windows PowerShell required.'}
    foreach($n in [Environment]::GetEnvironmentVariables().Keys){if($n -clike 'N5_*' -or $n -clike 'N7_*'){throw 'N7.LaunchContext: daily client rejects inherited transaction/worker capabilities.'}}
    function LaunchBudget {if($bootstrap.ElapsedMilliseconds -ge ($TimeoutMilliseconds-1000)){throw 'N7.Deadline: daily bootstrap budget exhausted.'}}
    function LaunchPath([string]$Path){
        if($Path -cnotmatch '^[A-Z]:\\' -or [IO.Path]::GetFullPath($Path).TrimEnd('\') -cne $Path -or $Path -match '[\x00-\x1f"<>|?*]' -or $Path.Substring(2).Contains(':')){throw 'N7.LaunchPath: canonical local path required.'}
        foreach($part in $Path.Substring(3).Split('\')){if(-not $part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)'){throw 'N7.LaunchPath: ambiguous segment.'}}
        for($p=$Path;$p;$p=[IO.Path]::GetDirectoryName($p)){try{$a=[IO.File]::GetAttributes($p)}catch [IO.FileNotFoundException]{continue}catch [IO.DirectoryNotFoundException]{continue};if($a -band [IO.FileAttributes]::ReparsePoint){throw 'N7.LaunchPath: reparse rejected.'}}
    }
    function LaunchHash([byte[]]$Bytes){$sha=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}}
    function LaunchBytes([string]$Path,[int]$Limit){
        LaunchBudget;LaunchPath $Path
        $stream=[IO.File]::Open($Path,'Open','Read','Read');$locks.Add($stream)
        if($stream.Length -gt $Limit){throw 'N7.LaunchBytes: byte bound.'}
        $bytes=New-Object byte[] ([int]$stream.Length);$i=0
        while($i -lt $bytes.Length){LaunchBudget;$n=$stream.Read($bytes,$i,$bytes.Length-$i);if(-not $n){throw 'N7.LaunchBytes: truncated read.'};$i+=$n}
        if($stream.Length -ne $bytes.Length){throw 'N7.LaunchBytes: changed read.'}
        ,$bytes
    }
    function LaunchJson([byte[]]$Bytes){
        $value=$utf8.GetString($Bytes)|ConvertFrom-Json -ErrorAction Stop
        if($value -isnot [System.Management.Automation.PSCustomObject] -or [Convert]::ToBase64String($utf8.GetBytes(($value|ConvertTo-Json -Depth 30))) -cne [Convert]::ToBase64String($Bytes)){throw 'N7.LaunchJson: canonical same-host object required.'}
        $value
    }
    function LaunchFields($Value,[string[]]$Names){if($Value -isnot [System.Management.Automation.PSCustomObject]){throw 'N7.LaunchFields: object required.'};$actual=@($Value.PSObject.Properties.Name);if($actual.Count -ne $Names.Count -or @($actual|Where-Object {$_ -cnotin $Names}).Count){throw 'N7.LaunchFields: closed fields required.'}}
    $control='__WINSIGHT_PRIVATE_CONTROL_ROOT__'
    if($BindingSha256 -cnotmatch '^[0-9a-f]{64}$' -or $BindingPath -cne ($control+'\bindings\'+$BindingSha256+'.json')){throw 'N7.LaunchBinding: exact current-task content-addressed binding required.'}
    $bindingBytes=LaunchBytes $BindingPath 32768
    if((LaunchHash $bindingBytes) -cne $BindingSha256){throw 'N7.LaunchBinding: raw binding pin mismatch.'}
    $b=LaunchJson $bindingBytes
    LaunchFields $b @('schema','backend','purpose','releaseApproved','instanceId','appId','displayName','roots','host','sources','package','shellSpec','priorReceipt')
    LaunchFields $b.sources @('path','bytes','sha256')
    if(($b.schema -isnot [int] -and $b.schema -isnot [long]) -or $b.schema -ne 3 -or $b.backend -isnot [string] -or $b.backend -cne 'InnoPerUserLive/1' -or $b.sources.sha256 -isnot [string] -or $b.sources.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $b.sources.path -isnot [string] -or $b.sources.path -cne ($control+'\objects\'+$b.sources.sha256+'.json') -or ($b.sources.bytes -isnot [int] -and $b.sources.bytes -isnot [long])){throw 'N7.LaunchBinding: typed live source pin required.'}
    $sourceBytes=LaunchBytes $b.sources.path 32768
    if($sourceBytes.Length -ne $b.sources.bytes -or (LaunchHash $sourceBytes) -cne $b.sources.sha256){throw 'N7.LaunchSource: raw source manifest pin mismatch.'}
    $sources=LaunchJson $sourceBytes;LaunchFields $sources @('protocol','files')
    if($sources.protocol -isnot [string] -or $sources.protocol -cne 'StandardSources/1' -or $sources.files -isnot [array] -or $sources.files.Count -ne 9){throw 'N7.LaunchSource: source9 required.'}
    $runtime=$control+'\runtime\'+$b.sources.sha256
    # These are final source identities, not caller-chosen pins. There is no
    # reverse K->launcher literal hash, so source9 remains cycle-free.
    $known=@{
        'NativeInstallCoordinator.psm1'='a44397a8e8c356b58af29b4428bd271cb3155c379cfce024495904af47fb624e'
        'Invoke-NativeInstallCoordinator.ps1'='6c530bd587750f969a0f5aa4b23997ba6849212424e042382a5876d4ecf162f6'
        'NativeInstaller.psm1'='768e725b9ea3e5193b4d87d2430a63801c98af5b876f1328c31e49c60500c572'
        'NativeDeploymentJob.psm1'='1f5600efadac9afc1f5afb2a7f6158643fcb25aeaf9400f58022674c69763331'
        'NativeStandardLiveShell.psm1'='e0343ec8769a66e0911b65a3d34d72d279f92bc291f0e3d9a46b9070a0f4edba'
    }
    $names=@($known.Keys)+@('Invoke-NativeInstaller.ps1','Invoke-NativeDeployment.ps1','Invoke-NativeDeployWorker.ps1','Start-WinSightNative.ps1')
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $selfBytes=LaunchBytes $PSCommandPath 32768;$selfHash=LaunchHash $selfBytes
    if($PSCommandPath -cne ([IO.Path]::GetDirectoryName($control)+'\install\shell\Start-WinSightNative.ps1')){throw 'N7.LaunchLocation: installed shell client required.'}
    foreach($f in $sources.files){
        LaunchFields $f @('name','bytes','sha256')
        if($f.name -isnot [string] -or $f.name -cnotin $names -or -not $seen.Add($f.name) -or ($f.bytes -isnot [int] -and $f.bytes -isnot [long]) -or $f.bytes -lt 0 -or $f.sha256 -isnot [string] -or $f.sha256 -cnotmatch '^[0-9a-f]{64}$'){throw 'N7.LaunchSource: closed typed unique slots required.'}
        if($known.ContainsKey($f.name) -and $f.sha256 -cne $known[$f.name]){throw 'N7.LaunchTrustedSource: runtime is not this published launcher source version.'}
        $bytes=LaunchBytes ($runtime+'\'+$f.name) 2097152
        if($bytes.Length -ne $f.bytes -or (LaunchHash $bytes) -cne $f.sha256){throw 'N7.LaunchSource: actual co-located runtime source mismatch.'}
        if($f.name -ceq 'Start-WinSightNative.ps1' -and ($selfHash -cne $f.sha256 -or $selfBytes.Length -ne $f.bytes)){throw 'N7.LaunchSelf: installed launcher differs from source9.'}
    }
    LaunchBudget
    Import-Module ($runtime+'\NativeDeploymentJob.psm1') -ErrorAction Stop
    $tick=Get-NativeTick
    $start=($tick-[long][Math]::Ceiling($bootstrap.Elapsed.TotalMilliseconds)-32+4294967296L)%4294967296L
    $loaded=Import-Module ($runtime+'\NativeInstallCoordinator.psm1') -PassThru -ErrorAction Stop
    $validated=Invoke-NativeInstallCoordinator -Command ValidateLaunch -BindingPath $BindingPath -BindingSha256 $BindingSha256 -StartTick $start -DeadlineMilliseconds $TimeoutMilliseconds
    $result=& $loaded {param($V,$O) Start-N7ValidatedLaunch $V $O} $validated $launchOutcome
} catch {$primary=$_;$primary.Exception.Data['N7LaunchStartOutcome']=$launchOutcome} finally {
    foreach($problem in $launchOutcome.cleanupErrors){$cleanup.Add($problem)}
    foreach($resource in $locks){try{$resource.Dispose()}catch{$cleanup.Add($_.Exception.Message)}}
}
if($null -ne $primary -or $cleanup.Count){
    $message=if($null -ne $primary){$primary.Exception.Message}else{'N7.LaunchCleanup: source handle cleanup failed.'}
    [Console]::Error.WriteLine(([pscustomobject]@{protocol='NativeAppStartFailure/1';status='Failed';message=$message;cleanupErrors=$cleanup.ToArray();appMayHaveStarted=($launchOutcome.startAttempted -and ($null -eq $launchOutcome.startSucceeded -or $launchOutcome.startSucceeded));uiReady=$false}|ConvertTo-Json -Depth 8))
    exit 1
}
[Console]::Out.WriteLine(($result|ConvertTo-Json -Depth 8))
exit 0
