Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:N5EngineHash = '768e725b9ea3e5193b4d87d2430a63801c98af5b876f1328c31e49c60500c572'
$script:N5JobHash = '1f5600efadac9afc1f5afb2a7f6158643fcb25aeaf9400f58022674c69763331'
$script:N5Utf8 = [Text.UTF8Encoding]::new($false, $true)
$script:N5Limit = 2097152

function Get-N5Hash([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function ConvertTo-N5Bytes($Value) { ,$script:N5Utf8.GetBytes(($Value | ConvertTo-Json -Depth 30)) }
function Assert-N5Fields($Value, [string[]]$Names) {
    if ($null -eq $Value -or $Value -isnot [pscustomobject]) { throw 'N5.Schema: object required.' }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Names.Count -or @($actual | Where-Object { $_ -cnotin $Names }).Count) { throw 'N5.Schema: closed fields required.' }
}
function Assert-N5Hash($Value, [switch]$Nullable) {
    if ($Nullable -and $null -eq $Value) { return }
    if ($Value -isnot [string] -or $Value -cnotmatch '^[0-9a-f]{64}$') { throw 'N5.Schema: lowercase SHA256 required.' }
}
function Assert-N5Integer($Value, [long]$Minimum, [long]$Maximum) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt $Minimum -or $Value -gt $Maximum) { throw 'N5.Schema: bounded integer required.' }
}
function Assert-N5Path([string]$Path) {
    if ($Path -cmatch '[^\x20-\x7e]' -or $Path -notmatch '^[A-Za-z]:\\' -or $Path -match '[\x00-\x1f"'';{}<>|?*~]') { throw 'N5.Path: canonical ASCII local path required.' }
    if ($Path.Substring(2).Contains(':')) { throw 'N5.Path: alternate stream rejected.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full -cne $Path -or $Path.Length -le 3) { throw 'N5.Path: noncanonical root.' }
    foreach ($part in $Path.Substring(3).Split('\')) {
        if (-not $part -or $part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw 'N5.Path: ambiguous path segment.' }
    }
    for ($p = $Path; $p; $p = [IO.Path]::GetDirectoryName($p)) {
        if ([IO.File]::Exists($p)) { if ($p -cne $Path) { throw 'N5.Path: file ancestor.' } }
        if ([IO.Directory]::Exists($p) -or [IO.File]::Exists($p)) {
            if (([IO.File]::GetAttributes($p) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'N5.Path: reparse rejected.' }
        }
    }
    $Path
}
function Assert-N5Separate([string]$A, [string]$B) {
    if ($A -ieq $B -or $A.StartsWith($B + '\',[StringComparison]::OrdinalIgnoreCase) -or $B.StartsWith($A + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'N5.Path: roots overlap.' }
}
function Read-N5Bytes([string]$Path, [int]$Limit = 2097152) {
    $null = Assert-N5Path $Path
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if ($stream.Length -gt $Limit) { throw 'N5.Bytes: input exceeds bound.' }
        $bytes = New-Object byte[] ([int]$stream.Length)
        $offset = 0
        while ($offset -lt $bytes.Length) { $n=$stream.Read($bytes,$offset,$bytes.Length-$offset); if ($n -eq 0) { throw 'N5.Bytes: truncated read.' }; $offset += $n }
        ,$bytes
    } finally { $stream.Dispose() }
}
function ConvertFrom-N5Bytes([byte[]]$Bytes) {
    if ($Bytes.Length -gt $script:N5Limit -or $Bytes.Length -eq 0) { throw 'N5.Bytes: invalid JSON size.' }
    $text = $script:N5Utf8.GetString($Bytes)
    $value = $text | ConvertFrom-Json
    # Exact same-host canonical bytes also reject duplicate keys, BOM and trailing text.
    if ([Convert]::ToBase64String((ConvertTo-N5Bytes $value)) -cne [Convert]::ToBase64String($Bytes)) { throw 'N5.Serializer: canonical same-host bytes required.' }
    $value
}
function Read-N5Object([string]$Path,[string]$Hash,[int]$Limit = 2097152) {
    Assert-N5Hash $Hash
    $bytes = Read-N5Bytes $Path $Limit
    if ((Get-N5Hash $bytes) -cne $Hash) { throw 'N5.Trust: object hash mismatch.' }
    ConvertFrom-N5Bytes $bytes
}
function Get-N5Host {
    $path = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    [pscustomobject]@{ edition=[string]$PSVersionTable.PSEdition; version=$PSVersionTable.PSVersion.ToString(); executableSha256=(Get-N5Hash (Read-N5Bytes $path 16777216)); machine=[Environment]::MachineName; userSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value; serializer='ConvertTo-Json/Depth30/UTF8-noBOM' }
}
function Assert-N5Files($Files,[switch]$Shell) {
    if ($Files -isnot [array] -or $Files.Count -lt 1 -or $Files.Count -gt 4096) { throw 'N5.Schema: bounded nonempty file array required.' }
    $seen=@{}; $total=0L
    foreach($f in $Files) {
        Assert-N5Fields $f @('path','bytes','sha256'); Assert-N5Hash $f.sha256; Assert-N5Integer $f.bytes 0 16777216
        if ($f.path -isnot [string] -or $f.path.Length -gt 180) { throw 'N5.Schema: file path required.' }
        if ($Shell) { if ($f.path -cnotmatch '^[A-Za-z0-9_-]+\.txt$') { throw 'N5.Schema: inert shell .txt only.' } }
        elseif ($f.path -cnotmatch '^app/[A-Za-z0-9_. /-]+$' -or $f.path.Contains('//')) { throw 'N5.Schema: payload relative path.' }
        foreach($part in $f.path.Split('/')) { if ($part -in @('.','..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw 'N5.Schema: payload segment.' } }
        if ($seen.ContainsKey($f.path)) { throw 'N5.Schema: duplicate file.' }; $seen[$f.path]=$true
        $total += $f.bytes
    }
    if ($total -gt 67108864) { throw 'N5.Verification: inert payload byte budget exceeded.' }
}
function Read-N5Binding([string]$Path,[string]$Hash) {
    $b = Read-N5Object $Path $Hash 32768
    Assert-N5Fields $b @('schema','protocol','mode','appId','instanceId','installRoot','payloadRoot','controlRoot','dataRoot','shellRoot','engineSha256','jobSha256','coordinatorSha256','entrySha256','host','shellFiles')
    Assert-N5Integer $b.schema 1 1
    if ($b.protocol -cne 'N5.FileVerification/1' -or $b.mode -cnotin @('Verification','Production') -or $b.appId -cne 'WinSight.Native.Preview' -or $b.instanceId -isnot [string] -or $b.instanceId -cnotmatch '^[0-9a-f]{32}$') { throw 'N5.Binding: identity/protocol mismatch.' }
    foreach($name in @('installRoot','payloadRoot','controlRoot','dataRoot','shellRoot')) { if ($b.$name -isnot [string]) { throw 'N5.Binding: root type.' }; $null=Assert-N5Path $b.$name }
    if ($b.payloadRoot -cne (Join-Path $b.installRoot 'payload') -or $b.shellRoot -cne (Join-Path $b.installRoot 'shell')) { throw 'N5.Binding: fixed deployment/shell subtrees required.' }
    Assert-N5Separate $b.installRoot $b.controlRoot; Assert-N5Separate $b.installRoot $b.dataRoot; Assert-N5Separate $b.controlRoot $b.dataRoot
    Assert-N5Separate $Path $b.payloadRoot; Assert-N5Separate $Path $b.controlRoot; Assert-N5Separate $Path $b.dataRoot; Assert-N5Separate $Path $b.shellRoot
    foreach($name in @('engineSha256','jobSha256','coordinatorSha256','entrySha256')) { Assert-N5Hash $b.$name }
    if ($b.engineSha256 -cne $script:N5EngineHash -or $b.jobSha256 -cne $script:N5JobHash) { throw 'N5.Binding: frozen engine/job mismatch.' }
    foreach($row in @(@('NativeInstaller.psm1',$b.engineSha256),@('NativeDeploymentJob.psm1',$b.jobSha256),@('NativeInstallCoordinator.psm1',$b.coordinatorSha256),@('Invoke-NativeInstallCoordinator.ps1',$b.entrySha256))) {
        if ((Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot $row[0]))) -cne $row[1]) { throw 'N5.Bootstrap: fixed sibling hash mismatch.' }
    }
    Assert-N5Fields $b.host @('edition','version','executableSha256','machine','userSid','serializer')
    if ((Get-N5Hash (ConvertTo-N5Bytes $b.host)) -cne (Get-N5Hash (ConvertTo-N5Bytes (Get-N5Host)))) { throw 'N5.Host: exact same-host serializer required.' }
    if ([IntPtr]::Size -ne 8) { throw 'N5.Host: x64 required.' }
    Assert-N5Files $b.shellFiles -Shell
    $b
}
function Assert-N5State($State,$B) {
    Assert-N5Fields $State @('schema','installRoot','dataRoot','current','releases'); Assert-N5Integer $State.schema 1 1
    if ($State.installRoot -cne $B.payloadRoot -or $State.dataRoot -cne $B.dataRoot) { throw 'N5.State: roots mismatch.' }
    Assert-N5Hash $State.current
    if ($State.releases -isnot [array] -or $State.releases.Count -lt 1 -or $State.releases.Count -gt 64) { throw 'N5.State: release bound.' }
    $ids=@{}
    foreach($r in $State.releases) { Assert-N5Fields $r @('id','files'); Assert-N5Hash $r.id; Assert-N5Files $r.files; if ($ids.ContainsKey($r.id)) { throw 'N5.State: duplicate release.' }; $ids[$r.id]=$true }
    if (-not $ids.ContainsKey($State.current)) { throw 'N5.State: missing current.' }
}
function Get-N5StateHash($State) { if ($null -ne $State) { Get-N5Hash (ConvertTo-N5Bytes $State) } else { return $null } }
function Get-N5ActualState($B) {
    $path=Join-Path $B.payloadRoot 'state.json'
    if ([IO.File]::Exists($path)) { Get-N5Hash (Read-N5Bytes $path) }
    elseif ([IO.Directory]::Exists($path)) { throw 'N5.State: state is directory.' }
    else { return $null }
}
function Get-N5ObjectPath($B,[string]$Area,[string]$Hash) { Join-Path $B.controlRoot ($Area + '\' + $Hash + '.json') }
function Assert-N5Intent($I,$B,[string]$BindingHash) {
    Assert-N5Fields $I @('schema','kind','mode','bindingSha256','transactionId','operation','previousReceiptSha256','stageDirectory','manifestSha256','journal','journalSha256','previousStateSha256','nextStateSha256')
    Assert-N5Integer $I.schema 1 1
    if ($I.kind -cne 'Intent' -or $I.mode -cne 'Verification' -or $I.bindingSha256 -cne $BindingHash -or $I.transactionId -isnot [string] -or $I.transactionId -cnotmatch '^[0-9a-f]{32}$' -or $I.operation -cnotin @('Apply','Remove')) { throw 'N5.Intent: context mismatch.' }
    foreach($n in @('previousReceiptSha256','manifestSha256','previousStateSha256','nextStateSha256')) { Assert-N5Hash $I.$n -Nullable }; Assert-N5Hash $I.journalSha256
    $j=$I.journal; Assert-N5Fields $j @('schema','operation','installRoot','dataRoot','previous','next','copyProtocol'); Assert-N5Integer $j.schema 1 1
    if ($j.operation -cne $I.operation -or $j.installRoot -cne $B.payloadRoot -or $j.dataRoot -cne $B.dataRoot -or $j.copyProtocol -cne 'staged-v1') { throw 'N5.Intent: native journal mismatch.' }
    if ($null -ne $j.previous) { Assert-N5State $j.previous $B }
    if ($I.operation -ceq 'Apply') {
        if ($I.stageDirectory -isnot [string]) { throw 'N5.Intent: stage required.' }; $null=Assert-N5Path $I.stageDirectory
        foreach($n in @('installRoot','controlRoot','dataRoot')) { Assert-N5Separate $I.stageDirectory $B.$n }
        Assert-N5Hash $I.manifestSha256; Assert-N5State $j.next $B
        $old=@(); if ($null -ne $j.previous) { $old=@($j.previous.releases) }
        $candidates=@($j.next.releases | Where-Object { $_.id -ceq $I.manifestSha256 })
        if ($candidates.Count -ne 1 -or $j.next.current -cne $I.manifestSha256 -or @($old | Where-Object { $_.id -ceq $I.manifestSha256 }).Count -or (Get-N5Hash (ConvertTo-N5Bytes @($old + $candidates))) -cne (Get-N5Hash (ConvertTo-N5Bytes $j.next.releases))) { throw 'N5.Intent: release transition mismatch.' }
    } elseif ($null -ne $I.stageDirectory -or $null -ne $I.manifestSha256 -or $null -ne $j.next -or $null -eq $j.previous) { throw 'N5.Intent: Remove mismatch.' }
    if ((Get-N5Hash (ConvertTo-N5Bytes $j)) -cne $I.journalSha256 -or (Get-N5StateHash $j.previous) -cne $I.previousStateSha256 -or (Get-N5StateHash $j.next) -cne $I.nextStateSha256) { throw 'N5.Intent: pre-recorded native pins mismatch.' }
}
function New-N5Receipt($I,[string]$IntentHash) {
    [pscustomobject]@{ schema=1; kind='VerificationCommit'; mode='Verification'; bindingSha256=$I.bindingSha256; intentSha256=$IntentHash; transactionId=$I.transactionId; operation=$I.operation; previousReceiptSha256=$I.previousReceiptSha256; journalSha256=$I.journalSha256; previousStateSha256=$I.previousStateSha256; nextStateSha256=$I.nextStateSha256; selection=$(if($null -ne $I.journal.next){$I.journal.next.current}else{$null}); shellEvidence=$(if($I.operation -ceq 'Apply'){'InertFilesPresent'}else{'InertFilesAbsent'}) }
}
function Read-N5Receipt($B,[string]$Hash,[string]$BindingHash) {
    $r=Read-N5Object (Get-N5ObjectPath $B 'receipts' $Hash) $Hash
    Assert-N5Fields $r @('schema','kind','mode','bindingSha256','intentSha256','transactionId','operation','previousReceiptSha256','journalSha256','previousStateSha256','nextStateSha256','selection','shellEvidence')
    Assert-N5Hash $r.intentSha256
    $i=Read-N5Object (Get-N5ObjectPath $B 'intents' $r.intentSha256) $r.intentSha256
    Assert-N5Intent $i $B $BindingHash
    if ((Get-N5Hash (ConvertTo-N5Bytes (New-N5Receipt $i $r.intentSha256))) -cne $Hash) { throw 'N5.Receipt: intent/context mismatch.' }
    [pscustomobject]@{ receipt=$r; intent=$i }
}
function Get-N5Anchor($B) {
    $path=Join-Path $B.controlRoot 'anchor.json'
    if ([IO.File]::Exists($path)) { $a=ConvertFrom-N5Bytes (Read-N5Bytes $path 1024); Assert-N5Fields $a @('schema','mode','receiptSha256'); Assert-N5Integer $a.schema 1 1; if($a.mode -cne 'Verification'){throw 'N5.Anchor: mode mismatch.'}; Assert-N5Hash $a.receiptSha256; $a.receiptSha256 }
    elseif ([IO.Directory]::Exists($path)) { throw 'N5.Anchor: directory collision.' }
    else { return $null }
}
function Assert-N5Anchor($B,$Expected) {
    if ((Get-N5Anchor $B) -cne $Expected) { throw 'N5.Anchor: compare-and-swap conflict; no adoption.' }
}
function Get-N5Tree([string]$Root) {
    if ([IO.File]::Exists($Root)) { throw 'N5.Layout: directory is file.' }
    if (-not [IO.Directory]::Exists($Root)) { return }
    $queue=[Collections.Generic.Queue[string]]::new(); $queue.Enqueue($Root); $count=0
    while($queue.Count) {
        foreach($p in [IO.Directory]::EnumerateFileSystemEntries($queue.Dequeue())) {
            $count++; if($count -gt 16384){throw 'N5.Layout: entry bound exceeded.'}
            $null=Assert-N5Path $p; $a=[IO.File]::GetAttributes($p); $dir=($a -band [IO.FileAttributes]::Directory) -ne 0
            [pscustomobject]@{path=$p; relative=$p.Substring($Root.Length+1).Replace('\','/'); directory=$dir}
            if($dir){$queue.Enqueue($p)}
        }
    }
}
function Assert-N5Control($B,[string]$BindingHash) {
    foreach($e in @(Get-N5Tree $B.controlRoot)) {
        if($e.directory -and $e.relative -cin @('intents','receipts','finalized','recovered','evidence')){continue}
        if(-not $e.directory -and $e.relative -ceq 'anchor.json'){continue}
        if(-not $e.directory -and $e.relative -cmatch '^(intents|receipts|finalized|recovered)/([0-9a-f]{64})\.json$') {
            $area=$Matches[1]; $hash=$Matches[2]; $bytes=Read-N5Bytes $e.path; $object=ConvertFrom-N5Bytes $bytes
            if($area -cin @('intents','receipts') -and (Get-N5Hash $bytes) -cne $hash){throw 'N5.Control: content address mismatch.'}
            if($area -ceq 'intents') {
                Assert-N5Intent $object $B $BindingHash
                if($null -eq $object.journal.previous){
                    if($null -ne $object.previousReceiptSha256){throw 'N5.Control: fresh intent has a previous receipt.'}
                } else {
                    Assert-N5Hash $object.previousReceiptSha256
                    $previous=Read-N5Receipt $B $object.previousReceiptSha256 $BindingHash
                    if($previous.receipt.operation -cne 'Apply' -or $previous.receipt.nextStateSha256 -cne $object.previousStateSha256){throw 'N5.Control: previous receipt/state relationship mismatch.'}
                }
            }
            elseif($area -ceq 'receipts') { $null=Read-N5Receipt $B $hash $BindingHash }
            else {
                Assert-N5Fields $object @('schema','kind','mode','bindingSha256','intentSha256','receiptSha256','operation','stateSha256')
                Assert-N5Hash $object.intentSha256
                $intent=Read-N5Object (Get-N5ObjectPath $B 'intents' $object.intentSha256) $object.intentSha256
                Assert-N5Intent $intent $B $BindingHash
                if($area -ceq 'finalized') {
                    $receiptHash=Get-N5Hash (ConvertTo-N5Bytes (New-N5Receipt $intent $object.intentSha256))
                    if($hash -cne $receiptHash){throw 'N5.Control: finalized filename must identify its receipt.'}
                    $null=Read-N5Receipt $B $receiptHash $BindingHash
                    $expected=New-N5Fact $intent $object.intentSha256 $receiptHash 'Finalized'
                } else {
                    if($hash -cne $object.intentSha256){throw 'N5.Control: recovered filename must identify its intent.'}
                    # Empty receipt string is ONLY the deterministic first-install recovery case.
                    $expected=New-N5Fact $intent $hash $intent.previousReceiptSha256 'Recovered'
                    if($null -ne $intent.previousReceiptSha256){$null=Read-N5Receipt $B $intent.previousReceiptSha256 $BindingHash}
                }
                if((Get-N5Hash $bytes) -cne (Get-N5Hash (ConvertTo-N5Bytes $expected))){throw 'N5.Control: fact schema/context mismatch.'}
            }
            continue
        }
        # Native initialization recovery owns only this fixed evidence subdirectory.
        if($e.relative -cmatch '^evidence/[0-9a-f]{32}(/(initialization-recovery\.json|journal-[0-9a-f]{64}\.new))?$'){continue}
        throw 'N5.Control: unknown or ambiguous entry retained.'
    }
}
function Assert-N5Payload($B,$State,[string]$Area='versions',[switch]$Partial,[switch]$Journal) {
    $files=@{}; $dirs=@{'versions'=$true}
    if($null -ne $State) {
        foreach($r in $State.releases) {
            foreach($f in $r.files) {
                $rel=$Area+'/'+$r.id+'/'+$f.path; $files[$rel]=$f; $p=$rel
                while($p.Contains('/')){$p=$p.Substring(0,$p.LastIndexOf('/'));$dirs[$p]=$true}
            }
        }
    }
    foreach($e in @(Get-N5Tree $B.payloadRoot)) {
        if(-not $e.directory -and $e.relative -ceq 'state.json'){continue}
        if($Journal -and -not $e.directory -and $e.relative -ceq 'journal.json'){continue}
        if($e.directory){if(-not $dirs.ContainsKey($e.relative)){throw 'N5.Payload: unknown directory.'};continue}
        if(-not $files.ContainsKey($e.relative)){throw 'N5.Payload: unknown file.'}
        $f=$files[$e.relative]; $bytes=Read-N5Bytes $e.path 16777216
        if($bytes.Length -ne $f.bytes -or (Get-N5Hash $bytes) -cne $f.sha256){throw 'N5.Payload: changed file.'}
    }
    if(-not $Partial){foreach($rel in $files.Keys){if(-not [IO.File]::Exists((Join-Path $B.payloadRoot $rel))){throw 'N5.Payload: missing known file.'}}}
}
function Assert-N5Shell($B,[string]$Operation) {
    $files=@{}; foreach($f in $B.shellFiles){$files[$f.path]=$f}
    foreach($e in @(Get-N5Tree $B.shellRoot)) {
        if($e.directory -or -not $files.ContainsKey($e.relative) -or $Operation -ceq 'Remove'){throw 'N5.Shell: unknown/present inert file.'}
        $f=$files[$e.relative];$bytes=Read-N5Bytes $e.path 16777216
        if($bytes.Length -ne $f.bytes -or (Get-N5Hash $bytes) -cne $f.sha256){throw 'N5.Shell: inert file mismatch.'}
    }
    if($Operation -ceq 'Apply'){foreach($f in $B.shellFiles){if(-not [IO.File]::Exists((Join-Path $B.shellRoot $f.path))){throw 'N5.Shell: inert file missing.'}}}
}
function Write-N5Atomic([string]$Path,[byte[]]$Bytes,[switch]$Replace) {
    $null=Assert-N5Path $Path
    if($Bytes.Length -gt $script:N5Limit){throw 'N5.Bytes: output bound exceeded.'}
    if([IO.File]::Exists($Path) -and -not $Replace){
        if((Get-N5Hash (Read-N5Bytes $Path)) -cne (Get-N5Hash $Bytes)){throw 'N5.Immutable: object collision.'};return
    }
    $temporary=$Path+'.new'
    if([IO.File]::Exists($temporary) -or [IO.Directory]::Exists($temporary)){throw 'N5.Atomic: unresolved temporary retained.'}
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null
    $s=[IO.File]::Open($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$s.Write($Bytes,0,$Bytes.Length);$s.Flush($true)}finally{$s.Dispose()}
    if([IO.File]::Exists($Path)){[IO.File]::Replace($temporary,$Path,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temporary,$Path)}
    if((Get-N5Hash (Read-N5Bytes $Path)) -cne (Get-N5Hash $Bytes)){throw 'N5.Atomic: published bytes mismatch.'}
}
function New-N5Fact($I,[string]$IntentHash,[string]$ReceiptHash,[string]$Kind) {
    [pscustomobject]@{schema=1;kind=$Kind;mode='Verification';bindingSha256=$I.bindingSha256;intentSha256=$IntentHash;receiptSha256=$ReceiptHash;operation=$I.operation;stateSha256=$(if($Kind -ceq 'Finalized'){$I.nextStateSha256}else{$I.previousStateSha256})}
}
function Assert-N5FinalFact($B,$I,[string]$IntentHash,[string]$ReceiptHash) {
    $expected=ConvertTo-N5Bytes (New-N5Fact $I $IntentHash $ReceiptHash 'Finalized')
    $actual=Read-N5Bytes (Get-N5ObjectPath $B 'finalized' $ReceiptHash)
    if((Get-N5Hash $actual) -cne (Get-N5Hash $expected)){throw 'N5.Finalize: durable final facts mismatch.'}
    if((Get-N5ActualState $B) -cne $I.nextStateSha256){throw 'N5.Finalize: final state mismatch.'}
    Assert-N5Payload $B $I.journal.next
    Assert-N5Shell $B $I.operation
}
function Assert-N5Arguments($P) {
    foreach($n in @('Command','Operation','BindingPath','BindingSha256')){if(-not $P.ContainsKey($n) -or -not $P[$n]){throw "N5.Arguments: $n required."}}
    Assert-N5Hash $P.BindingSha256
    foreach($n in @('IntentSha256','ExpectedReceiptSha256','ManifestSha256')){if($P.ContainsKey($n)){Assert-N5Hash $P[$n]}}
    if($P.Command -cnotin @('Inspect','CreateIntent','Prepare','Commit','Finalize','Recover','ResumeFinalize') -or $P.Operation -cnotin @('Apply','Remove')){throw 'N5.Arguments: closed command/operation required.'}
    $allowed=@('Command','Operation','BindingPath','BindingSha256','StartTick','DeadlineMilliseconds')
    if($P.Command -ceq 'Inspect'){$allowed+=@('ExpectedReceiptSha256')}
    else {
        $allowed+=@('TransactionId','ExpectedReceiptSha256')
        if(-not $P.ContainsKey('TransactionId') -or $P.TransactionId -cnotmatch '^[0-9a-f]{32}$'){throw 'N5.Arguments: lowercase GUID transaction required.'}
        if($P.Command -ceq 'CreateIntent'){
            if($P.Operation -ceq 'Apply'){$allowed+=@('StageDirectory','ManifestSha256');if(-not $P.StageDirectory -or -not $P.ManifestSha256){throw 'N5.Arguments: stage and manifest required.'}}
        }else{$allowed+='IntentSha256';if(-not $P.ContainsKey('IntentSha256')){throw 'N5.Arguments: pre-retained intent pin required.'}}
        if($P.Command -cin @('Finalize','ResumeFinalize') -and -not $P.ContainsKey('ExpectedReceiptSha256')){throw 'N5.Arguments: committed receipt pin required.'}
    }
    foreach($k in $P.Keys){if($k -cnotin $allowed){throw 'N5.Arguments: command-specific combination rejected.'}}
}
function Invoke-N5Kernel($P,$B) {
    Assert-N5Control $B $P.BindingSha256
    $bindingHash=$P.BindingSha256;$command=$P.Command;$operation=$P.Operation
    $expected=$null;if($P.ContainsKey('ExpectedReceiptSha256')){$expected=$P.ExpectedReceiptSha256}
    $anchor=Get-N5Anchor $B
    if($command -ceq 'Inspect') {
        Assert-N5Anchor $B $expected
        if($expected){$prior=Read-N5Receipt $B $expected $bindingHash}
        return [pscustomobject]@{schema=1;status='MetadataInspected';auditScope='ControlMetadataOnly';payloadHealth='NotAssessed';shellHealth='NotAssessed';lifecycleReconciled=$false;mode=$B.mode;liveAdaptersAvailable=$false;anchorSha256=$anchor;bindingSha256=$bindingHash;payloadStateSha256=(Get-N5ActualState $B);shellEvidence='VerificationOnlyNotOSCommit'}
    }
    if($command -ceq 'CreateIntent') {
        Assert-N5Anchor $B $expected
        $previous=$null
        if($expected){
            $prior=Read-N5Receipt $B $expected $bindingHash
            if($prior.receipt.operation -cne 'Apply'){throw 'N5.Intent: removed instance cannot be adopted.'}
            Assert-N5FinalFact $B $prior.intent $prior.receipt.intentSha256 $expected
            $previous=$prior.intent.journal.next
        } elseif($null -ne (Get-N5ActualState $B)){throw 'N5.Trust: state without trusted prior receipt rejected.'}
        Assert-N5Payload $B $previous
        if((Get-N5ActualState $B) -cne (Get-N5StateHash $previous)){throw 'N5.Intent: prior state changed.'}
        $next=$null;$stage=$null;$manifestHash=$null
        if($operation -ceq 'Apply') {
            $stage=Assert-N5Path $P.StageDirectory
            foreach($n in @('installRoot','controlRoot','dataRoot')){Assert-N5Separate $stage $B.$n}
            $manifestHash=$P.ManifestSha256
            $m=Read-N5Object (Join-Path $stage 'release-manifest.json') $manifestHash
            Assert-N5Fields $m @('schema','runtime','selfContained','releaseApproved','appOnlyPreview','files'); Assert-N5Integer $m.schema 1 1
            if($m.runtime -cne 'win-x64' -or $m.selfContained -isnot [bool] -or -not $m.selfContained -or $m.releaseApproved -isnot [bool] -or $m.releaseApproved -or $m.appOnlyPreview -isnot [bool]){throw 'N5.Stage: manifest type mismatch.'}
            Assert-N5Files $m.files
            $null=Read-NativeStage $stage $manifestHash Verification
            $old=@();if($null -ne $previous){$old=@($previous.releases)}
            $candidate=[pscustomobject]@{id=$manifestHash;files=@($m.files)}
            $next=[pscustomobject]@{schema=1;installRoot=$B.payloadRoot;dataRoot=$B.dataRoot;current=$manifestHash;releases=@($old)+@($candidate)}
        }elseif($null -eq $previous){throw 'N5.Intent: nothing installed.'}
        $journal=[pscustomobject]@{schema=1;operation=$operation;installRoot=$B.payloadRoot;dataRoot=$B.dataRoot;previous=$previous;next=$next;copyProtocol='staged-v1'}
        $i=[pscustomobject]@{schema=1;kind='Intent';mode='Verification';bindingSha256=$bindingHash;transactionId=$P.TransactionId;operation=$operation;previousReceiptSha256=$expected;stageDirectory=$stage;manifestSha256=$manifestHash;journal=$journal;journalSha256=(Get-N5Hash (ConvertTo-N5Bytes $journal));previousStateSha256=(Get-N5StateHash $previous);nextStateSha256=(Get-N5StateHash $next)}
        Assert-N5Intent $i $B $bindingHash
        $bytes=ConvertTo-N5Bytes $i;$ih=Get-N5Hash $bytes
        foreach($e in @(Get-N5Tree (Join-Path $B.controlRoot 'intents'))){$other=ConvertFrom-N5Bytes (Read-N5Bytes $e.path);if($other.transactionId -ceq $P.TransactionId -and (Get-N5Hash (Read-N5Bytes $e.path)) -cne $ih){throw 'N5.Intent: transaction id reused.'}}
        Write-N5Atomic (Get-N5ObjectPath $B 'intents' $ih) $bytes
        return [pscustomobject]@{schema=1;status='IntentCreated';mode='Verification';intentSha256=$ih;intentPath=(Get-N5ObjectPath $B 'intents' $ih);journalSha256=$i.journalSha256;expectedCommitReceiptSha256=(Get-N5Hash (ConvertTo-N5Bytes (New-N5Receipt $i $ih)));previousStateSha256=$i.previousStateSha256;nextStateSha256=$i.nextStateSha256}
    }
    $ih=$P.IntentSha256;$i=Read-N5Object (Get-N5ObjectPath $B 'intents' $ih) $ih
    Assert-N5Intent $i $B $bindingHash
    if($i.transactionId -cne $P.TransactionId -or $i.operation -cne $operation){throw 'N5.Intent: operation/transaction mismatch.'}
    $r=New-N5Receipt $i $ih;$rh=Get-N5Hash (ConvertTo-N5Bytes $r)
    $journalPath=Join-Path $B.payloadRoot 'journal.json'
    $state=Get-N5ActualState $B
    $recoveredPath=Get-N5ObjectPath $B 'recovered' $ih
    if([IO.File]::Exists($recoveredPath)){throw 'N5.Recover: already recovered; explicit new transaction required.'}
    if($command -cin @('Finalize','ResumeFinalize')) {
        if($expected -cne $rh){throw 'N5.Finalize: exact committed receipt pin required.'}
        Assert-N5Anchor $B $rh;$null=Read-N5Receipt $B $rh $bindingHash
        Assert-N5Shell $B $operation
        if(-not [IO.File]::Exists($journalPath)){
            Assert-N5FinalFact $B $i $ih $rh
            return [pscustomobject]@{schema=1;status='AlreadyFinalized';mode='Verification';receiptSha256=$rh;stateSha256=$state}
        }
        $null=Read-N5Object $journalPath $i.journalSha256
        if($state -cne $i.nextStateSha256){throw 'N5.Finalize: activated state mismatch.'}
        $args=@{Operation=$operation;InstallRoot=$B.payloadRoot;DataRoot=$B.dataRoot;Mode='Verification';TransactionPhase='Finalize';JournalSha256=$i.journalSha256}
        $hash=if($operation -ceq 'Apply'){$i.nextStateSha256}else{$i.previousStateSha256};if($hash){$args.ExpectedStateSha256=$hash}
        $result=Invoke-NativeTransaction @args 6>$null
        if($result.operation -cne 'Finalized' -or (Get-N5ActualState $B) -cne $i.nextStateSha256){throw 'N5.Finalize: native final result mismatch.'}
        Assert-N5Payload $B $i.journal.next
        Write-N5Atomic (Get-N5ObjectPath $B 'finalized' $rh) (ConvertTo-N5Bytes (New-N5Fact $i $ih $rh 'Finalized'))
        return [pscustomobject]@{schema=1;status='Finalized';mode='Verification';receiptSha256=$rh;stateSha256=(Get-N5ActualState $B)}
    }
    if($expected -cne $i.previousReceiptSha256){throw 'N5.Trust: expected previous receipt differs from intent.'}
    if($command -ceq 'Commit' -and $anchor -ceq $rh){
        $null=Read-N5Receipt $B $rh $bindingHash;Assert-N5Shell $B $operation
        if($state -cne $i.nextStateSha256){throw 'N5.Commit: committed state changed.'}
        if([IO.File]::Exists($journalPath)){$null=Read-N5Object $journalPath $i.journalSha256;Assert-N5Payload $B $(if($operation -ceq 'Apply'){$i.journal.next}else{$i.journal.previous}) $(if($operation -ceq 'Apply'){'versions'}else{'removed'}) -Journal}
        else{Assert-N5FinalFact $B $i $ih $rh}
        return [pscustomobject]@{schema=1;status='AlreadyCommitted';mode='Verification';receiptSha256=$rh;stateSha256=$state}
    }
    Assert-N5Anchor $B $expected
    if($expected){$prior=Read-N5Receipt $B $expected $bindingHash;if($prior.receipt.nextStateSha256 -cne $i.previousStateSha256){throw 'N5.Trust: prior receipt state differs.'}}
    elseif($null -ne $i.journal.previous){throw 'N5.Trust: unanchored previous state.'}
    $args=@{Operation=$operation;InstallRoot=$B.payloadRoot;DataRoot=$B.dataRoot;Mode='Verification'}
    if($i.previousStateSha256){$args.ExpectedStateSha256=$i.previousStateSha256}
    if($command -ceq 'Prepare') {
        if($state -cne $i.previousStateSha256){throw 'N5.Prepare: prior state changed.'}
        if([IO.File]::Exists($journalPath)){
            $null=Read-N5Object $journalPath $i.journalSha256
            Assert-N5Payload $B $(if($operation -ceq 'Apply'){$i.journal.next}else{$i.journal.previous}) -Journal
            return [pscustomobject]@{schema=1;status='AlreadyPrepared';mode='Verification';intentSha256=$ih;journalSha256=$i.journalSha256}
        }
        if($operation -ceq 'Apply'){$args.StageDirectory=$i.stageDirectory;$args.ManifestSha256=$i.manifestSha256}
        $args.TransactionPhase='Prepare';$result=Invoke-NativeTransaction @args 6>$null
        if($result.journalSha256 -cne $i.journalSha256 -or $result.previousStateSha256 -cne $i.previousStateSha256 -or $result.nextStateSha256 -cne $i.nextStateSha256){throw 'N5.Prepare: predicted native pins mismatch; retained.'}
        $null=Read-N5Object $journalPath $i.journalSha256
        return [pscustomobject]@{schema=1;status='Prepared';mode='Verification';intentSha256=$ih;journalSha256=$i.journalSha256}
    }
    if($command -ceq 'Recover') {
        $recovery=@{InstallRoot=$B.payloadRoot;DataRoot=$B.dataRoot;JournalSha256=$i.journalSha256;Mode='Verification';InitializationOperation=$operation;InitializationEvidenceRoot=(Join-Path $B.controlRoot ('evidence\'+$i.transactionId))}
        if($i.previousStateSha256){$recovery.ExpectedStateSha256=$i.previousStateSha256}
        if($operation -ceq 'Apply'){$recovery.StageDirectory=$i.stageDirectory;$recovery.ManifestSha256=$i.manifestSha256}
        $result=Invoke-NativeRecovery @recovery
        if((Get-N5ActualState $B) -cne $i.previousStateSha256){throw 'N5.Recover: restored state mismatch.'}
        if($result.journalRetained){throw 'N5.Recover: quarantine retained; explicit evidence resolution required.'}
        Assert-N5Payload $B $i.journal.previous
        Write-N5Atomic $recoveredPath (ConvertTo-N5Bytes (New-N5Fact $i $ih $expected 'Recovered'))
        return [pscustomobject]@{schema=1;status='RecoveredPayloadOnly';mode='Verification';intentSha256=$ih;stateSha256=(Get-N5ActualState $B);shellStatus='NotRepairedByRecovery'}
    }
    if($command -ceq 'Commit') {
        $null=Read-N5Object $journalPath $i.journalSha256
        Assert-N5Shell $B $operation
        $args.TransactionPhase='Activate';$args.JournalSha256=$i.journalSha256
        $result=Invoke-NativeTransaction @args 6>$null
        if($result.operation -cne 'Activated' -or (Get-N5ActualState $B) -cne $i.nextStateSha256){throw 'N5.Commit: native activated state mismatch.'}
        # The immutable, durably flushed receipt MUST precede the CAS anchor.
        Write-N5Atomic (Get-N5ObjectPath $B 'receipts' $rh) (ConvertTo-N5Bytes $r)
        Assert-N5Anchor $B $expected
        Write-N5Atomic (Join-Path $B.controlRoot 'anchor.json') (ConvertTo-N5Bytes ([pscustomobject]@{schema=1;mode='Verification';receiptSha256=$rh})) -Replace
        return [pscustomobject]@{schema=1;status='VerificationCommitted';mode='Verification';receiptSha256=$rh;stateSha256=(Get-N5ActualState $B);shellEvidence=$r.shellEvidence;liveShellCommitted=$false}
    }
    throw 'N5.Arguments: unreachable command.'
}

function Initialize-N5Clock {
    if((Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot 'NativeDeploymentJob.psm1'))) -cne $script:N5JobHash){throw 'N5.Bootstrap: frozen job module mismatch.'}
    Import-Module (Join-Path $PSScriptRoot 'NativeDeploymentJob.psm1') -ErrorAction Stop
}
function Get-N5Remaining { Get-NativeDeadlineRemaining $script:N5Start $script:N5Deadline }
function Read-N5Pipe($Pipe,[int]$Count) {
    $bytes=New-Object byte[] $Count;$offset=0
    while($offset -lt $Count){$task=$Pipe.ReadAsync($bytes,$offset,$Count-$offset);if(-not $task.Wait((Get-N5Remaining))){throw 'N5.Deadline: pipe read.'};$n=$task.GetAwaiter().GetResult();if($n -eq 0){throw 'N5.Transport: pipe EOF.'};$offset+=$n}
    ,$bytes
}
function Send-N5Pipe($Pipe,$Value) {
    $bytes=ConvertTo-N5Bytes $Value;if($bytes.Length -gt 32768){throw 'N5.Transport: request bound.'}
    foreach($part in @([BitConverter]::GetBytes([int]$bytes.Length),$bytes)){$task=$Pipe.WriteAsync($part,0,$part.Length);if(-not $task.Wait((Get-N5Remaining))){throw 'N5.Deadline: pipe write.'};$null=$task.GetAwaiter().GetResult()}
}
function Receive-N5Pipe($Pipe) {
    $length=[BitConverter]::ToInt32((Read-N5Pipe $Pipe 4),0)
    if($length -lt 1 -or $length -gt 32768){throw 'N5.Transport: bounded frame required.'}
    ConvertFrom-N5Bytes (Read-N5Pipe $Pipe $length)
}
# NM-N7-OWNED-CAPABILITY-01: all membership comes from retained Jobs, never result files.
function Get-N7JobMembers([IntPtr]$Job) {
    if ((Get-N5Remaining) -le 0) { throw 'N7.Deadline: membership budget.' }
    $capacity=4096;$size=8+$capacity*[IntPtr]::Size
    $buffer=[Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        if (-not [WinSight.DeploymentJobApi]::QueryInformationJobObject($Job,3,$buffer,$size,[IntPtr]::Zero)) { throw 'N7.JobQueryUnknown: native false; cached LastError unqualified.' }
        $assigned=[Runtime.InteropServices.Marshal]::ReadInt32($buffer,0);$listed=[Runtime.InteropServices.Marshal]::ReadInt32($buffer,4)
        if ($assigned -lt 0 -or $listed -lt 0 -or $assigned -ne $listed -or $listed -gt $capacity) { throw 'N7.JobQueryUnknown: incomplete/out-of-bounds membership.' }
        $ids=[Collections.Generic.List[long]]::new();$seen=[Collections.Generic.HashSet[long]]::new()
        for($i=0;$i -lt $listed;$i++) {
            $value=[Runtime.InteropServices.Marshal]::ReadIntPtr($buffer,8+$i*[IntPtr]::Size).ToInt64()
            if($value -le 0 -or $value -gt [uint32]::MaxValue -or -not $seen.Add($value)){throw 'N7.JobQueryUnknown: invalid/duplicate process identity.'}
            $ids.Add($value)
        }
        if ((Get-N5Remaining) -le 0) { throw 'N7.Deadline: membership budget.' }
        ,$ids.ToArray()
    } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer) }
}
function Assert-N7JobLimits([IntPtr]$Job) {
    if((Get-N5Remaining) -le 0){throw 'N7.Deadline: limit query budget.'}
    $size=112;if([IntPtr]::Size -eq 8){$size=144}
    $buffer=[Runtime.InteropServices.Marshal]::AllocHGlobal($size)
    try {
        if(-not [WinSight.DeploymentJobApi]::QueryInformationJobObject($Job,9,$buffer,$size,[IntPtr]::Zero)){throw 'N7.JobQueryUnknown: limit query false.'}
        $flags=[Runtime.InteropServices.Marshal]::ReadInt32($buffer,16)
        if(($flags -band 0x1800) -ne 0 -or ($flags -band 0x2000) -eq 0){throw 'N7.JobLimits: breakaway permitted or kill-on-close absent.'}
        if((Get-N5Remaining) -le 0){throw 'N7.Deadline: limit query budget.'}
        $flags
    } finally { [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer) }
}
function Initialize-N5Readers($R,$Process) {
    $R.streams=@($Process.StandardOutput,$Process.StandardError)
    for($i=0;$i -lt 2;$i++){$R.buffers+=,(New-Object char[] 2048);$R.tasks+=,$R.streams[$i].ReadAsync($R.buffers[$i],0,2048);$R.states[$i]='Pending'}
}
function Import-N7Protocol {
    Import-Module (Join-Path $PSScriptRoot 'NativeStandardLiveShell.psm1') -PassThru -ErrorAction Stop
}
function Assert-N7OwnerPlan($P,$Plan) {
    $live=Import-N7Protocol
    & $live {
        param($P,$Plan,$Directory,$Start,$Deadline)
        Assert-NLFields $P @('bindingPath','bindingSha256','intentSha256','previousReceiptSha256','operation','transactionId','StartTick','DeadlineMilliseconds')
        Assert-NLPath $P.bindingPath;Assert-NLHash $P.bindingSha256;Assert-NLHash $P.intentSha256;Assert-NLHash $P.previousReceiptSha256 -Nullable
        Assert-NLString $P.operation;Assert-NLString $P.transactionId
        Assert-NLInteger $P.StartTick 0 4294967295L;Assert-NLInteger $P.DeadlineMilliseconds 1000 160000
        if($P.operation -cnotin @('Apply','Remove') -or $P.transactionId -cnotmatch '^[0-9a-f]{32}$' -or $P.StartTick -ne $Start -or $P.DeadlineMilliseconds -ne $Deadline){throw 'N7.OwnerContext: operation/transaction/original clock mismatch.'}
        if($Plan -isnot [pscustomobject] -or $null -eq $Plan.PSObject.Properties['mode']){throw 'N7.OwnerPlan: mode required.'};Assert-NLString $Plan.mode
        $planFields=@('protocol','purpose','executable','fixture','mode','sourceSha256','adapterSha256')
        if($Plan.mode.StartsWith('Separate',[StringComparison]::Ordinal)){$planFields+='sourceManifest'}
        Assert-NLFields $Plan $planFields
        foreach($n in @('protocol','purpose','mode')){Assert-NLString $Plan.$n}
        Assert-NLPin $Plan.executable;Assert-NLPin $Plan.fixture;Assert-NLHash $Plan.sourceSha256;Assert-NLHash $Plan.adapterSha256
        # This private seam is deliberately not a genuine-Inno plan resolver or public test mode.
        if($Plan.protocol -cne 'NativeShellPlan/1' -or $Plan.purpose -cne 'InertOwnerQualification' -or $Plan.mode -cnotin @('Observe','Timeout','Separate','SeparateTimeout','SeparateDead')){throw 'N7.OwnerPlan: no qualified production plan resolver.'}
        $fixtureRoot=[IO.Path]::GetFullPath((Join-Path $Directory '..\..\.dev\n7-a\fixtures'))+'\'
        if(-not $Plan.fixture.path.StartsWith($fixtureRoot,[StringComparison]::Ordinal) -or $Plan.fixture.path.Contains('"') -or [IO.Path]::GetExtension($Plan.fixture.path) -cne '.ps1'){throw 'N7.OwnerPlan: private inert fixture scope.'}
        $current=[Diagnostics.Process]::GetCurrentProcess()
        try{$hostPath=$current.MainModule.FileName}finally{$current.Dispose()}
        if($Plan.executable.path -cne $hostPath -or $PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1 -or [IntPtr]::Size -ne 8){throw 'N7.OwnerPlan: fixed actual PS5.1 x64 host only.'}
        foreach($pin in @($Plan.executable,$Plan.fixture)){
            $actual=Get-NLFilePin $pin.path $Start $Deadline
            if($actual.bytes -ne $pin.bytes -or $actual.sha256 -cne $pin.sha256){throw 'N7.OwnerPlan: physical pin mismatch.'}
        }
        $binding=Read-NativeLiveBinding -BindingPath $P.bindingPath -BindingSha256 $P.bindingSha256 -StartTick $Start -DeadlineMilliseconds $Deadline
        Assert-NLBinding $binding
        if($Plan.mode.StartsWith('Separate',[StringComparison]::Ordinal)){
            Assert-NLPin $Plan.sourceManifest
            if(-not $Plan.sourceManifest.path.StartsWith($fixtureRoot,[StringComparison]::Ordinal)){throw 'N7.OwnerGraph: private source graph must stay in fixture scope.'}
            $control=[IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($Plan.sourceManifest.path))
            $sources=Read-NLGraphObject $Plan.sourceManifest $control $Start $Deadline;Assert-NLSources $sources
            if($Plan.sourceManifest.sha256 -cne $binding.sources.sha256 -or $Plan.sourceManifest.bytes -ne $binding.sources.bytes){throw 'N7.OwnerGraph: binding source digest/length mismatch.'}
            foreach($entry in $sources.files){
                $pin=[pscustomobject]@{path=($control+'\runtime\'+$Plan.sourceManifest.sha256+'\'+$entry.name);bytes=$entry.bytes;sha256=$entry.sha256};Assert-NLPhysicalPin $pin $control $Start $Deadline
                if($entry.name -cin @('NativeInstallCoordinator.psm1','Invoke-NativeInstallCoordinator.ps1','NativeStandardLiveShell.psm1','NativeDeploymentJob.psm1')){$actual=Get-NLFilePin (Join-Path $Directory $entry.name) $Start $Deadline;if($actual.sha256 -cne $entry.sha256 -or $actual.bytes -ne $entry.bytes){throw 'N7.OwnerGraph: callback/owner current source differs from staged graph.'}}
            }
        }
    } ([pscustomobject]$P) $Plan $PSScriptRoot $script:N5Start $script:N5Deadline
    $adapterHash=Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot 'NativeStandardLiveShell.psm1'))
    $sourceHash=Get-N5Hash (ConvertTo-N5Bytes ([pscustomobject]@{coordinator=(Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot 'NativeInstallCoordinator.psm1')));entry=(Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot 'Invoke-NativeInstallCoordinator.ps1')));adapter=$adapterHash;job=$script:N5JobHash}))
    if($adapterHash -cne $Plan.adapterSha256 -or $sourceHash -cne $Plan.sourceSha256){throw 'N7.OwnerSource: actual sibling graph mismatch.'}
}
function Invoke-N7CallbackClient {
    # The actual entry invokes this only after fixed sibling pins and exclusive arguments pass.
    foreach($n in @('N7_OWNER_PIPE','N7_NONCE','N7_REQUEST','N7_SOURCE','N7_GRAPH','N7_BINDING','N7_INTENT','N7_OPERATION','N7_TRANSACTION','N7_PARENT_PID','N7_PARENT_START','N7_START','N7_DEADLINE')){if(-not [Environment]::GetEnvironmentVariable($n)){throw 'N7.CallbackContext: incomplete inherited owner session.'}}
    if($env:N7_OWNER_PIPE -cnotmatch '^WinSightN7-[0-9a-f]{32}$'){throw 'N7.CallbackContext: constrained local pipe name required.'}
    foreach($v in @($env:N7_NONCE,$env:N7_REQUEST,$env:N7_SOURCE,$env:N7_GRAPH,$env:N7_BINDING,$env:N7_INTENT)){Assert-N5Hash $v}
    if($env:N7_PREVIOUS){Assert-N5Hash $env:N7_PREVIOUS}
    if($env:N7_OPERATION -cnotin @('Apply','Remove') -or $env:N7_TRANSACTION -cnotmatch '^[0-9a-f]{32}$' -or $env:N7_PARENT_PID -cnotmatch '^[1-9][0-9]{0,9}$' -or $env:N7_PARENT_START -cnotmatch '^[1-9][0-9]{0,18}$'){throw 'N7.CallbackContext: parent/operation/transaction fields.'}
    Initialize-N5Clock;$script:N5Start=[long]$env:N7_START;$script:N5Deadline=[int]$env:N7_DEADLINE
    if($script:N5Start -lt 0 -or $script:N5Start -gt 4294967295L -or $script:N5Deadline -lt 1000 -or $script:N5Deadline -gt 160000 -or (Get-N5Remaining) -le 0){throw 'N7.Deadline: callback original clock invalid/expired.'}
    $current=[Diagnostics.Process]::GetCurrentProcess()
    try{$clientTicks=$current.StartTime.ToUniversalTime().Ticks;$clientId=$current.Id}finally{$current.Dispose()}
    $frame=[pscustomobject]@{protocol='NativeShellCallback/1';ownerRequestSha256=$env:N7_REQUEST;nonce=$env:N7_NONCE;sourceSha256=$env:N7_SOURCE;bindingSha256=$env:N7_BINDING;intentSha256=$env:N7_INTENT;previousReceiptSha256=$(if($env:N7_PREVIOUS){$env:N7_PREVIOUS}else{$null});operation=$env:N7_OPERATION;transactionId=$env:N7_TRANSACTION;sequence=1;command='Inspect';processId=$clientId;processStartTicks=$clientTicks;startTick=$script:N5Start;deadlineMilliseconds=$script:N5Deadline;sourceGraphSha256=$env:N7_GRAPH;parentProcessId=[int]$env:N7_PARENT_PID;parentStartTicks=[long]$env:N7_PARENT_START}
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$env:N7_OWNER_PIPE,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    $replies=[Collections.Generic.List[object]]::new()
    try{
        $pipe.Connect([Math]::Min(3000,(Get-N5Remaining)))
        foreach($command in @('Inspect','Complete')){
            $frame.command=$command;$frame.sequence=if($command -ceq 'Inspect'){1}else{2};Send-N5Pipe $pipe $frame;$reply=Receive-N5Pipe $pipe
            Assert-N5Fields $reply @('protocol','status','reason','sequence')
            if($reply.protocol -isnot [string] -or $reply.status -isnot [string]){throw 'N7.CallbackReply: scalar reply fields required.'}
            Assert-N5Integer $reply.sequence 0 2
            if($reply.status -ceq 'Rejected'){if($reply.reason -isnot [string]){throw 'N7.CallbackReply: rejection reason required.'};throw ('N7.CallbackRejected: '+$reply.reason)}
            $expected=if($command -ceq 'Inspect'){'OwnerObserved'}else{'OwnerCompleted'}
            if($reply.protocol -cne 'NativeShellCallbackReply/1' -or $reply.status -cne $expected -or $reply.sequence -ne $frame.sequence -or $null -ne $reply.reason){throw 'N7.CallbackReply: owner acknowledgment mismatch.'};$replies.Add($reply)
        }
        [pscustomobject]@{protocol='NativeShellClientResult/1';processId=$clientId;processStartTicks=$clientTicks;retainedParentPid=$frame.parentProcessId;sourceGraphSha256=$frame.sourceGraphSha256;replies=$replies.ToArray();readOnly=$true;osAuthority=$false}
    }finally{$pipe.Dispose()}
}
function Receive-N7OwnerFrame($Pipe) {
    try{$header=Read-N5Pipe $Pipe 4}catch{if($_.Exception.Message -ceq 'N5.Transport: pipe EOF.'){throw 'N7.CallbackDisconnected: session EOF.'};throw}
    $length=[BitConverter]::ToInt32($header,0)
    if($length -lt 1 -or $length -gt 32768){throw 'N7.OwnerFrame: byte bound.'}
    $live=Import-N7Protocol
    & $live {param($Bytes,$Start,$Deadline) ConvertFrom-NLBytes $Bytes $Start $Deadline 32768} (Read-N5Pipe $Pipe $length) $script:N5Start $script:N5Deadline
}
function Initialize-N7PipePeerApi {
    if('WinSight.NativeCoordinatorPipePeerApi' -as [type]){return}
    # N7-GC-IDENTITY-01: the only added native query binds a server connection's origin.
    $name=[Reflection.AssemblyName]::new('WinSight.NativeCoordinatorPipePeerApi')
    if($null -ne [AppDomain].GetMethod('DefineDynamicAssembly',[type[]]@([Reflection.AssemblyName],[Reflection.Emit.AssemblyBuilderAccess]))){$assembly=[AppDomain]::CurrentDomain.DefineDynamicAssembly($name,[Reflection.Emit.AssemblyBuilderAccess]::Run)}else{$assembly=[Reflection.Emit.AssemblyBuilder]::DefineDynamicAssembly($name,[Reflection.Emit.AssemblyBuilderAccess]::Run)}
    $builder=$assembly.DefineDynamicModule('Native').DefineType('WinSight.NativeCoordinatorPipePeerApi',[Reflection.TypeAttributes]'Public,Sealed,Abstract')
    $method=$builder.DefinePInvokeMethod('GetNamedPipeClientProcessId','kernel32.dll',[Reflection.MethodAttributes]'Public,Static,PinvokeImpl',[Reflection.CallingConventions]::Standard,[bool],[type[]]@([IntPtr],[uint32].MakeByRefType()),[Runtime.InteropServices.CallingConvention]::Winapi,[Runtime.InteropServices.CharSet]::Unicode)
    $null=$method.DefineParameter(2,[Reflection.ParameterAttributes]::Out,'ClientProcessId')
    $method.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
    $null=$builder.CreateType()
}
function Get-N7PipePeerProcessId($Server) {
    if((Get-N5Remaining) -le 0){throw 'N7.Deadline: pipe peer query budget.'}
    if($Server -isnot [IO.Pipes.NamedPipeServerStream] -or -not $Server.IsConnected){throw 'N7.PipePeerUnknown: connected server endpoint required.'}
    $safe=$Server.SafePipeHandle
    if($safe.IsClosed -or $safe.IsInvalid){throw 'N7.PipePeerUnknown: unavailable server handle.'}
    Initialize-N7PipePeerApi
    $added=$false
    try{
        $safe.DangerousAddRef([ref]$added)
        [uint32]$peer=0
        $ok=[WinSight.NativeCoordinatorPipePeerApi]::GetNamedPipeClientProcessId($safe.DangerousGetHandle(),[ref]$peer)
        if(-not $ok -or $peer -eq 0 -or $peer -gt [int]::MaxValue -or $safe.IsClosed -or -not $Server.IsConnected){throw 'N7.PipePeerUnknown: native failure/invalid identity/closed connection; no cached error or frame fallback.'}
        if((Get-N5Remaining) -le 0){throw 'N7.Deadline: pipe peer query budget.'}
        [int]$peer
    }finally{if($added){$safe.DangerousRelease()}}
}
function Assert-N7SeparatePipePeer([int]$Peer,[int]$Claimed,[int]$RetainedParent) {
    if($Peer -ne $Claimed){throw ('N7.OwnerPeerMismatch: connectedPeer='+$Peer+' claimed='+$Claimed+'.')}
    if($Peer -eq $PID -or $Peer -eq $RetainedParent){throw ('N7.OwnerPeerExcluded: peer='+$Peer+' worker='+$PID+' retainedParent='+$RetainedParent+'.')}
}
function Assert-N7OwnerFrame($Context,$Frame) {
    if($null -eq (Get-Variable -Name N7Capabilities -Scope Script -ErrorAction SilentlyContinue) -or -not $script:N7Capabilities.ContainsKey($Context.nonce) -or -not [Object]::ReferenceEquals($script:N7Capabilities[$Context.nonce],$Context)){throw 'N7.OwnerCapability: no retained live owner reference.'}
    $live=Import-N7Protocol
    & $live {
        param($F,$Separate)
        $names=@('protocol','ownerRequestSha256','nonce','sourceSha256','bindingSha256','intentSha256','previousReceiptSha256','operation','transactionId','sequence','command','processId','processStartTicks','startTick','deadlineMilliseconds')
        if($Separate){$names+=@('sourceGraphSha256','parentProcessId','parentStartTicks');Assert-NLFields $F $names;Assert-NLHash $F.sourceGraphSha256;Assert-NLInteger $F.parentProcessId 1 ([int]::MaxValue);Assert-NLInteger $F.parentStartTicks 1}else{Assert-NLFields $F $names}
        foreach($n in @('protocol','operation','transactionId','command')){Assert-NLString $F.$n}
        foreach($n in @('ownerRequestSha256','nonce','sourceSha256','bindingSha256','intentSha256')){Assert-NLHash $F.$n}
        Assert-NLHash $F.previousReceiptSha256 -Nullable
        Assert-NLInteger $F.sequence 1 2;Assert-NLInteger $F.processId 1 ([int]::MaxValue);Assert-NLInteger $F.processStartTicks 1;Assert-NLInteger $F.startTick 0 4294967295L;Assert-NLInteger $F.deadlineMilliseconds 1000 160000
    } $Frame $Context.separate
    if($Frame.protocol -cne 'NativeShellCallback/1' -or $Frame.command -cnotin @('Inspect','Complete')){throw 'N7.OwnerCommand: command not implemented; no commit authority.'}
    foreach($n in @('nonce','ownerRequestSha256','sourceSha256','bindingSha256','intentSha256','previousReceiptSha256','operation','transactionId','startTick','deadlineMilliseconds')){if($Frame.$n -cne $Context.$n){throw ('N7.OwnerBinding: '+$n+' mismatch.')}}
    if($Frame.sequence -ne ($Context.sequence+1) -or ($Frame.command -ceq 'Inspect' -and $Context.phase -cne 'AwaitObservation') -or ($Frame.command -ceq 'Complete' -and $Context.phase -cne 'Observed')){throw 'N7.OwnerReplay: one-use sequence/phase rejected.'}
    $p=$Context.process
    if($p -isnot [Diagnostics.Process] -or $p.HasExited){throw 'N7.OwnerParentDead: authoritative retained parent is not live.'}
    $members=Get-N7JobMembers ([IntPtr]::Zero)
    if($members -notcontains [long]$PID -or $members -notcontains [long]$p.Id){throw 'N7.OwnerMembership: actual parent/worker membership absent.'}
    $candidate=$null;$fact=$null;$peer=$null
    try{
        if($Context.separate){
            # Never use a frame PID to select a process. Endpoint handles must not be inherited/duplicated.
            $peer=Get-N7PipePeerProcessId $Context.server
            Assert-N7SeparatePipePeer $peer $Frame.processId $p.Id
            if($Frame.sourceGraphSha256 -cne $Context.plan.sourceManifest.sha256){throw 'N7.OwnerGraph: callback source graph mismatch.'}
            if($Frame.parentProcessId -ne $p.Id -or $Frame.parentStartTicks -ne $p.StartTime.ToUniversalTime().Ticks){throw 'N7.OwnerIdentity: retained parent identity mismatch.'}
            if($members -notcontains [long]$peer){throw 'N7.OwnerMembership: actual pipe peer absent/foreign/dead.'}
            $candidate=[Diagnostics.Process]::GetProcessById($peer);$null=$candidate.Handle
            if($candidate.HasExited -or $candidate.StartTime.ToUniversalTime().Ticks -ne $Frame.processStartTicks -or $candidate.MainModule.FileName -cne $Context.plan.executable.path){throw 'N7.OwnerClientIdentity: actual callback image/start/liveness mismatch.'}
            $pin=& $live {param($Path,$Start,$Deadline) Get-NLFilePin $Path $Start $Deadline} $candidate.MainModule.FileName $script:N5Start $script:N5Deadline
            if($pin.sha256 -cne $Context.plan.executable.sha256 -or $pin.bytes -ne $Context.plan.executable.bytes){throw 'N7.OwnerClientIdentity: actual callback executable pin mismatch.'}
            if($null -ne $Context.clientProcess -and ($Context.clientProcess.Id -ne $candidate.Id -or $Context.clientProcess.HasExited -or $Context.clientProcess.StartTime.ToUniversalTime().Ticks -ne $Frame.processStartTicks)){throw 'N7.OwnerClientIdentity: retained callback identity changed.'}
            $fact=[pscustomobject]@{processId=$candidate.Id;pipePeerProcessId=$peer;startTicks=$candidate.StartTime.ToUniversalTime().Ticks;imageSha256=$pin.sha256;actualMembers=$members;retainedParentPid=$p.Id;retainedParentLive=$true;sourceGraphSha256=$Frame.sourceGraphSha256}
        }elseif($Frame.processId -ne $p.Id -or $Frame.processStartTicks -ne $p.StartTime.ToUniversalTime().Ticks){throw 'N7.OwnerIdentity: actual retained child differs/exited.'}
        Assert-N7OwnerPlan $Context.parameters $Context.plan
        if($p.HasExited){throw 'N7.OwnerParentDead: parent exited during observation.'}
        if($Context.separate){
            if($candidate.HasExited -or (Get-N7JobMembers ([IntPtr]::Zero)) -notcontains [long]$peer -or (Get-N7PipePeerProcessId $Context.server) -ne $peer){throw 'N7.OwnerClientIdentity: peer exited/lost membership/connection during observation.'}
            # Attach only after ALL checks, so rejection cannot install a claimed member.
            if($null -eq $Context.clientProcess){$Context.clientProcess=$candidate;$candidate=$null}
            $Context.clientFact=$fact
        }
        $Context.sequence=$Frame.sequence;$Context.phase=if($Frame.command -ceq 'Inspect'){'Observed'}else{'Complete'}
        if($null -ne (Get-Variable N7ObservationSessions -Scope Script -ErrorAction SilentlyContinue) -and $script:N7ObservationSessions.ContainsKey($Context.nonce)){
            $session=$script:N7ObservationSessions[$Context.nonce]
            if(-not [Object]::ReferenceEquals($session.context,$Context)){throw 'N7.LiveSessionReference: owner session changed.'}
            $session.sequence=$Context.sequence;$session.phase=$Context.phase
        }
        [pscustomobject]@{protocol='NativeShellCallbackReply/1';status=$(if($Frame.command -ceq 'Inspect'){'OwnerObserved'}else{'OwnerCompleted'});reason=$null;sequence=$Frame.sequence}
    }finally{if($null -ne $candidate){$candidate.Dispose()}}
}
# Observational retention is not a transaction, Prepared state, or producer lease.
function Assert-N7ObservationSession($Context,$Correlation) {
    $entry=$null
    if($null -ne (Get-Variable N7ObservationSessions -Scope Script -ErrorAction SilentlyContinue)){
        foreach($candidate in $script:N7ObservationSessions.Values){if([Object]::ReferenceEquals($candidate.context,$Context)){$entry=$candidate;break}}
    }
    if($null -eq $entry){throw 'N7.LiveSessionReference: no registered observation session.'}
    if($Context.nonce -isnot [string] -or $Context.nonce -cne $entry.nonce){throw 'N7.LiveSessionBinding: nonce.'}
    if($null -eq (Get-Variable N7Capabilities -Scope Script -ErrorAction SilentlyContinue) -or -not $script:N7Capabilities.ContainsKey($entry.nonce) -or -not [Object]::ReferenceEquals($script:N7Capabilities[$entry.nonce],$Context)){throw 'N7.LiveSessionRevoked: owner reference revoked.'}
    if($Correlation -isnot [System.Management.Automation.PSCustomObject]){throw 'N7.LiveCorrelationType: raw object required.'}
    Assert-N5Fields $Correlation @('sourceSha256','bindingSha256','intentSha256','sequence','phase')
    foreach($name in @('sourceSha256','bindingSha256','intentSha256')){
        Assert-N5Hash $Correlation.$name
        if($Context.$name -isnot [string] -or $Correlation.$name -cne $entry.$name -or $Context.$name -cne $entry.$name){throw ('N7.LiveCorrelationMismatch: '+$name+'.')}
    }
    Assert-N5Integer $Correlation.sequence 0 2
    if($Correlation.phase -isnot [string]){throw 'N7.LivePhaseType: raw string required.'}
    if(($Context.sequence -isnot [int] -and $Context.sequence -isnot [long]) -or $Context.sequence -ne $entry.sequence -or $Correlation.sequence -ne $entry.sequence){throw 'N7.LiveSequence: owner sequence mismatch.'}
    if($Context.phase -isnot [string] -or $Context.phase -cne $entry.phase -or $Correlation.phase -cne $entry.phase){throw 'N7.LivePhase: owner phase mismatch.'}
    foreach($name in @('operation','transactionId','bindingPath')){if($Context.$name -isnot [string] -or $Context.$name -cne $entry.$name){throw ('N7.LiveSessionBinding: '+$name+'.')}}
    Assert-N5Hash $Context.previousReceiptSha256 -Nullable
    if($Context.previousReceiptSha256 -cne $entry.previousReceiptSha256){throw 'N7.LiveSessionBinding: previousReceiptSha256.'}
    if($Context.startTick -isnot [long] -or $Context.deadlineMilliseconds -isnot [int] -or $Context.startTick -ne $entry.startTick -or $Context.deadlineMilliseconds -ne $entry.deadlineMilliseconds -or $script:N5Start -ne $entry.startTick -or $script:N5Deadline -ne $entry.deadlineMilliseconds){throw 'N7.LiveClock: original owner clock mismatch.'}
    if((Get-N5Remaining) -le 5000){throw 'N7.LiveDeadline: observation cleanup reserve.'}
    $p=$entry.process
    if(-not [Object]::ReferenceEquals($Context.process,$p) -or -not [Object]::ReferenceEquals($Context.server,$entry.server) -or $p -isnot [Diagnostics.Process] -or $entry.server -isnot [IO.Pipes.NamedPipeServerStream]){throw 'N7.LiveResources: retained resource references differ.'}
    if($p.HasExited -or $p.Handle -eq [IntPtr]::Zero -or $p.Id -ne $entry.processId -or $p.StartTime.ToUniversalTime().Ticks -ne $entry.processStartTicks){throw 'N7.LiveProcess: retained process identity/liveness lost.'}
    if(-not $entry.server.IsConnected){throw 'N7.LivePeer: retained pipe disconnected.'}
    $members=Get-N7JobMembers ([IntPtr]::Zero)
    if($members -notcontains [long]$PID -or $members -notcontains [long]$p.Id){throw 'N7.LiveMembership: retained process/worker absent.'}
    $peer=Get-N7PipePeerProcessId $entry.server
    if(-not $Context.separate -and $peer -ne $p.Id){throw 'N7.LivePeer: retained native peer changed.'}
    if($Context.separate){
        if($null -eq $Context.clientProcess -or $Context.clientProcess.HasExited -or $peer -ne $Context.clientProcess.Id -or $members -notcontains [long]$peer){throw 'N7.LivePeer: retained callback peer absent/changed.'}
    }
}
function New-N7LiveTransactionContext($RetainedSession,$Correlation) {
    Assert-N7ObservationSession $RetainedSession $Correlation
    # No trusted graph/intent/selected-authority registration, Pending, Prepare,
    # or genuine producer exists here. Never promote Inspect/Observed to Prepared.
    throw 'N7.ProducerProvenanceUnavailable: observational retention cannot mint a transaction or fresh lease.'
}
function Invoke-N7OwnedWorker($Request,$ParentPipe) {
    Assert-N5Fields $Request @('schema','protocol','nonce','parentPid','assigned','parameters','plan')
    if($Request.protocol -isnot [string] -or $Request.protocol -cne 'NativeShellOwner/1' -or $Request.nonce -isnot [string] -or $Request.nonce -cne $env:N5_NONCE -or $Request.parentPid -isnot [int] -and $Request.parentPid -isnot [long] -or $Request.parentPid -ne [int]$env:N5_PARENT -or $Request.assigned -isnot [bool] -or -not $Request.assigned){throw 'N7.OwnerContext: authenticated assignment mismatch.'}
    Assert-N7OwnerPlan $Request.parameters $Request.plan
    $self=Get-N7JobMembers ([IntPtr]::Zero)
    if($self -notcontains [long]$PID){throw 'N7.OwnerMembership: worker not assigned before native birth.'}
    $plan=$Request.plan;$parameters=$Request.parameters
    $server=$null;$native=$null;$context=$null;$started=$false;$success=$false
    $r=[pscustomobject]@{tasks=@();buffers=@();states=@('NotStarted','NotStarted');streams=@();text=@('','');problem=$null}
    $replyCount=0;$rejections=[Collections.Generic.List[string]]::new();$nativeCleanup=[Collections.Generic.List[string]]::new();$primary=$null;$result=$null
    try {
        $pipeName='WinSightN7-'+[Guid]::NewGuid().ToString('N')
        $server=[IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous,65536,65536)
        $connect=$server.WaitForConnectionAsync()
        $native=[Diagnostics.Process]::new();$si=[Diagnostics.ProcessStartInfo]::new()
        $si.FileName=$plan.executable.path;$si.Arguments='-NoProfile -NonInteractive -File "'+$plan.fixture.path+'"';$si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
        $si.StandardOutputEncoding=$script:N5Utf8;$si.StandardErrorEncoding=$script:N5Utf8
        foreach($n in @($si.EnvironmentVariables.Keys)){if($n -clike 'N5_*' -or $n -clike 'N7_*'){$si.EnvironmentVariables.Remove($n)}}
        $values=@{N7_OWNER_PIPE=$pipeName;N7_NONCE=$Request.nonce;N7_REQUEST=$env:N5_REQUEST;N7_SOURCE=$plan.sourceSha256;N7_BINDING=$parameters.bindingSha256;N7_INTENT=$parameters.intentSha256;N7_PREVIOUS=$parameters.previousReceiptSha256;N7_OPERATION=$parameters.operation;N7_TRANSACTION=$parameters.transactionId;N7_START=$script:N5Start.ToString();N7_DEADLINE=$script:N5Deadline.ToString();N7_MODE=$plan.mode;N7_COORDINATOR=(Join-Path $PSScriptRoot 'NativeInstallCoordinator.psm1')}
        if($plan.mode.StartsWith('Separate',[StringComparison]::Ordinal)){
            $values.N7_GRAPH=$plan.sourceManifest.sha256;$values.N7_MODULE_HASH=$env:N5_MODULE;$values.N7_ENTRY_HASH=$env:N5_ENTRY;$values.N7_ENTRY=(Join-Path $PSScriptRoot 'Invoke-NativeInstallCoordinator.ps1')
        }
        foreach($n in $values.Keys){if($null -ne $values[$n]){$si.EnvironmentVariables[$n]=[string]$values[$n]}}
        $native.StartInfo=$si
        if((Get-N5Remaining) -le 10000){throw 'N7.Deadline: no native-start cleanup reserve.'}
        # Worker was assigned before request release. Native birth inherits that non-breakaway Job.
        $started=$native.Start();if(-not $started){throw 'N7.OwnerStart: native child not started.'};$null=$native.Handle
        Initialize-N5Readers $r $native
        $ticks=$native.StartTime.ToUniversalTime().Ticks
        Send-N5Pipe $ParentPipe ([pscustomobject]@{protocol='NativeShellOwnerEvent/1';event='Started';nonce=$Request.nonce;requestSha256=$env:N5_REQUEST;childPid=$native.Id;childStartTicks=$ticks;executableSha256=$plan.executable.sha256})
        $permit=Receive-N5Pipe $ParentPipe
        Assert-N5Fields $permit @('protocol','nonce','requestSha256','childPid','childStartTicks')
        Assert-N5Hash $permit.nonce;Assert-N5Hash $permit.requestSha256;Assert-N5Integer $permit.childPid 1 ([int]::MaxValue);Assert-N5Integer $permit.childStartTicks 1 ([long]::MaxValue)
        if($permit.protocol -isnot [string] -or $permit.protocol -cne 'NativeShellOwnerPermit/1' -or $permit.nonce -cne $Request.nonce -or $permit.requestSha256 -cne $env:N5_REQUEST -or $permit.childPid -ne $native.Id -or $permit.childStartTicks -ne $ticks){throw 'N7.OwnerContext: retained dispatcher permit mismatch.'}
        $context=[pscustomobject]@{nonce=$Request.nonce;ownerRequestSha256=$env:N5_REQUEST;sourceSha256=$plan.sourceSha256;bindingPath=$parameters.bindingPath;bindingSha256=$parameters.bindingSha256;intentSha256=$parameters.intentSha256;previousReceiptSha256=$parameters.previousReceiptSha256;operation=$parameters.operation;transactionId=$parameters.transactionId;startTick=$script:N5Start;deadlineMilliseconds=$script:N5Deadline;process=$native;parameters=$parameters;plan=$plan;sequence=0;phase='AwaitObservation';separate=$plan.mode.StartsWith('Separate',[StringComparison]::Ordinal);clientProcess=$null;clientFact=$null;server=$server}
        $script:N7Capabilities=@{};$script:N7Capabilities[$context.nonce]=$context
        # Only this engine retention point registers; descriptive input has no registrar.
        $script:N7ObservationSessions=@{}
        $script:N7ObservationSessions[$context.nonce]=[pscustomobject]@{context=$context;nonce=$context.nonce;process=$native;server=$server;processId=$native.Id;processStartTicks=$ticks;startTick=$script:N5Start;deadlineMilliseconds=$script:N5Deadline;sourceSha256=$context.sourceSha256;bindingSha256=$context.bindingSha256;intentSha256=$context.intentSha256;sequence=0;phase='AwaitObservation';operation=$context.operation;transactionId=$context.transactionId;bindingPath=$context.bindingPath;previousReceiptSha256=$context.previousReceiptSha256}
        if(-not $connect.Wait([Math]::Max(0,(Get-N5Remaining)-5000))){throw 'N7.Deadline: callback connect.'};$null=$connect.GetAwaiter().GetResult()
        $sessions=1
        while($context.phase -cne 'Complete') {
            if($replyCount -ge 32){throw 'N7.OwnerFrame: callback count bound.'};$fatal=$null
            try{$frame=Receive-N7OwnerFrame $server;$reply=Assert-N7OwnerFrame $context $frame}
            catch {
                if((Get-N5Remaining) -le 5000){throw}
                if($context.separate -and $_.Exception.Message.StartsWith('N7.CallbackDisconnected:',[StringComparison]::Ordinal)){
                    $sessions++;if($sessions -gt 4){throw 'N7.OwnerFrame: session count bound.'};$server.Disconnect();$connect=$server.WaitForConnectionAsync();if(-not $connect.Wait([Math]::Max(0,(Get-N5Remaining)-5000))){throw 'N7.Deadline: callback reconnect.'};$null=$connect.GetAwaiter().GetResult();continue
                }
                $reason=Limit-N5Message $_.Exception.Message 256;$rejections.Add($reason)
                if($reason.StartsWith('N7.OwnerParentDead:',[StringComparison]::Ordinal)){$fatal=$_}
                $reply=[pscustomobject]@{protocol='NativeShellCallbackReply/1';status='Rejected';reason=$reason;sequence=0}
            }
            Send-N5Pipe $server $reply;$replyCount++;if($null -ne $fatal){throw $fatal}
        }
        while((Get-N5Remaining) -gt 4000){Update-N5Readers $r;if($r.problem){throw $r.problem};if($native.HasExited -and $r.states[0] -ceq 'Eof' -and $r.states[1] -ceq 'Eof'){$success=$true;break};Start-Sleep -Milliseconds 5}
        if(-not $success){throw 'N7.Deadline: retained native process/readers.'}
        if($native.ExitCode -ne 0 -or $r.text[1].Length){throw 'N7.OwnerExit: native exit/stderr not clean.'}
        $result=[pscustomobject]@{protocol='NativeShellOwnerResult/1';status='ObservedOnly';requestSha256=$env:N5_REQUEST;childPid=$native.Id;childStartTicks=$ticks;childExitCode=$native.ExitCode;childStdoutState=$r.states[0];childStderrState=$r.states[1];callbackCount=$replyCount;rejections=$rejections.ToArray();nativeOutput=$r.text[0];phase=$context.phase;startTick=$script:N5Start;deadlineMilliseconds=$script:N5Deadline;liveCommit=$false;callbackClient=$context.clientFact}
    } catch {$primary=$_} finally {
        try{if($null -ne $context -and $null -ne (Get-Variable N7ObservationSessions -Scope Script -ErrorAction SilentlyContinue)){$script:N7ObservationSessions.Remove($context.nonce)}}catch{$nativeCleanup.Add('Observation session revocation: '+$_.Exception.Message)}
        if($null -ne $context -and $null -ne (Get-Variable N7Capabilities -Scope Script -ErrorAction SilentlyContinue)){$script:N7Capabilities.Remove($context.nonce)}
        try{if($started -and -not $success -and -not $native.HasExited){$native.Kill();if(-not $native.WaitForExit([Math]::Max(0,(Get-N5Remaining)-1000))){throw 'retained child wait incomplete'}}}catch{$nativeCleanup.Add($_.Exception.Message)}
        foreach($stream in $r.streams){try{$stream.Close()}catch{$nativeCleanup.Add($_.Exception.Message)}}
        try{if($r.tasks.Count -and -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$r.tasks,[Math]::Max(0,(Get-N5Remaining)-500))){throw 'native reader wait incomplete'}}catch{$nativeCleanup.Add($_.Exception.Message)}
        $client=if($null -ne $context){$context.clientProcess}else{$null}
        foreach($resource in @($server,$native,$client)){try{if($null -ne $resource){$resource.Dispose()}}catch{$nativeCleanup.Add($_.Exception.Message)}}
    }
    if($null -ne $primary){$primary.Exception.Data['N7NativeCleanup']=$nativeCleanup.ToArray();throw $primary}
    if($nativeCleanup.Count){throw ('N7.NativeCleanup: '+($nativeCleanup -join '; '))}
    $result
}
function Invoke-N7OwnerPermit($Pipe,$Job,$Worker,$Plan,[string]$Nonce,[string]$RequestHash) {
    $ownerEvent=Receive-N5Pipe $Pipe
    Assert-N5Fields $ownerEvent @('protocol','event','nonce','requestSha256','childPid','childStartTicks','executableSha256')
    Assert-N5Integer $ownerEvent.childPid 1 ([int]::MaxValue);Assert-N5Integer $ownerEvent.childStartTicks 1 ([long]::MaxValue)
    foreach($n in @('nonce','requestSha256','executableSha256')){Assert-N5Hash $ownerEvent.$n}
    if($ownerEvent.protocol -isnot [string] -or $ownerEvent.event -isnot [string] -or $ownerEvent.protocol -cne 'NativeShellOwnerEvent/1' -or $ownerEvent.event -cne 'Started' -or $ownerEvent.nonce -cne $Nonce -or $ownerEvent.requestSha256 -cne $RequestHash -or $ownerEvent.executableSha256 -cne $Plan.executable.sha256 -or $Worker.HasExited){throw 'N7.OwnerIdentity: retained worker event mismatch.'}
    $members=Get-N7JobMembers $Job
    if($members -notcontains [long]$Worker.Id -or $members -notcontains [long]$ownerEvent.childPid){throw 'N7.OwnerMembership: actual root Job lacks child/worker.'}
    Send-N5Pipe $Pipe ([pscustomobject]@{protocol='NativeShellOwnerPermit/1';nonce=$Nonce;requestSha256=$RequestHash;childPid=$ownerEvent.childPid;childStartTicks=$ownerEvent.childStartTicks})
    ,$members
}
function Invoke-N5Worker {
    $names=@('N5_PIPE','N5_NONCE','N5_PARENT','N5_PARENT_START','N5_REQUEST','N5_MODULE','N5_ENTRY','N5_START','N5_DEADLINE')
    foreach($n in $names){if(-not [Environment]::GetEnvironmentVariable($n)){throw 'N5.Worker: incomplete capability.'}}
    if($env:N5_PIPE -cnotmatch '^WinSightN5-[0-9a-f]{32}$' -or $env:N5_NONCE -cnotmatch '^[0-9a-f]{64}$'){throw 'N5.Worker: invalid capability.'}
    $script:N5Start=[long]$env:N5_START;$script:N5Deadline=[int]$env:N5_DEADLINE
    if($script:N5Start -lt 0 -or $script:N5Start -gt 4294967295L -or $script:N5Deadline -lt 1000 -or $script:N5Deadline -gt 160000 -or (Get-N5Remaining) -le 0){throw 'N5.Worker: invalid shared deadline.'}
    foreach($row in @(@('NativeInstallCoordinator.psm1',$env:N5_MODULE),@('Invoke-NativeInstallCoordinator.ps1',$env:N5_ENTRY))){Assert-N5Hash $row[1];if((Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot $row[0]))) -cne $row[1]){throw 'N5.Worker: sibling identity mismatch.'}}
    Assert-N5Hash $env:N5_REQUEST
    $parent=[Diagnostics.Process]::GetProcessById([int]$env:N5_PARENT)
    try{if($parent.StartTime.ToUniversalTime().Ticks.ToString() -cne $env:N5_PARENT_START -or $parent.MainModule.FileName -cne [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName -or $parent.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId){throw 'N5.Worker: parent identity mismatch.'}}finally{$parent.Dispose()}
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$env:N5_PIPE,[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous)
    try{
        $pipe.Connect([Math]::Min(3000,(Get-N5Remaining)))
        Send-N5Pipe $pipe ([pscustomobject]@{nonce=$env:N5_NONCE;childPid=$PID;parentPid=[int]$env:N5_PARENT})
        $envelope=Receive-N5Pipe $pipe
        Assert-N5Fields $envelope @('childPid','request')
        $request=$envelope.request
        if($envelope.childPid -ne $PID -or (Get-N5Hash (ConvertTo-N5Bytes $request)) -cne $env:N5_REQUEST){throw 'N5.Worker: request pin/owned PID mismatch.'}
        if($request -is [System.Management.Automation.PSCustomObject] -and $null -ne $request.PSObject.Properties['schema'] -and ($request.schema -is [int] -or $request.schema -is [long])){
            if($request.schema -eq 4){return Invoke-N7LiveWorker $request $pipe}
            if($request.schema -eq 5){return Invoke-N7LiveCoreWorker $request}
            if($request.schema -eq 6){return Invoke-N7LaunchWorker $request}
            if($request.schema -eq 7){return Invoke-N7PreparedCopyWorker $request}
            if($request.schema -eq 3){return Invoke-N7OwnedWorker $request $pipe}
        }
        Assert-N5Fields $request @('schema','nonce','parentPid','assigned','parameters')
        if($request.schema -ne 1 -or $request.nonce -cne $env:N5_NONCE -or $request.parentPid -ne [int]$env:N5_PARENT -or $request.assigned -isnot [bool] -or -not $request.assigned){throw 'N5.Worker: assignment challenge mismatch.'}
        $p=@{};foreach($prop in $request.parameters.PSObject.Properties){$p[$prop.Name]=$prop.Value}
        Assert-N5Arguments $p
        if($p.StartTick -ne $script:N5Start -or $p.DeadlineMilliseconds -ne $script:N5Deadline){throw 'N5.Worker: deadline context mismatch.'}
        $b=Read-N5Binding $p.BindingPath $p.BindingSha256
        if($b.mode -ceq 'Production' -and $p.Command -cne 'Inspect'){throw 'N5.AdapterUnavailable: Production mutating commands are disabled.'}
        Import-Module (Join-Path $PSScriptRoot 'NativeInstaller.psm1') -ErrorAction Stop
        $key=Get-N5Hash ($script:N5Utf8.GetBytes($b.payloadRoot.ToUpperInvariant()))
        $mutex=[Threading.Mutex]::new($false,('Local\WinSight.NativeCoordinator.'+$key));$held=$false
        try{
            try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true}
            if(-not $held){throw 'N5.Concurrent: coordinator writer rejected.'}
            # Hold the engine's cooperating identity mutex across the entire CAS sequence.
            $coreMutex=[Threading.Mutex]::new($false,('Local\WinSight.NativeInstaller.'+$key.ToUpperInvariant()));$coreHeld=$false
            try{
                try{$coreHeld=$coreMutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$coreHeld=$true}
                if(-not $coreHeld){throw 'N5.Concurrent: native writer rejected.'}
                if((Get-N5Remaining) -le 0){throw 'N5.Deadline: before kernel.'}
                Invoke-N5Kernel $p $b
            }finally{if($coreHeld){$coreMutex.ReleaseMutex()};$coreMutex.Dispose()}
        }finally{if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}
    }finally{$pipe.Dispose();foreach($n in $names){[Environment]::SetEnvironmentVariable($n,$null)}}
}
function Limit-N5Message([string]$Message,[int]$Limit=2048) {
    if($Message.Length -gt $Limit){return $Message.Substring(0,$Limit)}
    $Message
}
function New-N5Failure($ErrorRecord,$Settlement) {
    $message=Limit-N5Message $ErrorRecord.Exception.Message
    if($ErrorRecord.Exception.Data.Contains('N5WorkerStderr')){
        $message=Limit-N5Message ($message+' Worker stderr: '+[string]$ErrorRecord.Exception.Data['N5WorkerStderr'])
    }
    $id=[string]$ErrorRecord.FullyQualifiedErrorId
    if($message -match '^(N[57]\.[A-Za-z]+)'){$id=$Matches[1]}
    [pscustomobject]@{schema=1;status=$(if($Settlement.cleanupState -cin @('Incomplete','Unknown')){'BlockedUnsettled'}else{'Failed'});primaryError=[pscustomobject]@{id=(Limit-N5Message $id 128);category=[string]$ErrorRecord.CategoryInfo.Category;message=$message};settlement=$Settlement}
}
function New-N5PrestartSettlement([long]$Elapsed) {
    [pscustomobject]@{workerStarted=$false;workerPid=$null;workerExited=$null;workerExitCode=$null;activeOwnedProcesses=$null;stdoutState='NotStarted';stderrState='NotStarted';readersSettled=$null;cleanupState='NotStarted';cleanupErrors=@();elapsedMilliseconds=$Elapsed;jobCreated=$false;pipeCreated=$false}
}
function Update-N5Readers($R,[switch]$Discard) {
    for($i=0;$i -lt $R.tasks.Count;$i++) {
        if($R.states[$i] -cne 'Pending' -or -not $R.tasks[$i].IsCompleted){continue}
        try {
            $n=$R.tasks[$i].GetAwaiter().GetResult()
            if($n -eq 0){$R.states[$i]='Eof';continue}
            if(-not $Discard){
                $R.text[$i]+=[string]::new($R.buffers[$i],0,$n)
                if($script:N5Utf8.GetByteCount($R.text[0])+$script:N5Utf8.GetByteCount($R.text[1]) -gt 32768){
                    $R.text[$i]='';$R.problem='N5.Process: output byte bound exceeded.'
                }
            }
            $R.tasks[$i]=$R.streams[$i].ReadAsync($R.buffers[$i],0,2048)
        }catch{
            $R.states[$i]='Faulted'
            if(-not $R.problem){$R.problem='N5.Reader: '+(Limit-N5Message $_.Exception.Message 256)}
        }
    }
}
function Invoke-N5Dispatch($P,$B,$OwnerPlan=$null,$LiveSession=$null) {
    $parent=$null;$pipe=$null;$process=$null;$job=[IntPtr]::Zero
    $started=$false;$assigned=$false;$complete=$false;$primary=$null;$result=$null
    $facts=New-N5PrestartSettlement 0
    $cleanup=[Collections.Generic.List[string]]::new()
    $r=[pscustomobject]@{tasks=@();buffers=@();states=@('NotStarted','NotStarted');streams=@();text=@('','');problem=$null}
    $ownerMembers=@();$workerMembers=@();$ownerFlags=$null
    if($null -ne $OwnerPlan){
        if($null -eq $LiveSession){Assert-N7OwnerPlan $P $OwnerPlan}else{Assert-N7LiveSession $LiveSession 'Preparing'}
        $facts|Add-Member -NotePropertyName rootMemberCount -NotePropertyValue $null
    }
    try{
        $hostPath=[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $parent=[Diagnostics.Process]::GetCurrentProcess()
        $nonce=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
        $pipeName='WinSightN5-'+[Guid]::NewGuid().ToString('N')
        $pipe=[IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous,65536,65536);$facts.pipeCreated=$true
        $process=[Diagnostics.Process]::new()
        $job=New-NativeDeploymentJob;$facts.jobCreated=$true
        if($null -ne $OwnerPlan){$ownerFlags=Assert-N7JobLimits $job}
        $si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=$hostPath;$si.Arguments='-NoProfile -NonInteractive -File "'+(Join-Path $PSScriptRoot 'Invoke-NativeInstallCoordinator.ps1')+'"';$si.UseShellExecute=$false;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
        $si.StandardOutputEncoding=$script:N5Utf8;$si.StandardErrorEncoding=$script:N5Utf8
        $values=@{N5_PIPE=$pipeName;N5_NONCE=$nonce;N5_PARENT=$PID.ToString();N5_PARENT_START=$parent.StartTime.ToUniversalTime().Ticks.ToString();N5_MODULE=$B.coordinatorSha256;N5_ENTRY=$B.entrySha256;N5_START=$script:N5Start.ToString();N5_DEADLINE=$script:N5Deadline.ToString();N5_REQUEST=('0'*64)}
        if($P.ContainsKey('preparedAbortCopy')){
            foreach($name in @($si.EnvironmentVariables.Keys)){if($name -like 'N5_*' -or $name -like 'N7_*'){$si.EnvironmentVariables.Remove($name)}}
            $si.EnvironmentVariables.Remove('PSModulePath')
        }
        foreach($n in @($si.EnvironmentVariables.Keys)){if($n -clike 'N5_*'){$si.EnvironmentVariables.Remove($n)}}
        foreach($n in $values.Keys){$si.EnvironmentVariables[$n]=$values[$n]}
        # Request pin is fixed before Start; the authenticated envelope adds the owned PID.
        $request=[pscustomobject]@{schema=1;nonce=$nonce;parentPid=$PID;assigned=$true;parameters=[pscustomobject]$P}
        if($P.ContainsKey('liveCorePhase')){$request=[pscustomobject]@{schema=5;protocol='NativeLiveCore/1';nonce=$nonce;parentPid=$PID;assigned=$true;parameters=[pscustomobject]$P}}
        if($P.ContainsKey('launchValidation')){
            if($null -ne $OwnerPlan -or $null -ne $LiveSession){throw 'N7.LaunchRequest: validator cannot own a native transaction.'}
            $request=[pscustomobject]@{schema=6;protocol='NativeLaunchValidator/1';nonce=$nonce;parentPid=$PID;assigned=$true;parameters=[pscustomobject]$P}
            $si.EnvironmentVariables.Remove('PSModulePath')
        }
        if($P.ContainsKey('preparedAbortCopy')){
            if($null -ne $OwnerPlan -or $null -ne $LiveSession -or $P.ContainsKey('launchValidation') -or $P.ContainsKey('liveCorePhase')){throw 'N7.CopyRequest: no native/other worker role permitted.'}
            $request=[pscustomobject]@{schema=7;protocol='NativePreparedAbortCopy/1';nonce=$nonce;parentPid=$PID;assigned=$true;parameters=[pscustomobject]$P}
        }
        if($null -ne $OwnerPlan){
            $schema=3;$protocol='NativeShellOwner/1';if($null -ne $LiveSession){$schema=4;$protocol='NativeLiveProducer/1'}
            $request=[pscustomobject]@{schema=$schema;protocol=$protocol;nonce=$nonce;parentPid=$PID;assigned=$true;parameters=[pscustomobject]$P;plan=$OwnerPlan}
        }
        $requestHash=Get-N5Hash (ConvertTo-N5Bytes $request)
        $si.EnvironmentVariables['N5_REQUEST']=$requestHash
        $process.StartInfo=$si
        if((Get-N5Remaining) -le 10000){throw 'N5.Deadline: insufficient dispatch reserve.'}
        $connect=$pipe.WaitForConnectionAsync();$started=$process.Start();if(-not $started){throw 'N5.Process: worker not started.'}
        $facts.workerStarted=$true;$facts.workerPid=$process.Id
        Initialize-N5Readers $r $process
        if(-not [WinSight.DeploymentJobApi]::AssignProcessToJobObject($job,$process.Handle)){throw 'N5.AssignmentUnknown: native false; cached LastError unqualified.'};$assigned=$true
        if($null -ne $OwnerPlan){$workerMembers=Get-N7JobMembers $job;if($workerMembers -notcontains [long]$process.Id){throw 'N7.OwnerMembership: worker absent before assignment gate.'}}
        if(-not $connect.Wait([Math]::Min(10000,[Math]::Max(0,(Get-N5Remaining)-10000)))){throw 'N5.Transport: worker connect deadline.'}
        $null=$connect.GetAwaiter().GetResult()
        $hello=Receive-N5Pipe $pipe;Assert-N5Fields $hello @('nonce','childPid','parentPid')
        if($hello.nonce -cne $nonce -or $hello.childPid -ne $process.Id -or $hello.parentPid -ne $PID){throw 'N5.Transport: owned child challenge mismatch.'}
        Send-N5Pipe $pipe ([pscustomobject]@{childPid=$process.Id;request=$request})
        if($null -ne $OwnerPlan){
            if($null -eq $LiveSession){$ownerMembers=Invoke-N7OwnerPermit $pipe $job $process $OwnerPlan $nonce $requestHash}
            else{
                $ownerMembers=Invoke-N7LivePermit $pipe $job $process $OwnerPlan $nonce $requestHash $LiveSession
                if($LiveSession.intent.operation -ceq 'Remove'){
                    foreach($phase in @('Preflight','Checkpoint')){Receive-N7RemoveRole $pipe $job $process $OwnerPlan $nonce $requestHash $LiveSession $phase}
                }
            }
        }
        while((Get-N5Remaining) -gt 10000){
            Update-N5Readers $r
            if($r.problem){throw $r.problem}
            if($process.HasExited -and $r.states[0] -ceq 'Eof' -and $r.states[1] -ceq 'Eof' -and (Get-NativeJobActiveProcesses $job) -eq 0){$complete=$true;break}
            Start-Sleep -Milliseconds 10
        }
        if(-not $complete){throw 'N5.Deadline: execution/drain exceeded shared budget; explicit recovery required.'}
        if($process.ExitCode -ne 0){throw ('N5.WorkerFailure: exit='+$process.ExitCode+' '+(Limit-N5Message $r.text[1].Trim() 2048))}
        if($r.text[1].Length){throw 'N5.Process: unexpected stderr retained in failure.'}
        $result=ConvertFrom-N5Bytes ($script:N5Utf8.GetBytes($r.text[0].TrimEnd([char[]]@("`r","`n"))))
        if($null -ne $LiveSession){
            # Continuation is inside the lifetime of the original root Job, Process and readers.
            $LiveSession.worker=$process;$LiveSession.job=$job;$LiveSession.readers=$r
            $LiveSession.phase='SettledPrecommit'
            $result=Complete-N7LiveTransaction $LiveSession $result
        }
    }catch{$primary=$_}
    finally{
        # Every resource has an independent cleanup guard: a failed Job API must not
        # skip process settlement, the second reader, or remaining handle disposal.
        if($job -ne [IntPtr]::Zero){
            try{$facts.activeOwnedProcesses=Get-NativeJobActiveProcesses $job}catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256));$facts.activeOwnedProcesses=$null}
            try{if($null -eq $facts.activeOwnedProcesses -or $facts.activeOwnedProcesses -gt 0){if(-not [WinSight.DeploymentJobApi]::TerminateJobObject($job,124)){throw 'N5.Cleanup: owned job termination failed.'}}}catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256))}
        }
        if($started){
            try{if(-not $assigned -and -not $process.HasExited){$process.Kill()}}catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256))}
            try{$facts.workerExited=$process.WaitForExit([Math]::Max(0,(Get-N5Remaining)-500));if($facts.workerExited){$facts.workerExitCode=$process.ExitCode}}catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256));$facts.workerExited=$null}
        }
        if($job -ne [IntPtr]::Zero){
            try{
                do{$facts.activeOwnedProcesses=Get-NativeJobActiveProcesses $job;if($facts.activeOwnedProcesses -eq 0){break};Start-Sleep -Milliseconds 10}while((Get-N5Remaining) -gt 400)
                if($null -ne $OwnerPlan){$facts.rootMemberCount=(Get-N7JobMembers $job).Count;if($facts.rootMemberCount -ne 0){throw 'N7.OwnerSettlement: root members remain.'}}
            }catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256));$facts.activeOwnedProcesses=$null}
        }
        if($r.tasks.Count){
            try{
                while((Get-N5Remaining) -gt 250 -and @($r.states | Where-Object {$_ -ceq 'Pending'}).Count){Update-N5Readers $r -Discard;Start-Sleep -Milliseconds 5}
                $waited=[Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$r.tasks,[Math]::Max(0,(Get-N5Remaining)-200))
                if(-not $waited){$cleanup.Add('N5.Cleanup: reader WaitAll returned false.')}
                Update-N5Readers $r -Discard
            }catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256))}
        }
        foreach($stream in $r.streams){try{$stream.Close()}catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256))}}
        # Closing a stream can fault an outstanding task; inspect each terminal state,
        # never interpret completed/faulted/canceled as successful EOF.
        for($i=0;$i -lt $r.tasks.Count;$i++){
            try{if($r.tasks[$i].IsFaulted -or $r.tasks[$i].IsCanceled){$r.states[$i]='Faulted';$null=$r.tasks[$i].Exception}elseif(-not $r.tasks[$i].IsCompleted){$r.states[$i]='Pending'}}catch{$r.states[$i]='Unknown';$cleanup.Add((Limit-N5Message $_.Exception.Message 256))}
        }
        if($null -ne $LiveSession){
            $nativeExit=$null
            try{if($null -ne $LiveSession.native -and $LiveSession.native.HasExited){$nativeExit=[int]$LiveSession.native.ExitCode}}catch{$cleanup.Add('Native exit observation: '+$_.Exception.Message)}
            $roleExit=$null
            try{if($null -ne $LiveSession.removeRole -and $LiveSession.removeRole.process.HasExited){$roleExit=$LiveSession.removeRole.process.ExitCode}}catch{$cleanup.Add('Delegated exit observation: '+$_.Exception.Message)}
            $facts|Add-Member -NotePropertyName delegatedExitCode -NotePropertyValue $roleExit
            $facts|Add-Member -NotePropertyName nativeRootExitCode -NotePropertyValue $nativeExit
            $facts|Add-Member -NotePropertyName livePhase -NotePropertyValue $LiveSession.phase
            $facts|Add-Member -NotePropertyName payloadRecovery -NotePropertyValue 'NotAttempted'
            if($null -ne $primary -and $LiveSession.phase -cin @('Prepared','SettledPrecommit') -and -not $LiveSession.activationAttempted -and $null -ne $nativeExit -and $facts.activeOwnedProcesses -eq 0 -and $r.states[0] -ceq 'Eof' -and $r.states[1] -ceq 'Eof' -and (Get-N5Remaining) -gt 15000){
                try{
                    Assert-N7Pending $LiveSession
                    $recoveryParameters=@{BindingPath=$LiveSession.bindingPath;BindingSha256=$LiveSession.graph.bindingSha256;IntentSha256=$LiveSession.intentHash;liveCorePhase='Recover';StartTick=$script:N5Start;DeadlineMilliseconds=$script:N5Deadline}
                    $recovered=Invoke-N5Dispatch $recoveryParameters $LiveSession.dispatchBinding
                    $facts.payloadRecovery=$recovered.operation
                    Add-N7Event $LiveSession 'ShellRepairRequired' $LiveSession.intent.journalSha256
                }catch{$facts.payloadRecovery='Failed';$cleanup.Add('Payload recovery: '+(Limit-N5Message $_.Exception.Message 256))}
            }
            $LiveSession.revoked=$true
            if($null -ne $LiveSession.removeRole){try{$LiveSession.removeRole.process.Dispose()}catch{$cleanup.Add('Delegated Process disposal: '+$_.Exception.Message)}}
            if($null -ne $LiveSession.native){try{$LiveSession.native.Dispose()}catch{$cleanup.Add('Native Process disposal: '+$_.Exception.Message)}}
        }
        if($job -ne [IntPtr]::Zero){try{if(-not [WinSight.DeploymentJobApi]::CloseHandle($job)){throw 'N5.Cleanup: Job CloseHandle failed.'}}catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256))}}
        foreach($resource in @($pipe,$process,$parent)){if($null -ne $resource){try{$resource.Dispose()}catch{$cleanup.Add((Limit-N5Message $_.Exception.Message 256))}}}
        $facts.stdoutState=$r.states[0];$facts.stderrState=$r.states[1]
        if($started){$facts.readersSettled=($r.tasks.Count -eq 2 -and $r.states[0] -ceq 'Eof' -and $r.states[1] -ceq 'Eof')}
        $facts.cleanupErrors=@($cleanup | Select-Object -First 8)
        if($r.problem -and $facts.cleanupErrors.Count -lt 8){$facts.cleanupErrors+=,(Limit-N5Message $r.problem 256)}
        $clean=$cleanup.Count -eq 0 -and (-not $started -or ($facts.workerExited -eq $true -and $facts.activeOwnedProcesses -eq 0 -and $facts.readersSettled -eq $true))
        if($null -ne $OwnerPlan -and $facts.rootMemberCount -ne 0){$clean=$false}
        $facts.cleanupState=if($clean){'Settled'}else{'Incomplete'}
        $facts.elapsedMilliseconds=((Get-NativeTick)-$script:N5Start+4294967296L)%4294967296L
    }
    if($null -eq $primary -and $facts.cleanupState -cne 'Settled'){try{throw 'N5.Cleanup: owned resources/readers not proved settled.'}catch{$primary=$_}}
    if($null -ne $primary){
        if($r.text[1].Length){$primary.Exception.Data['N5WorkerStderr']=Limit-N5Message $r.text[1].Trim() 1536}
        $primary.Exception.Data['N5Failure']=New-N5Failure $primary $facts;throw $primary
    }
    if($null -ne $OwnerPlan){$result|Add-Member -NotePropertyName ownerSettlement -NotePropertyValue ([pscustomobject]@{workerMembershipBeforeGate=$workerMembers;actualRootMembersBeforePermit=$ownerMembers;rootMemberCount=$facts.rootMemberCount;jobFlags=$ownerFlags;handlesClosed=$true;completeMemberIdentitySpectrum=$false})}
    if($P.ContainsKey('launchValidation')){
        if($facts.workerExitCode -isnot [int] -or $facts.workerExitCode -ne 0 -or $facts.workerExited -ne $true -or $facts.activeOwnedProcesses -ne 0 -or $facts.stdoutState -cne 'Eof' -or $facts.stderrState -cne 'Eof' -or $facts.cleanupState -cne 'Settled'){throw 'N7.LaunchSettlement: actual validator completion required.'}
        $result|Add-Member -NotePropertyName validatorSettlement -NotePropertyValue ([pscustomobject]@{workerExited=$facts.workerExited;exitCode=$facts.workerExitCode;activeOwnedProcesses=$facts.activeOwnedProcesses;stdoutState=$facts.stdoutState;stderrState=$facts.stderrState;cleanupState=$facts.cleanupState;handlesClosed=$true})
    }
    $result|Add-Member -NotePropertyName settlement -NotePropertyValue ([pscustomobject]@{workerPid=$facts.workerPid;exitCode=$facts.workerExitCode;activeOwnedProcesses=$facts.activeOwnedProcesses;readersSettled=$facts.readersSettled;elapsedMilliseconds=$facts.elapsedMilliseconds;startTick=$script:N5Start;deadlineMilliseconds=$script:N5Deadline;bootstrapElapsedMilliseconds=$script:N5BootstrapElapsed})
    $result
}
function Invoke-NativeInstallCoordinator {
    [CmdletBinding()]
    param(
        [ValidateSet('Inspect','CreateIntent','Prepare','Commit','Finalize','Recover','ResumeFinalize','ValidateLaunch','RunOwnedShell','AbortPreparedCopy')][string]$Command,
        [ValidateSet('Apply','Remove')][string]$Operation,
        [string]$BindingPath,[string]$BindingSha256,[string]$TransactionId,[string]$StageDirectory,[string]$ManifestSha256,[string]$IntentSha256,[string]$ExpectedReceiptSha256,[string]$OuterResultSha256,
        [string]$CopyCaseRoot,[string]$CopyWitnessSha256,
        [ValidateRange(1000,160000)][int]$DeadlineMilliseconds=140000,[long]$StartTick=-1
    )
    $bootstrap=[Diagnostics.Stopwatch]::StartNew()
    $nativeReady=$false
    try {
        Initialize-N5Clock
        $sample=Get-NativeTick
        $nativeReady=$true
        $script:N5BootstrapElapsed=[long][Math]::Ceiling($bootstrap.Elapsed.TotalMilliseconds)
        $script:N5Start=$StartTick;$script:N5Deadline=$DeadlineMilliseconds
        # Sample native tick first, then elapsed Stopwatch duration; subtracting an
        # extra 32ms conservatively covers GetTickCount quantization without mixing epochs.
        if($StartTick -eq -1){$script:N5Start=($sample-$script:N5BootstrapElapsed-32+4294967296L)%4294967296L}
        if([Environment]::GetEnvironmentVariable('N5_PIPE')){
            if($PSBoundParameters.Count){throw 'N5.Worker: public arguments mixed with internal capability.'}
            return Invoke-N5Worker
        }
        if($script:N5Start -lt 0 -or $script:N5Start -gt 4294967295L -or (Get-N5Remaining) -le 0){throw 'N5.Deadline: invalid/expired native tick.'}
        foreach($n in @('N5_NONCE','N5_PARENT','N5_PARENT_START','N5_REQUEST','N5_MODULE','N5_ENTRY','N5_START','N5_DEADLINE')){if([Environment]::GetEnvironmentVariable($n)){throw 'N5.Worker: stray internal environment rejected.'}}
        $p=@{};foreach($k in $PSBoundParameters.Keys){$p[$k]=$PSBoundParameters[$k]};$p.StartTick=$script:N5Start;$p.DeadlineMilliseconds=$DeadlineMilliseconds
        if($Command -ceq 'AbortPreparedCopy'){return Invoke-N7PreparedCopyDispatch $p}
        if($p.ContainsKey('CopyCaseRoot') -or $p.ContainsKey('CopyWitnessSha256')){throw 'N7.CopyArguments: copy arguments cannot select a legacy/live command.'}
        # Dispatch by explicit schema, never by absent roots or a caller's claimed settlement.
        $wireBytes=Read-N5Bytes $BindingPath 32768
        Assert-N5Hash $BindingSha256
        if ((Get-N5Hash $wireBytes) -cne $BindingSha256) { throw 'N5.Trust: object hash mismatch.' }
        $wire=ConvertFrom-N5Bytes $wireBytes
        if ($wire -is [System.Management.Automation.PSCustomObject] -and $null -ne $wire.PSObject.Properties['schema'] -and ($wire.schema -is [int] -or $wire.schema -is [long]) -and $wire.schema -eq 3) {
            $live=Import-Module (Join-Path $PSScriptRoot 'NativeStandardLiveShell.psm1') -PassThru -ErrorAction Stop
            & $live {
                param($Parameters,$Bytes,$Tick,$Milliseconds)
                $binding=ConvertFrom-NLBytes $Bytes $Tick $Milliseconds 32768
                Assert-NLBinding $binding
                Assert-NLArguments $Parameters
            } $p $wireBytes $script:N5Start $script:N5Deadline
            return Invoke-N7LiveRequest $p $live
        }
        Assert-N5Arguments $p
        $b=Read-N5Binding $BindingPath $BindingSha256
        if((Get-N5Remaining) -le 0){throw 'N5.Deadline: bootstrap exhausted shared budget.'}
        if($b.mode -ceq 'Production' -and $Command -cne 'Inspect'){throw 'N5.AdapterUnavailable: Production mutating commands are disabled.'}
        Invoke-N5Dispatch $p $b
    }catch{
        if(-not $_.Exception.Data.Contains('N5Failure')){
            $primary=$_;$settlement=New-N5PrestartSettlement ([long][Math]::Ceiling($bootstrap.Elapsed.TotalMilliseconds))
            if($nativeReady -and $script:N5Start -ge 0 -and $script:N5Start -le 4294967295L){
                try{$settlement.elapsedMilliseconds=((Get-NativeTick)-$script:N5Start+4294967296L)%4294967296L}catch{$settlement.cleanupErrors=@('Native elapsed observation unavailable; elapsed is invocation Stopwatch duration.')}
            }
            $primary.Exception.Data['N5Failure']=New-N5Failure $primary $settlement
        }
        if($_.Exception.Data.Contains('N7LaunchValidatorCleanup') -and @($_.Exception.Data['N7LaunchValidatorCleanup']).Count){
            Add-N7LaunchCleanupFailureMessage $_
        }
        if($_.Exception.Data.Contains('N7CopyPythonCleanup') -and @($_.Exception.Data['N7CopyPythonCleanup']).Count){
            $failure=$_.Exception.Data['N5Failure']
            $failure.primaryError.message=(Limit-N5Message $failure.primaryError.message 1200)+' [Copy Python secondary cleanup: '+(Limit-N5Message (@($_.Exception.Data['N7CopyPythonCleanup']) -join '; ') 500)+']'
        }
        throw
    }
}
# Live state is a singleton owned by this imported module, never a deserialized lease.
$script:N7LiveActive=$null
function Get-N7RecordHash($Value){if($null -eq $Value){return $null};Get-N5Hash (ConvertTo-N5Bytes $Value)}
function Assert-N7LiveRecord($R){
    if($R -isnot [System.Management.Automation.PSCustomObject] -or $null -eq $R.PSObject.Properties['protocol'] -or $R.protocol -isnot [string]){throw 'N7.ControlRecord: raw object/protocol required.'}
    $fields=switch($R.protocol){
        'StandardPending/1' {@('protocol','bindingSha256','intentSha256','transactionId','operation','previousReceiptSha256','journalSha256')}
        'StandardLifecycleEvent/1' {@('protocol','bindingSha256','intentSha256','transactionId','operation','previousEventSha256','phase','recordSha256')}
        'StandardOuterResult/1' {@('protocol','bindingSha256','intentSha256','transactionId','operation','startTick','deadlineMilliseconds','rootPid','rootStartTicks','rootExitCode','workerPid','workerExitCode','activeOwnedProcesses','stdoutState','stderrState','checkpoint','jobFlags')}
        'StandardOuterResult/2' {@('protocol','bindingSha256','intentSha256','transactionId','operation','startTick','deadlineMilliseconds','rootPid','rootStartTicks','rootExitCode','workerPid','workerExitCode','activeOwnedProcesses','stdoutState','stderrState','checkpoint','jobFlags','delegatedRole')}
        'StandardRemovalAnchor/1' {@('protocol','bindingSha256','intentSha256','transactionId','operation','previousReceiptSha256','receiptSha256','outerResultSha256')}
        'StandardFinalization/1' {@('protocol','bindingSha256','intentSha256','transactionId','operation','receiptSha256','outerResultSha256','stableDigest')}
        default {throw 'N7.ControlRecord: unknown protocol.'}
    }
    Assert-N5Fields $R $fields
    foreach($n in @('bindingSha256','intentSha256')){Assert-N5Hash $R.$n}
    if($R.transactionId -isnot [string] -or $R.operation -isnot [string] -or $R.transactionId -cnotmatch '^[0-9a-f]{32}$' -or $R.operation -cnotin @('Apply','Remove')){throw 'N7.ControlRecord: identity.'}
    foreach($n in @('previousReceiptSha256','previousEventSha256','recordSha256')){if($null -ne $R.PSObject.Properties[$n] -and $null -ne $R.$n){Assert-N5Hash $R.$n}}
    switch($R.protocol){
        'StandardPending/1' {Assert-N5Hash $R.journalSha256}
        'StandardLifecycleEvent/1' {if($R.phase -isnot [string] -or $R.phase -cnotin @('Intent','Prepared','Settled','Activated','Committed','Finalized','ShellRepairRequired')){throw 'N7.ControlRecord: lifecycle phase.'}}
        {$_ -cin @('StandardOuterResult/1','StandardOuterResult/2')} {
            if($R.protocol -ceq 'StandardOuterResult/2'){
                if($R.operation -ceq 'Remove'){
                    $role=$R.delegatedRole
                    if($role -isnot [System.Management.Automation.PSCustomObject]){throw 'N7.ControlRecord: raw delegated role required.'}
                    Assert-N5Fields $role @('role','processId','startTicks','image','exitCode')
                    Assert-N5Integer $role.exitCode 0 0
                    if($role.role -isnot [string] -or $role.role -cne 'InnoRemoveSecondPhase'){throw 'N7.ControlRecord: delegated role/exit0 required.'}
                    Assert-N5Integer $role.processId 1 ([int]::MaxValue);Assert-N5Integer $role.startTicks 1 ([long]::MaxValue)
                    $live=Import-N7Protocol;& $live {param($P) Assert-NLPin $P} $role.image
                }elseif($null -ne $R.delegatedRole){throw 'N7.ControlRecord: Apply delegated role must be null.'}
            }
            Assert-N5Integer $R.startTick 0 4294967295L;Assert-N5Integer $R.deadlineMilliseconds 1000 160000
            foreach($n in @('rootPid','workerPid')){Assert-N5Integer $R.$n 1 ([int]::MaxValue)};Assert-N5Integer $R.rootStartTicks 1 ([long]::MaxValue)
            foreach($n in @('rootExitCode','workerExitCode')){Assert-N5Integer $R.$n ([int]::MinValue) ([int]::MaxValue)}
            Assert-N5Integer $R.activeOwnedProcesses 0 0;Assert-N5Integer $R.jobFlags 8192 8192
            foreach($n in @('stdoutState','stderrState','checkpoint')){if($R.$n -isnot [string]){throw 'N7.ControlRecord: scalar state required.'}}
            if($R.rootExitCode -ne 0 -or $R.workerExitCode -ne 0 -or $R.activeOwnedProcesses -ne 0 -or $R.stdoutState -cne 'Eof' -or $R.stderrState -cne 'Eof' -or $R.checkpoint -cne 'NoCommit' -or $R.jobFlags -ne 8192){throw 'N7.ControlRecord: unsettled producer.'}
        }
        'StandardRemovalAnchor/1' {if($R.operation -cne 'Remove'){throw 'N7.ControlRecord: removal only.'};Assert-N5Hash $R.previousReceiptSha256;Assert-N5Hash $R.receiptSha256;Assert-N5Hash $R.outerResultSha256}
        'StandardFinalization/1' {Assert-N5Hash $R.receiptSha256;Assert-N5Hash $R.outerResultSha256;Assert-N5Hash $R.stableDigest}
    }
}
function Write-N7Record([string]$Path,$Value,$ExpectedHash=$null){
    if((Get-N5Remaining) -le 2000){throw 'N7.Deadline: durable write reserve.'}
    $live=Import-N7Protocol
    & $live {param($P) Assert-NLNoReparse $P} $Path
    $bytes=ConvertTo-N5Bytes $Value;if($bytes.Length -gt 2097152){throw 'N7.ControlRecord: size.'};$hash=Get-N5Hash $bytes
    $actual=$null;if([IO.File]::Exists($Path)){$actual=Get-N5Hash (Read-N5Bytes $Path)}
    if($actual -cne $ExpectedHash){throw 'N7.ControlCAS: raw expected-prior mismatch.'}
    $null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    $tmp=$Path+'.new';$backup=$Path+'.previous'
    if([IO.File]::Exists($tmp) -or [IO.File]::Exists($backup)){throw 'N7.ControlCAS: unresolved write/backup.'}
    $s=[IO.File]::Open($tmp,'CreateNew','Write','None');try{$s.Write($bytes,0,$bytes.Length);$s.Flush($true)}finally{$s.Dispose()}
    $again=$null;if([IO.File]::Exists($Path)){$again=Get-N5Hash (Read-N5Bytes $Path)}
    if($again -cne $ExpectedHash){throw 'N7.ControlCAS: prior changed before replace.'}
    if($null -eq $ExpectedHash){[IO.File]::Move($tmp,$Path)}else{[IO.File]::Replace($tmp,$Path,$backup)}
    if((Get-N5Hash (Read-N5Bytes $Path)) -cne $hash){throw 'N7.ControlCAS: durable readback mismatch.'}
    [pscustomobject]@{path=$Path;bytes=[long]$bytes.Length;sha256=$hash}
}
function Save-N7Object($S,[string]$Directory,$Value){
    $hash=Get-N7RecordHash $Value;$path=$S.graph.binding.roots.controlRoot+'\'+$Directory+'\'+$hash+'.json'
    Write-N7Record $path $Value
}
function New-N7Pending($S){[pscustomobject]@{protocol='StandardPending/1';bindingSha256=$S.graph.bindingSha256;intentSha256=$S.intentHash;transactionId=$S.intent.transactionId;operation=$S.intent.operation;previousReceiptSha256=$S.intent.previousReceiptSha256;journalSha256=$S.intent.journalSha256}}
function Add-N7Event($S,[string]$Phase,$Hash=$null){
    $r=[pscustomobject]@{protocol='StandardLifecycleEvent/1';bindingSha256=$S.graph.bindingSha256;intentSha256=$S.intentHash;transactionId=$S.intent.transactionId;operation=$S.intent.operation;previousEventSha256=$S.lastEvent;phase=$Phase;recordSha256=$Hash}
    Assert-N7LiveRecord $r;$pin=Save-N7Object $S 'events' $r;$S.lastEvent=$pin.sha256
}
function Assert-N7Pending($S){
    $bytes=Read-N5Bytes ($S.graph.binding.roots.controlRoot+'\pending.json');$r=ConvertFrom-N5Bytes $bytes;Assert-N7LiveRecord $r
    if((Get-N5Hash $bytes) -cne (Get-N7RecordHash (New-N7Pending $S))){throw 'N7.Pending: active intent changed.'}
    $journal=Read-N5Bytes ($S.graph.binding.roots.deploymentRoot+'\journal.json')
    if((Get-N5Hash $journal) -cne $S.intent.journalSha256){throw 'N7.Prepared: actual Core journal differs from pre-mutation intent.'}
}
function Import-N7Core {
    $path=Join-Path $PSScriptRoot 'NativeInstaller.psm1'
    if((Get-N5Hash (Read-N5Bytes $path)) -cne $script:N5EngineHash){throw 'N7.CorePin: frozen engine differs.'}
    Import-Module $path -PassThru -ErrorAction Stop
}
function Assert-N7AuthorityPrior($Graph,$Authority,[string]$Operation){
    $b=$Graph.binding
    if($Operation -ceq 'Apply' -and $Authority.status -ceq 'Absent' -and $Authority.kind -ceq 'Absent' -and -not $Authority.chainValidated -and $null -eq $Authority.selectedReceiptSha256 -and $null -eq $Authority.selected){
        if($null -ne $b.priorReceipt){throw 'N7.AuthorityPrior: absent Apply requires explicit null binding priorReceipt.'}
        return
    }
    if($Authority.status -cne 'Apply' -or -not $Authority.chainValidated -or -not $Authority.anchorRechecked -or -not $Authority.candidateRelationshipValidated -or $Authority.verifiedReceiptSha256 -cne $Authority.selectedReceiptSha256 -or $null -eq $Authority.selected -or $Authority.selected.receipt.operation -cne 'Apply'){throw 'N7.AuthorityPrior: rechecked selected Apply chain required.'}
    if($Operation -ceq 'Remove' -and $Authority.relationship -ceq 'Current'){
        if($Authority.selected.receipt.bindingSha256 -cne $Graph.bindingSha256){throw 'N7.AuthorityPrior: Remove current binding differs from selected receipt.'}
        return
    }
    if($Operation -cnotin @('Apply','Remove') -or $Authority.relationship -cne 'Prior' -or $null -eq $b.priorReceipt -or $b.priorReceipt.sha256 -cne $Authority.selectedReceiptSha256 -or $b.priorReceipt.path -cne ($b.roots.controlRoot+'\receipts\'+$Authority.selectedReceiptSha256+'.json') -or $b.priorReceipt.bytes -ne $Authority.selected.receiptBytes){throw 'N7.AuthorityPrior: candidate operation must pin the exact validated selected receipt.'}
}
function New-N7LiveIntent($Graph,$Authority,[string]$Operation,[string]$Transaction){
    Assert-N7AuthorityPrior $Graph $Authority $Operation
    $b=$Graph.binding;$core=Import-N7Core
    $prior=$null;$expected=$null
    if($Authority.chainValidated){$prior=$Authority.selectedReceiptSha256;$expected=$Authority.selected.receipt.nextStateSha256}
    $stage=$null;$manifest=$null;if($Operation -ceq 'Apply'){$manifest=$Graph.package.payloadManifest.sha256;$stage=$b.roots.controlRoot+'\stage\'+$manifest}
    # Prediction uses the frozen Core validators and its exact property order/serializer.
    # The expected canonical state also fences the later separate Core Prepare call.
    $journal=& $core {
        param($B,$Stage,$Manifest,$Expected,$Op)
        $root=Resolve-NativeRoot $B.roots.deploymentRoot;$data=Resolve-NativeRoot $B.roots.dataRoot
        Assert-NativeSeparate $root $data
        if(Test-Path -LiteralPath (Join-Path $root 'journal.json')){throw 'N7.RecoveryRequired: existing Core journal.'}
        $previous=$null
        if(Test-Path -LiteralPath (Join-Path $root 'state.json')){
            if(-not $Expected){throw 'N7.UnknownOwnership: active Core state without selected authority.'}
            $previous=Read-NativeState $root $data $Expected;Assert-NativeLayout $root $previous.releases;Assert-NativeInstalled $root $previous
            if((Get-NativeJsonHash $previous) -cne $Expected){throw 'N7.CorePrediction: noncanonical previous state.'}
        }else{
            if($Expected){throw 'N7.CorePrediction: missing selected state.'}
            $entries=@(Get-NativeTree $root);if($entries.Count -and -not ($entries.Count -eq 1 -and $entries[0].PSIsContainer -and $entries[0].FullName -ceq (Join-Path $root 'versions'))){throw 'N7.UnknownOwnership: nonempty unselected Core root.'}
        }
        $next=$null
        if($Op -ceq 'Apply'){
            Assert-NativeSeparate $Stage $root;Assert-NativeSeparate $Stage $data
            $m=Read-NativeStage $Stage $Manifest 'Production';$releases=@();if($null -ne $previous){$releases=@($previous.releases)}
            if(@($releases|Where-Object {$_.id -ceq $Manifest}).Count){throw 'N7.SameRelease: frozen Core rejects duplicate manifest ID; no mutation.'}
            $candidate=[pscustomobject]@{id=$Manifest;files=@($m.files)}
            $next=[pscustomobject]@{schema=1;installRoot=$root;dataRoot=$data;current=$Manifest;releases=@($releases)+@($candidate)}
        }elseif($null -eq $previous){throw 'N7.UnknownOwnership: Remove requires selected installed state.'}
        [pscustomobject]@{schema=1;operation=$Op;installRoot=$root;dataRoot=$data;previous=$previous;next=$next;copyProtocol='staged-v1'}
    } $b $stage $manifest $expected $Operation
    $intent=[pscustomobject]@{protocol='StandardIntent/1';bindingSha256=$Graph.bindingSha256;transactionId=$Transaction;operation=$Operation;previousReceiptSha256=$prior;stageDirectory=$stage;manifestSha256=$manifest;shellSpecSha256=$b.shellSpec.sha256;journal=$journal;journalSha256=(Get-N7RecordHash $journal);previousStateSha256=(Get-N7RecordHash $journal.previous);nextStateSha256=(Get-N7RecordHash $journal.next)}
    $live=Import-N7Protocol
    & $live {param($I,$G) Assert-NLIntent $I $G.binding $G.bindingSha256 ([pscustomobject]@{package=$G.package;manifest=$G.payloadManifest})} $intent $Graph
    $intent
}
function Resolve-N7GenuinePlan($Graph,$Authority,$Intent){
    Assert-N7AuthorityPrior $Graph $Authority $Intent.operation
    $exe=$Graph.package.setup
    if($Intent.operation -ceq 'Remove'){
        if(-not $Authority.chainValidated -or $Authority.selected.receipt.operation -cne 'Apply'){throw 'N7.UnknownOwnership: selected Apply receipt required.'}
        $path=$Graph.binding.roots.installRoot+'\unins000.exe';$files=@($Authority.selected.observation.stable.files|Where-Object {$_.path -ceq $path -and $_.state -ceq 'Present'})
        if($files.Count -ne 1){throw 'N7.UnknownOwnership: genuine uninstaller receipt pin absent.'}
        $exe=[pscustomobject]@{path=$path;bytes=$files[0].bytes;sha256=$files[0].sha256}
    }
    $live=Import-N7Protocol
    & $live {param($Pin,$Start,$Deadline) $actual=Get-NLFilePin $Pin.path $Start $Deadline;if($actual.bytes -ne $Pin.bytes -or $actual.sha256 -cne $Pin.sha256){throw 'N7.ProducerPin: genuine executable changed.'}} $exe $script:N5Start $script:N5Deadline
    [pscustomobject]@{protocol='NativeGenuinePlan/1';executable=$exe;operation=$Intent.operation}
}
function Assert-N7LiveSession($S,[string]$Phase){
    if($null -eq $script:N7LiveActive -or -not [object]::ReferenceEquals($S,$script:N7LiveActive) -or $S.revoked -or $S.phase -cne $Phase -or -not $S.mutexHeld -or $S.start -ne $script:N5Start -or $S.deadline -ne $script:N5Deadline -or (Get-N5Remaining) -le 1500){throw 'N7.LiveCapability: retained genuine context/clock/mutex required.'}
}
# A2-SRC-001: Job0 cannot establish the business exit of Inno's second phase.
# Discover the source-defined role independently of any event PID; this is not OS parent attestation.
function Find-N7RemoveRole($Job,$Plan,[long]$RootTicks){
    $found=[Collections.Generic.List[object]]::new();$accepted=$false
    try{
        $members=Get-N7JobMembers $Job
        foreach($member in $members){
            $candidate=$null
            try{
                $candidate=[Diagnostics.Process]::GetProcessById([int]$member);$handle=$candidate.Handle
                if($candidate.HasExited){continue};$path=$candidate.MainModule.FileName
                if([IO.Path]::GetFileName($path) -cnotmatch '^_unins.*\.tmp$'){continue}
                $ticks=$candidate.StartTime.ToUniversalTime().Ticks
                $live=Import-N7Protocol
                $pin=& $live {param($P,$T,$D) Get-NLFilePin $P $T $D} $path $script:N5Start $script:N5Deadline
                if($ticks -lt $RootTicks -or $pin.bytes -ne $Plan.executable.bytes -or $pin.sha256 -cne $Plan.executable.sha256){throw 'N7.RemoveRoleIdentity: unexpected second-phase image/start.'}
                if($candidate.HasExited -or (Get-N7JobMembers $Job) -notcontains [long]$candidate.Id){throw 'N7.RemoveRoleIdentity: second phase exited/lost Job during capture.'}
                $found.Add([pscustomobject]@{process=$candidate;handle=$handle;processId=$candidate.Id;startTicks=$ticks;image=$pin});$candidate=$null
            }finally{if($null -ne $candidate){$candidate.Dispose()}}
        }
        if($found.Count -ne 1){throw 'N7.RemoveRoleIdentity: exactly one actual owned second phase required.'}
        $accepted=$true;$found[0]
    }finally{if(-not $accepted){foreach($role in $found){$role.process.Dispose()}}}
}
function Assert-N7SameRemoveRole($Retained,$Observed){
    if($null -eq $Retained -or $null -eq $Observed -or $Retained.processId -ne $Observed.processId -or $Retained.startTicks -ne $Observed.startTicks -or $Retained.image.path -cne $Observed.image.path -or $Retained.image.bytes -ne $Observed.image.bytes -or $Retained.image.sha256 -cne $Observed.image.sha256){throw 'N7.RemoveRoleChanged: second-phase identity changed.'}
}
function Assert-N7RemoveRoleEvent($RoleEvent,[string]$Nonce,[string]$RequestHash,[string]$Phase,$Role){
    if($RoleEvent -isnot [System.Management.Automation.PSCustomObject]){throw 'N7.RemoveRoleEvent: raw event required.'}
    Assert-N5Fields $RoleEvent @('protocol','event','nonce','requestSha256','phase','sequence','role','processId','startTicks','image')
    foreach($n in @('protocol','event','phase','role')){if($RoleEvent.$n -isnot [string]){throw 'N7.RemoveRoleEvent: scalar string required.'}}
    Assert-N5Hash $RoleEvent.nonce;Assert-N5Hash $RoleEvent.requestSha256
    Assert-N5Integer $RoleEvent.processId 1 ([int]::MaxValue);Assert-N5Integer $RoleEvent.startTicks 1 ([long]::MaxValue);Assert-N5Integer $RoleEvent.sequence 1 2
    $live=Import-N7Protocol;& $live {param($P) Assert-NLPin $P} $RoleEvent.image
    if($RoleEvent.protocol -cne 'NativeLiveRemoveRoleEvent/1' -or $RoleEvent.event -cne 'Retain' -or $RoleEvent.role -cne 'InnoRemoveSecondPhase' -or $RoleEvent.nonce -cne $Nonce -or $RoleEvent.requestSha256 -cne $RequestHash -or $RoleEvent.phase -cne $Phase -or $RoleEvent.sequence -ne $(if($Phase -ceq 'Preflight'){1}else{2})){throw 'N7.RemoveRoleEvent: unexpected role/phase/binding.'}
    Assert-N7SameRemoveRole $Role $RoleEvent
}
function Receive-N7RemoveRole($Pipe,$Job,$Worker,$Plan,$Nonce,$RequestHash,$S,[string]$Phase){
    Assert-N7LiveSession $S 'Prepared'
    if($S.intent.operation -cne 'Remove' -or $S.roleSequence -ne $(if($Phase -ceq 'Preflight'){0}else{1}) -or (Get-N7PipePeerProcessId $Pipe) -ne $Worker.Id -or $Worker.HasExited){throw 'N7.RemoveRoleEvent: owned worker/sequence required.'}
    $roleEvent=Receive-N5Pipe $Pipe;$observed=$null
    try{
        $observed=Find-N7RemoveRole $Job $Plan $S.nativeTicks
        Assert-N7RemoveRoleEvent $roleEvent $Nonce $RequestHash $Phase $observed
        if($null -ne $S.removeRole){Assert-N7SameRemoveRole $S.removeRole $observed}
        if($Worker.HasExited -or (Get-N7PipePeerProcessId $Pipe) -ne $Worker.Id -or $observed.process.HasExited -or (Get-N7JobMembers $Job) -notcontains [long]$observed.processId){throw 'N7.RemoveRoleIdentity: role/worker lost before retention acknowledgment.'}
        if($null -eq $S.removeRole){$S.removeRole=$observed;$observed=$null}
        $S.roleSequence=$roleEvent.sequence
        Send-N5Pipe $Pipe ([pscustomobject]@{protocol='NativeLiveRemoveRolePermit/1';nonce=$Nonce;requestSha256=$RequestHash;sequence=$roleEvent.sequence;processId=$S.removeRole.processId;startTicks=$S.removeRole.startTicks})
    }finally{if($null -ne $observed){$observed.process.Dispose()}}
}
function Get-N7RemoveRoleOutcome($S){
    if($S.intent.operation -cne 'Remove'){return $null}
    $role=$S.removeRole
    if($S.roleSequence -ne 2 -or $null -eq $role -or $role.process -isnot [Diagnostics.Process]){throw 'N7.RemoveRoleExit: retained second-phase Process required; records cannot restore it.'}
    $p=$role.process
    if($p.Handle -ne $role.handle -or $p.Id -ne $role.processId -or $p.StartTime.ToUniversalTime().Ticks -ne $role.startTicks -or -not $p.HasExited){throw 'N7.RemoveRoleExit: changed/active second phase.'}
    $exit=$p.ExitCode
    if($exit -isnot [int] -or $exit -ne 0){throw ('N7.RemoveRoleExit: actual second-phase Int32 exit='+$exit)}
    [pscustomobject]@{role='InnoRemoveSecondPhase';processId=$role.processId;startTicks=$role.startTicks;image=$role.image;exitCode=$exit}
}
function Assert-N7Settled($S){
    if($null -eq $S -or $null -eq $script:N7LiveActive -or -not [object]::ReferenceEquals($S,$script:N7LiveActive)){throw 'N7.LiveCapability: no active genuine context.'}
    Assert-N7LiveSession $S $S.phase
    if($S.phase -cnotin @('SettledPrecommit','Activated','Committed') -or $null -eq $S.native -or $null -eq $S.worker -or $S.job -eq [IntPtr]::Zero -or -not $S.native.HasExited -or $S.native.ExitCode -ne 0 -or $S.native.StartTime.ToUniversalTime().Ticks -ne $S.nativeTicks -or -not $S.worker.HasExited -or $S.worker.ExitCode -ne 0 -or (Get-NativeJobActiveProcesses $S.job) -ne 0 -or (Get-N7JobMembers $S.job).Count -ne 0 -or $S.readers.states[0] -cne 'Eof' -or $S.readers.states[1] -cne 'Eof'){throw 'N7.BlockedUnsettled: retained native/worker/Job0/EOF required.'}
    $null=Get-N7RemoveRoleOutcome $S
    $null=Assert-N7JobLimits $S.job;Assert-N7Pending $S
}
function Assert-N7GenuineFresh($Graph,$IntentHash,$Stage,$Path){
    $s=$script:N7LiveActive;Assert-N7Settled $s
    if(-not [object]::ReferenceEquals($Graph,$s.graph) -or $IntentHash -cne $s.intentHash){throw 'N7.FreshAssertion: registered graph/intent reference mismatch.'}
    $e=$Graph.expectedShell
    $allowed=@($e.files.path)+$e.uninstallerPaths+@($e.menu.links|ForEach-Object {$e.menu.path+'\'+$_.name})+@($e.menu.path,$e.registry.key,(Join-Path $PSScriptRoot 'NativeInstallCoordinator.psm1'))
    if($Stage -cnotin @('Begin','Return','Identity','OwnerSource','Registry','Menu','LinkRaw','LinkFields','LinkRecheck','File') -or ($null -ne $Path -and $Path -cnotin $allowed)){throw 'N7.FreshAssertion: undeclared observation path/stage.'}
}
function Get-N7GenuineFresh($S){
    Assert-N7Settled $S;Assert-N7ShellBoundary $S.graph
    $live=Import-N7Protocol;$owner=$ExecutionContext.SessionState.Module
    & $live {param($G,$I,$Assert,$Owner,$Start,$Deadline,$Op) Get-NLFreshObservation $G $I $Assert $Owner $Start $Deadline $Op} $S.graph $S.intentHash ${function:Assert-N7GenuineFresh} $owner $script:N5Start $script:N5Deadline $S.intent.operation
}
function Invoke-N7LiveCoreWorker($Request){
    Assert-N5Fields $Request @('schema','protocol','nonce','parentPid','assigned','parameters')
    if($Request.protocol -cne 'NativeLiveCore/1' -or $Request.nonce -cne $env:N5_NONCE -or $Request.parentPid -ne [int]$env:N5_PARENT -or $Request.assigned -isnot [bool] -or -not $Request.assigned){throw 'N7.CoreWorker: assignment.'}
    $p=$Request.parameters;Assert-N5Fields $p @('BindingPath','BindingSha256','IntentSha256','liveCorePhase','StartTick','DeadlineMilliseconds')
    if($p.liveCorePhase -cnotin @('Activate','Finalize','Recover') -or $p.StartTick -ne $script:N5Start -or $p.DeadlineMilliseconds -ne $script:N5Deadline){throw 'N7.CoreWorker: closed phase/clock.'}
    $null=Import-N7Protocol;$g=Read-NativeLiveGraph $p.BindingPath $p.BindingSha256 $script:N5Start $script:N5Deadline
    $i=(Read-NativeLiveIntent $p.BindingPath $p.BindingSha256 $p.IntentSha256 $script:N5Start $script:N5Deadline).intent
    $s=[pscustomobject]@{graph=$g;intent=$i;intentHash=$p.IntentSha256};Assert-N7Pending $s
    $null=Import-N7Core;$expected=$i.previousStateSha256;if($p.liveCorePhase -ceq 'Finalize' -and $i.operation -ceq 'Apply'){$expected=$i.nextStateSha256}
    if($p.liveCorePhase -ceq 'Recover'){
        # Only the still-running parent can issue this after owned settlement, before activation.
        return Invoke-NativeRecovery -InstallRoot $g.binding.roots.deploymentRoot -DataRoot $g.binding.roots.dataRoot -JournalSha256 $i.journalSha256 -ExpectedStateSha256 $i.previousStateSha256 -Mode Production -InformationAction SilentlyContinue
    }
    Invoke-NativeTransaction -Operation $i.operation -InstallRoot $g.binding.roots.deploymentRoot -DataRoot $g.binding.roots.dataRoot -Mode Production -TransactionPhase $p.liveCorePhase -JournalSha256 $i.journalSha256 -ExpectedStateSha256 $expected -InformationAction SilentlyContinue
}
function Invoke-N7CorePhase($S,[string]$Phase){
    Assert-N7Settled $S
    $p=@{BindingPath=$S.bindingPath;BindingSha256=$S.graph.bindingSha256;IntentSha256=$S.intentHash;liveCorePhase=$Phase;StartTick=$script:N5Start;DeadlineMilliseconds=$script:N5Deadline}
    Invoke-N5Dispatch $p $S.dispatchBinding
}
function Set-N7RegistryAnchor($S,[string]$Name,$Expected,$Next){
    Assert-N7Settled $S
    if($Name -cnotin @('NativePendingIntentSha256','NativeCommittedReceiptSha256')){throw 'N7.AnchorCAS: closed value name.'}
    $live=Import-N7Protocol;$r=& $live {param($T,$D) Read-NLRegistry $T $D} $script:N5Start $script:N5Deadline
    if($r.state -cne 'Present' -or $r.committedReceiptSha256 -cne $(if($Name -ceq 'NativeCommittedReceiptSha256'){$Expected}elseif($S.phase -ceq 'Committed'){$S.receiptHash}else{$S.intent.previousReceiptSha256})){throw 'N7.AnchorCAS: selected expected-prior changed.'}
    foreach($v in $S.graph.expectedShell.registry.values){$actual=@($r.values|Where-Object {$_.name -ceq $v.name});if($actual.Count -ne 1 -or $actual[0].kind -cne $v.kind -or $actual[0].value -cne $v.value){throw 'N7.AnchorCAS: genuine ordinary values changed.'}}
    $base=$null;$key=$null
    try{
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser','Registry64');$key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSight.Native.Preview_is1',$true)
        if($null -eq $key){throw 'N7.AnchorCAS: genuine registration absent; never create.'}
        $actual=$key.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if($actual -cne $Expected){throw 'N7.AnchorCAS: raw value expected-prior mismatch.'}
        if($null -ne $actual -and $key.GetValueKind($Name) -ne [Microsoft.Win32.RegistryValueKind]::String){throw 'N7.AnchorCAS: REG_SZ required.'}
        if($null -eq $Next){$key.DeleteValue($Name,$true)}else{$key.SetValue($Name,$Next,[Microsoft.Win32.RegistryValueKind]::String)};$key.Flush()
        if($key.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) -cne $Next){throw 'N7.AnchorAckLost: reconciliation required; never rollback.'}
    }finally{if($key){$key.Dispose()};if($base){$base.Dispose()}}
}
function Complete-N7LiveTransaction($S,$WorkerResult){
    Assert-N7Settled $S
    if($S.continuationConsumed){throw 'N7.LiveCapability: postsettlement continuation already consumed.'};$S.continuationConsumed=$true
    if($WorkerResult.protocol -cne 'NativeLiveProducerResult/1' -or $WorkerResult.checkpoint -cne 'NoCommit'){throw 'N7.Checkpoint: worker did not complete genuine NoCommit checkpoints.'}
    $outer=[pscustomobject]@{protocol='StandardOuterResult/2';delegatedRole=(Get-N7RemoveRoleOutcome $S);bindingSha256=$S.graph.bindingSha256;intentSha256=$S.intentHash;transactionId=$S.intent.transactionId;operation=$S.intent.operation;startTick=$S.start;deadlineMilliseconds=$S.deadline;rootPid=$S.native.Id;rootStartTicks=$S.nativeTicks;rootExitCode=[int]$S.native.ExitCode;workerPid=$S.worker.Id;workerExitCode=[int]$S.worker.ExitCode;activeOwnedProcesses=0;stdoutState=$S.readers.states[0];stderrState=$S.readers.states[1];checkpoint='NoCommit';jobFlags=(Assert-N7JobLimits $S.job)}
    Assert-N7LiveRecord $outer;$outerPin=Save-N7Object $S 'outer-results' $outer;Add-N7Event $S 'Settled' $outerPin.sha256
    $observation=Get-N7GenuineFresh $S
    if($observation.anchors.committedReceiptSha256 -cne $(if($S.intent.operation -ceq 'Apply'){$S.intent.previousReceiptSha256}else{$null}) -or ($null -ne $observation.anchors.pendingIntentSha256 -and $observation.anchors.pendingIntentSha256 -cne $S.intentHash)){throw 'N7.AnchorChanged: postsettlement anchors differ.'}
    if($S.intent.operation -ceq 'Apply'){
        if($null -eq $observation.anchors.pendingIntentSha256){Set-N7RegistryAnchor $S 'NativePendingIntentSha256' $null $S.intentHash}
        $observation=Get-N7GenuineFresh $S
    }
    $obsPin=Save-N7Object $S 'observations' $observation;$digest=Get-NativeLiveStableDigest $observation
    $S.activationAttempted=$true
    $null=Invoke-N7CorePhase $S 'Activate';$S.phase='Activated';Add-N7Event $S 'Activated' $S.intent.journalSha256
    $b=$S.graph.binding;$i=$S.intent;$previousSetup=$null;if($S.authority.chainValidated){$previousSetup=$S.authority.selected.metadata.package.setup}
    $receipt=[pscustomobject]@{protocol='StandardCommitReceipt/1';bindingSha256=$S.graph.bindingSha256;instanceId=$b.instanceId;appId=$b.appId;roots=$b.roots;host=$b.host;transactionId=$i.transactionId;operation=$i.operation;intentSha256=$S.intentHash;previousReceiptSha256=$i.previousReceiptSha256;manifestSha256=$i.manifestSha256;selection=$(if($i.operation -ceq 'Apply'){$i.journal.next.current}else{$null});journalSha256=$i.journalSha256;previousStateSha256=$i.previousStateSha256;nextStateSha256=$i.nextStateSha256;sourceManifestSha256=$b.sources.sha256;packageSha256=$b.package.sha256;shellSpecSha256=$b.shellSpec.sha256;observation=$obsPin;stableDigest=$digest;outerResult=$(if($i.operation -ceq 'Remove'){$outerPin}else{$null});trustedPreviousSetup=$previousSetup}
    $live=Import-N7Protocol;& $live {param($R,$B,$H) Assert-NLReceipt $R $B $H} $receipt $b $S.graph.bindingSha256
    $receiptPin=Save-N7Object $S 'receipts' $receipt;$S.receiptHash=$receiptPin.sha256
    if($i.operation -ceq 'Apply'){Set-N7RegistryAnchor $S 'NativeCommittedReceiptSha256' $i.previousReceiptSha256 $receiptPin.sha256}
    else{
        $anchor=[pscustomobject]@{protocol='StandardRemovalAnchor/1';bindingSha256=$S.graph.bindingSha256;intentSha256=$S.intentHash;transactionId=$i.transactionId;operation='Remove';previousReceiptSha256=$i.previousReceiptSha256;receiptSha256=$receiptPin.sha256;outerResultSha256=$outerPin.sha256}
        Assert-N7LiveRecord $anchor;$null=Write-N7Record ($b.roots.controlRoot+'\removal-anchor.json') $anchor
    }
    $S.phase='Committed';Add-N7Event $S 'Committed' $receiptPin.sha256
    # N7-POSTCOMMIT-DIAG-01: a failure between commit and finalize used to leave only a stale pending file.
    # Record the phase and message first so the next run has evidence instead of an empty log.
    try{
    $after=Get-N7GenuineFresh $S
    if((Get-NativeLiveStableDigest $after) -cne $digest -or ($i.operation -ceq 'Apply' -and $after.anchors.committedReceiptSha256 -cne $receiptPin.sha256)){Add-N7Event $S 'ShellRepairRequired' $receiptPin.sha256;throw 'N7.ShellRepairRequired: postanchor shell changed; receipt immutable, no rollback.'}
    if($i.operation -ceq 'Apply'){Set-N7RegistryAnchor $S 'NativePendingIntentSha256' $S.intentHash $null}
    $null=Invoke-N7CorePhase $S 'Finalize'
    $final=[pscustomobject]@{protocol='StandardFinalization/1';bindingSha256=$S.graph.bindingSha256;intentSha256=$S.intentHash;transactionId=$i.transactionId;operation=$i.operation;receiptSha256=$receiptPin.sha256;outerResultSha256=$outerPin.sha256;stableDigest=$digest}
    Assert-N7LiveRecord $final;$null=Write-N7Record ($b.roots.controlRoot+'\finalized\'+$i.transactionId+'.json') $final
    Add-N7Event $S 'Finalized' $receiptPin.sha256
    $pending=$b.roots.controlRoot+'\pending.json';if((Get-N5Hash (Read-N5Bytes $pending)) -cne (Get-N7RecordHash (New-N7Pending $S))){throw 'N7.Pending: changed after Finalize.'};[IO.File]::Delete($pending)
    $S.phase='Finalized'
    [pscustomobject]@{protocol='NativeLiveTransactionResult/1';status='Finalized';operation=$i.operation;transactionId=$i.transactionId;intentSha256=$S.intentHash;receipt=$receiptPin;outerResult=$outerPin;restartRecoveryQualified=$false}
    }
    catch{
        try{
            $failure=[pscustomobject]@{protocol='StandardPostCommitFailure/1';bindingSha256=$S.graph.bindingSha256;intentSha256=$S.intentHash;transactionId=$S.intent.transactionId;operation=$S.intent.operation;receiptSha256=$receiptPin.sha256;phase=$S.phase;errorType=$_.Exception.GetType().Name;errorMessage=[string]$_.Exception.Message}
            $null=Save-N7Object $S 'failures' $failure
        }catch{}
        throw
    }
}
function Invoke-N7LivePermit($Pipe,$Job,$Worker,$Plan,$Nonce,$RequestHash,$S){
    $producerEvent=Receive-N5Pipe $Pipe;Assert-N5Fields $producerEvent @('protocol','event','nonce','requestSha256','childPid','childStartTicks','executableSha256')
    if($producerEvent.protocol -cne 'NativeLiveProducerEvent/1' -or $producerEvent.event -cne 'Started' -or $producerEvent.nonce -cne $Nonce -or $producerEvent.requestSha256 -cne $RequestHash -or $producerEvent.executableSha256 -cne $Plan.executable.sha256 -or $Worker.HasExited){throw 'N7.ProducerIdentity: invalid permit event.'}
    Assert-N5Integer $producerEvent.childPid 1 ([int]::MaxValue);Assert-N5Integer $producerEvent.childStartTicks 1 ([long]::MaxValue)
    $members=Get-N7JobMembers $Job;if($members -notcontains [long]$Worker.Id -or $members -notcontains [long]$producerEvent.childPid){throw 'N7.ProducerMembership: actual root Job lacks producer/worker.'}
    $native=[Diagnostics.Process]::GetProcessById($producerEvent.childPid)
    try{
        $null=$native.Handle
        if($native.HasExited -or $native.StartTime.ToUniversalTime().Ticks -ne $producerEvent.childStartTicks -or $native.MainModule.FileName -cne $Plan.executable.path){throw 'N7.ProducerIdentity: actual retained Process differs.'}
        $live=Import-N7Protocol;& $live {param($Pin,$T,$D) $a=Get-NLFilePin $Pin.path $T $D;if($a.bytes -ne $Pin.bytes -or $a.sha256 -cne $Pin.sha256){throw 'N7.ProducerIdentity: image bytes changed.'}} $Plan.executable $script:N5Start $script:N5Deadline
        Assert-N7Pending $S;$S.native=$native;$S.nativeTicks=$producerEvent.childStartTicks;$native=$null;$S.phase='Prepared';Add-N7Event $S 'Prepared' $S.intent.journalSha256
        Send-N5Pipe $Pipe ([pscustomobject]@{protocol='NativeLiveProducerPermit/1';nonce=$Nonce;requestSha256=$RequestHash;childPid=$producerEvent.childPid;childStartTicks=$producerEvent.childStartTicks})
    }finally{if($null -ne $native){$native.Dispose()}}
    ,$members
}
function Wait-N7CallbackConnection([Threading.Tasks.Task]$Connect,$Native,[string]$Phase){
    # N7-PRODUCER-EARLY-EXIT-MASKED-01: do not turn an already failed producer into a shared-deadline error.
    while(-not $Connect.IsCompleted){
        if($Native.HasExited -and $Native.ExitCode -ne 0){throw ('N7.ProducerExit: native exited '+[int]$Native.ExitCode+' before '+$Phase+' callback connection.')}
        $remaining=[Math]::Max(0,(Get-N5Remaining)-10000)
        if($remaining -le 0){throw 'N7.CallbackDeadline: genuine callback absent.'}
        $null=$Connect.Wait([int][Math]::Min(100,$remaining))
    }
    $null=$Connect.GetAwaiter().GetResult()
}
function Wait-N7CallbackPeerClose($Pipe){
    # Buffered WriteAsync completion does not mean the client consumed the reply.
    # Keep this connection alive until the existing client closes it in finally.
    # EOF is lifecycle settlement only, not a new acknowledgment or authority.
    $remaining=[Math]::Max(0,(Get-N5Remaining)-10000)
    if($remaining -le 0){throw 'N7.CallbackCloseDeadline: cleanup reserve reached.'}
    $probe=New-Object byte[] 1
    $read=$Pipe.ReadAsync($probe,0,1)
    if(-not $read.Wait($remaining)){
        # Explicitly break the server connection before disposal. A queued read
        # can retain the native handle and unread output after Dispose alone.
        $Pipe.Disconnect()
        $settle=[Math]::Min(1000,(Get-N5Remaining))
        try{$null=$read.Wait($settle)}catch{if(-not $read.IsCompleted){throw}}
        if(-not $read.IsCompleted){throw 'N7.CallbackCloseUnsettled: disconnected read remains active.'}
        throw ('N7.CallbackCloseDeadline: peer did not close; disconnected read settled '+$read.Status+'.')
    }
    if($read.GetAwaiter().GetResult() -ne 0){throw 'N7.CallbackTrailingData: one reply per connection required.'}
}
function Invoke-N7LiveWorker($Request,$ParentPipe){
    Assert-N5Fields $Request @('schema','protocol','nonce','parentPid','assigned','parameters','plan')
    if($Request.protocol -cne 'NativeLiveProducer/1' -or $Request.nonce -cne $env:N5_NONCE -or $Request.parentPid -ne [int]$env:N5_PARENT -or $Request.assigned -isnot [bool] -or -not $Request.assigned){throw 'N7.ProducerWorker: assignment challenge.'}
    $p=$Request.parameters;Assert-N5Fields $p @('BindingPath','BindingSha256','IntentSha256','StartTick','DeadlineMilliseconds')
    if($p.StartTick -ne $script:N5Start -or $p.DeadlineMilliseconds -ne $script:N5Deadline){throw 'N7.ProducerWorker: original clock.'}
    $null=Import-N7Protocol;$g=Read-NativeLiveGraph $p.BindingPath $p.BindingSha256 $script:N5Start $script:N5Deadline
    $i=(Read-NativeLiveIntent $p.BindingPath $p.BindingSha256 $p.IntentSha256 $script:N5Start $script:N5Deadline).intent
    $authority=Get-NativeLiveAuthority $p.BindingPath $p.BindingSha256 $script:N5Start $script:N5Deadline
    $plan=Resolve-N7GenuinePlan $g $authority $i
    if((Get-N7RecordHash $plan) -cne (Get-N7RecordHash $Request.plan)){throw 'N7.ProducerWorker: locally resolved plan differs.'}
    $s=[pscustomobject]@{graph=$g;intent=$i;intentHash=$p.IntentSha256}
    $pending=ConvertFrom-N5Bytes (Read-N5Bytes ($g.binding.roots.controlRoot+'\pending.json'));Assert-N7LiveRecord $pending
    if((Get-N7RecordHash $pending) -cne (Get-N7RecordHash (New-N7Pending $s))){throw 'N7.Pending: wrong file-only pending.'}
    $null=Import-N7Core
    $coreArguments=@{Operation=$i.operation;InstallRoot=$g.binding.roots.deploymentRoot;DataRoot=$g.binding.roots.dataRoot;Mode='Production';TransactionPhase='Prepare';ExpectedStateSha256=$i.previousStateSha256;InformationAction='SilentlyContinue'}
    if($i.operation -ceq 'Apply'){$coreArguments.StageDirectory=$i.stageDirectory;$coreArguments.ManifestSha256=$i.manifestSha256}
    $prepared=Invoke-NativeTransaction @coreArguments 6>$null
    if($prepared.operation -cne 'Prepared' -or $prepared.journalSha256 -cne $i.journalSha256 -or $prepared.previousStateSha256 -cne $i.previousStateSha256 -or $prepared.nextStateSha256 -cne $i.nextStateSha256){throw 'N7.CorePrepare: exact frozen phase result differs.'}
    Assert-N7Pending $s
    $server=$null;$native=$null;$peer=$null;$workerRole=$null;$roles=[Collections.Generic.List[object]]::new()
    try{
        $name='WinSightN7-'+[Guid]::NewGuid().ToString('N');$nonce=[Guid]::NewGuid().ToString('N')+[Guid]::NewGuid().ToString('N')
        $server=[IO.Pipes.NamedPipeServerStream]::new($name,'InOut',1,'Byte','Asynchronous',65536,65536)
        $connect=$server.WaitForConnectionAsync();$native=[Diagnostics.Process]::new();$si=[Diagnostics.ProcessStartInfo]::new()
        $si.FileName=$plan.executable.path;$si.UseShellExecute=$false
        $si.Arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
        if($i.operation -ceq 'Apply'){$si.Arguments+=' /DIR="'+$g.binding.roots.installRoot+'"'}
        foreach($n in @($si.EnvironmentVariables.Keys)){if($n -like 'N5_*' -or $n -like 'N7_*'){$si.EnvironmentVariables.Remove($n)}}
        $si.EnvironmentVariables.Remove('PSModulePath')
        $envs=@{N7_OWNER_PIPE=$name;N7_START=$script:N5Start;N7_DEADLINE=$script:N5Deadline;N7_MODULE_HASH=$env:N5_MODULE;N7_ENTRY_HASH=$env:N5_ENTRY;N7_LIVE_NONCE=$nonce;N7_LIVE_BINDING=$p.BindingSha256;N7_LIVE_INTENT=$p.IntentSha256;N7_LIVE_OPERATION=$i.operation;N7_LIVE_TRANSACTION=$i.transactionId;N7_LIVE_ENTRY=(Join-Path $PSScriptRoot 'Invoke-NativeInstallCoordinator.ps1');N7_LIVE_HOST=$g.binding.host.executable.path;N7_LIVE_BINDING_PATH=$p.BindingPath;N7_LIVE_TASK_ROOT=$g.binding.roots.taskRoot}
        foreach($n in $envs.Keys){$si.EnvironmentVariables[$n]=[string]$envs[$n]}
        $native.StartInfo=$si;if(-not $native.Start()){throw 'N7.ProducerStart: native Start false.'};$null=$native.Handle;$ticks=$native.StartTime.ToUniversalTime().Ticks
        if((Get-N7JobMembers ([IntPtr]::Zero)) -notcontains [long]$native.Id){throw 'N7.ProducerMembership: native not born in inherited Job.'}
        Send-N5Pipe $ParentPipe ([pscustomobject]@{protocol='NativeLiveProducerEvent/1';event='Started';nonce=$env:N5_NONCE;requestSha256=$env:N5_REQUEST;childPid=$native.Id;childStartTicks=$ticks;executableSha256=$plan.executable.sha256})
        $permit=Receive-N5Pipe $ParentPipe;Assert-N5Fields $permit @('protocol','nonce','requestSha256','childPid','childStartTicks')
        if($permit.protocol -cne 'NativeLiveProducerPermit/1' -or $permit.nonce -cne $env:N5_NONCE -or $permit.requestSha256 -cne $env:N5_REQUEST -or $permit.childPid -ne $native.Id -or $permit.childStartTicks -ne $ticks){throw 'N7.ProducerPermit: parent assignment mismatch.'}
        foreach($phase in @('Preflight','Checkpoint')){
            Wait-N7CallbackConnection $connect $native $phase
            $frame=Receive-N7OwnerFrame $server
            Assert-N7CheckpointFrame $frame
            $id=Get-N7PipePeerProcessId $server;Assert-N7SeparatePipePeer $id $frame.processId $native.Id
            $peer=[Diagnostics.Process]::GetProcessById($id);$null=$peer.Handle
            $members=Get-N7JobMembers ([IntPtr]::Zero)
            if($frame.protocol -cne 'NativeLiveCheckpoint/1' -or $frame.nonce -cne $nonce -or $frame.bindingSha256 -cne $p.BindingSha256 -or $frame.intentSha256 -cne $p.IntentSha256 -or $frame.operation -cne $i.operation -or $frame.transactionId -cne $i.transactionId -or $frame.command -cne $phase -or $frame.sequence -ne $(if($phase -ceq 'Preflight'){1}else{2}) -or $frame.startTick -ne $script:N5Start -or $frame.deadlineMilliseconds -ne $script:N5Deadline -or $peer.HasExited -or $peer.StartTime.ToUniversalTime().Ticks -ne $frame.processStartTicks -or $peer.MainModule.FileName -cne $g.binding.host.executable.path -or $members -notcontains [long]$peer.Id){throw 'N7.CallbackIdentity: genuine peer/frame mismatch.'}
            $role=$native
            if($i.operation -ceq 'Remove'){
                $observed=$null
                try{
                    $observed=Find-N7RemoveRole ([IntPtr]::Zero) $plan $ticks
                    if($null -eq $workerRole){$workerRole=$observed;$roles.Add($observed.process);$observed=$null}else{Assert-N7SameRemoveRole $workerRole $observed}
                }finally{if($null -ne $observed){$observed.process.Dispose()}}
                $role=$workerRole.process
                # The callback stays blocked until the PARENT independently opens this role.
                Send-N5Pipe $ParentPipe ([pscustomobject]@{protocol='NativeLiveRemoveRoleEvent/1';event='Retain';nonce=$env:N5_NONCE;requestSha256=$env:N5_REQUEST;phase=$phase;sequence=$frame.sequence;role='InnoRemoveSecondPhase';processId=$workerRole.processId;startTicks=$workerRole.startTicks;image=$workerRole.image})
                $ack=Receive-N5Pipe $ParentPipe
                Assert-N5Fields $ack @('protocol','nonce','requestSha256','sequence','processId','startTicks')
                if($ack.protocol -cne 'NativeLiveRemoveRolePermit/1' -or $ack.nonce -cne $env:N5_NONCE -or $ack.requestSha256 -cne $env:N5_REQUEST -or $ack.sequence -ne $frame.sequence -or $ack.processId -ne $workerRole.processId -or $ack.startTicks -ne $workerRole.startTicks){throw 'N7.RemoveRolePermit: parent retention acknowledgment mismatch.'}
            }
            Assert-N7Pending $s
            if($role.HasExited -or $peer.HasExited -or (Get-N7PipePeerProcessId $server) -ne $id){throw 'N7.CallbackIdentity: peer/producer lost before acknowledgment.'}
            Send-N5Pipe $server ([pscustomobject]@{protocol='NativeLiveCheckpointReply/1';command=$phase;sequence=$frame.sequence;status='NoCommit'})
            Wait-N7CallbackPeerClose $server
            $peer.Dispose();$peer=$null;$server.Disconnect();if($phase -ceq 'Preflight'){$connect=$server.WaitForConnectionAsync()}
        }
        if(-not $native.WaitForExit([Math]::Max(0,(Get-N5Remaining)-10000))){throw 'N7.ProducerDeadline: native root did not exit.'}
        if($native.ExitCode -ne 0){throw ('N7.ProducerExit: native Int32 exit='+$native.ExitCode)}
        foreach($delegated in $roles){
            if(-not $delegated.WaitForExit([Math]::Max(0,(Get-N5Remaining)-10000))){throw 'N7.ProducerDeadline: second phase did not exit.'}
            if($delegated.ExitCode -isnot [int] -or $delegated.ExitCode -ne 0){throw ('N7.RemoveRoleExit: worker observed second-phase Int32 exit='+$delegated.ExitCode)}
        }
        [pscustomobject]@{protocol='NativeLiveProducerResult/1';checkpoint='NoCommit';nativeExitCode=[int]$native.ExitCode;roleCount=$roles.Count;businessCommit=$false}
    }finally{foreach($r in @($peer,$server,$native)+$roles.ToArray()){if($null -ne $r){$r.Dispose()}}}
}
function Assert-N7CheckpointFrame($Frame){
    if($Frame -isnot [System.Management.Automation.PSCustomObject]){throw 'N7.CallbackFrame: raw object required.'}
    Assert-N5Fields $Frame @('protocol','nonce','bindingSha256','intentSha256','operation','transactionId','command','sequence','processId','processStartTicks','startTick','deadlineMilliseconds')
    foreach($n in @('protocol','operation','transactionId','command')){if($Frame.$n -isnot [string]){throw 'N7.CallbackFrame: scalar string required.'}}
    foreach($n in @('nonce','bindingSha256','intentSha256')){Assert-N5Hash $Frame.$n}
    Assert-N5Integer $Frame.sequence 1 2;Assert-N5Integer $Frame.processId 1 ([int]::MaxValue);Assert-N5Integer $Frame.processStartTicks 1 ([long]::MaxValue);Assert-N5Integer $Frame.startTick 0 4294967295L;Assert-N5Integer $Frame.deadlineMilliseconds 1000 160000
    if($Frame.protocol -cne 'NativeLiveCheckpoint/1' -or $Frame.operation -cnotin @('Apply','Remove') -or $Frame.command -cnotin @('Preflight','Checkpoint') -or $Frame.transactionId -cnotmatch '^[0-9a-f]{32}$'){throw 'N7.CallbackFrame: closed protocol/identity.'}
}
function Invoke-N7LiveCallbackClient([string]$Phase){
    if($Phase -cnotin @('Preflight','Checkpoint') -or $env:N7_OWNER_PIPE -cnotmatch '^WinSightN7-[0-9a-f]{32}$' -or $env:N7_LIVE_NONCE -cnotmatch '^[0-9a-f]{64}$'){throw 'N7.CallbackContext: genuine inherited context required.'}
    foreach($n in @('N7_LIVE_BINDING','N7_LIVE_INTENT')){Assert-N5Hash ([Environment]::GetEnvironmentVariable($n))}
    if($env:N7_LIVE_OPERATION -cnotin @('Apply','Remove') -or $env:N7_LIVE_TRANSACTION -cnotmatch '^[0-9a-f]{32}$'){throw 'N7.CallbackContext: identity.'}
    Initialize-N5Clock
    $script:N5Start=[long]$env:N7_START;$script:N5Deadline=[int]$env:N7_DEADLINE
    $self=[Diagnostics.Process]::GetCurrentProcess();$pipe=$null
    try{
        $pipe=[IO.Pipes.NamedPipeClientStream]::new('.',$env:N7_OWNER_PIPE,'InOut','Asynchronous');$pipe.Connect([Math]::Max(0,(Get-N5Remaining)-2000))
        $seq=if($Phase -ceq 'Preflight'){1}else{2}
        Send-N5Pipe $pipe ([pscustomobject]@{protocol='NativeLiveCheckpoint/1';nonce=$env:N7_LIVE_NONCE;bindingSha256=$env:N7_LIVE_BINDING;intentSha256=$env:N7_LIVE_INTENT;operation=$env:N7_LIVE_OPERATION;transactionId=$env:N7_LIVE_TRANSACTION;command=$Phase;sequence=$seq;processId=$PID;processStartTicks=$self.StartTime.ToUniversalTime().Ticks;startTick=$script:N5Start;deadlineMilliseconds=$script:N5Deadline})
        $reply=Receive-N5Pipe $pipe;Assert-N5Fields $reply @('protocol','command','sequence','status')
        if($reply.protocol -cne 'NativeLiveCheckpointReply/1' -or $reply.command -cne $Phase -or $reply.sequence -ne $seq -or $reply.status -cne 'NoCommit'){throw 'N7.CallbackReply: no genuine NoCommit acknowledgment.'}
        $reply
    }finally{if($pipe){$pipe.Dispose()};$self.Dispose()}
}
function Assert-N7ShellBoundary($Graph){
    $live=Import-N7Protocol
    & $live {
        param($G,$Start,$Deadline)
        $root=$G.binding.roots.installRoot;Assert-NLNoReparse $root
        if([IO.Directory]::Exists($root)){
            foreach($p in [IO.Directory]::EnumerateFileSystemEntries($root)){
                Assert-NLDeadline $Start $Deadline;Assert-NLNoReparse $p
                if($p -cnotin (@($G.binding.roots.shellRoot,$G.binding.roots.deploymentRoot)+$G.expectedShell.uninstallerPaths)){throw 'N7.UnknownOwnership: foreign install-root entry.'}
            }
        }
        $shell=$G.binding.roots.shellRoot;$queue=[Collections.Generic.Queue[string]]::new();if([IO.Directory]::Exists($shell)){$queue.Enqueue($shell)};$count=0
        while($queue.Count){
            foreach($p in [IO.Directory]::EnumerateFileSystemEntries($queue.Dequeue())){
                $count++;if($count -gt 8192){throw 'N7.ShellBoundary: entry bound.'};Assert-NLDeadline $Start $Deadline;Assert-NLNoReparse $p
                if([IO.Directory]::Exists($p)){
                    if(-not @($G.expectedShell.files|Where-Object {$_.path.StartsWith($p+'\',[StringComparison]::Ordinal)}).Count){throw 'N7.UnknownOwnership: undeclared shell directory.'};$queue.Enqueue($p)
                }elseif($p -cnotin @($G.expectedShell.files.path)){throw 'N7.UnknownOwnership: undeclared shell file.'}
            }
        }
        $menu=$G.expectedShell.menu.path;Assert-NLNoReparse $menu
        if([IO.Directory]::Exists($menu)){
            $links=@($G.expectedShell.menu.links|ForEach-Object {$menu+'\'+$_.name})
            foreach($p in [IO.Directory]::EnumerateFileSystemEntries($menu)){Assert-NLDeadline $Start $Deadline;Assert-NLNoReparse $p;if($p -cnotin $links -or [IO.Directory]::Exists($p)){throw 'N7.UnknownOwnership: foreign exact-menu entry.'}}
        }
    } $Graph $script:N5Start $script:N5Deadline
}
# Launch has no native lease or mutating phase. Only this module instance retains
# the just-settled validator result; serialized descriptors cannot authorize Start.
$script:N7LaunchResult=$null
function Assert-N7LaunchAbsent([string]$Path){
    $null=Assert-N5Path $Path
    try{$null=[IO.File]::GetAttributes($Path)}catch [IO.FileNotFoundException]{return}catch [IO.DirectoryNotFoundException]{return}
    throw ('N7.LaunchRecoveryRequired: unresolved record '+$Path)
}
function Assert-N7LaunchCurrent($O,[string]$BindingHash){
    $a=$O.authority
    if($O.status -cne 'ObservedApply' -or $a.status -cne 'Apply' -or $a.kind -cne 'Apply' -or $a.relationship -cne 'Current' -or $a.chainValidated -ne $true -or $a.anchorRechecked -ne $true -or $a.candidateRelationshipValidated -ne $true -or $null -ne $a.pendingIntentSha256 -or $a.verifiedReceiptSha256 -cne $a.selectedReceiptSha256 -or $null -eq $a.selected -or $a.selected.receipt.operation -cne 'Apply' -or $a.selected.receipt.bindingSha256 -cne $BindingHash -or @($O.mismatches).Count){throw 'N7.LaunchAuthority: healthy actual CURRENT selected Apply required.'}
}
function Get-N7LaunchMutex($B){
    [Threading.Mutex]::new($false,('Local\WinSight-N7-'+(Get-N5Hash $script:N5Utf8.GetBytes($B.roots.controlRoot.ToUpperInvariant()))))
}
function Assert-N7LaunchSources($B,$Sources){
    $live=Import-N7Protocol
    & $live {param($S) Assert-NLSources $S} $Sources
    $runtime=$B.roots.controlRoot+'\runtime\'+$B.sources.sha256
    if($PSScriptRoot -cne $runtime){throw 'N7.LaunchRuntime: validator must execute from the exact co-located source9 runtime.'}
    foreach($f in $Sources.files){
        $path=$runtime+'\'+$f.name
        $pin=& $live {param($P,$S,$D) Get-NLFilePin $P $S $D} $path $script:N5Start $script:N5Deadline
        if($pin.bytes -ne $f.bytes -or $pin.sha256 -cne $f.sha256){throw 'N7.LaunchSourcePin: current runtime source differs.'}
    }
    foreach($row in @(@('NativeInstaller.psm1',$script:N5EngineHash),@('NativeDeploymentJob.psm1',$script:N5JobHash))){if(@($Sources.files|Where-Object {$_.name -ceq $row[0] -and $_.sha256 -ceq $row[1]}).Count -ne 1){throw 'N7.LaunchFrozenSource: protected source changed.'}}
}
function Invoke-N7LaunchDispatch($P){
    $script:N7LaunchResult=$null
    $live=Import-N7Protocol
    $b=Read-NativeLiveBinding $P.BindingPath $P.BindingSha256 $script:N5Start $script:N5Deadline
    $sources=& $live {param($B,$S,$D) Read-NLGraphObject $B.sources $B.roots.controlRoot $S $D} $b $script:N5Start $script:N5Deadline
    Assert-N7LaunchSources $b $sources
    $dispatch=[pscustomobject]@{coordinatorSha256=(@($sources.files|Where-Object {$_.name -ceq 'NativeInstallCoordinator.psm1'})[0].sha256);entrySha256=(@($sources.files|Where-Object {$_.name -ceq 'Invoke-NativeInstallCoordinator.ps1'})[0].sha256)}
    $parameters=@{};foreach($k in $P.Keys){$parameters[$k]=$P[$k]};$parameters.launchValidation=$true
    $result=Invoke-N5Dispatch $parameters $dispatch
    Assert-N5Fields $result @('protocol','descriptor','entryAccounting','validatorSettlement','settlement')
    if($result.protocol -cne 'NativeLaunchValidation/1'){throw 'N7.LaunchResult: wrong validator result.'}
    Assert-N7LaunchDescriptor $result.descriptor
    $script:N7LaunchResult=$result
    $result
}
function Assert-N7LaunchAssignment($Request){
    Assert-N5Fields $Request @('schema','protocol','nonce','parentPid','assigned','parameters')
    Assert-N5Integer $Request.schema 6 6;Assert-N5Integer $Request.parentPid 1 ([int]::MaxValue);Assert-N5Hash $Request.nonce
    if($Request.protocol -isnot [string] -or $Request.protocol -cne 'NativeLaunchValidator/1' -or $Request.nonce -cne $env:N5_NONCE -or $Request.parentPid -ne [int]$env:N5_PARENT -or $Request.assigned -isnot [bool] -or -not $Request.assigned){throw 'N7.LaunchAssignment: closed assigned nonce/parent required.'}
    Assert-N5Fields $Request.parameters @('Command','BindingPath','BindingSha256','StartTick','DeadlineMilliseconds','launchValidation')
    $p=@{};foreach($prop in $Request.parameters.PSObject.Properties){if($prop.Name -cne 'launchValidation'){$p[$prop.Name]=$prop.Value}}
    $live=Import-N7Protocol
    & $live {param($P) Assert-NLArguments $P} $p
    if($p.Command -cne 'ValidateLaunch' -or $Request.parameters.launchValidation -isnot [bool] -or -not $Request.parameters.launchValidation -or $p.StartTick -ne $script:N5Start -or $p.DeadlineMilliseconds -ne $script:N5Deadline){throw 'N7.LaunchRequest: read-only command/original clock required.'}
    $p
}
function ConvertFrom-N7LaunchJson([byte[]]$Bytes){
    if($Bytes.Length -eq 0 -or $Bytes.Length -gt 1048576){throw 'N7.LaunchJson: bounded JSON required.'}
    $text=$script:N5Utf8.GetString($Bytes);$position=0;$last=$null
    $objects=[Collections.Generic.Stack[object]]::new()
    $regex=[regex]::new('\G[\x20\x09\x0a\x0d]*("(?:[^"\\\x00-\x1f]|\\["\\/bfnrt]|\\u[0-9a-fA-F]{4})*"|[{}\[\]:,]|true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)')
    while($position -lt $text.Length){
        if((Get-N5Remaining) -le 0){throw 'N7.Deadline: launch JSON.'}
        $m=$regex.Match($text,$position)
        if(-not $m.Success){if($text.Substring($position) -cmatch '\A[\x20\x09\x0a\x0d]*\z'){break};throw 'N7.LaunchJson: invalid raw token.'}
        $token=$m.Groups[1].Value;$position+=$m.Length
        if($token -ceq '{'){$objects.Push([Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase));if($objects.Count -gt 16){throw 'N7.LaunchJson: object depth.'}}
        elseif($token -ceq '}'){if(-not $objects.Count){throw 'N7.LaunchJson: object end.'};$null=$objects.Pop()}
        elseif($token -ceq ':'){
            if(-not $objects.Count -or $null -eq $last -or -not $last.StartsWith('"')){throw 'N7.LaunchJson: property required.'}
            $name=$last|ConvertFrom-Json
            if(-not $objects.Peek().Add($name)){throw 'N7.LaunchJson: duplicate decoded property.'}
        }
        $last=$token
    }
    $value=$text|ConvertFrom-Json -ErrorAction Stop
    if($objects.Count -or $value -isnot [System.Management.Automation.PSCustomObject]){throw 'N7.LaunchJson: object required.'}
    $value
}
function Read-N7LaunchSettings([string]$DataRoot){
    $path=$DataRoot+'\settings.json';$null=Assert-N5Path $path
    try{$bytes=Read-N5Bytes $path 1048576}catch [IO.FileNotFoundException]{return [pscustomobject]@{path=$path;state='Absent';bytes=$null;sha256=$null}}catch [IO.DirectoryNotFoundException]{return [pscustomobject]@{path=$path;state='Absent';bytes=$null;sha256=$null}}
    $settings=ConvertFrom-N7LaunchJson $bytes
    foreach($prop in $settings.PSObject.Properties){
        if($prop.Name -cin @('inferenceExecutable','modelDirectory','manifestPath','indexDirectory') -and $null -ne $prop.Value){throw 'N7.LaunchSettingsOverride: task settings must use the pinned application-relative runtime and task index.'}
    }
    [pscustomobject]@{path=$path;state='Present';bytes=[long]$bytes.Length;sha256=(Get-N5Hash $bytes)}
}
function Assert-N7LaunchRoles($G,[string]$ReleaseRoot){
    $launch=$G.package.launch
    if($launch.app.path -cne 'app/WinSight.App.exe' -or $launch.cli.path -cne 'app/winsight-native.exe'){throw 'N7.LaunchRoles: exact native App/CLI required.'}
    $requiredHelpers=@('app/inference/WinSight.InferenceHost.exe','app/inference/onnxruntime.dll','app/inference/runtimes/win-x64/native/winsight_tokenizer.dll')
    foreach($p in $requiredHelpers){if(@($launch.helpers|Where-Object {$_.path -ceq $p}).Count -ne 1){throw 'N7.LaunchRoles: inference/native/tokenizer helper pin required.'}}
    $profilePath='app/inference/model-manifest.json'
    if(@($launch.profiles|Where-Object {$_.path -ceq $profilePath}).Count -ne 1){throw 'N7.LaunchRoles: locked profile manifest required.'}
    $model=ConvertFrom-N7LaunchJson (Read-N5Bytes ($ReleaseRoot+'\'+$profilePath.Replace('/','\')) 1048576)
    Assert-N5Fields $model @('repository','revision','files')
    if($model.repository -cne 'onnx-community/embeddinggemma-2-ONNX' -or $model.revision -cne 'daa72c51243991dfcaf9f9137d2c573d8f7790c0' -or $model.files -isnot [array] -or $model.files.Count -ne 11){throw 'N7.LaunchProfile: locked embedding profile required.'}
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($f in $model.files){
        Assert-N5Fields $f @('path','size','sha256');Assert-N5Integer $f.size 0 ([long]::MaxValue);Assert-N5Hash $f.sha256
        $relative='app/models/'+$f.path
        $records=@($G.payloadManifest.files|Where-Object {$_.path -ceq $relative})
        $roles=@(@($launch.models)+@($launch.profiles)|Where-Object {$_.path -ceq $relative})
        if(-not $paths.Add($relative) -or $records.Count -ne 1 -or $roles.Count -ne 1 -or $records[0].bytes -ne $f.size -or $records[0].sha256 -cne $f.sha256){throw 'N7.LaunchProfile: exact model/tokenizer/preprocessing inventory and role pins required.'}
    }
    foreach($p in @('config.json','preprocessor_config.json','processor_config.json','tokenizer.json','tokenizer_config.json','onnx/audio_encoder_q4.onnx','onnx/audio_encoder_q4.onnx_data','onnx/model_q4.onnx','onnx/model_q4.onnx_data','onnx/vision_encoder_q4.onnx','onnx/vision_encoder_q4.onnx_data')){if(-not $paths.Contains('app/models/'+$p)){throw 'N7.LaunchProfile: incomplete locked profile.'}}
}
function Assert-N7LaunchDescriptor($D){
    Assert-N5Fields $D @('protocol','binding','receipt','state','manifest','sourceManifest','releaseRoot','dataRoot','app','cli','criticalFiles','arguments','workingDirectory','settings')
    if($D.protocol -cne 'NativeLaunchDescriptor/1'){throw 'N7.LaunchDescriptor: protocol.'}
    foreach($n in @('binding','receipt','state','manifest','sourceManifest','app','cli')){Assert-N5Fields $D.$n @('path','bytes','sha256');$null=Assert-N5Path $D.$n.path;Assert-N5Integer $D.$n.bytes 0 ([long]::MaxValue);Assert-N5Hash $D.$n.sha256}
    foreach($n in @('releaseRoot','dataRoot','workingDirectory')){$null=Assert-N5Path $D.$n}
    if($D.arguments -isnot [string] -or $D.arguments -cne ('--DATA-DIR "'+$D.dataRoot+'"') -or $D.app.path -cne ($D.releaseRoot+'\app\WinSight.App.exe') -or $D.cli.path -cne ($D.releaseRoot+'\app\winsight-native.exe') -or $D.workingDirectory -cne ($D.releaseRoot+'\app')){throw 'N7.LaunchDescriptor: closed App/data/argv/working directory required.'}
    if($D.criticalFiles -isnot [array] -or $D.criticalFiles.Count -lt 3 -or $D.criticalFiles.Count -gt 4096){throw 'N7.LaunchDescriptor: critical pins.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($f in $D.criticalFiles){Assert-N5Fields $f @('path','bytes','sha256');$null=Assert-N5Path $f.path;Assert-N5Hash $f.sha256;Assert-N5Integer $f.bytes 0 ([long]::MaxValue);if(-not $f.path.StartsWith($D.releaseRoot+'\app\',[StringComparison]::Ordinal) -or -not $seen.Add($f.path)){throw 'N7.LaunchDescriptor: critical file boundary/duplicate.'}}
    Assert-N5Fields $D.settings @('path','state','bytes','sha256')
    if($D.settings.path -cne ($D.dataRoot+'\settings.json') -or $D.settings.state -cnotin @('Absent','Present')){throw 'N7.LaunchDescriptor: task settings boundary.'}
    if($D.settings.state -ceq 'Present'){Assert-N5Hash $D.settings.sha256;Assert-N5Integer $D.settings.bytes 0 1048576}elseif($null -ne $D.settings.sha256 -or $null -ne $D.settings.bytes){throw 'N7.LaunchDescriptor: explicit absent settings.'}
}
function Add-N7LaunchCleanupFailureMessage($ErrorRecord){
    $diagnostics=@($ErrorRecord.Exception.Data['N7LaunchValidatorCleanup'])
    $failure=$ErrorRecord.Exception.Data['N5Failure'];$original=$failure.primaryError.message
    try{
        # The unchanged parent retains only the first 2048 stderr characters.
        # Bound the serialized prefix so both primary and secondary summaries survive.
        for($limit=128;$limit -ge 8;$limit=[int]($limit/2)){
            $details=@(foreach($problem in $diagnostics){Limit-N5Message $problem $limit})
            $failure.primaryError.message=(Limit-N5Message $original ($limit*2))+'; N7.LaunchCleanup (secondary): '+($details -join '; ')
            $prefix=[pscustomobject]@{schema=$failure.schema;status=$failure.status;primaryError=$failure.primaryError}
            if((ConvertTo-N5Bytes $prefix).Length -le 1536){return}
        }
        throw 'N7.LaunchCleanupEncoding: failure prefix exceeds reporting reserve.'
    }catch{
        $ErrorRecord.Exception.Data['N7LaunchCleanupFormattingError']=$_
        $failure.primaryError.message=(Limit-N5Message $original 128)+'; N7.LaunchCleanup (secondary): diagnostic formatting failed; original diagnostics retained in ErrorRecord.'
    }
}
function Invoke-N7LaunchWorker($Request){
    $p=Assert-N7LaunchAssignment $Request
    $g=Read-NativeLiveGraph $p.BindingPath $p.BindingSha256 $script:N5Start $script:N5Deadline
    Assert-N7LaunchSources $g.binding $g.sources
    $mutex=Get-N7LaunchMutex $g.binding;$held=$false;$primary=$null;$cleanup=[Collections.Generic.List[string]]::new()
    try{
        try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true;throw 'N7.LaunchRecoveryRequired: abandoned coordinator.'}
        if(-not $held){throw 'N7.LaunchBusy: cooperating transaction active.'}
        foreach($path in @(($g.binding.roots.controlRoot+'\pending.json'),($g.binding.roots.controlRoot+'\removal-anchor.json'),($g.binding.roots.deploymentRoot+'\journal.json'))){Assert-N7LaunchAbsent $path}
        $o=Get-NativeLiveObservation -BindingPath $p.BindingPath -BindingSha256 $p.BindingSha256 -StartTick $script:N5Start -DeadlineMilliseconds $script:N5Deadline
        Assert-N7LaunchCurrent $o $p.BindingSha256
        $r=$o.authority.selected.receipt;$root=$g.binding.roots.deploymentRoot;$data=$g.binding.roots.dataRoot
        if($r.selection -cne $g.package.payloadManifest.sha256 -or $r.manifestSha256 -cne $r.selection){throw 'N7.LaunchSelection: current graph must be the actual selected release.'}
        $live=Import-N7Protocol
        $state=& $live {param($P,$H,$S,$D) Read-NLJson $P $H $S $D} ($root+'\state.json') $r.nextStateSha256 $script:N5Start $script:N5Deadline
        & $live {param($S,$B) Assert-NLDomainState $S $B} $state $g.binding
        $release=@($state.releases|Where-Object {$_.id -ceq $state.current})
        if($state.current -cne $r.selection -or $release.Count -ne 1 -or (Get-N5Hash (ConvertTo-N5Bytes $release[0].files)) -cne (Get-N5Hash (ConvertTo-N5Bytes $g.payloadManifest.files))){throw 'N7.LaunchState: selected canonical Core inventory differs from raw manifest.'}
        $core=Import-N7Core
        & $core {param($Root,$Data,$Hash) $s=Read-NativeState $Root $Data $Hash;Assert-NativeLayout $Root $s.releases;Assert-NativeInstalled $Root $s} $root $data $r.nextStateSha256
        if((Get-N5Remaining) -le 10000){throw 'N7.Deadline: full Core validation left no settlement reserve.'}
        $version=$root+'\versions\'+$state.current
        & $live {
            param($M,$Root,$S,$D)
            $inventory=Assert-NLInventory $M;Assert-NLPayloadCoverage $inventory $Root $S $D
            foreach($f in $M.files){Assert-NLDeadline $S $D;Assert-NLGraphPath ($Root+'\'+$f.path.Replace('/','\')) $Root}
        } $g.payloadManifest $version $script:N5Start $script:N5Deadline
        Assert-N7LaunchRoles $g $version
        $settings=Read-N7LaunchSettings $data
        $critical=@(foreach($f in (@($g.package.launch.app,$g.package.launch.cli)+$g.package.launch.helpers+$g.package.launch.models+$g.package.launch.profiles)){[pscustomobject]@{path=($version+'\'+$f.path.Replace('/','\'));bytes=$f.bytes;sha256=$f.sha256}})
        $d=[pscustomobject]@{protocol='NativeLaunchDescriptor/1';binding=[pscustomobject]@{path=$p.BindingPath;bytes=[long](Read-N5Bytes $p.BindingPath 32768).Length;sha256=$p.BindingSha256};receipt=[pscustomobject]@{path=($g.binding.roots.controlRoot+'\receipts\'+$o.authority.selectedReceiptSha256+'.json');bytes=[long]$o.authority.selected.receiptBytes;sha256=$o.authority.selectedReceiptSha256};state=[pscustomobject]@{path=($root+'\state.json');bytes=[long](Read-N5Bytes ($root+'\state.json')).Length;sha256=$r.nextStateSha256};manifest=$g.package.payloadManifest;sourceManifest=$g.binding.sources;releaseRoot=$version;dataRoot=$data;app=$critical[0];cli=$critical[1];criticalFiles=$critical;arguments=('--DATA-DIR "'+$data+'"');workingDirectory=($version+'\app');settings=$settings}
        Assert-N7LaunchDescriptor $d
        $again=Get-NativeLiveAuthority -BindingPath $p.BindingPath -BindingSha256 $p.BindingSha256 -StartTick $script:N5Start -DeadlineMilliseconds $script:N5Deadline
        if($again.status -cne 'Apply' -or $again.relationship -cne 'Current' -or -not $again.anchorRechecked -or $again.selectedReceiptSha256 -cne $d.receipt.sha256 -or $null -ne $again.pendingIntentSha256){throw 'N7.LaunchSelectionChanged: selected authority changed during validation.'}
        [pscustomobject]@{protocol='NativeLaunchValidation/1';descriptor=$d}
    }catch{$primary=$_}finally{
        if($held){try{$mutex.ReleaseMutex()}catch{$cleanup.Add('ReleaseMutex: '+$_.Exception.Message)}}
        try{$mutex.Dispose()}catch{$cleanup.Add('Mutex.Dispose: '+$_.Exception.Message)}
    }
    if($null -ne $primary){$primary.Exception.Data['N7LaunchValidatorCleanup']=$cleanup.ToArray();throw $primary}
    if($cleanup.Count){
        $failure=[InvalidOperationException]::new('N7.LaunchCleanup: validator mutex cleanup failed.')
        $failure.Data['N7LaunchValidatorCleanup']=$cleanup.ToArray();throw $failure
    }
}
function Start-N7ValidatedLaunch($Result,[hashtable]$StartOutcome=@{}){
    $StartOutcome.startAttempted=$false;$StartOutcome.startSucceeded=$null;$StartOutcome.processId=$null;$StartOutcome.cleanupErrors=@()
    if($null -eq $script:N7LaunchResult -or -not [object]::ReferenceEquals($Result,$script:N7LaunchResult)){throw 'N7.LaunchReplay: only the just-settled in-process result may be consumed.'}
    $script:N7LaunchResult=$null
    Assert-N5Fields $Result @('protocol','descriptor','entryAccounting','validatorSettlement','settlement')
    $v=$Result.validatorSettlement;Assert-N5Fields $v @('workerExited','exitCode','activeOwnedProcesses','stdoutState','stderrState','cleanupState','handlesClosed')
    if($v.exitCode -isnot [int] -or $v.exitCode -ne 0 -or $v.workerExited -ne $true -or $v.activeOwnedProcesses -ne 0 -or $v.stdoutState -cne 'Eof' -or $v.stderrState -cne 'Eof' -or $v.cleanupState -cne 'Settled' -or $v.handlesClosed -ne $true){throw 'N7.LaunchSettlement: actual Job0/dual EOF/exit0/cleanup required.'}
    $d=$Result.descriptor;Assert-N7LaunchDescriptor $d
    $live=Import-N7Protocol
    $b=Read-NativeLiveBinding $d.binding.path $d.binding.sha256 $script:N5Start $script:N5Deadline
    $sources=& $live {param($B,$S,$D) Read-NLGraphObject $B.sources $B.roots.controlRoot $S $D} $b $script:N5Start $script:N5Deadline
    Assert-N7LaunchSources $b $sources
    $mutex=Get-N7LaunchMutex $b;$held=$false;$locks=[Collections.Generic.List[IDisposable]]::new();$app=$null;$cleanup=[Collections.Generic.List[string]]::new();$primary=$null
    try{
        try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true;throw 'N7.LaunchRecoveryRequired: abandoned coordinator before Start.'}
        if(-not $held){throw 'N7.LaunchBusy: transaction before Start.'}
        foreach($path in @(($b.roots.controlRoot+'\pending.json'),($b.roots.controlRoot+'\removal-anchor.json'),($b.roots.deploymentRoot+'\journal.json'))){Assert-N7LaunchAbsent $path}
        $pins=@($d.binding,$d.receipt,$d.state,$d.manifest,$d.sourceManifest,$b.package,$b.shellSpec)+$d.criticalFiles+@(foreach($f in $sources.files){[pscustomobject]@{path=($PSScriptRoot+'\'+$f.name);bytes=$f.bytes;sha256=$f.sha256}})
        if($d.settings.state -ceq 'Present'){$pins+=,[pscustomobject]@{path=$d.settings.path;bytes=$d.settings.bytes;sha256=$d.settings.sha256}}
        foreach($pin in $pins){
            $null=Assert-N5Path $pin.path
            $stream=[IO.File]::Open($pin.path,'Open','Read','Read');$locks.Add($stream)
            $actual=& $live {param($P,$S,$D) Get-NLFilePin $P $S $D} $pin.path $script:N5Start $script:N5Deadline
            if($actual.bytes -ne $pin.bytes -or $actual.sha256 -cne $pin.sha256){throw 'N7.LaunchPinChanged: critical pin changed before Start.'}
        }
        $settings=Read-N7LaunchSettings $b.roots.dataRoot
        if((Get-N5Hash (ConvertTo-N5Bytes $settings)) -cne (Get-N5Hash (ConvertTo-N5Bytes $d.settings))){throw 'N7.LaunchSettingsChanged: task settings changed before Start.'}
        $o=Get-NativeLiveObservation -BindingPath $d.binding.path -BindingSha256 $d.binding.sha256 -StartTick $script:N5Start -DeadlineMilliseconds $script:N5Deadline
        Assert-N7LaunchCurrent $o $d.binding.sha256
        if($o.authority.selectedReceiptSha256 -cne $d.receipt.sha256 -or $o.authority.selected.receipt.nextStateSha256 -cne $d.state.sha256 -or $o.authority.selected.receipt.selection -cne [IO.Path]::GetFileName($d.releaseRoot)){throw 'N7.LaunchSelectionChanged: current authority no longer selects validated release.'}
        if((Get-N5Remaining) -le 1000){throw 'N7.Deadline: insufficient Start/cleanup reserve.'}
        $si=[Diagnostics.ProcessStartInfo]::new();$si.FileName=$d.app.path;$si.Arguments=$d.arguments;$si.WorkingDirectory=$d.workingDirectory;$si.UseShellExecute=$false
        foreach($n in @($si.EnvironmentVariables.Keys)){if($n -clike 'N5_*' -or $n -clike 'N7_*'){$si.EnvironmentVariables.Remove($n)}}
        # This is deliberately after the short-lived validator Job was closed.
        # A broader Job inherited from the launcher's host is not bypassed.
        $app=[Diagnostics.Process]::new();$app.StartInfo=$si
        # A throwing Start leaves success unknown; a later failure must not erase the side effect.
        $StartOutcome.startAttempted=$true
        $StartOutcome.startSucceeded=$app.Start()
        if(-not $StartOutcome.startSucceeded){throw 'N7.LaunchStart: App process did not start.'}
        $StartOutcome.processId=$app.Id
        [pscustomobject]@{protocol='NativeAppStart/1';status='Started';processId=$StartOutcome.processId;uiReady=$false;validatorJobClosed=$true;broaderHostJobNotBypassed=$true;receiptSha256=$d.receipt.sha256}
    }catch{
        $primary=$_
        $primary.Exception.Data['N7LaunchStartOutcome']=$StartOutcome
        throw
    }finally{
        if($null -ne $app){try{$app.Dispose()}catch{$cleanup.Add($_.Exception.Message)}}
        foreach($resource in $locks){try{$resource.Dispose()}catch{$cleanup.Add($_.Exception.Message)}}
        if($held){try{$mutex.ReleaseMutex()}catch{$cleanup.Add($_.Exception.Message)}}
        try{$mutex.Dispose()}catch{$cleanup.Add($_.Exception.Message)}
        $StartOutcome.cleanupErrors=$cleanup.ToArray()
        if($null -ne $primary){$primary.Exception.Data['N7LaunchCleanup']=$StartOutcome.cleanupErrors}
        if($cleanup.Count -and $null -eq $primary){
            $failure=[InvalidOperationException]::new('N7.LaunchCleanup: App may have started; '+($cleanup -join '; '))
            $failure.Data['N7LaunchStartOutcome']=$StartOutcome;$failure.Data['N7LaunchCleanup']=$StartOutcome.cleanupErrors
            throw $failure
        }
    }
}
function Invoke-N7LiveRequest($P,$Live){
    if($P.Command -ceq 'ValidateLaunch'){return Invoke-N7LaunchDispatch $P}
    $g=Read-NativeLiveGraph $P.BindingPath $P.BindingSha256 $script:N5Start $script:N5Deadline
    if($P.Command -ceq 'Inspect'){
        $inspect=@{BindingPath=$P.BindingPath;BindingSha256=$P.BindingSha256;StartTick=$script:N5Start;DeadlineMilliseconds=$script:N5Deadline}
        if($P.ContainsKey('ExpectedReceiptSha256')){$inspect.ExpectedReceiptSha256=$P.ExpectedReceiptSha256}
        $observation=Get-NativeLiveObservation @inspect
        if($P.ContainsKey('ExpectedReceiptSha256') -and $observation.status -ceq 'Absent'){throw 'N7.ExpectedReceiptAbsent: expected committed snapshot is not selected/verifiable.'}
        return $observation
    }
    if($P.Command -cne 'RunOwnedShell'){throw 'N7.ActiveContextRequired: public phases/restart recovery cannot reconstruct genuine native capability.'}
    $mutex=[Threading.Mutex]::new($false,('Local\WinSight-N7-'+(Get-N5Hash $script:N5Utf8.GetBytes($g.binding.roots.controlRoot.ToUpperInvariant()))));$held=$false;$s=$null
    try{
        try{$held=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$held=$true;throw 'N7.RecoveryRequired: abandoned live coordinator; no automatic adoption.'}
        if(-not $held){throw 'N7.LiveBusy: cooperating transaction active.'}
        $c=$g.binding.roots.controlRoot
        foreach($path in @($c+'\pending.json',$c+'\removal-anchor.json',$c+'\finalized\'+$P.TransactionId+'.json')){if([IO.File]::Exists($path)){throw 'N7.RecoveryRequired: existing pending/removal/transaction record; never adopt.'}}
        Assert-N7ShellBoundary $g
        $observed=Get-NativeLiveObservation -BindingPath $P.BindingPath -BindingSha256 $P.BindingSha256 -StartTick $script:N5Start -DeadlineMilliseconds $script:N5Deadline
        if($observed.status -cnotin @('Absent','ObservedApply')){throw ('N7.UnknownOwnership: initial observation='+$observed.status)}
        $a=$observed.authority
        if($a.pendingIntentSha256){throw 'N7.RecoveryRequired: outstanding registry pending anchor.'}
        if($P.Operation -ceq 'Remove' -and $observed.status -cne 'ObservedApply'){throw 'N7.UnknownOwnership: Remove requires actual selected healthy Apply.'}
        $intent=New-N7LiveIntent $g $a $P.Operation $P.TransactionId
        $dispatch=[pscustomobject]@{coordinatorSha256=(@($g.sources.files|Where-Object {$_.name -ceq 'NativeInstallCoordinator.psm1'})[0].sha256);entrySha256=(@($g.sources.files|Where-Object {$_.name -ceq 'Invoke-NativeInstallCoordinator.ps1'})[0].sha256)}
        $s=[pscustomobject]@{graph=$g;authority=$a;intent=$intent;intentHash=(Get-N7RecordHash $intent);bindingPath=$P.BindingPath;dispatchBinding=$dispatch;phase='Preparing';revoked=$false;continuationConsumed=$false;activationAttempted=$false;receiptHash=$null;mutexHeld=$true;start=$script:N5Start;deadline=$script:N5Deadline;lastEvent=$null;native=$null;nativeTicks=0L;removeRole=$null;roleSequence=0;worker=$null;job=[IntPtr]::Zero;readers=$null}
        if($null -ne $script:N7LiveActive){throw 'N7.LiveBusy: module singleton already active.'};$script:N7LiveActive=$s
        $plan=Resolve-N7GenuinePlan $g $a $intent
        $null=Save-N7Object $s 'intents' $intent;Add-N7Event $s 'Intent' $s.intentHash
        $pending=New-N7Pending $s;Assert-N7LiveRecord $pending;$null=Write-N7Record ($c+'\pending.json') $pending
        $request=@{BindingPath=$P.BindingPath;BindingSha256=$P.BindingSha256;IntentSha256=$s.intentHash;StartTick=$script:N5Start;DeadlineMilliseconds=$script:N5Deadline}
        Invoke-N5Dispatch $request $dispatch $plan $s
    }finally{
        if($null -ne $s){$s.revoked=$true;$s.mutexHeld=$false};$script:N7LiveActive=$null
        if($held){$mutex.ReleaseMutex()};$mutex.Dispose()
    }
}
# Additive copy-only adapter: never a native transaction/recovery capability.
function Assert-N7PreparedCopyArguments([hashtable]$P) {
    $allowed=@('Command','CopyCaseRoot','CopyWitnessSha256','StartTick','DeadlineMilliseconds')
    if($P.Count -ne $allowed.Count -or @($P.Keys|Where-Object {$_ -cnotin $allowed}).Count -or $P.Command -cne 'AbortPreparedCopy'){throw 'N7.CopyArguments: closed copy-only selector required.'}
    Assert-N5Hash $P.CopyWitnessSha256
    Assert-N5Integer $P.StartTick 0 4294967295L
    Assert-N5Integer $P.DeadlineMilliseconds 1000 160000
    if($P.StartTick -ne $script:N5Start -or $P.DeadlineMilliseconds -ne $script:N5Deadline){throw 'N7.CopyArguments: original shared clock required.'}
    if($P.CopyCaseRoot -isnot [string] -or $P.CopyCaseRoot -notmatch '^[A-Za-z]:\\' -or $P.CopyCaseRoot.IndexOf('"') -ge 0){throw 'N7.CopyBoundary: absolute copy case required.'}
    $base=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($PSScriptRoot))) '.dev\prepared-abort-copy-v1\cases'
    $base=[IO.Path]::GetFullPath($base);$root=[IO.Path]::GetFullPath($P.CopyCaseRoot)
    if(-not [string]::Equals([IO.Path]::GetDirectoryName($root),$base,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($root) -cnotmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$' -or -not [IO.Directory]::Exists($root)){throw 'N7.CopyBoundary: direct existing qualified copy case required.'}
    for($path=$root;$path;$path=[IO.Path]::GetDirectoryName($path)){if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)){throw 'N7.CopyBoundary: reparse ancestor refused.'}}
    if(-not [IO.File]::Exists((Join-Path $root 'witness.json'))){throw 'N7.CopyBoundary: pinned witness file required.'}
    $root
}
function Get-N7PreparedCopySources {
    $python='__WINSIGHT_PRIVATE_PYTHON_EXE__'
    if($python.StartsWith('__')){throw 'N7.CopySource: source-only template; private runtime must be materialized and re-pinned before use.'}
    $bridge=Join-Path $PSScriptRoot 'InvokePreparedAbortCopy.py'
    $library=Join-Path $PSScriptRoot 'PreparedAbortCopy.py'
    foreach($row in @(@($python,'a2a4eff8d0b0c845284c607d50a3b5b966ac5a3121736a2e38e165bd6644d9fe'),@($bridge,'c94826d323fe6dae37f946218bb91ab5e59726690b6cf6e4886049b97610e44b'),@($library,'6c1e0188c313228181168c64e610c1250fe56a51c036f8a22e7b4dfb5502828d'))){
        for($path=$row[0];$path;$path=[IO.Path]::GetDirectoryName($path)){if(([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReparsePoint)){throw 'N7.CopySource: reparse executable/source refused.'}}
        if((Get-N5Hash (Read-N5Bytes $row[0] 2097152)) -cne $row[1]){throw 'N7.CopySource: fixed trusted sibling/runtime bytes differ.'}
    }
    [pscustomobject]@{python=$python;bridge=$bridge;library=$library}
}
function Invoke-N7PreparedCopyDispatch([hashtable]$P) {
    $null=Get-N7PreparedCopySources
    $root=Assert-N7PreparedCopyArguments $P
    if($null -ne $script:N7LiveActive){throw 'N7.CopyScope: cannot attach to a native live session.'}
    if((Get-N5Remaining) -le 30000){throw 'N7.CopyDeadline: insufficient worker/cleanup reserve.'}
    $parameters=@{};foreach($key in $P.Keys){$parameters[$key]=$P[$key]};$parameters.CopyCaseRoot=$root;$parameters.preparedAbortCopy=$true
    $sources=[pscustomobject]@{coordinatorSha256=(Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot 'NativeInstallCoordinator.psm1')));entrySha256=(Get-N5Hash (Read-N5Bytes (Join-Path $PSScriptRoot 'Invoke-NativeInstallCoordinator.ps1') 32768))}
    Invoke-N5Dispatch $parameters $sources
}
function ConvertFrom-N7PreparedCopyWire([string]$Text) {
    if($Text.Length -gt 32768 -or $Text -cmatch '[^\x09\x0a\x20-\x7e]'){throw 'N7.CopyWire: bounded ASCII LF wire required.'}
    $spec=[ordered]@{protocol='S';scope='S';state='S';planSha256='S';transactionId='S';intentSha256='S';pendingSha256='S';journalSha256='S';retainedCandidate='S';originalFileCount='I';snapshotSha256='S';nativeAdmissionAllowed='B';nativeAuthorityResumed='B';installed='B';liveRecovered='B';pendingAndJournalRetainedAtCopiedPaths='B';result='S'}
    $lines=$Text.Split([char]10)
    if($lines.Count -ne $spec.Count+2 -or $lines[0] -cne 'NativePreparedAbortCopyWire/1' -or $lines[$lines.Count-1] -cne ''){throw 'N7.CopyWire: exact header/field-count/final LF required.'}
    $values=[ordered]@{};$index=1
    foreach($item in $spec.GetEnumerator()){
        $columns=$lines[$index].Split([char]9);$index++
        if($columns.Count -ne 3 -or $columns[0] -cne $item.Key -or $columns[1] -cne $item.Value){throw 'N7.CopyWire: closed ordered unique fields and raw type tags required.'}
        switch -CaseSensitive ($item.Value) {
            'S' {if($columns[2] -cnotmatch '^[\x20-\x7e]{1,128}$'){throw 'N7.CopyWire: raw bounded string required.'};$values[$item.Key]=$columns[2]}
            'I' {if($columns[2] -cnotmatch '^(0|[1-9][0-9]{0,4})$'){throw 'N7.CopyWire: raw canonical decimal integer required.'};$number=[int]::Parse($columns[2],[Globalization.CultureInfo]::InvariantCulture);Assert-N5Integer $number 1 10000;$values[$item.Key]=$number}
            'B' {if($columns[2] -cnotin @('true','false')){throw 'N7.CopyWire: raw boolean required.'};$values[$item.Key]=($columns[2] -ceq 'true')}
        }
    }
    [pscustomobject]$values
}
function Initialize-N7PreparedCopyReaders($R,$Process) {
    # Leaf-only encoding: no emitted preamble, strict UTF-8, no BOM detection.
    # Own the base streams; the shared legacy N5 readers remain unchanged.
    $R.streams=@()
    foreach($base in @($Process.StandardOutput.BaseStream,$Process.StandardError.BaseStream)){
        $reader=[IO.StreamReader]::new($base,[Text.UTF8Encoding]::new($false,$true),$false,2048,$false)
        $R.streams+=,$reader
    }
    for($i=0;$i -lt 2;$i++){$R.buffers+=,(New-Object char[] 2048);$R.tasks+=,$R.streams[$i].ReadAsync($R.buffers[$i],0,2048);$R.states[$i]='Pending'}
}
function Invoke-N7PreparedCopyPython([string]$CaseRoot,[string]$WitnessSha256) {
    $sources=Get-N7PreparedCopySources
    $budget=[int][Math]::Min(120000,(Get-N5Remaining)-15000)
    if($budget -lt 1000){throw 'N7.CopyDeadline: Python cleanup reserve unavailable.'}
    $process=$null;$started=$false;$settled=$false;$primary=$null;$code=$null;$ownedPid=$null;$copyResult=$null
    $cleanup=[Collections.Generic.List[string]]::new()
    $readers=[pscustomobject]@{tasks=@();buffers=@();states=@('NotStarted','NotStarted');streams=@();text=@('','');problem=$null}
    $leafClock=[Diagnostics.Stopwatch]::StartNew()
    try{
        $process=[Diagnostics.Process]::new();$si=[Diagnostics.ProcessStartInfo]::new()
        $si.FileName=$sources.python;$si.Arguments='-I -B -S "'+$sources.bridge+'" --copy-root "'+$CaseRoot+'" --witness-sha256 '+$WitnessSha256+' --budget-milliseconds '+$budget
        $si.UseShellExecute=$false;$si.CreateNoWindow=$true;$si.RedirectStandardOutput=$true;$si.RedirectStandardError=$true
        $si.StandardOutputEncoding=$script:N5Utf8;$si.StandardErrorEncoding=$script:N5Utf8
        foreach($name in @($si.EnvironmentVariables.Keys)){if($name -like 'N5_*' -or $name -like 'N7_*'){$si.EnvironmentVariables.Remove($name)}}
        $si.EnvironmentVariables.Remove('PSModulePath')
        $process.StartInfo=$si;$started=$process.Start();if(-not $started){throw 'N7.CopyPython: Start returned false.'};$ownedPid=$process.Id;$null=$process.Handle
        Initialize-N7PreparedCopyReaders $readers $process
        while((Get-N5Remaining) -gt 12000 -and $leafClock.ElapsedMilliseconds -lt $budget+1000){
            Update-N5Readers $readers
            if($readers.problem){throw $readers.problem}
            if($process.HasExited -and $readers.states[0] -ceq 'Eof' -and $readers.states[1] -ceq 'Eof'){$settled=$true;break}
            Start-Sleep -Milliseconds 10
        }
        if(-not $settled){throw 'N7.CopyPython: bounded execution/drain incomplete.'}
        $code=$process.ExitCode
        if(($code -isnot [int] -and $code -isnot [long]) -or $code -ne 0){throw ('N7.CopyPython: nonzero/unknown exit; '+(Limit-N5Message $readers.text[1] 2000))}
        if($readers.text[1].Length){throw ('N7.CopyPython: unexpected stderr; '+(Limit-N5Message $readers.text[1] 2000))}
        $copyResult=ConvertFrom-N7PreparedCopyWire $readers.text[0]
        if($copyResult.GetType() -ne [System.Management.Automation.PSCustomObject]){throw 'N7.CopyResult: concrete result object required.'}
        Assert-N5Fields $copyResult @('protocol','scope','state','planSha256','transactionId','intentSha256','pendingSha256','journalSha256','retainedCandidate','originalFileCount','snapshotSha256','nativeAdmissionAllowed','nativeAuthorityResumed','installed','liveRecovered','pendingAndJournalRetainedAtCopiedPaths','result')
        foreach($key in @('planSha256','intentSha256','pendingSha256','journalSha256','retainedCandidate','snapshotSha256')){Assert-N5Hash $copyResult.$key}
        Assert-N5Integer $copyResult.originalFileCount 1 10000
        if($copyResult.protocol -cne 'PreparedAbortCopyReceipt/1' -or $copyResult.scope -cne 'FileCopyQualificationOnly' -or $copyResult.state -cne 'AbortedCopyFenced' -or $copyResult.result -cnotin @('AbortedCopyFenced','AlreadyAbortedCopyFenced') -or $copyResult.transactionId -isnot [string] -or $copyResult.transactionId -cnotmatch '^[0-9a-f]{32}$'){throw 'N7.CopyResult: closed copy-only receipt required.'}
        foreach($key in @('nativeAdmissionAllowed','nativeAuthorityResumed','installed','liveRecovered')){if($copyResult.$key -isnot [bool] -or $copyResult.$key){throw 'N7.CopyResult: native/installed authority refused.'}}
        if($copyResult.pendingAndJournalRetainedAtCopiedPaths -isnot [bool] -or -not $copyResult.pendingAndJournalRetainedAtCopiedPaths){throw 'N7.CopyResult: original copied fences must be retained.'}
    }catch{$primary=$_}
    finally{
        if($started -and -not $settled){
            try{if(-not $process.HasExited){$process.Kill()};if(-not $process.WaitForExit([Math]::Min(2000,[Math]::Max(0,(Get-N5Remaining)-2000)))){throw 'retained Python process exit not observed'}}catch{$cleanup.Add('PythonExit: '+$_.Exception.Message)}
            try{
                while((Get-N5Remaining) -gt 2000 -and @($readers.tasks|Where-Object {-not $_.IsCompleted}).Count){Update-N5Readers $readers -Discard;Start-Sleep -Milliseconds 10}
            }catch{$cleanup.Add('PythonDrain: '+$_.Exception.Message)}
        }
        foreach($stream in $readers.streams){try{$stream.Close()}catch{$cleanup.Add('PythonStreamClose: '+$_.Exception.Message)}}
        try{if($readers.tasks.Count -and -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$readers.tasks,[Math]::Min(1500,[Math]::Max(0,(Get-N5Remaining)-500)))){throw 'Python reader tasks incomplete'}}catch{$cleanup.Add('PythonReaderSettle: '+$_.Exception.Message)}
        try{if($null -ne $process){$process.Dispose()}}catch{$cleanup.Add('PythonDispose: '+$_.Exception.Message)}
    }
    if($null -ne $primary){$primary.Exception.Data['N7CopyPythonCleanup']=$cleanup.ToArray();throw $primary}
    if(-not $settled -or $cleanup.Count -or @($readers.tasks|Where-Object {-not $_.IsCompleted}).Count){throw ('N7.CopyPythonCleanup: incomplete settlement; '+($cleanup -join '; '))}
    [pscustomobject]@{protocol='NativePreparedAbortCopyResult/1';scope='FileCopyQualificationOnly';copyReceipt=$copyResult;python=[pscustomobject]@{started=$started;pid=$ownedPid;exited=$settled;exitCode=$code;stdoutState=$readers.states[0];stderrState=$readers.states[1];readersSettled=$true;cleanupState='Settled';cleanupErrors=$cleanup.ToArray();elapsedMilliseconds=$leafClock.ElapsedMilliseconds};nativeAuthorityResumed=$false;nativeAdmissionAllowed=$false}
}
function Invoke-N7PreparedCopyWorker($Request) {
    if($Request.GetType() -ne [System.Management.Automation.PSCustomObject]){throw 'N7.CopyRequest: concrete request required.'}
    Assert-N5Fields $Request @('schema','protocol','nonce','parentPid','assigned','parameters')
    Assert-N5Integer $Request.schema 7 7;Assert-N5Integer $Request.parentPid 1 ([int]::MaxValue)
    if($Request.protocol -cne 'NativePreparedAbortCopy/1' -or $Request.nonce -cne $env:N5_NONCE -or $Request.parentPid -ne [int]$env:N5_PARENT -or $Request.assigned -isnot [bool] -or -not $Request.assigned -or $Request.parameters.GetType() -ne [System.Management.Automation.PSCustomObject]){throw 'N7.CopyRequest: assigned-worker/source challenge required.'}
    Assert-N5Fields $Request.parameters @('Command','CopyCaseRoot','CopyWitnessSha256','StartTick','DeadlineMilliseconds','preparedAbortCopy')
    if($Request.parameters.preparedAbortCopy -isnot [bool] -or -not $Request.parameters.preparedAbortCopy){throw 'N7.CopyRequest: copy-only request marker required.'}
    $p=@{};foreach($property in $Request.parameters.PSObject.Properties){if($property.Name -cne 'preparedAbortCopy'){$p[$property.Name]=$property.Value}}
    $root=Assert-N7PreparedCopyArguments $p
    if($null -ne $script:N7LiveActive){throw 'N7.CopyScope: no native live session may be consumed.'}
    Invoke-N7PreparedCopyPython $root $p.CopyWitnessSha256
}

Export-ModuleMember -Function Invoke-NativeInstallCoordinator
