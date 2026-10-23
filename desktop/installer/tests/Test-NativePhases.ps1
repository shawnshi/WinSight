[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [string]$EntryPath,
    [string[]]$CaseNames
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# PS7/.NET test host; PS5 execution and cross-runtime state serialization are not claimed.
# All payloads are bytes, never executable fixtures. The caller must also bound this process to 150s.
$clock = [Diagnostics.Stopwatch]::StartNew()
$results = [Collections.Generic.List[object]]::new()
$facts = [Collections.Generic.List[string]]::new()
$plan = [ordered]@{}
$fixtureNumber = 0
$fixtures = [Collections.Generic.List[object]]::new()
$exitCode = 1
$failure = $null
$artifactCreated = $false
$moduleHash = $null
$entryHash = $null
$selected = @()
$unselected = @()
$fullCaseNames = @()
$selectionValidated = $false
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "ORACLE: $Message" }
    $script:facts.Add($Message)
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ObjectHash($Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 30)))).ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function Json([string]$Path) { [IO.File]::ReadAllText($Path) | ConvertFrom-Json }
function StateHash($F) { if (Test-Path -LiteralPath "$($F.install)/state.json") { Hash "$($F.install)/state.json" } else { $null } }
function Snapshot([string]$Root) {
    $rows = [Collections.Generic.List[string]]::new()
    if (-not (Test-Path -LiteralPath $Root)) { return '<absent>' }
    function Walk([string]$Path) {
        foreach ($item in @(Get-ChildItem -LiteralPath $Path -Force | Sort-Object Name)) {
            $rel = $item.FullName.Substring($Root.Length).Replace('\','/')
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $rows.Add("link:${rel}:$($item.LinkTarget)") }
            elseif ($item.PSIsContainer) { $rows.Add("dir:$rel"); Walk $item.FullName }
            else { $rows.Add("file:${rel}:$(Hash $item.FullName)") }
        }
    }
    Walk $Root
    $rows -join "`n"
}
function Negative([scriptblock]$Body, [string]$Reason, [string]$UnchangedRoot = '', [string]$RequiredMessage = '') {
    $before = if ($UnchangedRoot) { Snapshot $UnchangedRoot } else { $null }
    $caught = $null
    try { & $Body | Out-Null } catch { $caught = $_ }
    Assert ($null -ne $caught) "$Reason rejected"
    $script:facts.Add("native exception: $($caught.Exception.GetType().FullName): $($caught.Exception.Message)")
    if ($RequiredMessage) { Assert ($caught.Exception.Message.Contains($RequiredMessage)) "$Reason reached the intended fault, not an unrelated guard" }
    if ($UnchangedRoot) { Assert ((Snapshot $UnchangedRoot) -ceq $before) "$Reason made no filesystem mutation" }
}
function Hook([string]$Point) {
    { param($phaseName, $contextPath); if ($phaseName -ceq $Point) { throw "N4-EXPECTED-INTERRUPTION:$Point" } }.GetNewClosure()
}
function Stage([string]$Name, [byte]$Value) {
    $path = [IO.Path]::GetFullPath((Join-Path $ArtifactRoot $Name))
    [IO.Directory]::CreateDirectory("$path/app/assets") | Out-Null
    $bytes = [byte[]]::new(196608)
    [Array]::Fill[byte]($bytes, $Value)
    [IO.File]::WriteAllBytes("$path/app/WinSight.App.exe", $bytes)
    [IO.File]::WriteAllText("$path/app/assets/payload.txt", "inert-$Value")
    [IO.File]::WriteAllText("$path/app/version.txt", "version-$Value")
    $files = @('app/WinSight.App.exe','app/assets/payload.txt','app/version.txt') | ForEach-Object {
        [pscustomobject]@{ path = $_; bytes = (Get-Item -LiteralPath "$path/$_").Length; sha256 = Hash "$path/$_" }
    }
    [pscustomobject]@{ schema=1; runtime='win-x64'; selfContained=$true; releaseApproved=$false; appOnlyPreview=$true; files=@($files) } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$path/release-manifest.json" -Encoding utf8NoBOM
    [pscustomobject]@{ path=$path; hash=Hash "$path/release-manifest.json"; files=@($files) }
}
function Fixture {
    $script:fixtureNumber++
    $base = Join-Path $ArtifactRoot ('case-{0:d3}' -f $script:fixtureNumber)
    $data = [IO.Path]::GetFullPath("$base/data")
    [IO.Directory]::CreateDirectory($data) | Out-Null
    foreach ($name in @('models','settings','index','profile')) {
        [IO.Directory]::CreateDirectory("$data/$name") | Out-Null
        [IO.File]::WriteAllText("$data/$name/marker", "inert retained $name")
    }
    $f = [pscustomobject]@{ install=[IO.Path]::GetFullPath("$base/install"); data=$data; dataSnapshot=Snapshot $data }
    $script:fixtures.Add($f)
    $f
}
function Tx($F, [string]$Operation, [string]$Phase, $S = $null, [string]$Expected = '', [string]$Journal = '', [scriptblock]$Fault = $null) {
    $p = @{ Operation=$Operation; TransactionPhase=$Phase; InstallRoot=$F.install; DataRoot=$F.data; Mode='Verification' }
    if ($null -ne $S) { $p.StageDirectory=$S.path; $p.ManifestSha256=$S.hash }
    if ($Expected) { $p.ExpectedStateSha256=$Expected }
    if ($Journal) { $p.JournalSha256=$Journal }
    if ($null -ne $Fault) { $p.TestHook=$Fault }
    Invoke-NativeTransaction @p
}
function Payload($F, $S, [string]$Area = 'versions') {
    foreach ($file in $S.files) {
        $path = "$($F.install)/$Area/$($S.hash)/$($file.path)"
        Assert ((Test-Path -LiteralPath $path -PathType Leaf) -and (Hash $path) -ceq $file.sha256) "exact $Area payload $($S.hash)/$($file.path)"
    }
}
function PhaseResult($F, $R, [string]$Operation, [string]$Phase, $Previous, $Next, [string]$Journal) {
    Assert ($R.operation -ceq $Phase) "$Operation result is $Phase"
    Assert ($R.journalSha256 -ceq $Journal) 'returned hash binds immutable journal bytes'
    Assert ($R.previousStateSha256 -ceq $Previous) 'returned previous-state intent is exact'
    Assert ($R.nextStateSha256 -ceq $Next) 'returned next-state intent is exact'
    Assert ($R.stateSha256 -ceq (StateHash $F)) 'reported current-state hash is physical current state'
}
function Prepare($F, [string]$Operation, $S = $null, [string]$Expected = '') {
    $r = Tx $F $Operation Prepare $S $Expected
    $j = Json "$($F.install)/journal.json"
    $jh = Hash "$($F.install)/journal.json"
    Assert ($j.schema -eq 1 -and $j.operation -ceq $Operation) 'schema1 journal operation bound'
    Assert ($j.installRoot -ceq $F.install -and $j.dataRoot -ceq $F.data) 'journal roots bound'
    $previous = if ($null -ne $j.previous) { ObjectHash $j.previous } else { $null }
    $next = if ($null -ne $j.next) { ObjectHash $j.next } else { $null }
    Assert ($previous -ceq $(if ($Expected) { $Expected } else { $null })) 'journal previous content hashes to trusted input'
    Assert ((StateHash $F) -ceq $previous) 'Prepare retains prior physical state (absent on initial Apply)'
    if ($Operation -ceq 'Apply') {
        Assert ($j.next.current -ceq $S.hash) 'next state selects requested manifest'
        Payload $F $S
    } else {
        Assert ($null -eq $j.next) 'Remove journal has no next state'
        Assert (-not (Test-Path -LiteralPath "$($F.install)/removed")) 'Remove Prepare has not moved releases'
    }
    PhaseResult $F $r $Operation Prepared $previous $next $jh
    [pscustomobject]@{ hash=$jh; previous=$previous; next=$next; journal=$j }
}
function Activate($F, [string]$Operation, $P) {
    $r = Tx $F $Operation Activate $null $P.previous $P.hash
    PhaseResult $F $r $Operation Activated $P.previous $P.next $P.hash
    Assert ((Hash "$($F.install)/journal.json") -ceq $P.hash) 'Activate retains immutable journal'
    Assert ((StateHash $F) -ceq $P.next) 'Activate publishes exact journal next (Remove absent)'
}
function Finalize($F, [string]$Operation, $P) {
    $pin = if ($Operation -ceq 'Apply') { $P.next } else { $P.previous }
    $r = Tx $F $Operation Finalize $null $pin $P.hash
    PhaseResult $F $r $Operation Finalized $P.previous $P.next $P.hash
    Assert (-not (Test-Path -LiteralPath "$($F.install)/journal.json")) 'Finalize removes matching journal'
}
function Installed($F) {
    $p = Prepare $F Apply $script:a
    Activate $F Apply $p
    Finalize $F Apply $p
    $p.next
}
function Recover($F, $P, $OldStage = $null) {
    Invoke-NativeRecovery -InstallRoot $F.install -DataRoot $F.data -JournalSha256 $P.hash | Out-Null
    Assert ((StateHash $F) -ceq $P.previous) 'Recover restores exact previous state or first-install absence'
    if ($null -ne $OldStage) { Payload $F $OldStage }
}
function Case([string]$Name, [scriptblock]$Body) { $script:plan[$Name] = $Body }
function EntryJson($Result) {
    # The existing wrapper emits a RECOVERY information line before its success JSON.
    $start=[regex]::Match($Result.stdout, '(?m)^\{')
    Assert $start.Success 'wrapper emitted a JSON object'
    if ($start.Index) { $script:facts.Add('wrapper diagnostics: '+$Result.stdout.Substring(0,$start.Index).Trim()) }
    $Result.stdout.Substring($start.Index) | ConvertFrom-Json
}
function Entry($F, [string[]]$Arguments) {
    if ($clock.Elapsed.TotalSeconds -gt 105) { throw 'N4-BUDGET: insufficient time for bounded wrapper probe' }
    $psi = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $psi.UseShellExecute=$false; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
    foreach ($arg in (@('-NoLogo','-NoProfile','-NonInteractive','-File',$EntryPath,'-InstallRoot',$F.install,'-DataRoot',$F.data) + $Arguments)) { $psi.ArgumentList.Add($arg) }
    $proc = [Diagnostics.Process]::new(); $proc.StartInfo=$psi; $started=$false
    $entryClock=[Diagnostics.Stopwatch]::StartNew()
    $budgetMs=[Math]::Min(20000, 145000-$clock.ElapsedMilliseconds)
    $remaining={param([int]$Cap) [int][Math]::Max(0, [Math]::Min($Cap, $budgetMs-$entryClock.ElapsedMilliseconds))}
    try {
        $started=$proc.Start()
        if (-not $started) { throw 'N4-FIXTURE: wrapper process did not start' }
        $stdout=$proc.StandardOutput.ReadToEndAsync(); $stderr=$proc.StandardError.ReadToEndAsync()
        if (-not $proc.WaitForExit((& $remaining 15000))) {
            $proc.Kill($true)
            if (-not $proc.WaitForExit((& $remaining 3000))) { throw 'N4-FIXTURE: timed-out wrapper failed to settle within shared deadline' }
            throw 'N4-TIMEOUT: wrapper exceeded execution budget; killed and settled; no retry'
        }
        foreach ($task in @($stdout,$stderr)) {
            if (-not $task.Wait((& $remaining 2000))) { throw 'N4-TIMEOUT: wrapper output did not settle within shared execution/drain/cleanup deadline' }
        }
        # Result access is safe only after both bounded waits proved completion; no byte-bound claim.
        [pscustomobject]@{ code=$proc.ExitCode; stdout=$stdout.Result; stderr=$stderr.Result }
    } finally {
        try {
            if ($started -and -not $proc.HasExited) {
                $proc.Kill($true)
                if (-not $proc.WaitForExit((& $remaining 3000))) { throw 'N4-FIXTURE: wrapper failed final settlement within shared deadline' }
            }
        } finally {
            if ($started) { $proc.StandardOutput.Dispose(); $proc.StandardError.Dispose() }
            $proc.Dispose()
        }
    }
}
try {
    foreach ($path in @($ModulePath,$ArtifactRoot)) { if (-not [IO.Path]::IsPathFullyQualified($path)) { throw 'Absolute ModulePath and ArtifactRoot required' } }
    if (Test-Path -LiteralPath $ArtifactRoot) { throw 'ArtifactRoot must be fresh, not an existing directory' }
    $ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $ModulePath = (Resolve-Path -LiteralPath $ModulePath).Path
    $moduleHash = Hash $ModulePath
    if ($EntryPath) {
        if (-not [IO.Path]::IsPathFullyQualified($EntryPath)) { throw 'EntryPath must be absolute' }
        $EntryPath = (Resolve-Path -LiteralPath $EntryPath).Path
        Assert ((Hash (Join-Path (Split-Path $EntryPath) 'NativeInstaller.psm1')) -ceq $moduleHash) 'entry wrapper uses matching frozen module bytes'
        $entryHash = Hash $EntryPath
    }
    [IO.Directory]::CreateDirectory($ArtifactRoot) | Out-Null
    $artifactCreated = $true
    Import-Module $ModulePath -Force -WarningAction SilentlyContinue
    $script:a = Stage 'source-A' 31; $script:b = Stage 'source-B' 67
    $sourceA = Snapshot $a.path; $sourceB = Snapshot $b.path

    Case 'initial-upgrade-commit-and-replays' {
        $f = Fixture; $old = Installed $f; Payload $f $a
        $p = Prepare $f Apply $b $old
        Negative { Tx $f Apply Prepare $b $old } 'duplicate Prepare with unresolved journal' $f.install
        Negative { Tx $f Apply Finalize $null $p.next $p.hash } 'Finalize before activation' $f.install
        Activate $f Apply $p; Payload $f $a; Payload $f $b
        $before = Snapshot $f.install
        $replay=$null; $rejected=$null
        try { $replay=Tx $f Apply Activate $null $old $p.hash } catch { $rejected=$_ }
        if ($rejected) { $facts.Add("Expected safe replay rejection: $($rejected.Exception.Message)") }
        else { PhaseResult $f $replay Apply Activated $p.previous $p.next $p.hash }
        Assert ((Snapshot $f.install) -ceq $before) 'exact activation replay either proved success or rejected without mutation'
        Finalize $f Apply $p; Payload $f $a; Payload $f $b
        Negative { Tx $f Apply Finalize $null $p.next $p.hash } 'Finalize without journal cannot prove success' $f.install
    }
    foreach ($phase in @('Prepare','Activate')) {
        foreach ($initial in @($true,$false)) {
            $phaseCopy=$phase; $initialCopy=$initial
            Case "recover-Apply-$phase-initial-$initial" ({
                $f=Fixture; $old=if (-not $initialCopy) { Installed $f } else { '' }
                $s=if ($initialCopy) { $a } else { $b }
                $p=Prepare $f Apply $s $old
                if ($phaseCopy -eq 'Activate') { Activate $f Apply $p }
                if ($initialCopy) { Recover $f $p } else { Recover $f $p $a }
            }.GetNewClosure())
        }
        $phaseCopy=$phase
        Case "recover-Remove-$phase" ({
            $f=Fixture; $old=Installed $f; $p=Prepare $f Remove $null $old
            Payload $f $a
            if ($phaseCopy -eq 'Activate') { Activate $f Remove $p; Payload $f $a removed }
            Recover $f $p $a
        }.GetNewClosure())
    }
    Case 'Remove-commit-deletes-known-files' {
        $f=Fixture; $old=Installed $f
        $upgrade=Prepare $f Apply $b $old; Activate $f Apply $upgrade; Finalize $f Apply $upgrade
        $p=Prepare $f Remove $null $upgrade.next; Activate $f Remove $p
        Payload $f $a removed; Payload $f $b removed
        Finalize $f Remove $p
        foreach ($s in @($a,$b)) { foreach ($file in $s.files) {
            Assert (-not (Test-Path -LiteralPath "$($f.install)/removed/$($s.hash)/$($file.path)")) 'known removed payload actually deleted'
            Assert (-not (Test-Path -LiteralPath "$($f.install)/versions/$($s.hash)/$($file.path)")) 'no active known payload remains'
        } }
        Assert ($null -eq (StateHash $f)) 'Remove Finalize keeps state absent'
        Negative { Tx $f Remove Finalize $null $p.previous $p.hash } 'Remove finalized replay lacks proof' $f.install
    }
    foreach ($point in @('AfterPrepare','AfterActivate','DuringStateWrite','AfterStateTemporary','DuringCopy','DuringJournalWrite','AfterJournalTemporary')) {
        $pointCopy=$point
        Case "interruption-$point" ({
            $f=Fixture; $old=Installed $f
            if ($pointCopy -in @('AfterActivate','DuringStateWrite','AfterStateTemporary')) {
                $p=Prepare $f Apply $b $old
                Negative { Tx $f Apply Activate $null $old $p.hash (Hook $pointCopy) } $pointCopy '' "N4-EXPECTED-INTERRUPTION:$pointCopy"
            } else {
                Negative { Tx $f Apply Prepare $b $old '' (Hook $pointCopy) } $pointCopy '' "N4-EXPECTED-INTERRUPTION:$pointCopy"
                $jp="$($f.install)/journal.json"
                if (-not (Test-Path -LiteralPath $jp)) { $jp+='.new' }
                $p=[pscustomobject]@{hash=Hash $jp; previous=$old}
            }
            Assert ((Hash "$($f.install)/state.json") -ceq $(if ($pointCopy -eq 'AfterActivate') { $p.next } else { $old })) 'interrupted phase has specified physical selection'
            if ($pointCopy -in @('AfterPrepare','AfterActivate')) {
                Payload $f $b
                Assert ((Hash "$($f.install)/journal.json") -ceq $p.hash) 'new failure-only hook retains trusted durable journal'
            }
            if ($pointCopy -in @('DuringStateWrite','AfterStateTemporary')) {
                Assert (Test-Path -LiteralPath "$($f.install)/state.json.new") 'state temporary evidence retained'
                if ($pointCopy -eq 'DuringStateWrite') { Assert ((Get-Item -LiteralPath "$($f.install)/state.json.new").Length -eq 64) 'state interrupted before complete temporary write' }
                else { Assert ((Hash "$($f.install)/state.json.new") -ceq $p.next) 'state complete temporary equals exact intended next state' }
            }
            if ($pointCopy -eq 'DuringCopy') {
                $copy="$($f.install)/copying/$($p.hash)/app/WinSight.App.exe"
                Assert ((Get-Item -LiteralPath $copy).Length -eq 65536) 'real partial copy is one flushed chunk'
            }
            if ($pointCopy -eq 'DuringJournalWrite') {
                Assert ((Get-Item -LiteralPath "$($f.install)/journal.json.new").Length -eq 64) 'partial journal evidence is physical 64 bytes'
                Negative { Invoke-NativeRecovery -InstallRoot $f.install -DataRoot $f.data -JournalSha256 $p.hash } 'truncated journal cannot authorize recovery' $f.install
            } else { Recover $f $p $a }
        }.GetNewClosure())
    }
    foreach ($point in @('AfterPrepare','AfterActivate')) {
        $pointCopy=$point
        Case "Remove-interruption-$point" ({
            $f=Fixture; $old=Installed $f
            if ($pointCopy -eq 'AfterPrepare') {
                Negative { Tx $f Remove Prepare $null $old '' (Hook $pointCopy) } $pointCopy '' "N4-EXPECTED-INTERRUPTION:$pointCopy"
                $p=[pscustomobject]@{hash=Hash "$($f.install)/journal.json"; previous=$old; next=$null}
                Assert ((StateHash $f) -ceq $old) 'Remove AfterPrepare retains prior selection'
                Payload $f $a
            } else {
                $p=Prepare $f Remove $null $old
                Negative { Tx $f Remove Activate $null $old $p.hash (Hook $pointCopy) } $pointCopy '' "N4-EXPECTED-INTERRUPTION:$pointCopy"
                Assert ($null -eq (StateHash $f)) 'Remove AfterActivate has durably removed selection'
                Payload $f $a removed
            }
            Assert ((Hash "$($f.install)/journal.json") -ceq $p.hash) 'Remove hook retains immutable journal'
            Recover $f $p $a
        }.GetNewClosure())
    }
    Case 'partial-Remove-finalize-restart' {
        $f=Fixture; $old=Installed $f; $p=Prepare $f Remove $null $old; Activate $f Remove $p
        Negative { Tx $f Remove Finalize $null $old $p.hash (Hook DuringFinalizeRemove) } 'DuringFinalizeRemove' '' 'N4-EXPECTED-INTERRUPTION:DuringFinalizeRemove'
        $present=@($a.files | Where-Object { Test-Path -LiteralPath "$($f.install)/removed/$($a.hash)/$($_.path)" })
        Assert ($present.Count -eq ($a.files.Count-1)) 'hook fires after exactly one actual known-file deletion'
        Assert ((Hash "$($f.install)/journal.json") -ceq $p.hash) 'partial irreversible cleanup retains immutable journal'
        Negative { Invoke-NativeRecovery -InstallRoot $f.install -DataRoot $f.data -JournalSha256 $p.hash } 'rollback cannot claim usable previous release after deletion' $f.install
        Finalize $f Remove $p
        foreach ($file in $a.files) { Assert (-not (Test-Path -LiteralPath "$($f.install)/removed/$($a.hash)/$($file.path)")) 'resume deletes remaining known bytes' }
    }
    foreach ($phase in @('Activate','Finalize')) {
        foreach ($kind in @('missing-journal-hash','wrong-journal-hash','wrong-state-hash','wrong-data-root','wrong-operation','tampered-journal','unrelated-state')) {
            $phaseCopy=$phase; $kindCopy=$kind
            Case "guard-$phase-$kind" ({
                $f=Fixture; $old=Installed $f; $p=Prepare $f Apply $b $old
                if ($phaseCopy -eq 'Finalize') { Activate $f Apply $p }
                $j=$p.hash; $pin=if ($phaseCopy -eq 'Finalize') { $p.next } else { $old }; $op='Apply'; $target=$f
                switch ($kindCopy) {
                    'missing-journal-hash' { $j='' }
                    'wrong-journal-hash' { $j='0'*64 }
                    'wrong-state-hash' { $pin='0'*64 }
                    'wrong-data-root' { $target=[pscustomobject]@{install=$f.install;data="$($f.data)-wrong"} }
                    'wrong-operation' { $op='Remove' }
                    'tampered-journal' { [IO.File]::AppendAllText("$($f.install)/journal.json", ' ') }
                    'unrelated-state' { [IO.File]::AppendAllText("$($f.install)/state.json", ' ') }
                }
                Negative { Tx $target $op $phaseCopy $null $pin $j } "$phaseCopy $kindCopy" $f.install
            }.GetNewClosure())
        }
    }
    foreach ($operation in @('Apply','Remove')) {
        foreach ($kind in @('unknown','modified','reparse')) {
            $operationCopy=$operation; $kindCopy=$kind
            Case "payload-$operation-$kind" ({
                $f=Fixture; $old=Installed $f
                $s=if ($operationCopy -eq 'Apply') { $b } else { $null }
                $p=Prepare $f $operationCopy $s $old
                if ($operationCopy -eq 'Remove') { Activate $f Remove $p }
                $dir=if ($operationCopy -eq 'Apply') { "$($f.install)/versions/$($b.hash)/app" } else { "$($f.install)/removed/$($a.hash)/app" }
                switch ($kindCopy) {
                    'unknown' { [IO.File]::WriteAllText("$dir/foreign.bin", 'unknown must survive') }
                    'modified' { [IO.File]::AppendAllText("$dir/version.txt", 'modified must survive') }
                    'reparse' {
                        try { New-Item -ItemType Junction -Path "$dir/foreign-link" -Target $f.data -ErrorAction Stop | Out-Null }
                        catch { throw "N4-UNAVAILABLE: junction fixture failed: $($_.Exception.Message)" }
                        Assert ([bool]((Get-Item -LiteralPath "$dir/foreign-link" -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) 'fixture is a real reparse entry'
                    }
                }
                $phase=if ($operationCopy -eq 'Apply') { 'Activate' } else { 'Finalize' }
                Negative { Tx $f $operationCopy $phase $null $old $p.hash } "$operationCopy refuses $kindCopy evidence" $f.install
            }.GetNewClosure())
        }
    }
    Case 'recovery-refuses-untrusted-context-and-evidence' {
        foreach ($kind in @('journal-hash','data-root','unknown','modified','state')) {
            $f=Fixture; $old=Installed $f; $p=Prepare $f Apply $b $old; Activate $f Apply $p
            $data=$f.data; $journal=$p.hash
            switch ($kind) {
                'journal-hash' { $journal='0'*64 }
                'data-root' { $data+="-wrong" }
                'unknown' { [IO.File]::WriteAllText("$($f.install)/foreign.bin", 'foreign evidence') }
                'modified' { [IO.File]::AppendAllText("$($f.install)/versions/$($b.hash)/app/version.txt", 'modified evidence') }
                'state' { [IO.File]::AppendAllText("$($f.install)/state.json", ' ') }
            }
            if ($kind -eq 'state') {
                # NM-N4-ORACLE-01: legacy Recover binds parsed state to its trusted journal, not raw whitespace.
                Assert ((Hash "$($f.install)/state.json") -cne $p.next) 'whitespace fixture actually changes raw active-state hash'
                Assert ((ObjectHash (Json "$($f.install)/state.json")) -ceq $p.next) 'whitespace fixture retains exact journal-next JSON content'
                $priorPayload=Snapshot "$($f.install)/versions/$($a.hash)"
                Recover $f $p $a
                Assert ((Snapshot "$($f.install)/versions/$($a.hash)") -ceq $priorPayload) 'legacy whitespace recovery preserves all previous payload bytes/layout'
                Assert ((Snapshot $f.data) -ceq $f.dataSnapshot) 'legacy whitespace recovery preserves exact DataRoot bytes/layout'
                Assert (-not (Test-Path -LiteralPath "$($f.install)/versions/$($b.hash)") -and -not (Test-Path -LiteralPath "$($f.install)/journal.json")) 'legacy whitespace recovery completes trusted rollback of candidate/journal'
            } else {
                Negative { Invoke-NativeRecovery -InstallRoot $f.install -DataRoot $data -JournalSha256 $journal } "Recover refuses $kind" $f.install
            }
        }
    }
    Case 'Prepare-Remove-validates-complete-installed-release' {
        foreach ($kind in @('missing','modified','unknown')) {
            $f=Fixture; $old=Installed $f
            $path="$($f.install)/versions/$($a.hash)/app/version.txt"
            switch ($kind) {
                'missing' { [IO.File]::Delete($path) }
                'modified' { [IO.File]::AppendAllText($path, 'modified evidence') }
                'unknown' { [IO.File]::WriteAllText("$($f.install)/versions/$($a.hash)/app/unknown.bin", 'foreign evidence') }
            }
            Negative { Tx $f Remove Prepare $null $old } "Remove Prepare rejects $kind installed content" $f.install
            Assert (-not (Test-Path -LiteralPath "$($f.install)/journal.json")) 'invalid Remove Prepare produces no journal'
        }
    }
    Case 'Prepare-rejects-trusted-noncanonical-state' {
        foreach ($operation in @('Apply','Remove')) {
            $f=Fixture; $old=Installed $f
            $path="$($f.install)/state.json"
            $state=Json $path
            [IO.File]::WriteAllText($path, (' '+[IO.File]::ReadAllText($path)+"`n"), [Text.UTF8Encoding]::new($false))
            $trustedRaw=Hash $path
            Assert ($trustedRaw -cne $old) 'whitespace-varied valid JSON has a different TRUE trusted raw hash'
            Assert ((ObjectHash (Json $path)) -ceq (ObjectHash $state)) 'noncanonical state retains legitimate parsed content'
            $s=if ($operation -eq 'Apply') { $b } else { $null }
            Negative { Tx $f $operation Prepare $s $trustedRaw } "$operation Prepare refuses truly trusted noncanonical state" $f.install
            Assert ((Hash $path) -ceq $trustedRaw) 'trusted noncanonical bytes not normalized or rewritten'
            Assert (-not (Test-Path -LiteralPath "$($f.install)/journal.json") -and -not (Test-Path -LiteralPath "$($f.install)/journal.json.new") -and -not (Test-Path -LiteralPath "$($f.install)/copying")) 'noncanonical Prepare creates no journal or copy evidence'
            Assert ((Snapshot $f.data) -ceq $f.dataSnapshot) 'noncanonical rejection preserves exact DataRoot hashes and layout'
        }
    }
    Case 'Apply-Finalize-validates-all-releases' {
        foreach ($candidate in @($a,$b)) {
            $f=Fixture; $old=Installed $f; $p=Prepare $f Apply $b $old; Activate $f Apply $p
            [IO.File]::AppendAllText("$($f.install)/versions/$($candidate.hash)/app/version.txt", 'tampered')
            Negative { Tx $f Apply Finalize $null $p.next $p.hash } 'Finalize refuses modified prior or next release' $f.install
        }
    }
    Case 'Prepare-bad-source-and-missing-pin' {
        $f=Fixture; $old=Installed $f
        Negative { Tx $f Apply Prepare $b } 'upgrade Prepare requires previous hash' $f.install
        $bad=[pscustomobject]@{path=$b.path;hash=('0'*64)}
        Negative { Tx $f Apply Prepare $bad $old } 'Prepare rejects untrusted source manifest' $f.install
        Negative { Tx $f Remove Prepare } 'Remove Prepare requires previous hash' $f.install
    }
    if ($EntryPath) {
        Case 'entry-wrapper-phase-roundtrip' {
            $f=Fixture
            $r=Entry $f @('-Operation','Apply','-TransactionPhase','Prepare','-StageDirectory',$a.path,'-ManifestSha256',$a.hash)
            Assert ($r.code -eq 0) "entry Prepare exit=$($r.code); stderr=$($r.stderr)"
            $o=EntryJson $r; $j=Hash "$($f.install)/journal.json"
            Assert ($o.operation -ceq 'Prepared' -and $o.journalSha256 -ceq $j -and $null -eq (StateHash $f)) 'entry Prepare physical/result binding'
            $r=Entry $f @('-Operation','Apply','-TransactionPhase','Activate','-JournalSha256',$j)
            Assert ($r.code -eq 0) "entry Activate exit=$($r.code); stderr=$($r.stderr)"
            $o=EntryJson $r; $state=StateHash $f
            Assert ($o.operation -ceq 'Activated' -and $o.stateSha256 -ceq $state) 'entry Activate selected state binding'
            $r=Entry $f @('-Operation','Apply','-TransactionPhase','Finalize','-JournalSha256',$j,'-ExpectedStateSha256',$state)
            Assert ($r.code -eq 0) "entry Finalize exit=$($r.code); stderr=$($r.stderr)"
            Assert ((EntryJson $r).operation -ceq 'Finalized' -and -not (Test-Path -LiteralPath "$($f.install)/journal.json")) 'entry Finalize commits journal deletion'
            Payload $f $a
        }
        Case 'entry-Recover-phase-rejection' {
            $f=Fixture; $p=Prepare $f Apply $a; $before=Snapshot $f.install
            $r=Entry $f @('-Operation','Recover','-TransactionPhase','Activate','-JournalSha256',$p.hash)
            Assert ($r.code -ne 0 -and [bool]$r.stderr) "Recover rejects non-Standalone phase: $($r.stderr)"
            Assert ((Snapshot $f.install) -ceq $before) 'entry Recover invalid phase does not mutate'
        }
        Case 'real-named-mutex-blocks-all-phases' {
            $f=Fixture; $old=Installed $f; $p=Prepare $f Apply $b $old
            $sha=[Security.Cryptography.SHA256]::Create()
            try { $key=[Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($f.install.ToUpperInvariant()))) } finally { $sha.Dispose() }
            $mutex=[Threading.Mutex]::new($false, "Local\WinSight.NativeInstaller.$key")
            $held=$false
            try {
                $held=$mutex.WaitOne(0); Assert $held 'fixture owns real per-install named mutex'
                foreach ($phase in @('Prepare','Activate','Finalize')) {
                    $before=Snapshot $f.install
                    $entryArguments=@('-Operation','Apply','-TransactionPhase',$phase,'-ExpectedStateSha256',$old)
                    if ($phase -eq 'Prepare') { $entryArguments+=@('-StageDirectory',$b.path,'-ManifestSha256',$b.hash) } else { $entryArguments+=@('-JournalSha256',$p.hash) }
                    $r=Entry $f $entryArguments
                    Assert ($r.code -ne 0 -and $r.stderr.Contains('Concurrent installer operation rejected')) "$phase is rejected by real named mutex, not a different guard: $($r.stderr)"
                    Assert ((Snapshot $f.install) -ceq $before) "$phase mutex rejection preserves physical evidence"
                }
            } finally { if ($held) { $mutex.ReleaseMutex() }; $mutex.Dispose() }
        }
    }
    $fullCaseNames=@($plan.Keys)
    if (-not $EntryPath) { $fullCaseNames+=@('entry-wrapper-phase-roundtrip','entry-Recover-phase-rejection','real-named-mutex-blocks-all-phases') }
    if ($PSBoundParameters.ContainsKey('CaseNames')) {
        if (-not $CaseNames -or $CaseNames.Count -eq 0) { throw 'CaseNames must contain at least one exact known case name' }
        $unique=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($requested in $CaseNames) {
            if ([string]::IsNullOrWhiteSpace($requested) -or $fullCaseNames -cnotcontains $requested) { throw "Unknown exact CaseNames value: $requested" }
            if (-not $unique.Add($requested)) { throw "Duplicate CaseNames value: $requested" }
            if (@($plan.Keys) -cnotcontains $requested) { throw "Selected case requires EntryPath: $requested" }
        }
        $selected=@($plan.Keys | Where-Object { $unique.Contains($_) })
    } else { $selected=@($plan.Keys) }
    $unselected=@($fullCaseNames | Where-Object { $selected -cnotcontains $_ })
    $selectionValidated=$true
    foreach ($name in $selected) {
        $facts.Clear()
        if ($clock.Elapsed.TotalSeconds -gt 130) { $failure='N4-BUDGET: stopped before starting another case'; break }
        $caseClock=[Diagnostics.Stopwatch]::StartNew()
        $caseError=$null
        try { & $plan[$name] | Out-Null } catch { $caseError=$_ }
        try {
            Assert ((Snapshot $a.path) -ceq $sourceA -and (Snapshot $b.path) -ceq $sourceB) 'synthetic source bytes unchanged'
            foreach ($fixture in $fixtures) {
                Assert ((Snapshot $fixture.data) -ceq $fixture.dataSnapshot) "DataRoot hashes/layout unchanged: $($fixture.data)"
            }
        } catch { if ($null -eq $caseError) { $caseError=$_ } else { $facts.Add("ADDITIONAL ORACLE FAILURE: $($_.Exception.Message)") } }
        $status=if ($null -eq $caseError) { 'passed' } elseif ($caseError.Exception.Message.StartsWith('N4-UNAVAILABLE:')) { 'unavailable' } else { 'failed' }
        $category=if (-not $caseError) { $null } elseif ($status -eq 'unavailable') { 'fixture-capability-unavailable' } elseif ($caseError.Exception.Message.StartsWith('ORACLE:')) { 'oracle-assertion' } else { 'runtime-error-unattributed; inspect retained exception before blaming product' }
        $results.Add([pscustomobject]@{name=$name;status=$status;failureCategory=$category;seconds=$caseClock.Elapsed.TotalSeconds;facts=@($facts);error=$(if ($caseError) { $caseError.ToString() } else { $null });stack=$(if ($caseError) { $caseError.ScriptStackTrace } else { $null })})
        if ($caseError) { $failure="N4-FIRST-BLOCKER: ${name}: $($caseError.Exception.Message)"; break }
    }
    if (-not $failure) { $exitCode=0 }
} catch { $failure=$_.ToString() }
finally {
    $done=@($results | ForEach-Object name)
    $untested=@($fullCaseNames | Where-Object { $done -cnotcontains $_ })
    $summary=[pscustomobject]@{
        schema=1; status=$(if ($exitCode -eq 0 -and $PSBoundParameters.ContainsKey('CaseNames')) { 'passed-selected' } elseif ($exitCode -eq 0) { 'passed' } else { 'incomplete-or-failed' }); exitCode=$exitCode
        selectionValidated=$selectionValidated; fullCaseCount=$fullCaseNames.Count; selectedCaseCount=$selected.Count
        selectedCaseNames=@($selected); unselectedCaseNames=@($unselected); executedCaseCount=$results.Count
        selectedNotExecuted=@($selected | Where-Object { $done -cnotcontains $_ })
        modulePath=$ModulePath; moduleSha256=$moduleHash; entryPath=$EntryPath; entrySha256=$entryHash
        seconds=$clock.Elapsed.TotalSeconds; firstBlocker=$failure; cases=@($results); untested=@($untested)
        scope='Synthetic public module phases only; optional matching entry wrapper/mutex. No app/setup/registry/menu/network operations.'
        limitations=@('Caller must impose 150s process timeout; synchronous public module calls cannot be preempted in-process.', 'No complete P0-P6/standard installer acceptance; no production-mode source, external services, initialization recovery, or exhaustive malformed manifests.', 'No Remove interrupted directory-move hook case; state/journal interruptions target Apply upgrade; no source reparse case.', 'PS7 versus Windows PowerShell 5 serialization/runtime compatibility is unverified unless separately physically tested.')
    }
    $out=$summary | ConvertTo-Json -Depth 12
    if ($artifactCreated) { [IO.File]::WriteAllText((Join-Path $ArtifactRoot 'phase-results.json'), $out, [Text.UTF8Encoding]::new($false)) }
    [Console]::Out.WriteLine($out)
}
exit $exitCode
