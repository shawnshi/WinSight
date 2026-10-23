Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:NLRun = '__WINSIGHT_PRIVATE_RUN_ROOT__'
if ($script:NLRun.StartsWith('__')) { throw 'N7.UnconfiguredPreviewSource: materialize and re-pin a private copy before importing standard-live operations.' }
$script:NLUtf8 = [Text.UTF8Encoding]::new($false, $true)
$script:NLMenu = [IO.Path]::Combine([Environment]::GetFolderPath('ApplicationData'),'Microsoft\Windows\Start Menu\Programs\WinSight Native Preview')

function Assert-NLDeadline([long]$StartTick, [int]$DeadlineMilliseconds) {
    if ((Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds) -le 0) { throw 'N7.Deadline: shared budget exhausted.' }
}
function Get-NLHash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Assert-NLFields($Value, [string[]]$Names) {
    if ($Value -isnot [System.Management.Automation.PSCustomObject]) { throw 'N7.Object: JSON object required.' }
    $actual = @(foreach ($property in $Value.PSObject.Properties) { $property.Name })
    if ($actual.Count -ne $Names.Count) { throw 'N7.Fields: closed fields required.' }
    foreach ($name in $actual) { if ($name -cnotin $Names) { throw 'N7.Fields: unknown field.' } }
}
function Assert-NLString($Value) {
    if ($Value -isnot [string]) { throw 'N7.String: scalar string required.' }
}
function Assert-NLHash($Value, [switch]$Nullable) {
    if ($Nullable -and $null -eq $Value) { return }
    Assert-NLString $Value
    if ($Value -cnotmatch '^[0-9a-f]{64}$') { throw 'N7.Hash: lowercase SHA256 required.' }
}
function Assert-NLInteger($Value, [long]$Minimum=0, [long]$Maximum=[long]::MaxValue) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt $Minimum -or $Value -gt $Maximum) { throw 'N7.Integer: bounded Int64 required, not Boolean/float/array.' }
}
function Assert-NLPath($Value) {
    Assert-NLString $Value
    if ($Value -cnotmatch '^[A-Z]:\\' -or $Value -match '[\x00-\x1f"<>|?*]' -or $Value.Substring(2).Contains(':') -or [IO.Path]::GetFullPath($Value).TrimEnd('\') -cne $Value -or $Value.Length -le 3) { throw 'N7.Path: canonical absolute local path required.' }
    foreach ($part in $Value.Substring(3).Split('\')) {
        if (-not $part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw 'N7.Path: ambiguous path segment.' }
    }
}
function Assert-NLNoReparse([string]$Path) {
    Assert-NLPath $Path
    for ($p=$Path; $p; $p=[IO.Path]::GetDirectoryName($p)) {
        if ([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)) {
            if (([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'N7.Reparse: reparse path rejected.' }
        }
    }
}
function Assert-NLPin($Value, [switch]$Nullable) {
    if ($Nullable -and $null -eq $Value) { return }
    Assert-NLFields $Value @('path','bytes','sha256')
    Assert-NLPath $Value.path; Assert-NLInteger $Value.bytes; Assert-NLHash $Value.sha256
}
function Assert-NLArray($Value, [int]$Minimum=0) {
    if ($Value -isnot [array] -or $Value.Count -lt $Minimum -or $Value.Count -gt 4096) { throw 'N7.Records: bounded array required.' }
}
# Validate raw syntax and property uniqueness BEFORE ConvertFrom-Json can collapse keys.
function ConvertFrom-NLBytes([byte[]]$Bytes, [long]$StartTick, [int]$DeadlineMilliseconds, [int]$Limit=2097152) {
    if ($Bytes.Length -eq 0 -or $Bytes.Length -gt $Limit) { throw 'N7.JsonSize: bounded nonempty JSON required.' }
    $text = $script:NLUtf8.GetString($Bytes)
    $pattern = '\G[\x20\x09\x0a\x0d]*("(?:[^"\\\x00-\x1f]|\\["\\/bfnrt]|\\u[0-9a-fA-F]{4})*"|[{}\[\]:,]|true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)'
    $regex = [regex]::new($pattern)
    $tokens = [Collections.Generic.List[string]]::new(); $position=0
    while ($position -lt $text.Length) {
        Assert-NLDeadline $StartTick $DeadlineMilliseconds
        $m = $regex.Match($text,$position)
        if (-not $m.Success) {
            if ($text.Substring($position) -cmatch '\A[\x20\x09\x0a\x0d]*\z') { break }
            throw 'N7.JsonSyntax: invalid token.'
        }
        $tokens.Add($m.Groups[1].Value); $position += $m.Length
    }
    $cursor=@{i=0}
    function Read-NLToken($T,$C,[int]$Depth) {
        Assert-NLDeadline $StartTick $DeadlineMilliseconds
        if ($Depth -gt 30 -or $C.i -ge $T.Count) { throw 'N7.JsonSyntax: depth/truncation.' }
        $token=$T[$C.i]; $C.i++
        if ($token -ceq '{') {
            $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            if ($C.i -lt $T.Count -and $T[$C.i] -ceq '}') { $C.i++; return }
            while ($true) {
                if ($C.i -ge $T.Count) { throw 'N7.JsonSyntax: missing property.' }
                $key=$T[$C.i]; $C.i++
                if ($key -cnotmatch '^"[A-Za-z][A-Za-z0-9]*"$') { throw 'N7.JsonProperty: unescaped contract property required.' }
                if (-not $seen.Add($key)) { throw 'N7.DuplicateKey: duplicate property.' }
                if ($C.i -ge $T.Count -or $T[$C.i] -cne ':') { throw 'N7.JsonSyntax: missing colon.' }; $C.i++
                Read-NLToken $T $C ($Depth+1)
                if ($C.i -ge $T.Count) { throw 'N7.JsonSyntax: missing object end.' }
                $end=$T[$C.i]; $C.i++
                if ($end -ceq '}') { break }; if ($end -cne ',') { throw 'N7.JsonSyntax: invalid separator.' }
            }
        } elseif ($token -ceq '[') {
            $records=0
            if ($C.i -lt $T.Count -and $T[$C.i] -ceq ']') { $C.i++; return }
            while ($true) {
                $records++; if ($records -gt 4096) { throw 'N7.Records: array bound exceeded.' }
                Read-NLToken $T $C ($Depth+1)
                if ($C.i -ge $T.Count) { throw 'N7.JsonSyntax: missing array end.' }
                $end=$T[$C.i]; $C.i++
                if ($end -ceq ']') { break }; if ($end -cne ',') { throw 'N7.JsonSyntax: invalid separator.' }
            }
        } elseif ($token -cin @('}',']',':',',')) { throw 'N7.JsonSyntax: value required.' }
    }
    Read-NLToken $tokens $cursor 0
    if ($cursor.i -ne $tokens.Count) { throw 'N7.JsonSyntax: trailing value.' }
    $value=$text | ConvertFrom-Json -ErrorAction Stop
    Assert-NLDeadline $StartTick $DeadlineMilliseconds
    $canonical=$script:NLUtf8.GetBytes(($value | ConvertTo-Json -Depth 30))
    Assert-NLDeadline $StartTick $DeadlineMilliseconds
    if ([Convert]::ToBase64String($canonical) -cne [Convert]::ToBase64String($Bytes)) { throw 'N7.Serializer: exact same-host canonical bytes required.' }
    return $value
}
function Read-NLJson([string]$Path, [string]$Hash, [long]$StartTick, [int]$DeadlineMilliseconds, [int]$Limit=2097152, [long]$ExpectedBytes=-1) {
    Assert-NLHash $Hash; Assert-NLNoReparse $Path
    Assert-NLDeadline $StartTick $DeadlineMilliseconds
    $stream=[IO.File]::Open($Path,'Open','Read','Read')
    try {
        if ($stream.Length -gt $Limit) { throw 'N7.JsonSize: JSON bound exceeded.' }
        if ($ExpectedBytes -ge 0 -and $stream.Length -ne $ExpectedBytes) { throw 'N7.PinMismatch: JSON length mismatch.' }
        $bytes=New-Object byte[] ([int]$stream.Length); $offset=0
        while ($offset -lt $bytes.Length) {
            Assert-NLDeadline $StartTick $DeadlineMilliseconds
            $n=$stream.Read($bytes,$offset,[Math]::Min(65536,$bytes.Length-$offset))
            if ($n -eq 0) { throw 'N7.ShortRead: JSON changed.' }; $offset+=$n
        }
        if ($stream.Length -ne $bytes.Length -or (Get-NLHash $bytes) -cne $Hash) { throw 'N7.PinMismatch: JSON bytes/hash mismatch.' }
        ConvertFrom-NLBytes $bytes $StartTick $DeadlineMilliseconds $Limit
    } finally { $stream.Dispose() }
}
function Get-NLFilePin([string]$Path, [long]$StartTick, [int]$DeadlineMilliseconds) {
    Assert-NLNoReparse $Path; Assert-NLDeadline $StartTick $DeadlineMilliseconds
    $stream=[IO.File]::Open($Path,'Open','Read','Read'); $sha=$null
    try {
        $size=$stream.Length; $total=0L; $buffer=New-Object byte[] 65536
        $sha=[Security.Cryptography.SHA256]::Create()
        while (($n=$stream.Read($buffer,0,$buffer.Length)) -gt 0) {
            Assert-NLDeadline $StartTick $DeadlineMilliseconds
            $total+=$n; [void]$sha.TransformBlock($buffer,0,$n,$buffer,0)
        }
        Assert-NLDeadline $StartTick $DeadlineMilliseconds
        [void]$sha.TransformFinalBlock($buffer,0,0)
        if ($total -ne $size -or $stream.Length -ne $size) { throw 'N7.FileChanged: streaming size changed.' }
        [pscustomobject]@{path=$Path;bytes=[long]$total;sha256=([BitConverter]::ToString($sha.Hash).Replace('-','').ToLowerInvariant())}
    } finally { if ($sha) { $sha.Dispose() }; $stream.Dispose() }
}
function Assert-NLRoots($Roots) {
    Assert-NLFields $Roots @('taskRoot','installRoot','shellRoot','deploymentRoot','dataRoot','controlRoot')
    foreach ($name in @('taskRoot','installRoot','shellRoot','deploymentRoot','dataRoot','controlRoot')) { Assert-NLPath $Roots.$name }
    if ($Roots.taskRoot -cne $script:NLRun -or $Roots.installRoot -cne ($script:NLRun+'\install') -or $Roots.shellRoot -cne ($script:NLRun+'\install\shell') -or $Roots.deploymentRoot -cne ($script:NLRun+'\install\deployment') -or $Roots.dataRoot -cne ($script:NLRun+'\data') -or $Roots.controlRoot -cne ($script:NLRun+'\control')) { throw 'N7.Roots: exact run-01 roots required.' }
}
function Assert-NLHost($HostRecord) {
    Assert-NLFields $HostRecord @('edition','version','executable','machine','userSid','serializer')
    foreach ($name in @('edition','version','machine','userSid','serializer')) { Assert-NLString $HostRecord.$name }
    Assert-NLPin $HostRecord.executable
    if ($HostRecord.edition -cne 'Desktop' -or $HostRecord.version -cnotmatch '^5\.1\.[0-9]+\.[0-9]+$' -or $HostRecord.serializer -cne 'ConvertTo-Json/Depth30/UTF8-noBOM' -or $HostRecord.machine.Length -eq 0 -or $HostRecord.userSid -cnotmatch '^S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+$') { throw 'N7.Host: pinned Windows PowerShell 5.1 machine/SID required.' }
    # WINDIR and MainModule differ in casing on this host; only the system locator
    # is case-insensitive. The actual executable path/bytes/hash pin remains exact.
    if (-not [string]::Equals($HostRecord.executable.path,($env:WINDIR+'\System32\WindowsPowerShell\v1.0\powershell.exe'),[StringComparison]::OrdinalIgnoreCase)) { throw 'N7.Host: system x64 Windows PowerShell required.' }
}
function Assert-NLBinding($Binding) {
    Assert-NLFields $Binding @('schema','backend','purpose','releaseApproved','instanceId','appId','displayName','roots','host','sources','package','shellSpec','priorReceipt')
    Assert-NLInteger $Binding.schema 3 3
    foreach ($name in @('backend','purpose','instanceId','appId','displayName')) { Assert-NLString $Binding.$name }
    if ($Binding.releaseApproved -isnot [bool] -or $Binding.releaseApproved -or $Binding.backend -cne 'InnoPerUserLive/1' -or $Binding.purpose -cne 'InternalStandardCandidate' -or $Binding.instanceId -cne 'run-01' -or $Binding.appId -cne 'WinSight.Native.Preview' -or $Binding.displayName -cne 'WinSight Native Preview') { throw 'N7.Binding: live identity mismatch.' }
    Assert-NLRoots $Binding.roots; Assert-NLHost $Binding.host
    foreach ($name in @('sources','package','shellSpec','priorReceipt')) {
        Assert-NLPin $Binding.$name -Nullable:($name -ceq 'priorReceipt')
        if ($null -ne $Binding.$name -and (-not $Binding.$name.path.StartsWith($Binding.roots.controlRoot+'\',[StringComparison]::Ordinal) -or [IO.Path]::GetFileName($Binding.$name.path) -cne ($Binding.$name.sha256+'.json'))) { throw 'N7.BindingPin: content-addressed control JSON required.' }
    }
}
function Assert-NLFileRecord($Record) {
    Assert-NLFields $Record @('path','bytes','sha256'); Assert-NLString $Record.path
    Assert-NLInteger $Record.bytes; Assert-NLHash $Record.sha256
    if ($Record.path.Length -gt 240 -or $Record.path -cnotmatch '^app/[A-Za-z0-9_. /-]+$') { throw 'N7.FileRecord: restricted app relative path required.' }
    foreach ($part in $Record.path.Split('/')) {
        if (-not $part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw 'N7.FileRecord: ambiguous segment.' }
    }
}
function Assert-NLReceipt($Receipt, $Binding, [string]$BindingSha256) {
    Assert-NLBinding $Binding; Assert-NLHash $BindingSha256
    Assert-NLFields $Receipt @('protocol','bindingSha256','instanceId','appId','roots','host','transactionId','operation','intentSha256','previousReceiptSha256','manifestSha256','selection','journalSha256','previousStateSha256','nextStateSha256','sourceManifestSha256','packageSha256','shellSpecSha256','observation','stableDigest','outerResult','trustedPreviousSetup')
    foreach ($name in @('protocol','instanceId','appId','transactionId','operation')) { Assert-NLString $Receipt.$name }
    foreach ($name in @('bindingSha256','intentSha256','journalSha256','sourceManifestSha256','packageSha256','shellSpecSha256','stableDigest')) { Assert-NLHash $Receipt.$name }
    foreach ($name in @('previousReceiptSha256','manifestSha256','selection','previousStateSha256','nextStateSha256')) { Assert-NLHash $Receipt.$name -Nullable }
    Assert-NLRoots $Receipt.roots; Assert-NLHost $Receipt.host
    Assert-NLPin $Receipt.observation; Assert-NLPin $Receipt.outerResult -Nullable; Assert-NLPin $Receipt.trustedPreviousSetup -Nullable
    if ($Receipt.protocol -cne 'StandardCommitReceipt/1' -or $Receipt.instanceId -cne $Binding.instanceId -or $Receipt.appId -cne $Binding.appId -or $Receipt.transactionId -cnotmatch '^[0-9a-f]{32}$' -or $Receipt.operation -cnotin @('Apply','Remove')) { throw 'N7.ReceiptIdentity: invalid receipt identity.' }
    foreach ($name in @('edition','version','machine','userSid','serializer')) { if ($Receipt.host.$name -cne $Binding.host.$name) { throw 'N7.ReceiptHost: host mismatch.' } }
    foreach ($name in @('path','bytes','sha256')) { if ($Receipt.host.executable.$name -cne $Binding.host.executable.$name) { throw 'N7.ReceiptHost: host pin mismatch.' } }
    if ($Receipt.bindingSha256 -cne $BindingSha256 -or $Receipt.sourceManifestSha256 -cne $Binding.sources.sha256 -or $Receipt.packageSha256 -cne $Binding.package.sha256 -or $Receipt.shellSpecSha256 -cne $Binding.shellSpec.sha256) { throw 'N7.ReceiptPins: binding graph mismatch.' }
    if ($Receipt.operation -ceq 'Apply') {
        if ($null -eq $Receipt.manifestSha256 -or $null -eq $Receipt.selection -or $null -eq $Receipt.nextStateSha256 -or $null -ne $Receipt.outerResult) { throw 'N7.ReceiptState: Apply pins/explicit null outerResult required.' }
    } elseif ($null -ne $Receipt.manifestSha256 -or $null -ne $Receipt.selection -or $null -ne $Receipt.nextStateSha256 -or $null -eq $Receipt.previousReceiptSha256 -or $null -eq $Receipt.outerResult) { throw 'N7.ReceiptState: Remove previous/outer pins required.' }
}
function Assert-NLArguments($Parameters) {
    $allowed=@('Command','Operation','BindingPath','BindingSha256','TransactionId','StageDirectory','ManifestSha256','IntentSha256','ExpectedReceiptSha256','OuterResultSha256','StartTick','DeadlineMilliseconds')
    foreach ($key in $Parameters.Keys) { if ($key -cnotin $allowed) { throw 'N7.Arguments: unknown parameter.' } }
    foreach ($key in @('Command','BindingPath','BindingSha256','StartTick','DeadlineMilliseconds')) { if (-not $Parameters.ContainsKey($key)) { throw 'N7.Arguments: missing shared parameter.' } }
    Assert-NLString $Parameters.Command; Assert-NLPath $Parameters.BindingPath; Assert-NLHash $Parameters.BindingSha256
    Assert-NLInteger $Parameters.StartTick 0 4294967295L; Assert-NLInteger $Parameters.DeadlineMilliseconds 1000 160000
    if ($Parameters.Command -cnotin @('Inspect','CreateIntent','Prepare','Commit','Finalize','Recover','ResumeFinalize','ValidateLaunch','RunOwnedShell')) { throw 'N7.Arguments: unsupported command.' }
    foreach ($name in @('IntentSha256','ExpectedReceiptSha256','ManifestSha256','OuterResultSha256')) { if ($Parameters.ContainsKey($name)) { Assert-NLHash $Parameters[$name] } }
    $specific=@('ExpectedReceiptSha256')
    if ($Parameters.Command -ceq 'RunOwnedShell') {
        foreach($name in @('Operation','TransactionId')){if(-not $Parameters.ContainsKey($name)){throw 'N7.Arguments: live operation/transaction required.'}}
        if($Parameters.Operation -cnotin @('Apply','Remove') -or $Parameters.TransactionId -cnotmatch '^[0-9a-f]{32}$'){throw 'N7.Arguments: invalid live identity.'}
        $specific=@('Operation','TransactionId')
    }
    elseif ($Parameters.Command -ceq 'ValidateLaunch') { $specific=@() }
    elseif ($Parameters.Command -cne 'Inspect') {
        foreach ($name in @('Operation','TransactionId')) { if (-not $Parameters.ContainsKey($name)) { throw 'N7.Arguments: operation/transaction required.' }; Assert-NLString $Parameters[$name] }
        if ($Parameters.Operation -cnotin @('Apply','Remove') -or $Parameters.TransactionId -cnotmatch '^[0-9a-f]{32}$') { throw 'N7.Arguments: operation/transaction invalid.' }
        $specific+=@('Operation','TransactionId')
        if ($Parameters.Command -ceq 'CreateIntent') {
            if ($Parameters.Operation -ceq 'Apply') {
                foreach ($name in @('StageDirectory','ManifestSha256')) { if (-not $Parameters.ContainsKey($name)) { throw 'N7.Arguments: Apply stage/manifest required.' } }
                Assert-NLPath $Parameters.StageDirectory; $specific+=@('StageDirectory','ManifestSha256')
            }
        } else {
            if (-not $Parameters.ContainsKey('IntentSha256')) { throw 'N7.Arguments: intent required.' }; $specific+='IntentSha256'
            if ($Parameters.Command -cin @('Finalize','ResumeFinalize') -and -not $Parameters.ContainsKey('ExpectedReceiptSha256')) { throw 'N7.Arguments: actual new receipt required.' }
            if ($Parameters.Command -ceq 'Commit' -and $Parameters.Operation -ceq 'Remove') {
                if (-not $Parameters.ContainsKey('OuterResultSha256')) { throw 'N7.Arguments: Remove outer result required.' }; $specific+='OuterResultSha256'
            }
        }
    }
    foreach ($key in $Parameters.Keys) { if ($key -cnotin (@('Command','BindingPath','BindingSha256','StartTick','DeadlineMilliseconds')+$specific)) { throw 'N7.Arguments: unsupported command parameter combination.' } }
}
# Metadata only. A valid pinned binding is not a committed authority or process proof.
function Read-NativeLiveBinding {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$BindingPath,[Parameter(Mandatory=$true)][string]$BindingSha256,[Parameter(Mandatory=$true)][long]$StartTick,[Parameter(Mandatory=$true)][ValidateRange(1000,160000)][int]$DeadlineMilliseconds)
    if ($StartTick -lt 0 -or $StartTick -gt 4294967295L) { throw 'N7.Deadline: invalid native start tick.' }
    $jobPath=Join-Path $PSScriptRoot 'NativeDeploymentJob.psm1'
    Assert-NLNoReparse $jobPath
    if ((Get-FileHash -LiteralPath $jobPath -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant() -cne '1f5600efadac9afc1f5afb2a7f6158643fcb25aeaf9400f58022674c69763331') { throw 'N7.JobPin: frozen clock mismatch.' }
    Import-Module $jobPath -ErrorAction Stop
    $b=Read-NLJson $BindingPath $BindingSha256 $StartTick $DeadlineMilliseconds 32768
    Assert-NLBinding $b
    # A typed prior pointer is metadata only; only exact registry selection can confer authority.
    if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.ToString() -cne $b.host.version -or [IntPtr]::Size -ne 8 -or [Environment]::MachineName -cne $b.host.machine) { throw 'N7.HostMismatch: actual host differs.' }
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try { if ($identity.User.Value -cne $b.host.userSid) { throw 'N7.HostMismatch: actual SID differs.' } } finally { $identity.Dispose() }
    $process=[Diagnostics.Process]::GetCurrentProcess()
    try { $exe=$process.MainModule.FileName } finally { $process.Dispose() }
    if ($exe -cne $b.host.executable.path) { throw 'N7.HostMismatch: executable path differs.' }
    $pin=Get-NLFilePin $exe $StartTick $DeadlineMilliseconds
    if ($pin.bytes -ne $b.host.executable.bytes -or $pin.sha256 -cne $b.host.executable.sha256) { throw 'N7.HostMismatch: executable pin differs.' }
    Assert-NLDeadline $StartTick $DeadlineMilliseconds
    return $b
}
function Assert-NLGraphPath($Path,[string]$Parent) {
    Assert-NLPath $Path
    if($Path.Contains('@') -or -not $Path.StartsWith($Parent+'\',[StringComparison]::Ordinal)){throw 'N7.GraphPath: exact control ancestry/no markers required.'}
    Assert-NLNoReparse $Path
    for($p=$Path;$p.Length -gt $Parent.Length;$p=[IO.Path]::GetDirectoryName($p)){
        $directory=[IO.Path]::GetDirectoryName($p);$leaf=[IO.Path]::GetFileName($p)
        if([IO.Directory]::Exists($directory) -and ([IO.File]::Exists($p) -or [IO.Directory]::Exists($p))){
            $diskEntries=[IO.Directory]::GetFileSystemEntries($directory,$leaf)
            if($diskEntries.Length -ne 1 -or [IO.Path]::GetFileName($diskEntries[0]) -cne $leaf){throw 'N7.GraphPath: physical path case mismatch.'}
        }
    }
}
function Read-NLGraphObject($Pin,[string]$Control,[long]$Start,[int]$Deadline) {
    Assert-NLPin $Pin;Assert-NLGraphPath $Pin.path $Control
    if($Pin.path -cne ($Control+'\objects\'+$Pin.sha256+'.json')){throw 'N7.GraphPinPath: exact content-addressed object required.'}
    Read-NLJson $Pin.path $Pin.sha256 $Start $Deadline 2097152 $Pin.bytes
}
function Assert-NLPhysicalPin($Pin,[string]$Control,[long]$Start,[int]$Deadline) {
    Assert-NLPin $Pin;Assert-NLGraphPath $Pin.path $Control
    $actual=Get-NLFilePin $Pin.path $Start $Deadline
    if($actual.bytes -ne $Pin.bytes -or $actual.sha256 -cne $Pin.sha256){throw 'N7.GraphBytes: actual file pin mismatch.'}
}
function Assert-NLSources($Sources) {
    Assert-NLFields $Sources @('protocol','files');Assert-NLString $Sources.protocol;Assert-NLArray $Sources.files 1
    $names=@('NativeInstaller.psm1','Invoke-NativeInstaller.ps1','NativeDeploymentJob.psm1','NativeInstallCoordinator.psm1','Invoke-NativeInstallCoordinator.ps1','NativeStandardLiveShell.psm1','Invoke-NativeDeployment.ps1','Invoke-NativeDeployWorker.ps1','Start-WinSightNative.ps1')
    if($Sources.protocol -cne 'StandardSources/1' -or $Sources.files.Count -ne $names.Count){throw 'N7.SourceSlots: exactly nine source slots required.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($f in $Sources.files){
        Assert-NLFields $f @('name','bytes','sha256');Assert-NLString $f.name;Assert-NLInteger $f.bytes;Assert-NLHash $f.sha256
        if($f.name -cnotin $names -or -not $seen.Add($f.name)){throw 'N7.SourceSlots: unknown/case/duplicate source slot.'}
    }
}
function Assert-NLInventory($Manifest) {
    Assert-NLFields $Manifest @('schema','runtime','selfContained','releaseApproved','appOnlyPreview','files')
    Assert-NLInteger $Manifest.schema 1 1;Assert-NLString $Manifest.runtime
    foreach($n in @('selfContained','releaseApproved','appOnlyPreview')){if($Manifest.$n -isnot [bool]){throw 'N7.Inventory: Boolean flags required.'}}
    if($Manifest.runtime -cne 'win-x64' -or -not $Manifest.selfContained -or $Manifest.releaseApproved -or $Manifest.appOnlyPreview){throw 'N7.Inventory: native unapproved full package required.'}
    Assert-NLArray $Manifest.files 1
    $map=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase);$total=0L
    foreach($f in $Manifest.files){
        Assert-NLFileRecord $f
        if($map.ContainsKey($f.path)){throw 'N7.InventoryDuplicate: duplicate/case-colliding file.'}
        if([long]::MaxValue-$total -lt $f.bytes){throw 'N7.InventorySize: Int64 total overflow.'}
        $total+=$f.bytes;$map.Add($f.path,$f)
    }
    foreach($f in $Manifest.files){for($p=$f.path;$p.Contains('/');){$p=$p.Substring(0,$p.LastIndexOf('/'));if($map.ContainsKey($p)){throw 'N7.InventoryOverlap: file/directory overlap.'}}}
    [pscustomobject]@{map=$map;totalBytes=$total;fileCount=$Manifest.files.Count}
}
function Assert-NLPayloadCoverage($Inventory,[string]$PayloadRoot,[long]$Start,[int]$Deadline) {
    $queue=[Collections.Generic.Queue[string]]::new();$queue.Enqueue($PayloadRoot+'\app');$count=0;$nodes=0
    while($queue.Count){
        Assert-NLDeadline $Start $Deadline;$directory=$queue.Dequeue();Assert-NLNoReparse $directory
        foreach($path in [IO.Directory]::EnumerateFileSystemEntries($directory)){
            Assert-NLDeadline $Start $Deadline;$nodes++;if($nodes -gt 16384){throw 'N7.PayloadCoverage: filesystem node bound.'};Assert-NLGraphPath $path $PayloadRoot
            if([IO.Directory]::Exists($path)){$queue.Enqueue($path);continue}
            $count++;if($count -gt 4096){throw 'N7.PayloadCoverage: file record bound.'}
            $relative=$path.Substring($PayloadRoot.Length+1).Replace('\','/')
            if(-not $Inventory.map.ContainsKey($relative) -or $Inventory.map[$relative].path -cne $relative){throw 'N7.PayloadCoverage: unknown/case-mismatched physical payload file.'}
        }
    }
    if($count -ne $Inventory.fileCount){throw 'N7.PayloadCoverage: physical inventory coverage mismatch.'}
}
function Assert-NLPackage($Package,$Binding,$Inventory) {
    Assert-NLFields $Package @('protocol','setup','payloadManifest','fileCount','totalBytes','sourceManifest','assemblyEvidence','launch','appOnlyPreview','releaseApproved')
    Assert-NLString $Package.protocol;Assert-NLInteger $Package.fileCount 1 4096;Assert-NLInteger $Package.totalBytes
    foreach($n in @('setup','payloadManifest','sourceManifest','assemblyEvidence')){Assert-NLPin $Package.$n}
    foreach($n in @('appOnlyPreview','releaseApproved')){if($Package.$n -isnot [bool]){throw 'N7.Package: Boolean flags required.'}}
    if($Package.protocol -cne 'StandardPackage/1' -or $Package.appOnlyPreview -or $Package.releaseApproved){throw 'N7.Package: native unapproved package required.'}
    foreach($n in @('path','bytes','sha256')){if($Package.sourceManifest.$n -cne $Binding.sources.$n){throw 'N7.PackageSources: exact binding source pin required.'}}
    if($Package.fileCount -ne $Inventory.fileCount -or $Package.totalBytes -ne $Inventory.totalBytes){throw 'N7.PackageInventory: count/Int64 total mismatch.'}
    Assert-NLFields $Package.launch @('app','cli','helpers','models','profiles')
    foreach($n in @('helpers','models','profiles')){Assert-NLArray $Package.launch.$n 1}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($f in (@($Package.launch.app,$Package.launch.cli)+$Package.launch.helpers+$Package.launch.models+$Package.launch.profiles)){
        Assert-NLFileRecord $f
        if(-not $seen.Add($f.path)){throw 'N7.LaunchDuplicate: overlapping launch roles.'}
        if(-not $Inventory.map.ContainsKey($f.path)){throw 'N7.LaunchInventory: launch record absent from inventory.'}
        $expected=$Inventory.map[$f.path]
        if($f.path -cne $expected.path -or $f.bytes -ne $expected.bytes -or $f.sha256 -cne $expected.sha256){throw 'N7.LaunchInventory: exact inventory pin/case mismatch.'}
    }
}
function Get-NLShellCommands($Binding) {
    $runtime=$Binding.roots.controlRoot+'\runtime\'+$Binding.sources.sha256
    $uninstall='-NoProfile -NonInteractive -File "'+$runtime+'\Invoke-NativeDeployment.ps1" -Action Uninstall -BindingPath "@BindingPath@" -BindingSha256 @BindingSha256@'
    $launch='-NoProfile -NonInteractive -File "'+$Binding.roots.shellRoot+'\Start-WinSightNative.ps1" -BindingPath "@BindingPath@" -BindingSha256 @BindingSha256@'
    [pscustomobject]@{runtime=$runtime;uninstall=$uninstall;quiet=($uninstall+' -Quiet');launch=$launch;full=('"'+$Binding.host.executable.path+'" '+$uninstall);fullQuiet=('"'+$Binding.host.executable.path+'" '+$uninstall+' -Quiet')}
}
function Assert-NLShellSpec($Spec,$Binding,$Sources) {
    Assert-NLFields $Spec @('protocol','registry','menu','files','uninstallerPaths');Assert-NLString $Spec.protocol
    Assert-NLFields $Spec.registry @('hive','view','key','values')
    foreach($n in @('hive','view','key')){Assert-NLString $Spec.registry.$n}
    if($Spec.protocol -cne 'StandardShellSpec/1' -or $Spec.registry.hive -cne 'HKCU' -or $Spec.registry.view -cne 'Registry64' -or $Spec.registry.key -cne 'Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1'){throw 'N7.ShellIdentity: exact expected shell identity required.'}
    Assert-NLArray $Spec.registry.values 1
    $values=@{};$names=@('DisplayName','DisplayVersion','Publisher','InstallLocation','UninstallString','QuietUninstallString','NoModify','NoRepair');$cmd=Get-NLShellCommands $Binding
    if($Spec.registry.values.Count -ne 8){throw 'N7.ShellValues: exact eight values required.'}
    foreach($v in $Spec.registry.values){
        Assert-NLFields $v @('name','kind','value');Assert-NLString $v.name;Assert-NLString $v.kind
        if($v.name -cnotin $names -or $values.ContainsKey($v.name)){throw 'N7.ShellValues: unknown/duplicate/case value.'}
        if($v.name -cin @('NoModify','NoRepair')){Assert-NLInteger $v.value 1 1;if($v.kind -cne 'DWord'){throw 'N7.ShellValues: DWord kind required.'}}
        else{Assert-NLString $v.value;if($v.kind -cne 'String' -or -not $v.value.Length){throw 'N7.ShellValues: nonempty String required.'}}
        if($v.name -ceq 'UninstallString'){if($v.value -cne $cmd.full){throw 'N7.ShellTemplate: exact token count/position/quoting required.'}}
        elseif($v.name -ceq 'QuietUninstallString'){if($v.value -cne $cmd.fullQuiet){throw 'N7.ShellTemplate: exact quiet token command required.'}}
        elseif($v.value -is [string] -and $v.value.Contains('@')){throw 'N7.ShellTemplate: marker outside approved command field.'}
        $values[$v.name]=$v.value
    }
    if($values.DisplayName -cne $Binding.displayName -or $values.InstallLocation -cne $Binding.roots.installRoot){throw 'N7.ShellValues: expected name/install root mismatch.'}
    Assert-NLFields $Spec.menu @('path','links');Assert-NLPath $Spec.menu.path;Assert-NLArray $Spec.menu.links 1
    if($Spec.menu.path -cne $script:NLMenu -or $Spec.menu.links.Count -ne 2){throw 'N7.ShellMenu: exact current-user menu/two links required.'}
    $links=@{}
    foreach($link in $Spec.menu.links){
        Assert-NLFields $link @('name','target','arguments','workingDirectory');foreach($n in @('name','target','arguments','workingDirectory')){Assert-NLString $link.$n}
        if($link.name -cnotin @('WinSight Native Preview.lnk','Uninstall WinSight Native Preview.lnk') -or $links.ContainsKey($link.name)){throw 'N7.ShellMenu: exact distinct link names required.'}
        $argsExpected=if($link.name -ceq 'WinSight Native Preview.lnk'){$cmd.launch}else{$cmd.uninstall}
        $workExpected=if($link.name -ceq 'WinSight Native Preview.lnk'){$Binding.roots.shellRoot}else{$cmd.runtime}
        if($link.target -cne $Binding.host.executable.path -or $link.workingDirectory -cne $workExpected){throw 'N7.ShellMenu: exact target/working directory required.'}
        if($link.arguments -cne $argsExpected){throw 'N7.ShellTemplate: exact link token command required.'};$links[$link.name]=$link
    }
    Assert-NLArray $Spec.uninstallerPaths 1
    if($Spec.uninstallerPaths.Count -ne 2){throw 'N7.ShellUninstaller: exact pair required.'}
    foreach($p in $Spec.uninstallerPaths){Assert-NLPath $p}
    if($Spec.uninstallerPaths[0] -cne ($Binding.roots.installRoot+'\unins000.exe') -or $Spec.uninstallerPaths[1] -cne ($Binding.roots.installRoot+'\unins000.dat')){throw 'N7.ShellUninstaller: fixed genuine paths required; no ownership inferred.'}
    Assert-NLArray $Spec.files 1;$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$launcher=$null
    foreach($pin in $Spec.files){Assert-NLPin $pin;Assert-NLGraphPath $pin.path $Binding.roots.shellRoot;if(-not $seen.Add($pin.path)){throw 'N7.ShellFiles: duplicate expected path.'};if($pin.path -ceq ($Binding.roots.shellRoot+'\Start-WinSightNative.ps1')){$launcher=$pin}}
    if($null -eq $launcher){throw 'N7.ShellFiles: declared launcher required.'}
    $source=@($Sources.files|Where-Object {$_.name -ceq 'Start-WinSightNative.ps1'})[0]
    if($launcher.bytes -ne $source.bytes -or $launcher.sha256 -cne $source.sha256){throw 'N7.ShellFiles: launcher must match pinned source bytes.'}
}
function Resolve-NLShellTemplate($Binding,$Spec,[string]$BindingPath,[string]$BindingSha256) {
    Assert-NLHash $BindingSha256
    if($BindingPath -cne ($Binding.roots.controlRoot+'\bindings\'+$BindingSha256+'.json')){throw 'N7.BindingLocation: exact final hash filename required.'}
    # Rendering is a separate memory object. Never replace the template artifact/pin.
    $rendered=($Spec|ConvertTo-Json -Depth 30)|ConvertFrom-Json
    foreach($v in $rendered.registry.values){if($v.name -cin @('UninstallString','QuietUninstallString')){$v.value=$v.value.Replace('@BindingPath@',$BindingPath).Replace('@BindingSha256@',$BindingSha256)}}
    foreach($link in $rendered.menu.links){$link.arguments=$link.arguments.Replace('@BindingPath@',$BindingPath).Replace('@BindingSha256@',$BindingSha256)}
    if(($rendered|ConvertTo-Json -Depth 30).Contains('@')){throw 'N7.ShellTemplate: unresolved/unknown marker.'}
    $rendered
}
function Read-NativeLiveGraph {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$BindingPath,[Parameter(Mandatory=$true)][string]$BindingSha256,[Parameter(Mandatory=$true)][long]$StartTick,[Parameter(Mandatory=$true)][ValidateRange(1000,160000)][int]$DeadlineMilliseconds)
    $b=Read-NativeLiveBinding -BindingPath $BindingPath -BindingSha256 $BindingSha256 -StartTick $StartTick -DeadlineMilliseconds $DeadlineMilliseconds
    $c=$b.roots.controlRoot
    if($BindingPath -cne ($c+'\bindings\'+$BindingSha256+'.json')){throw 'N7.BindingLocation: exact content-addressed binding required.'}
    Assert-NLGraphPath $BindingPath $c
    $sources=Read-NLGraphObject $b.sources $c $StartTick $DeadlineMilliseconds;Assert-NLSources $sources
    $verified=[Collections.Generic.List[object]]::new()
    foreach($entry in $sources.files){$pin=[pscustomobject]@{path=($c+'\runtime\'+$b.sources.sha256+'\'+$entry.name);bytes=$entry.bytes;sha256=$entry.sha256};Assert-NLPhysicalPin $pin $c $StartTick $DeadlineMilliseconds;$verified.Add($pin)}
    $package=Read-NLGraphObject $b.package $c $StartTick $DeadlineMilliseconds
    # Validate envelope scalars/pins before choosing a dependent file to read.
    Assert-NLFields $package @('protocol','setup','payloadManifest','fileCount','totalBytes','sourceManifest','assemblyEvidence','launch','appOnlyPreview','releaseApproved')
    foreach($n in @('setup','payloadManifest','sourceManifest','assemblyEvidence')){Assert-NLPin $package.$n;Assert-NLGraphPath $package.$n.path $c}
    $manifest=Read-NLJson $package.payloadManifest.path $package.payloadManifest.sha256 $StartTick $DeadlineMilliseconds 2097152 $package.payloadManifest.bytes
    $inventory=Assert-NLInventory $manifest;Assert-NLPackage $package $b $inventory
    $artifactPaths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($pin in @($package.setup,$package.payloadManifest,$package.assemblyEvidence)){
        if(-not $artifactPaths.Add($pin.path) -or $pin.path.StartsWith($c+'\runtime\',[StringComparison]::OrdinalIgnoreCase) -or $pin.path.StartsWith($c+'\objects\',[StringComparison]::OrdinalIgnoreCase) -or $pin.path.StartsWith($c+'\bindings\',[StringComparison]::OrdinalIgnoreCase)){throw 'N7.ArtifactOverlap: package artifacts must be distinct from graph/source storage.'}
        Assert-NLPhysicalPin $pin $c $StartTick $DeadlineMilliseconds;$verified.Add($pin)
    }
    $payloadRoot=[IO.Path]::GetDirectoryName($package.payloadManifest.path)
    foreach($record in $manifest.files){$pin=[pscustomobject]@{path=($payloadRoot+'\'+$record.path.Replace('/','\'));bytes=$record.bytes;sha256=$record.sha256};if($artifactPaths.Contains($pin.path)){throw 'N7.ArtifactOverlap: inventory overlaps package artifact.'};Assert-NLPhysicalPin $pin $c $StartTick $DeadlineMilliseconds;$verified.Add($pin)}
    Assert-NLPayloadCoverage $inventory $payloadRoot $StartTick $DeadlineMilliseconds
    $spec=Read-NLGraphObject $b.shellSpec $c $StartTick $DeadlineMilliseconds;Assert-NLShellSpec $spec $b $sources
    $expected=Resolve-NLShellTemplate $b $spec $BindingPath $BindingSha256
    Assert-NLDeadline $StartTick $DeadlineMilliseconds
    [pscustomobject]@{protocol='NativeLiveGraph/1';bindingSha256=$BindingSha256;binding=$b;sources=$sources;package=$package;payloadManifest=$manifest;shellSpec=$spec;expectedShell=$expected;byteVerified=$verified.ToArray();jsonPinsVerified=@($b.sources,$b.package,$b.shellSpec);sourceCodeApproved=$false;osAuthority=$false;priorReceiptVerified=$false;releaseApproved=$false;expectedResourcesOnly=$true}
}
function Get-NLRecordHash($Value) {
    if($null -eq $Value){return $null}
    Get-NLHash ($script:NLUtf8.GetBytes(($Value|ConvertTo-Json -Depth 30)))
}
function Assert-NLDomainState($State,$Binding) {
    if($null -eq $State){return}
    Assert-NLFields $State @('schema','installRoot','dataRoot','current','releases');Assert-NLInteger $State.schema 1 1;Assert-NLPath $State.installRoot;Assert-NLPath $State.dataRoot;Assert-NLHash $State.current;Assert-NLArray $State.releases 1
    if($State.installRoot -cne $Binding.roots.deploymentRoot -or $State.dataRoot -cne $Binding.roots.dataRoot -or $State.releases.Count -gt 64){throw 'N7.DomainState: target/release bound mismatch.'}
    $seen=@{};foreach($r in $State.releases){Assert-NLFields $r @('id','files');Assert-NLHash $r.id;Assert-NLArray $r.files 1;if($seen.ContainsKey($r.id)){throw 'N7.DomainState: duplicate release.'};$seen[$r.id]=$true;$paths=@{};foreach($f in $r.files){Assert-NLFileRecord $f;if($paths.ContainsKey($f.path)){throw 'N7.DomainState: duplicate file.'};$paths[$f.path]=$true}}
    if(-not $seen.ContainsKey($State.current)){throw 'N7.DomainState: selected release absent.'}
}
function Read-NLDomainMetadata($Binding,[long]$Start,[int]$Deadline) {
    $c=$Binding.roots.controlRoot;$sources=Read-NLGraphObject $Binding.sources $c $Start $Deadline;Assert-NLSources $sources
    $package=Read-NLGraphObject $Binding.package $c $Start $Deadline
    Assert-NLFields $package @('protocol','setup','payloadManifest','fileCount','totalBytes','sourceManifest','assemblyEvidence','launch','appOnlyPreview','releaseApproved')
    foreach($n in @('setup','payloadManifest','sourceManifest','assemblyEvidence')){Assert-NLPin $package.$n;Assert-NLGraphPath $package.$n.path $c}
    $manifest=Read-NLJson $package.payloadManifest.path $package.payloadManifest.sha256 $Start $Deadline 2097152 $package.payloadManifest.bytes
    $inventory=Assert-NLInventory $manifest;Assert-NLPackage $package $Binding $inventory
    $spec=Read-NLGraphObject $Binding.shellSpec $c $Start $Deadline;Assert-NLShellSpec $spec $Binding $sources
    [pscustomobject]@{sources=$sources;package=$package;manifest=$manifest;spec=$spec;historicalExecutableBytesChecked=$false}
}
function Assert-NLIntent($Intent,$Binding,[string]$BindingHash,$Metadata) {
    Assert-NLFields $Intent @('protocol','bindingSha256','transactionId','operation','previousReceiptSha256','stageDirectory','manifestSha256','shellSpecSha256','journal','journalSha256','previousStateSha256','nextStateSha256')
    foreach($n in @('protocol','transactionId','operation')){Assert-NLString $Intent.$n};foreach($n in @('bindingSha256','shellSpecSha256','journalSha256')){Assert-NLHash $Intent.$n};foreach($n in @('previousReceiptSha256','manifestSha256','previousStateSha256','nextStateSha256')){Assert-NLHash $Intent.$n -Nullable}
    if($Intent.protocol -cne 'StandardIntent/1' -or $Intent.transactionId -cnotmatch '^[0-9a-f]{32}$' -or $Intent.operation -cnotin @('Apply','Remove') -or $Intent.bindingSha256 -cne $BindingHash -or $Intent.shellSpecSha256 -cne $Binding.shellSpec.sha256){throw 'N7.IntentBinding: protocol/identity/pins mismatch.'}
    $j=$Intent.journal;Assert-NLFields $j @('schema','operation','installRoot','dataRoot','previous','next','copyProtocol');Assert-NLInteger $j.schema 1 1
    foreach($n in @('operation','copyProtocol')){Assert-NLString $j.$n};Assert-NLPath $j.installRoot;Assert-NLPath $j.dataRoot
    if($j.operation -cne $Intent.operation -or $j.copyProtocol -cne 'staged-v1' -or $j.installRoot -cne $Binding.roots.deploymentRoot -or $j.dataRoot -cne $Binding.roots.dataRoot){throw 'N7.IntentJournal: journal identity mismatch.'}
    Assert-NLDomainState $j.previous $Binding;Assert-NLDomainState $j.next $Binding
    if((Get-NLRecordHash $j) -cne $Intent.journalSha256 -or (Get-NLRecordHash $j.previous) -cne $Intent.previousStateSha256 -or (Get-NLRecordHash $j.next) -cne $Intent.nextStateSha256){throw 'N7.IntentJournal: nested raw canonical hash links mismatch.'}
    if($Intent.operation -ceq 'Apply'){
        Assert-NLPath $Intent.stageDirectory
        if(-not $Intent.stageDirectory.StartsWith($Binding.roots.taskRoot+'\',[StringComparison]::Ordinal) -or $Intent.stageDirectory.StartsWith($Binding.roots.dataRoot,[StringComparison]::OrdinalIgnoreCase) -or $null -eq $j.next -or $Intent.manifestSha256 -cne $Metadata.package.payloadManifest.sha256 -or $j.next.current -cne $Intent.manifestSha256){throw 'N7.IntentApply: stage/manifest/next selection mismatch.'}
        $release=@($j.next.releases|Where-Object {$_.id -ceq $j.next.current})[0]
        if((Get-NLRecordHash $release.files) -cne (Get-NLRecordHash $Metadata.manifest.files)){throw 'N7.IntentApply: selected inventory differs from package metadata.'}
    }elseif($null -ne $Intent.stageDirectory -or $null -ne $Intent.manifestSha256 -or $null -ne $j.next -or $null -eq $j.previous){throw 'N7.IntentRemove: closed null/previous-state contract mismatch.'}
}
function Read-NativeLiveIntent {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$BindingPath,[Parameter(Mandatory=$true)][string]$BindingSha256,[Parameter(Mandatory=$true)][string]$IntentSha256,[Parameter(Mandatory=$true)][long]$StartTick,[Parameter(Mandatory=$true)][ValidateRange(1000,160000)][int]$DeadlineMilliseconds)
    Assert-NLHash $IntentSha256;$b=Read-NativeLiveBinding $BindingPath $BindingSha256 $StartTick $DeadlineMilliseconds
    if($BindingPath -cne ($b.roots.controlRoot+'\bindings\'+$BindingSha256+'.json')){throw 'N7.BindingLocation: canonical domain binding required.'}
    $meta=Read-NLDomainMetadata $b $StartTick $DeadlineMilliseconds;$path=$b.roots.controlRoot+'\intents\'+$IntentSha256+'.json';Assert-NLGraphPath $path $b.roots.controlRoot
    $intent=Read-NLJson $path $IntentSha256 $StartTick $DeadlineMilliseconds;Assert-NLIntent $intent $b $BindingSha256 $meta
    [pscustomobject]@{protocol='NativeIntentRead/1';intent=$intent;intentSha256=$IntentSha256;metadataValidated=$true;businessPrepared=$false;osAuthority=$false}
}
function Assert-NLFileFact($Fact) {
    if($Fact -isnot [pscustomobject] -or $null -eq $Fact.PSObject.Properties['state']){throw 'N7.FileFact: state required.'};Assert-NLString $Fact.state
    if($Fact.state -ceq 'Absent'){Assert-NLFields $Fact @('path','state')}elseif($Fact.state -ceq 'Present'){Assert-NLFields $Fact @('path','state','bytes','sha256');Assert-NLInteger $Fact.bytes;Assert-NLHash $Fact.sha256}else{throw 'N7.FileFact: unsupported state.'}
    Assert-NLPath $Fact.path;if($Fact.path.Length -gt 1024){throw 'N7.FileFact: path length bound.'}
}
function Assert-NLObservation($O) {
    Assert-NLFields $O @('protocol','bindingSha256','intentSha256','stable','volatile','anchors');Assert-NLString $O.protocol;Assert-NLHash $O.bindingSha256;Assert-NLHash $O.intentSha256 -Nullable
    if($O.protocol -cne 'LiveResourceObservation/2'){throw 'N7.Observation: distinct protocol2 required.'}
    Assert-NLFields $O.stable @('registry','menuPresent','links','files');Assert-NLFields $O.stable.registry @('state','values');Assert-NLString $O.stable.registry.state;Assert-NLArray $O.stable.registry.values
    if($O.stable.registry.state -cnotin @('Absent','Present') -or $O.stable.registry.values.Count -gt 8 -or ($O.stable.registry.state -ceq 'Absent' -and $O.stable.registry.values.Count)){throw 'N7.Observation: registry state/value bound.'}
    $seen=@{};foreach($v in $O.stable.registry.values){Assert-NLFields $v @('name','kind','value');Assert-NLString $v.name;Assert-NLString $v.kind;if($v.name -cnotin @('DisplayName','DisplayVersion','Publisher','InstallLocation','UninstallString','QuietUninstallString','NoModify','NoRepair') -or $seen.ContainsKey($v.name)){throw 'N7.Observation: duplicate/unknown registry value.'};$seen[$v.name]=$true;if($v.kind -ceq 'String'){Assert-NLString $v.value;if($v.value.Length -gt 8192){throw 'N7.Observation: string bound.'}}elseif($v.kind -ceq 'DWord'){Assert-NLInteger $v.value ([int]::MinValue) ([int]::MaxValue)}else{throw 'N7.Observation: registry kind.'}}
    if($O.stable.menuPresent -isnot [bool]){throw 'N7.Observation: Boolean menu fact required.'};Assert-NLArray $O.stable.links;Assert-NLArray $O.stable.files
    if($O.stable.links.Count -ne 2){throw 'N7.Observation: exact two link facts required.'}
    $paths=@{};foreach($link in $O.stable.links){Assert-NLFields $link @('file','target','arguments','workingDirectory');Assert-NLFileFact $link.file;if($paths.ContainsKey($link.file.path)){throw 'N7.Observation: duplicate link.'};$paths[$link.file.path]=$true
        if($null -eq $link.target){if($null -ne $link.arguments -or $null -ne $link.workingDirectory){throw 'N7.Observation: unknown link fields must all be null.'}}
        else{Assert-NLPath $link.target;Assert-NLPath $link.workingDirectory;Assert-NLString $link.arguments;if($link.arguments.Length -gt 8192 -or $link.file.state -cne 'Present'){throw 'N7.Observation: stored link field bound/state.'}}}
    $paths=@{};foreach($f in $O.stable.files){Assert-NLFileFact $f;if($paths.ContainsKey($f.path)){throw 'N7.Observation: duplicate file.'};$paths[$f.path]=$true}
    Assert-NLFields $O.volatile @('observedUtc','userSid','administratorToken','processId','elapsedMilliseconds');Assert-NLString $O.volatile.observedUtc;Assert-NLString $O.volatile.userSid;Assert-NLInteger $O.volatile.processId 1 ([int]::MaxValue);Assert-NLInteger $O.volatile.elapsedMilliseconds
    if($O.volatile.observedUtc.Length -gt 128 -or $O.volatile.userSid -cnotmatch '^S-1-5-21-[0-9]+-[0-9]+-[0-9]+-[0-9]+$' -or $O.volatile.administratorToken -isnot [bool]){throw 'N7.Observation: volatile scalar fields.'}
    Assert-NLFields $O.anchors @('pendingIntentSha256','committedReceiptSha256');Assert-NLHash $O.anchors.pendingIntentSha256 -Nullable;Assert-NLHash $O.anchors.committedReceiptSha256 -Nullable
}
function Copy-NLStableFileFact($Fact) {
    if($Fact.state -ceq 'Absent'){return [pscustomobject]@{path=$Fact.path;state=$Fact.state}}
    [pscustomobject]@{path=$Fact.path;state=$Fact.state;bytes=$Fact.bytes;sha256=$Fact.sha256}
}
function Get-NativeLiveStableDigest {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)]$Observation)
    Assert-NLObservation $Observation
    # Project stable fields only; property order is fixed and arrays sorted by bounded identity.
    $values=@($Observation.stable.registry.values|Sort-Object name -CaseSensitive|ForEach-Object {[pscustomobject]@{name=$_.name;kind=$_.kind;value=$_.value}})
    $links=@($Observation.stable.links|Sort-Object {$_.file.path} -CaseSensitive|ForEach-Object {[pscustomobject]@{file=(Copy-NLStableFileFact $_.file);target=$_.target;arguments=$_.arguments;workingDirectory=$_.workingDirectory}})
    $files=@($Observation.stable.files|Sort-Object path -CaseSensitive|ForEach-Object {Copy-NLStableFileFact $_})
    $stable=[pscustomobject]@{registry=[pscustomobject]@{state=$Observation.stable.registry.state;values=$values};menuPresent=$Observation.stable.menuPresent;links=$links;files=$files}
    $bytes=$script:NLUtf8.GetBytes(($stable|ConvertTo-Json -Depth 30));if($bytes.Length -gt 2097152){throw 'N7.JsonSize: stable facts byte bound.'};Get-NLHash $bytes
}
function Assert-NLHistoryStep($Seen,[string]$Hash,[int]$Depth) {
    Assert-NLHash $Hash;if($Depth -gt 16){throw 'N7.HistoryDepth: metadata chain exceeds16.'};if(-not $Seen.Add($Hash)){throw 'N7.HistoryCycle: repeated receipt hash.'}
}
function Read-NLReceiptChain($Candidate,[string]$Selected,[long]$Start,[int]$Deadline) {
    $c=$Candidate.roots.controlRoot;$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$nodes=[Collections.Generic.List[object]]::new();$hash=$Selected;$pin=$null
    while($null -ne $hash){
        Assert-NLDeadline $Start $Deadline;Assert-NLHistoryStep $seen $hash ($nodes.Count+1)
        $path=$c+'\receipts\'+$hash+'.json';Assert-NLGraphPath $path $c;$expected=-1L;if($null -ne $pin){Assert-NLPin $pin;if($pin.path -cne $path -or $pin.sha256 -cne $hash){throw 'N7.ReceiptPath: prior pin must name canonical receipt.'};$expected=$pin.bytes}
        $r=Read-NLJson $path $hash $Start $Deadline 2097152 $expected
        if($r -isnot [pscustomobject] -or $null -eq $r.PSObject.Properties['bindingSha256']){throw 'N7.ReceiptBinding: binding hash required.'};Assert-NLHash $r.bindingSha256
        $bindingPath=$c+'\bindings\'+$r.bindingSha256+'.json';Assert-NLGraphPath $bindingPath $c;$b=Read-NLJson $bindingPath $r.bindingSha256 $Start $Deadline 32768;Assert-NLBinding $b
        if((Get-NLRecordHash $b.host) -cne (Get-NLRecordHash $Candidate.host)){throw 'N7.ReceiptHost: candidate/current machine/SID/host mismatch.'}
        Assert-NLReceipt $r $b $r.bindingSha256
        if($r.operation -cne 'Apply'){throw 'N7.ApplyAuthorityOnly: removal authority not implemented.'}
        $prior=$b.priorReceipt;if($null -eq $prior){if($null -ne $r.previousReceiptSha256){throw 'N7.ReceiptPrior: missing binding prior pointer.'}}elseif($prior.sha256 -cne $r.previousReceiptSha256 -or $prior.path -cne ($c+'\receipts\'+$prior.sha256+'.json')){throw 'N7.ReceiptPrior: immutable binding prior mismatch.'}
        $meta=Read-NLDomainMetadata $b $Start $Deadline
        $ip=$c+'\intents\'+$r.intentSha256+'.json';Assert-NLGraphPath $ip $c;$intent=Read-NLJson $ip $r.intentSha256 $Start $Deadline;Assert-NLIntent $intent $b $r.bindingSha256 $meta
        foreach($n in @('transactionId','operation','previousReceiptSha256','manifestSha256','journalSha256','previousStateSha256','nextStateSha256','shellSpecSha256')){if($r.$n -cne $intent.$n){throw ('N7.ReceiptIntent: '+$n+' mismatch.')}}
        if($r.selection -cne $intent.journal.next.current){throw 'N7.ReceiptSelection: selected manifest mismatch.'}
        Assert-NLPin $r.observation;if($r.observation.path -cne ($c+'\observations\'+$r.observation.sha256+'.json')){throw 'N7.ObservationPath: canonical observation pin required.'}
        $o=Read-NLJson $r.observation.path $r.observation.sha256 $Start $Deadline 2097152 $r.observation.bytes;Assert-NLObservation $o
        if($o.bindingSha256 -cne $r.bindingSha256 -or $o.intentSha256 -cne $r.intentSha256 -or $o.volatile.userSid -cne $b.host.userSid -or (Get-NativeLiveStableDigest $o) -cne $r.stableDigest){throw 'N7.ReceiptObservation: observation pins/digest/SID mismatch.'}
        if(($null -ne $o.anchors.pendingIntentSha256 -and $o.anchors.pendingIntentSha256 -cne $r.intentSha256) -or ($null -ne $o.anchors.committedReceiptSha256 -and $o.anchors.committedReceiptSha256 -cne $r.previousReceiptSha256)){throw 'N7.ObservationHistory: historical anchor relationship mismatch.'}
        $expectedShell=Resolve-NLShellTemplate $b $meta.spec $bindingPath $r.bindingSha256
        $allowed=@($expectedShell.files.path)+$expectedShell.uninstallerPaths;$allowedLinks=@($expectedShell.menu.links|ForEach-Object {$expectedShell.menu.path+'\'+$_.name})
        if($o.stable.files.Count -ne $allowed.Count){throw 'N7.ObservationPaths: receipt file coverage mismatch.'};foreach($f in $o.stable.files){if($f.path -cnotin $allowed){throw 'N7.ObservationPaths: foreign receipt file.'}};foreach($link in $o.stable.links){if($link.file.path -cnotin $allowedLinks){throw 'N7.ObservationPaths: foreign receipt link.'}}
        $nodes.Add([pscustomobject]@{sha256=$hash;receipt=$r;binding=$b;intent=$intent;observation=$o;metadata=$meta;expectedShell=$expectedShell;receiptBytes=([IO.FileInfo]::new($path)).Length})
        $hash=$r.previousReceiptSha256;$pin=$prior
    }
    for($i=0;$i -lt $nodes.Count;$i++){
        $n=$nodes[$i];if($i+1 -lt $nodes.Count){$p=$nodes[$i+1];if($n.receipt.previousStateSha256 -cne $p.receipt.nextStateSha256){throw 'N7.HistoryState: prior committed state mismatch.'};if($null -ne $n.receipt.trustedPreviousSetup){foreach($field in @('path','bytes','sha256')){if($n.receipt.trustedPreviousSetup.$field -cne $p.metadata.package.setup.$field){throw 'N7.HistorySetup: prior setup metadata mismatch.'}}}}elseif($null -ne $n.receipt.previousStateSha256 -or $null -ne $n.receipt.trustedPreviousSetup){throw 'N7.HistoryRoot: unanchored prior state/setup.'}
    }
    ,$nodes.ToArray()
}
function Read-NLRegistry([long]$Start,[int]$Deadline) {
    Assert-NLDeadline $Start $Deadline;$base=$null;$key=$null
    try{
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
        $key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1',$false)
        if($null -eq $key){return [pscustomobject]@{state='Absent';values=@();pendingIntentSha256=$null;committedReceiptSha256=$null}}
        $values=[Collections.Generic.List[object]]::new();$pending=$null;$committed=$null;$missing=[object]::new()
        foreach($name in @('DisplayName','DisplayVersion','Publisher','InstallLocation','UninstallString','QuietUninstallString','NoModify','NoRepair','NativePendingIntentSha256','NativeCommittedReceiptSha256')){
            Assert-NLDeadline $Start $Deadline;$value=$key.GetValue($name,$missing,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames);if([object]::ReferenceEquals($value,$missing)){continue};$kind=$key.GetValueKind($name).ToString()
            if($name -cin @('NoModify','NoRepair')){if($kind -cne 'DWord'){throw 'N7.RegistryMalformed: DWord expected.'};Assert-NLInteger $value ([int]::MinValue) ([int]::MaxValue)}else{if($kind -cne 'String'){throw 'N7.RegistryMalformed: REG_SZ expected.'};Assert-NLString $value}
            if($name -ceq 'NativePendingIntentSha256'){Assert-NLHash $value;$pending=$value}elseif($name -ceq 'NativeCommittedReceiptSha256'){Assert-NLHash $value;$committed=$value}else{$values.Add([pscustomobject]@{name=$name;kind=$kind;value=$value})}
        }
        [pscustomobject]@{state='Present';values=$values.ToArray();pendingIntentSha256=$pending;committedReceiptSha256=$committed}
    }finally{if($null -ne $key){$key.Dispose()};if($null -ne $base){$base.Dispose()}}
}
function Get-NLReadError($Record) {
    $base=$Record.Exception.GetBaseException();$message=$Record.Exception.Message;$status='ProbeError'
    if($base -is [UnauthorizedAccessException] -or $base -is [Security.SecurityException]){$status='Unauthorized'}
    elseif($base -is [IO.FileNotFoundException] -or $base -is [IO.DirectoryNotFoundException]){$status='Mismatch'}
    elseif($message -match '^N7\.(Fields|String|Integer|Hash|Path:|Object|Json|Duplicate|Records|Observation:|FileFact|RegistryMalformed)'){$status='Malformed'}
    elseif($message -match '^N7\.(PinMismatch|Receipt|Intent|DomainState|History|ObservationPath|ObservationHistory|ObservationPaths|Package|Source|Graph|Launch|Shell|Roots|Host|BindingLocation|Candidate|ApplyAuthority)'){$status='Mismatch'}
    [pscustomobject]@{status=$status;type=$base.GetType().FullName;message=$message;id=[string]$Record.FullyQualifiedErrorId}
}
function Get-NativeLiveAuthority {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$BindingPath,[Parameter(Mandatory=$true)][string]$BindingSha256,[Parameter(Mandatory=$true)][long]$StartTick,[Parameter(Mandatory=$true)][ValidateRange(1000,160000)][int]$DeadlineMilliseconds)
    $b=Read-NativeLiveBinding $BindingPath $BindingSha256 $StartTick $DeadlineMilliseconds
    if($BindingPath -cne ($b.roots.controlRoot+'\bindings\'+$BindingSha256+'.json')){throw 'N7.BindingLocation: canonical domain binding required.'}
    $result=[pscustomobject]@{protocol='NativeLiveAuthority/1';status='ProbeError';kind='Unknown';selectedReceiptSha256=$null;verifiedReceiptSha256=$null;pendingIntentSha256=$null;relationship='NotAssessed';chainValidated=$false;candidateRelationshipValidated=$false;anchorRechecked=$false;selected=$null;chainDepth=0;registry=$null;error=$null;resourceHealth='NotAssessed';metadataOnly=$true;mutationPermission=$false}
    try{
        $r=Read-NLRegistry $StartTick $DeadlineMilliseconds;$result.registry=$r;$result.pendingIntentSha256=$r.pendingIntentSha256
        if($r.state -ceq 'Absent'){$result.status='Absent';$result.kind='Absent';return $result}
        if($r.state -cne 'Present'){throw 'N7.RegistryMalformed: unknown registry read state.'}
        if($null -eq $r.committedReceiptSha256){$result.status='UnknownOwnership';return $result}
        Assert-NLHash $r.committedReceiptSha256;$result.selectedReceiptSha256=$r.committedReceiptSha256
        $nodes=Read-NLReceiptChain $b $r.committedReceiptSha256 $StartTick $DeadlineMilliseconds;$result.selected=$nodes[0];$result.chainDepth=$nodes.Count;$result.chainValidated=$true;$result.verifiedReceiptSha256=$r.committedReceiptSha256;$result.kind='Apply'
        $again=Read-NLRegistry $StartTick $DeadlineMilliseconds
        if($again.state -cne 'Present' -or $again.committedReceiptSha256 -cne $r.committedReceiptSha256){$result.kind='Unknown';throw 'N7.CandidateAnchorChanged: registry selection changed during metadata read.'};$result.anchorRechecked=$true
        if($nodes[0].receipt.bindingSha256 -ceq $BindingSha256){$result.relationship='Current';$result.candidateRelationshipValidated=$true}
        elseif($null -ne $b.priorReceipt -and $b.priorReceipt.sha256 -ceq $r.committedReceiptSha256 -and $b.priorReceipt.path -ceq ($b.roots.controlRoot+'\receipts\'+$r.committedReceiptSha256+'.json') -and $b.priorReceipt.bytes -eq $nodes[0].receiptBytes){$result.relationship='Prior';$result.candidateRelationshipValidated=$true}
        else{$result.relationship='Unrelated';throw 'N7.CandidateRelationship: actual selected receipt is neither candidate current nor its exact prior pin.'}
        $result.status='Apply'
    }catch{$result.error=Get-NLReadError $_;$result.status=$result.error.status}
    $result
}
function Get-NLResourceFact([string]$Path,[long]$Limit,[long]$Start,[int]$Deadline) {
    Assert-NLDeadline $Start $Deadline;Assert-NLNoReparse $Path;$stream=$null;$sha=$null
    try{
        $stream=[IO.File]::Open($Path,'Open','Read','Read');if($stream.Length -gt $Limit){throw 'N7.ResourceBound: concrete resource exceeds probe bound.'};$size=$stream.Length;$sha=[Security.Cryptography.SHA256]::Create();$buffer=New-Object byte[] 65536;$total=0L
        while(($n=$stream.Read($buffer,0,$buffer.Length)) -gt 0){Assert-NLDeadline $Start $Deadline;$total+=$n;if($total -gt $Limit){throw 'N7.ResourceBound: resource grew beyond bound.'};[void]$sha.TransformBlock($buffer,0,$n,$buffer,0)};[void]$sha.TransformFinalBlock($buffer,0,0)
        if($total -ne $size -or $stream.Length -ne $size){throw 'N7.ResourceChanged: concrete resource changed during read.'}
        [pscustomobject]@{path=$Path;state='Present';bytes=[long]$total;sha256=([BitConverter]::ToString($sha.Hash).Replace('-','').ToLowerInvariant())}
    }catch [IO.FileNotFoundException]{[pscustomobject]@{path=$Path;state='Absent'}}catch [IO.DirectoryNotFoundException]{[pscustomobject]@{path=$Path;state='Absent'}}finally{if($null -ne $sha){$sha.Dispose()};if($null -ne $stream){$stream.Dispose()}}
}
function Read-NLKnownLink($Fact,$Trusted,[long]$Start,[int]$Deadline) {
    $result=[pscustomobject]@{file=$Fact;target=$null;arguments=$null;workingDirectory=$null}
    if($Fact.state -cne 'Present' -or $null -eq $Trusted -or $Trusted.file.state -cne 'Present' -or $Trusted.file.path -cne $Fact.path -or $Trusted.file.bytes -ne $Fact.bytes -or $Trusted.file.sha256 -cne $Fact.sha256){return $result}
    $stream=$null;$shell=$null;$link=$null
    try{
        $stream=[IO.File]::Open($Fact.path,'Open','Read','Read');if($stream.Length -gt 1048576){throw 'N7.ResourceBound: shortcut bound.'}
        # Hold a no-write/no-delete read handle across raw verification and stored-field reading.
        $actual=Get-NLResourceFact $Fact.path 1048576 $Start $Deadline
        if($actual.sha256 -cne $Fact.sha256 -or $actual.bytes -ne $Fact.bytes){throw 'N7.ResourceChanged: shortcut changed before stored-field read.'}
        $shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($Fact.path)
        $result.target=$link.TargetPath;$result.arguments=$link.Arguments;$result.workingDirectory=$link.WorkingDirectory
        Assert-NLDeadline $Start $Deadline;$result
    }finally{if($null -ne $link){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)};if($null -ne $shell){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)};if($null -ne $stream){$stream.Dispose()}}
}
function Get-NativeLiveObservation {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$BindingPath,[Parameter(Mandatory=$true)][string]$BindingSha256,[string]$IntentSha256,[string]$ExpectedReceiptSha256,[Parameter(Mandatory=$true)][long]$StartTick,[Parameter(Mandatory=$true)][ValidateRange(1000,160000)][int]$DeadlineMilliseconds)
    $b=Read-NativeLiveBinding $BindingPath $BindingSha256 $StartTick $DeadlineMilliseconds
    foreach($hash in @($IntentSha256,$ExpectedReceiptSha256)){if($hash){Assert-NLHash $hash}}
    $authority=Get-NativeLiveAuthority $BindingPath $BindingSha256 $StartTick $DeadlineMilliseconds
    $result=[pscustomobject]@{protocol='NativeObservationAssessment/1';status=$authority.status;authority=$authority;observation=$null;stableDigest=$null;mismatches=@();error=$authority.error;freshLinkProvenance='Unavailable';payloadHealth='NotAssessed';mutationPermission=$false}
    if($authority.status -cin @('Unauthorized','ProbeError','Malformed') -or ($null -ne $authority.selectedReceiptSha256 -and -not $authority.chainValidated)){return $result}
    try{
        if($IntentSha256){$null=Read-NativeLiveIntent $BindingPath $BindingSha256 $IntentSha256 $StartTick $DeadlineMilliseconds}
        if($authority.chainValidated){$expected=$authority.selected.expectedShell;$stored=$authority.selected.observation}else{$sources=Read-NLGraphObject $b.sources $b.roots.controlRoot $StartTick $DeadlineMilliseconds;Assert-NLSources $sources;$spec=Read-NLGraphObject $b.shellSpec $b.roots.controlRoot $StartTick $DeadlineMilliseconds;Assert-NLShellSpec $spec $b $sources;$expected=Resolve-NLShellTemplate $b $spec ($b.roots.controlRoot+'\bindings\'+$BindingSha256+'.json') $BindingSha256;$stored=$null}
        $registry=Read-NLRegistry $StartTick $DeadlineMilliseconds
        if($registry.committedReceiptSha256 -cne $authority.selectedReceiptSha256){throw 'N7.CandidateAnchorChanged: registry anchor changed before resource observation.'}
        Assert-NLNoReparse $expected.menu.path;$menuPresent=$true;try{[void][IO.File]::GetAttributes($expected.menu.path)}catch [IO.FileNotFoundException]{$menuPresent=$false}catch [IO.DirectoryNotFoundException]{$menuPresent=$false}
        $links=@();foreach($link in $expected.menu.links){$path=$expected.menu.path+'\'+$link.name;$fact=Get-NLResourceFact $path 1048576 $StartTick $DeadlineMilliseconds;$trusted=$null;if($null -ne $stored){$boundLinks=@($stored.stable.links|Where-Object {$_.file.path -ceq $path});if($boundLinks.Count){$trusted=$boundLinks[0]}};$links+=,(Read-NLKnownLink $fact $trusted $StartTick $DeadlineMilliseconds)}
        $paths=@($expected.files.path)+$expected.uninstallerPaths;if($paths.Count -gt 4096){throw 'N7.Records: observed file count bound.'};$files=@();foreach($path in $paths){$files+=,(Get-NLResourceFact $path 67108864 $StartTick $DeadlineMilliseconds)}
        $id=[Security.Principal.WindowsIdentity]::GetCurrent();try{$sid=$id.User.Value;$admin=[Security.Principal.WindowsPrincipal]::new($id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}finally{$id.Dispose()}
        $o=[pscustomobject]@{protocol='LiveResourceObservation/2';bindingSha256=$BindingSha256;intentSha256=$(if($IntentSha256){$IntentSha256}else{$null});stable=[pscustomobject]@{registry=[pscustomobject]@{state=$registry.state;values=$registry.values};menuPresent=$menuPresent;links=$links;files=$files};volatile=[pscustomobject]@{observedUtc=[DateTime]::UtcNow.ToString('o');userSid=$sid;administratorToken=$admin;processId=$PID;elapsedMilliseconds=(((Get-NativeTick)-$StartTick+4294967296L)%4294967296L)};anchors=[pscustomobject]@{pendingIntentSha256=$registry.pendingIntentSha256;committedReceiptSha256=$registry.committedReceiptSha256}}
        Assert-NLObservation $o;$result.observation=$o;$result.stableDigest=Get-NativeLiveStableDigest $o
        $bad=[Collections.Generic.List[string]]::new()
        if($authority.chainValidated){
            foreach($value in $expected.registry.values){$actual=@($registry.values|Where-Object {$_.name -ceq $value.name});if($actual.Count -ne 1 -or $actual[0].kind -cne $value.kind -or $actual[0].value -cne $value.value){$bad.Add('registry:'+ $value.name)}}
            foreach($link in $links){$spec=@($expected.menu.links|Where-Object {($expected.menu.path+'\'+$_.name) -ceq $link.file.path})[0];if($link.file.state -cne 'Present' -or $link.target -cne $spec.target -or $link.arguments -cne $spec.arguments -or $link.workingDirectory -cne $spec.workingDirectory){$bad.Add('link:'+ $link.file.path)}}
            foreach($file in $files){
                $prior=@($stored.stable.files|Where-Object {$_.path -ceq $file.path})
                $declared=@($expected.files|Where-Object {$_.path -ceq $file.path})
                $fileMismatch=$file.state -cne 'Present' -or $prior.Count -ne 1 -or $prior[0].state -cne 'Present' -or $file.sha256 -cne $prior[0].sha256 -or $file.bytes -ne $prior[0].bytes
                if($declared.Count -eq 1 -and ($file.state -cne 'Present' -or $file.sha256 -cne $declared[0].sha256 -or $file.bytes -ne $declared[0].bytes)){$fileMismatch=$true}
                if($fileMismatch){$bad.Add('file:'+ $file.path)}
            }
            if($ExpectedReceiptSha256 -and $ExpectedReceiptSha256 -cne $authority.verifiedReceiptSha256){$bad.Add('expectedReceipt')};$result.status=if($bad.Count -or $authority.status -ceq 'Mismatch'){'Mismatch'}else{'ObservedApply'}
        }else{$present=$registry.state -ceq 'Present' -or $menuPresent -or @($files|Where-Object {$_.state -ceq 'Present'}).Count -gt 0 -or @($links|Where-Object {$_.file.state -ceq 'Present'}).Count -gt 0;$result.status=if($present){'UnknownOwnership'}else{'Absent'}}
        $result.mismatches=$bad.ToArray()
    }catch{$result.error=Get-NLReadError $_;$result.status=$result.error.status}
    $result
}
# The genuine parent supplies a module-bound assertion over retained settled resources.
# The old observational factory does not grant this capability.
function Invoke-NLFreshFact($Lease,$Graph,$Intent,$Stage,$Path,$Read,[long]$Start,[int]$Deadline) {
    if($Lease -isnot [scriptblock] -or $Read -isnot [scriptblock]){throw 'N7.FreshAssertion: in-process closures required.'}
    $null=& $Lease $Graph $Intent $Stage $Path
    Assert-NLDeadline $Start $Deadline
    $value=& $Read
    $null=& $Lease $Graph $Intent $Stage $Path
    Assert-NLDeadline $Start $Deadline
    $value
}
function Read-NLFreshStoredFields([string]$Path) {
    $shell=$null;$shortcut=$null
    try{
        $shell=New-Object -ComObject WScript.Shell
        $shortcut=$shell.CreateShortcut($Path)
        [pscustomobject]@{target=$shortcut.TargetPath;arguments=$shortcut.Arguments;workingDirectory=$shortcut.WorkingDirectory}
    }finally{
        if($null -ne $shortcut){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)}
        if($null -ne $shell){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
    }
}
function Read-NLFreshLink($Spec,[string]$Path,$Lease,$Graph,$Intent,[long]$Start,[int]$Deadline) {
    $before=Invoke-NLFreshFact $Lease $Graph $Intent 'LinkRaw' $Path {Get-NLResourceFact $Path 1048576 $Start $Deadline} $Start $Deadline
    if($before.state -cne 'Present'){throw 'N7.FreshLinkAbsent: approved link required.'}
    $fields=Invoke-NLFreshFact $Lease $Graph $Intent 'LinkFields' $Path {Assert-NLNoReparse $Path;Read-NLFreshStoredFields $Path} $Start $Deadline
    Assert-NLFields $fields @('target','arguments','workingDirectory')
    foreach($name in @('target','arguments','workingDirectory')){Assert-NLString $fields.$name}
    $after=Invoke-NLFreshFact $Lease $Graph $Intent 'LinkRecheck' $Path {Get-NLResourceFact $Path 1048576 $Start $Deadline} $Start $Deadline
    if($after.state -cne 'Present' -or $after.path -cne $before.path -or $after.bytes -ne $before.bytes -or $after.sha256 -cne $before.sha256){throw 'N7.FreshLinkChanged: raw bytes changed during stored-field read.'}
    if($fields.target -cne $Spec.target -or $fields.arguments -cne $Spec.arguments -or $fields.workingDirectory -cne $Spec.workingDirectory){throw 'N7.FreshLinkFields: stored fields differ from approved declaration.'}
    [pscustomobject]@{file=$after;target=$fields.target;arguments=$fields.arguments;workingDirectory=$fields.workingDirectory}
}
function Assert-NLFreshDeclaration($Fact,$Pin) {
    Assert-NLPin $Pin
    if($Fact.path -cne $Pin.path -or $Fact.state -cne 'Present' -or $Fact.bytes -ne $Pin.bytes -or $Fact.sha256 -cne $Pin.sha256){throw 'N7.FreshDeclaration: file differs from independent declaration pin.'}
}
function Get-NLFreshObservation($Graph,$IntentSha256,$AssertLease,$OwnerModule,[long]$Start,[int]$Deadline,[ValidateSet('Apply','Remove')][string]$Operation='Apply') {
    if($AssertLease -isnot [scriptblock] -or $OwnerModule -isnot [Management.Automation.PSModuleInfo] -or -not [Object]::ReferenceEquals($AssertLease.Module,$OwnerModule) -or $OwnerModule.Path -cne (Join-Path $PSScriptRoot 'NativeInstallCoordinator.psm1')){throw 'N7.FreshAssertion: exact owner-module closure required.'}
    $loaded=@(Get-Module -All | Where-Object {[Object]::ReferenceEquals($_,$OwnerModule)})
    if($loaded.Count -ne 1){throw 'N7.FreshAssertion: owner module reference no longer imported.'}
    $null=& $AssertLease $Graph $IntentSha256 'Begin' $null
    Assert-NLDeadline $Start $Deadline
    if($Graph -isnot [System.Management.Automation.PSCustomObject]){throw 'N7.FreshGraph: raw registered graph required.'}
    Assert-NLHash $IntentSha256;Assert-NLHash $Graph.bindingSha256
    Assert-NLShellSpec $Graph.shellSpec $Graph.binding $Graph.sources
    $expected=Resolve-NLShellTemplate $Graph.binding $Graph.shellSpec ($Graph.binding.roots.controlRoot+'\bindings\'+$Graph.bindingSha256+'.json') $Graph.bindingSha256
    if((Get-NLRecordHash $expected) -cne (Get-NLRecordHash $Graph.expectedShell)){throw 'N7.FreshGraph: rendered shell declaration mismatch.'}
    $source=@($Graph.sources.files | Where-Object {$_.name -ceq 'NativeInstallCoordinator.psm1'})
    if($source.Count -ne 1){throw 'N7.FreshOwnerSource: owner source pin missing.'}
    $actual=Invoke-NLFreshFact $AssertLease $Graph $IntentSha256 'OwnerSource' $OwnerModule.Path {Get-NLFilePin $OwnerModule.Path $Start $Deadline} $Start $Deadline
    if($actual.bytes -ne $source[0].bytes -or $actual.sha256 -cne $source[0].sha256){throw 'N7.FreshOwnerSource: imported owner source pin differs.'}
    $registry=Invoke-NLFreshFact $AssertLease $Graph $IntentSha256 'Registry' $expected.registry.key {Read-NLRegistry $Start $Deadline} $Start $Deadline
    $menu=Invoke-NLFreshFact $AssertLease $Graph $IntentSha256 'Menu' $expected.menu.path {
        Assert-NLNoReparse $expected.menu.path
        try{$attributes=[IO.File]::GetAttributes($expected.menu.path)}catch [IO.FileNotFoundException]{return $false}catch [IO.DirectoryNotFoundException]{return $false}
        if(($attributes -band [IO.FileAttributes]::Directory) -eq 0){throw 'N7.FreshMenuType: directory required.'}
        $true
    } $Start $Deadline
    $links=@();foreach($spec in $expected.menu.links){
        $path=$expected.menu.path+'\'+$spec.name
        if($Operation -ceq 'Apply'){$links+=,(Read-NLFreshLink $spec $path $AssertLease $Graph $IntentSha256 $Start $Deadline)}
        else{
            $fact=Invoke-NLFreshFact $AssertLease $Graph $IntentSha256 'LinkRaw' $path {Get-NLResourceFact $path 1048576 $Start $Deadline} $Start $Deadline
            if($fact.state -cne 'Absent'){throw 'N7.ShellRepairRequired: removed shortcut remains.'}
            $links+=,[pscustomobject]@{file=$fact;target=$null;arguments=$null;workingDirectory=$null}
        }
    }
    $paths=@($expected.files.path)+$expected.uninstallerPaths
    if($paths.Count -gt 4096){throw 'N7.Records: observed file count bound.'}
    $files=@();foreach($path in $paths){
        $fact=Invoke-NLFreshFact $AssertLease $Graph $IntentSha256 'File' $path {Get-NLResourceFact $path 67108864 $Start $Deadline} $Start $Deadline
        if($Operation -ceq 'Apply'){
            foreach($pin in @($expected.files | Where-Object {$_.path -ceq $path})){Assert-NLFreshDeclaration $fact $pin}
            if($fact.state -cne 'Present'){throw 'N7.ShellRepairRequired: genuine final uninstaller pair required.'}
        }elseif($fact.state -cne 'Absent'){throw 'N7.ShellRepairRequired: declared shell/uninstaller file remains.'}
        $files+=,$fact
    }
    if($Operation -ceq 'Apply'){
        if($registry.state -cne 'Present' -or -not $menu){throw 'N7.ShellRepairRequired: registration/menu absent.'}
        foreach($v in $expected.registry.values){$actual=@($registry.values|Where-Object {$_.name -ceq $v.name});if($actual.Count -ne 1 -or $actual[0].kind -cne $v.kind -or $actual[0].value -cne $v.value){throw 'N7.ShellRepairRequired: ordinary registration values differ.'}}
    }elseif($registry.state -cne 'Absent' -or $menu){throw 'N7.ShellRepairRequired: removed registration/menu remains.'}
    $identity=Invoke-NLFreshFact $AssertLease $Graph $IntentSha256 'Identity' $null {
        $id=[Security.Principal.WindowsIdentity]::GetCurrent()
        try{[pscustomobject]@{sid=$id.User.Value;admin=[Security.Principal.WindowsPrincipal]::new($id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}}finally{$id.Dispose()}
    } $Start $Deadline
    $observation=[pscustomobject]@{protocol='LiveResourceObservation/2';bindingSha256=$Graph.bindingSha256;intentSha256=$IntentSha256;stable=[pscustomobject]@{registry=[pscustomobject]@{state=$registry.state;values=$registry.values};menuPresent=$menu;links=$links;files=$files};volatile=[pscustomobject]@{observedUtc=[DateTime]::UtcNow.ToString('o');userSid=$identity.sid;administratorToken=$identity.admin;processId=$PID;elapsedMilliseconds=(((Get-NativeTick)-$Start+4294967296L)%4294967296L)};anchors=[pscustomobject]@{pendingIntentSha256=$registry.pendingIntentSha256;committedReceiptSha256=$registry.committedReceiptSha256}}
    Assert-NLObservation $observation
    $null=& $AssertLease $Graph $IntentSha256 'Return' $null
    Assert-NLDeadline $Start $Deadline
    $observation
}
Export-ModuleMember -Function Read-NativeLiveBinding,Read-NativeLiveGraph,Read-NativeLiveIntent,Get-NativeLiveAuthority,Get-NativeLiveObservation,Get-NativeLiveStableDigest
