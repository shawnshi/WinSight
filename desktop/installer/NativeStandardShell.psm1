Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:NSRoot = '__WINSIGHT_PRIVATE_REPOSITORY_ROOT__'
if ($script:NSRoot.StartsWith('__')) { throw 'N6.UnconfiguredPreviewSource: materialize and re-pin a private copy before importing standard-shell operations.' }
$script:NSRun = $script:NSRoot + '\.dev\native-migration\release-acceptance\standard-install-checks\run-01'
$script:NSKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1'
$script:NSUtf8 = New-Object Text.UTF8Encoding($false, $true)

function Get-NSTick { return [long]([Diagnostics.Stopwatch]::GetTimestamp() * 1000.0 / [Diagnostics.Stopwatch]::Frequency) }
function Assert-NSJsonTokens([string]$Text, [long]$Deadline) {
    $tokens = New-Object Collections.Generic.List[string]
    $position=0
    while($position -lt $Text.Length) {
        Assert-NSDeadline $Deadline
        $m=[regex]::Match($Text.Substring($position),'\A\s*("(?:[^"\\\x00-\x1f]|\\["\\/bfnrt]|\\u[0-9a-fA-F]{4})*"|[{}\[\]:,]|true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)')
        if(-not $m.Success){if($Text.Substring($position).Trim().Length -eq 0){break};throw 'InvalidJsonToken'}
        $tokens.Add($m.Groups[1].Value);$position+=$m.Length
    }
    $cursor=@{i=0}
    function Read-NSValue($T,$C,[int]$Depth) {
        if($Depth -gt 16 -or $C.i -ge $T.Count){throw 'JsonDepthOrSyntax'}
        $token=$T[$C.i];$C.i++
        if($token -eq '{') {
            $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            if($T[$C.i] -eq '}'){$C.i++;return}
            while($true){
                $key=$T[$C.i];$C.i++
                if($key -notmatch '^"[^\\]*"$' -or -not $seen.Add($key)){throw 'DuplicateOrEscapedProperty'}
                if($T[$C.i] -ne ':'){throw 'InvalidJsonSyntax'};$C.i++
                Read-NSValue $T $C ($Depth+1)
                $end=$T[$C.i];$C.i++
                if($end -eq '}'){break};if($end -ne ','){throw 'InvalidJsonSyntax'}
            }
        } elseif($token -eq '['){
            if($T[$C.i] -eq ']'){$C.i++;return}
            while($true){Read-NSValue $T $C ($Depth+1);$end=$T[$C.i];$C.i++;if($end -eq ']'){break};if($end -ne ','){throw 'InvalidJsonSyntax'}}
        } elseif($token -in @('}',']',':',',')){throw 'InvalidJsonSyntax'}
    }
    Read-NSValue $tokens $cursor 0
    if($cursor.i -ne $tokens.Count){throw 'TrailingJson'}
}
function Assert-NSFields($Object, [string[]]$Names) {
    if ($null -eq $Object -or $Object -isnot [pscustomobject]) { throw 'InvalidObject' }
    $actual = @($Object.PSObject.Properties.Name)
    if (@(Compare-Object $actual $Names -CaseSensitive).Count) { throw 'UnknownOrMissingField' }
}
function Assert-NSStringFields($Object, [string[]]$Names) {
    # PowerShell collection comparisons filter elements, so equality alone cannot
    # establish a scalar JSON identity pin (NM-N6-RO-01).
    foreach ($name in $Names) {
        if ($Object.$name -isnot [string]) { throw ('InvalidStringField:' + $name) }
    }
}
function Assert-NSHash($Hash) {
    if ($Hash -isnot [string] -or $Hash -cnotmatch '^[a-f0-9]{64}$') { throw 'InvalidSHA256' }
}
function Assert-NSPath($Path, [string]$Expected) {
    if ($Path -isnot [string] -or $Path -cne $Expected) { throw 'ContextPathMismatch' }
}
function Assert-NSDeadline([long]$Deadline) {
    if ((Get-NSTick) -ge $Deadline) { throw 'ProbeDeadlineExceeded' }
}
function Assert-NSNoReparse([string]$Path) {
    $p = $Path
    while ($p) {
        if ([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)) {
            if (([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ReparsePointRejected' }
        }
        $p = [IO.Path]::GetDirectoryName($p)
    }
}
function Get-NSFile([string]$Path, [long]$Limit, [long]$Deadline) {
    Assert-NSDeadline $Deadline
    Assert-NSNoReparse $Path
    $stream = $null; $sha = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -gt $Limit) { throw 'FileSizeBoundExceeded' }
        $size = $stream.Length; $sha = [Security.Cryptography.SHA256]::Create()
        $buffer = New-Object byte[] 65536
        $total = 0L
        while (($n = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            Assert-NSDeadline $Deadline
            $total += $n
            if ($total -gt $Limit) { throw 'FileSizeBoundExceeded' }
            [void]$sha.TransformBlock($buffer, 0, $n, $buffer, 0)
        }
        [void]$sha.TransformFinalBlock($buffer, 0, 0)
        if ($total -ne $size -or $stream.Length -ne $size) { throw 'FileChangedDuringProbe' }
        return [ordered]@{ path=$Path; state='Present'; bytes=$size; sha256=([BitConverter]::ToString($sha.Hash).Replace('-','').ToLowerInvariant()) }
    } catch [IO.FileNotFoundException] { return [ordered]@{path=$Path;state='Absent'}
    } catch [IO.DirectoryNotFoundException] { return [ordered]@{path=$Path;state='Absent'}
    } finally {
        if ($stream) { $stream.Dispose() }; if ($sha) { $sha.Dispose() }
    }
}
function Read-NSJson([string]$Path, [string]$Hash, [long]$Deadline) {
    Assert-NSHash $Hash
    $file = Get-NSFile $Path 1048576 $Deadline
    if ($file.state -ne 'Present' -or $file.sha256 -cne $Hash) { throw 'PinnedJsonMismatch' }
    # Hash and parse the same exclusive-read bytes, not a second unpinned pathname read.
    $stream = [IO.File]::Open($Path,'Open','Read','Read')
    try {
        if ($stream.Length -gt 1048576) { throw 'FileSizeBoundExceeded' }
        $bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            Assert-NSDeadline $Deadline
            $n = $stream.Read($bytes,$offset,$bytes.Length-$offset)
            if ($n -eq 0) { throw 'ShortRead' }; $offset += $n
        }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
        if ($actual -cne $Hash) { throw 'PinnedJsonMismatch' }
        $text = $script:NSUtf8.GetString($bytes)
        Assert-NSJsonTokens $text $Deadline
        return ($text | ConvertFrom-Json -ErrorAction Stop)
    } finally { $stream.Dispose() }
}
function Get-NSToken {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = New-Object Security.Principal.WindowsPrincipal($id)
        return [ordered]@{sid=$id.User.Value; administratorToken=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator); elevationAssessment='AdministratorRoleOfActualToken'; processId=$PID; psVersion=$PSVersionTable.PSVersion.ToString()}
    } finally { $id.Dispose() }
}
function Assert-NSBinding($B) {
    Assert-NSFields $B @('schema','backend','purpose','releaseApproved','instanceId','appId','displayName','taskRoot','shellRoot','deploymentRoot','dataRoot','controlRoot','bootstrap','sourceManifest','packageManifest','host','registry','menu','priorReceipt')
    Assert-NSStringFields $B @('backend','purpose','instanceId','appId','displayName')
    if ($B.schema -isnot [long] -and $B.schema -isnot [int]) { throw 'InvalidSchema' }
    if ($B.schema -ne 2 -or $B.backend -cne 'InnoPerUser/1' -or $B.purpose -cne 'InternalStandardCandidate' -or $B.releaseApproved -isnot [bool] -or $B.releaseApproved) { throw 'UnsupportedBinding' }
    if ($B.instanceId -cne 'run-01' -or $B.appId -cne 'WinSight.Native.Preview' -or $B.displayName -cne 'WinSight Native Preview') { throw 'IdentityMismatch' }
    Assert-NSPath $B.taskRoot $script:NSRun
    Assert-NSPath $B.shellRoot ($script:NSRun+'\install\shell')
    Assert-NSPath $B.deploymentRoot ($script:NSRun+'\install\deployment')
    Assert-NSPath $B.dataRoot ($script:NSRun+'\data')
    Assert-NSPath $B.controlRoot ($script:NSRun+'\control')
    foreach ($field in @('bootstrap','sourceManifest','packageManifest')) { Assert-NSFields $B.$field @('path','sha256'); Assert-NSHash $B.$field.sha256 }
    Assert-NSPath $B.bootstrap.path ($B.shellRoot+'\Start-WinSightNative.ps1')
    Assert-NSPath $B.sourceManifest.path ($script:NSRoot+'\.dev\native-migration\release-acceptance\n6-live-base\manifest.json')
    Assert-NSPath $B.packageManifest.path ($B.controlRoot+'\package-manifest.json')
    Assert-NSFields $B.host @('computerName','sid','osVersion','is64BitProcess','platform')
    Assert-NSStringFields $B.host @('computerName','sid','osVersion','platform')
    $token = Get-NSToken
    if ($B.host.computerName -cne [Environment]::MachineName -or $B.host.sid -cne $token.sid -or $B.host.osVersion -cne [Environment]::OSVersion.VersionString -or $B.host.platform -cne 'Win32NT' -or $B.host.is64BitProcess -isnot [bool] -or -not $B.host.is64BitProcess -or -not [Environment]::Is64BitProcess) { throw 'HostMismatch' }
    Assert-NSFields $B.registry @('hive','view','key')
    Assert-NSStringFields $B.registry @('hive','view','key')
    if ($B.registry.hive -cne 'HKCU' -or $B.registry.view -cne 'Registry64' -or $B.registry.key -cne $script:NSKey) { throw 'RegistryMismatch' }
    Assert-NSFields $B.menu @('path','links')
    $menu = [Environment]::GetFolderPath('Programs')+'\WinSight Native Preview'
    Assert-NSPath $B.menu.path $menu
    if ($B.menu.links -isnot [array] -or $B.menu.links.Count -ne 2) { throw 'InvalidLinks' }
    $names = @('WinSight Native Preview.lnk','Uninstall WinSight Native Preview.lnk')
    for ($i=0;$i -lt 2;$i++) {
        $l=$B.menu.links[$i]; Assert-NSFields $l @('name','target','arguments','workingDirectory')
        Assert-NSStringFields $l @('name','target','arguments','workingDirectory')
        if ($l.name -cne $names[$i]) { throw 'LinkNameMismatch' }
        Assert-NSPath $l.workingDirectory $B.shellRoot
        if ($i -eq 0) {
            Assert-NSPath $l.target ($env:WINDIR+'\System32\WindowsPowerShell\v1.0\powershell.exe')
            if ($l.arguments -cne ('-NoProfile -NonInteractive -File "'+$B.bootstrap.path+'"')) { throw 'LaunchArgumentsMismatch' }
        } else {
            if ($l.target -isnot [string] -or $l.target -cnotmatch ('^'+[regex]::Escape($script:NSRun+'\install\')+'unins[0-9]{3}\.exe$') -or $l.arguments -cne '') { throw 'UninstallSpecMismatch' }
        }
    }
    if (-not [object]::ReferenceEquals($null, $B.priorReceipt)) { throw 'UnsupportedOwnershipReceipt' }
}

function Read-NativeStandardBinding {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BindingPath,[Parameter(Mandatory)][string]$BindingSha256)
    $deadline = (Get-NSTick) + 15000
    if (-not [IO.Path]::IsPathRooted($BindingPath) -or [IO.Path]::GetFullPath($BindingPath) -cne $BindingPath) { throw 'BindingPathMustBeCanonicalAbsolute' }
    $b = Read-NSJson $BindingPath $BindingSha256 $deadline
    Assert-NSBinding $b
    # A detached result is informational. Observation always reopens the pinned file.
    return $b
}
function ConvertTo-NSCanonical($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [string] -or $Value -is [bool] -or $Value -is [ValueType]) { return (ConvertTo-Json -InputObject $Value -Compress) }
    if ($Value -is [Collections.IDictionary]) {
        $parts = foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) { (ConvertTo-Json -InputObject ([string]$key) -Compress)+':'+(ConvertTo-NSCanonical $Value[$key]) }
        return '{'+($parts -join ',')+'}'
    }
    if ($Value -is [array]) { $parts=foreach($v in $Value){ConvertTo-NSCanonical $v}; return '['+($parts -join ',')+']' }
    $map = @{}; foreach($p in $Value.PSObject.Properties){$map[$p.Name]=$p.Value}; return (ConvertTo-NSCanonical $map)
}
function Get-NativeStandardStableDigest {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Observation)
    Assert-NSFields $Observation @('protocol','stable','volatile','status','SupportsOwnershipVerification','SupportsUpgrade','SupportsAnchorWrite','SupportsLiveCommit','PayloadHealthNotAssessed')
    if ($Observation.protocol -cne 'LiveResourceObservation/1') { throw 'UnsupportedObservation' }
    $bytes=$script:NSUtf8.GetBytes((ConvertTo-NSCanonical $Observation.stable))
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function Read-NSLinkUnit([string]$Path, [long]$Deadline) {
    $raw=Get-NSFile $Path 1048576 $Deadline
    if ($raw.state -ne 'Present') { return $raw }
    $shell=$null; $link=$null
    try {
        $shell=New-Object -ComObject WScript.Shell
        # CreateShortcut on an existing file reads IPersistFile; never Save or Resolve.
        $link=$shell.CreateShortcut($Path)
        return [ordered]@{raw=$raw;target=$link.TargetPath;arguments=$link.Arguments;workingDirectory=$link.WorkingDirectory}
    } finally {
        if ($link) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    }
}
function Read-NSRegistry {
    $base=$null; $key=$null
    try {
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
        $key=$base.OpenSubKey($script:NSKey,$false)
        if ($null -eq $key) { return [ordered]@{state='Absent';values=@();anchors=@()} }
        $values=@(); $anchors=@()
        # Never enumerate other keys or values. Missing expected values stay explicit.
        foreach($name in @('DisplayName','DisplayVersion','Publisher','InstallLocation','UninstallString','QuietUninstallString','NoModify','NoRepair','NativePendingReceiptSha256','NativeCommittedReceiptSha256')) {
            $value=$key.GetValue($name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            $entry=[ordered]@{name=$name;state='Absent'}
            if ($null -ne $value) { $entry=[ordered]@{name=$name;state='Present';kind=$key.GetValueKind($name).ToString();value=$value} }
            if ($name -like 'Native*') {$anchors+=,$entry} else {$values+=,$entry}
        }
        return [ordered]@{state='Present';values=$values;anchors=$anchors}
    } finally { if($key){$key.Dispose()};if($base){$base.Dispose()} }
}
function Get-NativeStandardObservation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BindingPath,[Parameter(Mandatory)][string]$BindingSha256)
    $start=Get-NSTick; $deadline=$start+20000
    $b=Read-NativeStandardBinding $BindingPath $BindingSha256
    $stable=[ordered]@{bindingPath=$BindingPath;bindingSha256=$BindingSha256;registry=$null;menu=$null;files=@();dynamicUninstallers='NotEnumeratedUnknownOwnership';uninstallCommand='NotParsedUnknownOwnership'}
    $volatile=[ordered]@{observedUtc=[DateTime]::UtcNow.ToString('o');token=(Get-NSToken);anchors=@();elapsedMilliseconds=0;limits=@{deadlineMilliseconds=20000;metadataBytes=1048576;bootstrapBytes=4194304;uninstallerBytes=67108864;logBytes=16777216};errors=@()}
    $status='Absent'; $unknown=$false
    try {
        $r=Read-NSRegistry
        $volatile.anchors=$r.anchors
        $stable.registry=[ordered]@{hive='HKCU';view='Registry64';key=$script:NSKey;state=$r.state;values=$r.values}
        if($r.state -eq 'Present'){$unknown=$true}
        Assert-NSDeadline $deadline
        Assert-NSNoReparse $b.menu.path
        $exists=[IO.Directory]::Exists($b.menu.path)
        # GetAttributes distinguishes missing from access denial (Exists alone cannot).
        try { [void][IO.File]::GetAttributes($b.menu.path); $exists=$true } catch [IO.FileNotFoundException] {$exists=$false} catch [IO.DirectoryNotFoundException] {$exists=$false}
        $links=@()
        foreach($spec in $b.menu.links){
            $raw=Get-NSFile ($b.menu.path+'\'+$spec.name) 1048576 $deadline
            if($raw.state -eq 'Present'){$unknown=$true}
            $links+=,[ordered]@{raw=$raw;parsed='NotParsedUnknownOwnership'}
        }
        $stable.menu=[ordered]@{path=$b.menu.path;state=$(if($exists){'Present'}else{'Absent'});links=$links}
        if($exists){$unknown=$true}
        foreach($path in @(($script:NSRun+'\install'),$b.shellRoot,$b.deploymentRoot,$b.dataRoot,$b.controlRoot)) {
            try { [void][IO.File]::GetAttributes($path); $unknown=$true; $stable.files+=,[ordered]@{path=$path;state='Present'} } catch [IO.FileNotFoundException] {$stable.files+=,[ordered]@{path=$path;state='Absent'}} catch [IO.DirectoryNotFoundException] {$stable.files+=,[ordered]@{path=$path;state='Absent'}}
        }
        foreach($field in @('bootstrap','sourceManifest','packageManifest')) {
            $file=Get-NSFile $b.$field.path $(if($field -eq 'bootstrap'){4194304}else{1048576}) $deadline
            if($file.state -eq 'Present' -and $file.sha256 -cne $b.$field.sha256){$status='Mismatch'}
            if($field -eq 'bootstrap' -and $file.state -eq 'Present'){$unknown=$true}
            $stable.files+=,$file
        }
        if($unknown){$status='UnknownOwnership'}
    } catch [UnauthorizedAccessException] { $status='Unauthorized';$volatile.errors+=,$_.Exception.GetType().FullName
    } catch { $status='ProbeError';$volatile.errors+=,($_.Exception.GetType().FullName+':'+$_.Exception.Message) }
    $volatile.elapsedMilliseconds=(Get-NSTick)-$start
    return [pscustomobject][ordered]@{protocol='LiveResourceObservation/1';stable=$stable;volatile=$volatile;status=$status;SupportsOwnershipVerification=$false;SupportsUpgrade=$false;SupportsAnchorWrite=$false;SupportsLiveCommit=$false;PayloadHealthNotAssessed=$true}
}
Export-ModuleMember -Function Read-NativeStandardBinding,Get-NativeStandardObservation,Get-NativeStandardStableDigest
