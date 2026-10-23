Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NativeHash([string]$Path) {
    # Do not depend on PowerShell module autoload across Windows PowerShell/Core.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Resolve-NativeRoot([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path.StartsWith('\\')) { throw 'An absolute local task root is required.' }
    foreach ($segment in $Path.Substring(3).Replace('/', '\').Split('\')) {
        if ($segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '[<>:"|?*~]' -or $segment -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw 'Ambiguous Windows task-root segment.' }
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\') -or $full -match '["'';{}\r\n]' ) { throw 'Unsafe task root.' }
    for ($p = $full; $p; $p = Split-Path $p -Parent) {
        if ((Test-Path -LiteralPath $p) -and ((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Reparse path: $p" }
    }
    $full
}

function Assert-NativeSeparate([string]$Left, [string]$Right) {
    if ($Left -eq $Right -or $Left.StartsWith($Right + '\', [StringComparison]::OrdinalIgnoreCase) -or $Right.StartsWith($Left + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Roots must be disjoint.' }
}

function Get-NativeTree([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse entry: $($item.FullName)" }
        $item
        if ($item.PSIsContainer) { Get-NativeTree $item.FullName }
    }
}

function Assert-NativeRelative([string]$Path) {
    if ($Path -notmatch '^app/[A-Za-z0-9_. /-]+$' -or $Path.Contains('//')) { throw "Unsafe payload path: $Path" }
    foreach ($part in $Path.Split('/')) {
        if ($part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part -match '^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\.|$)') { throw "Unsafe payload segment: $Path" }
    }
}

function Assert-NativeFiles($Files) {
    if (@($Files).Count -eq 0) { throw 'Empty payload.' }
    $seen = @{}
    foreach ($file in $Files) {
        Assert-NativeRelative $file.path
        if ($seen.ContainsKey($file.path) -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $file.bytes -isnot [ValueType] -or $file.bytes -lt 0) { throw 'Invalid file record or duplicate path.' }
        $seen[$file.path] = $true
    }
}

function Assert-NativePayload([string]$Root, $Files, [switch]$Partial) {
    Assert-NativeFiles $Files
    $allowed = @{}
    foreach ($file in $Files) { $allowed[$file.path] = $file }
    foreach ($item in @(Get-NativeTree $Root)) {
        $relative = $item.FullName.Substring($Root.Length + 1).Replace('\', '/')
        if ($item.PSIsContainer) {
            if (-not @($Files | Where-Object { $_.path.StartsWith($relative + '/', [StringComparison]::OrdinalIgnoreCase) }).Count) { throw "Unknown payload directory: $relative" }
        } else {
            if (-not $allowed.ContainsKey($relative)) { throw "Unknown payload file: $relative" }
            $file = $allowed[$relative]
            if ($item.Length -ne $file.bytes -or (Get-NativeHash $item.FullName) -cne $file.sha256) { throw "Payload mismatch: $relative" }
        }
    }
    if (-not $Partial) {
        foreach ($file in $Files) {
            if (-not (Test-Path -LiteralPath (Join-Path $Root $file.path) -PathType Leaf)) { throw "Missing payload: $($file.path)" }
        }
    }
}

function Read-NativeStage([string]$StageDirectory, [string]$ManifestSha256, [ValidateSet('Verification','Production')][string]$Mode) {
    $stage = Resolve-NativeRoot $StageDirectory
    $path = Join-Path $stage 'release-manifest.json'
    if ($ManifestSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-NativeHash $path) -cne $ManifestSha256) { throw 'Untrusted release manifest hash.' }
    $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.runtime -cne 'win-x64' -or $manifest.selfContained -ne $true -or $manifest.releaseApproved -ne $false -or $manifest.appOnlyPreview -isnot [bool]) { throw 'Unsupported release manifest.' }
    if ($Mode -eq 'Production' -and $manifest.appOnlyPreview) { throw 'Production candidate requires the full payload, not AppOnlyPreview.' }
    Assert-NativeFiles $manifest.files
    # Helper logs are diagnostic-only and never enter the installer payload.
    foreach ($item in @(Get-NativeTree $stage)) {
        $rel = $item.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if ($rel -eq 'release-manifest.json' -or $rel -eq 'logs' -or $rel -match '^logs/[^/]+\.log$' -or $rel -eq 'app' -or $rel.StartsWith('app/')) { continue }
        throw "Unknown release-stage entry: $rel"
    }
    $app = Join-Path $stage 'app'
    if (-not (Test-Path -LiteralPath $app -PathType Container)) { throw 'Missing app directory.' }
    # The same validator sees a payload root containing only app/, without helper logs.
    $actual = @(Get-NativeTree $app | Where-Object { -not $_.PSIsContainer })
    if ($actual.Count -ne @($manifest.files).Count) { throw 'Unknown or missing app files.' }
    foreach ($file in $manifest.files) {
        $source = Join-Path $stage $file.path
        $item = Get-Item -LiteralPath $source -Force
        if ($item.PSIsContainer -or $item.Length -ne $file.bytes -or (Get-NativeHash $source) -cne $file.sha256) { throw "Payload mismatch: $($file.path)" }
    }
    foreach ($item in @(Get-NativeTree $app | Where-Object PSIsContainer)) {
        $rel = $item.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if (-not @($manifest.files | Where-Object { $_.path.StartsWith($rel + '/', [StringComparison]::OrdinalIgnoreCase) }).Count) { throw "Unknown app directory: $rel" }
    }
    $manifest
}

function Get-NativeJsonHash($Value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 30)))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Write-NativeJson([string]$Path, $Value, [scriptblock]$TestHook, [string]$EventPrefix) {
    $temporary = $Path + '.new'
    if (Test-Path -LiteralPath $temporary) { throw "Unresolved temporary state: $temporary" }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 30))
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $first = [Math]::Min(64, $bytes.Length)
        $stream.Write($bytes, 0, $first)
        $stream.Flush()
        if ($TestHook) { & $TestHook ('During' + $EventPrefix + 'Write') }
        $stream.Write($bytes, $first, $bytes.Length - $first)
        $stream.Flush()
    } finally { $stream.Dispose() }
    if ($TestHook) { & $TestHook ('After' + $EventPrefix + 'Temporary') }
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temporary, $Path) }
}

function Assert-NativeState($State, [string]$InstallRoot, [string]$DataRoot) {
    if ($State.schema -ne 1 -or $State.installRoot -cne $InstallRoot -or $State.dataRoot -cne $DataRoot) { throw 'State target mismatch.' }
    $ids = @{}
    foreach ($release in @($State.releases)) {
        if ($release.id -cnotmatch '^[0-9a-f]{64}$' -or $ids.ContainsKey($release.id)) { throw 'Invalid release identity.' }
        Assert-NativeFiles $release.files
        $ids[$release.id] = $true
    }
    if (-not $ids.ContainsKey($State.current)) { throw 'Invalid current release.' }
}

function Read-NativeState([string]$Root, [string]$DataRoot, [string]$ExpectedStateSha256) {
    $path = Join-Path $Root 'state.json'
    if ($ExpectedStateSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-NativeHash $path) -cne $ExpectedStateSha256) { throw 'Trusted state hash required; state changed or tampered.' }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    Assert-NativeState $state $Root $DataRoot
    $state
}

function Assert-NativeLayout([string]$Root, $Releases, [string]$CandidateId = '', [switch]$Recovery, [string]$TransactionHash = '', [switch]$UninstallRecovery) {
    $ids = @{}
    foreach ($release in @($Releases)) { $ids[$release.id] = $true }
    if ($CandidateId) { $ids[$CandidateId] = $true }
    foreach ($item in @(Get-NativeTree $Root)) {
        $rel = $item.FullName.Substring($Root.Length + 1).Replace('\', '/')
        if (-not $item.PSIsContainer -and $rel -in @('state.json','journal.json')) { continue }
        if ($Recovery -and $TransactionHash -cmatch '^[0-9a-f]{64}$') {
            if (-not $item.PSIsContainer -and $rel -in @('state.json.new','journal.json.new')) { continue }
            if ($item.PSIsContainer -and $rel -in @('copying','quarantine')) { continue }
            if ($rel -eq ('copying/' + $TransactionHash) -or $rel.StartsWith('copying/' + $TransactionHash + '/')) { continue }
            if ($rel -eq ('quarantine/' + $TransactionHash) -or $rel.StartsWith('quarantine/' + $TransactionHash + '/')) { continue }
        }
        if ($item.PSIsContainer -and ($rel -eq 'versions' -or ($UninstallRecovery -and $rel -eq 'removed'))) { continue }
        if ($rel -match '^(versions|removed)/([0-9a-f]{64})(/|$)' -and ($Matches[1] -eq 'versions' -or $UninstallRecovery) -and $ids.ContainsKey($Matches[2])) { continue }
        throw "Unknown installation entry: $rel"
    }
}

function Assert-NativeInstalled([string]$Root, $State) {
    foreach ($release in @($State.releases)) { Assert-NativePayload (Join-Path $Root ('versions/' + $release.id)) $release.files }
}

function Remove-NativeCandidate([string]$Path, $Files) {
    Assert-NativePayload $Path $Files -Partial
    foreach ($file in $Files) {
        $target = Join-Path $Path $file.path
        if (Test-Path -LiteralPath $target) { [IO.File]::Delete($target) }
    }
    foreach ($dir in @(Get-NativeTree $Path | Where-Object PSIsContainer | Sort-Object { $_.FullName.Length } -Descending)) { [IO.Directory]::Delete($dir.FullName) }
    if (Test-Path -LiteralPath $Path) { [IO.Directory]::Delete($Path) }
}

function Assert-NativeUncertainPayload([string]$Root, $Files) {
    Assert-NativeFiles $Files
    foreach ($item in @(Get-NativeTree $Root)) {
        $relative = $item.FullName.Substring($Root.Length + 1).Replace('\', '/')
        $matches = @($Files | Where-Object { $_.path -eq $relative -or ($item.PSIsContainer -and $_.path.StartsWith($relative + '/', [StringComparison]::OrdinalIgnoreCase)) })
        if (-not $matches.Count -or (-not $item.PSIsContainer -and $item.Length -gt $matches[0].bytes)) { throw "Unknown or oversized incomplete entry: $relative" }
    }
}

function Restore-NativeInitialization {
    param([string]$Root, [string]$Data, [string]$JournalSha256,
        [string]$StageDirectory, [string]$ManifestSha256,
        [ValidateSet('Verification','Production')][string]$Mode,
        [string]$ExpectedStateSha256, [string]$InitializationEvidenceRoot,
        [ValidateSet('Apply','Remove')][string]$InitializationOperation = 'Apply')
    if (-not $InitializationEvidenceRoot) { throw 'Truncated initialization requires an explicit fresh evidence root and trusted inputs.' }
    $evidence = Resolve-NativeRoot $InitializationEvidenceRoot
    Assert-NativeSeparate $evidence $Root
    Assert-NativeSeparate $evidence $Data
    if (Test-Path -LiteralPath $evidence) { throw 'Initialization evidence root must be fresh; preserve existing evidence.' }
    $previous = $null
    if (Test-Path -LiteralPath (Join-Path $Root 'state.json')) {
        $previous = Read-NativeState $Root $Data $ExpectedStateSha256
        Assert-NativeInstalled $Root $previous
    } elseif ($ExpectedStateSha256) { throw 'Expected previous state is missing.' }
    $old = @(); if ($null -ne $previous) { $old = @($previous.releases) }
    Assert-NativeLayout $Root $old -Recovery -TransactionHash $JournalSha256
    foreach ($entry in @(Get-ChildItem -LiteralPath $Root -Force)) {
        if (($entry.Name -eq 'versions' -and $entry.PSIsContainer) -or
            ($entry.Name -eq 'state.json' -and -not $entry.PSIsContainer -and $null -ne $previous) -or
            ($entry.Name -eq 'journal.json.new' -and -not $entry.PSIsContainer)) { continue }
        throw 'Initialization recovery refuses non-initial transaction entries.'
    }
    if ($null -eq $previous -and (Test-Path -LiteralPath (Join-Path $Root 'versions')) -and
        @(Get-ChildItem -LiteralPath (Join-Path $Root 'versions') -Force).Count) { throw 'Unknown initial version bytes.' }
    $next = $null
    if ($InitializationOperation -eq 'Apply') {
        $stage = Resolve-NativeRoot $StageDirectory
        Assert-NativeSeparate $stage $Root; Assert-NativeSeparate $stage $Data; Assert-NativeSeparate $stage $evidence
        $manifest = Read-NativeStage $stage $ManifestSha256 $Mode
        if (@($old | Where-Object { $_.id -eq $ManifestSha256 }).Count) { throw 'Candidate already installed.' }
        $candidate = [pscustomobject]@{ id = $ManifestSha256; files = @($manifest.files) }
        $next = [pscustomobject]@{ schema = 1; installRoot = $Root; dataRoot = $Data; current = $ManifestSha256; releases = @($old) + @($candidate) }
    } elseif ($null -eq $previous) { throw 'Initialization removal requires a trusted previous state.' }
    $intent = [pscustomobject]@{ schema = 1; operation = $InitializationOperation; installRoot = $Root; dataRoot = $Data; previous = $previous; next = $next; copyProtocol = 'staged-v1' }
    if ((Get-NativeJsonHash $intent) -cne $JournalSha256) { throw 'Reconstructed initialization intent does not match trusted journal hash.' }
    $expected = [Text.Encoding]::UTF8.GetBytes(($intent | ConvertTo-Json -Depth 30))
    $temporary = Join-Path $Root 'journal.json.new'
    $null = Resolve-NativeRoot $temporary
    $item = Get-Item -LiteralPath $temporary -Force
    if ($item.PSIsContainer -or $item.Length -ge $expected.Length) { throw 'Not a truncated initial journal.' }
    $partial = [IO.File]::ReadAllBytes($temporary)
    for ($i = 0; $i -lt $partial.Length; $i++) { if ($partial[$i] -ne $expected[$i]) { throw 'Initial journal prefix does not match the trusted intent.' } }
    $partialHash = Get-NativeHash $temporary
    [IO.Directory]::CreateDirectory($evidence) | Out-Null
    Write-NativeJson (Join-Path $evidence 'initialization-recovery.json') ([pscustomobject]@{
        schema = 1; operation = $InitializationOperation; installRoot = $Root; dataRoot = $Data;
        journalSha256 = $JournalSha256; partialSha256 = $partialHash;
        previousStateSha256 = $ExpectedStateSha256; manifestSha256 = $ManifestSha256;
        action = 'preserve opaque initial journal; abort before any payload mutation'
    })
    $saved = Join-Path $evidence ('journal-' + $partialHash + '.new')
    [IO.File]::Move($temporary, $saved)
    if ((Get-NativeHash $saved) -cne $partialHash) { throw 'Initialization evidence changed during quarantine; do not continue automatically.' }
    if ($null -ne $previous) {
        $null = Read-NativeState $Root $Data $ExpectedStateSha256
        Assert-NativeLayout $Root $old
        Assert-NativeInstalled $Root $previous
    } else {
        if (@(Get-NativeTree $Root | Where-Object { -not $_.PSIsContainer -or $_.FullName -cne (Join-Path $Root 'versions') }).Count) { throw 'Initial root changed during recovery.' }
        $emptyVersions = Join-Path $Root 'versions'
        if (Test-Path -LiteralPath $emptyVersions) { [IO.Directory]::Delete($emptyVersions) }
    }
    [pscustomobject]@{ operation = 'RecoveredInitialization'; quarantine = $evidence; journalRetained = $false;
        stateSha256 = $(if ($null -ne $previous) { $ExpectedStateSha256 } else { $null }); journalSha256 = $JournalSha256 }
}

function Invoke-NativeRecoveryCore([string]$InstallRoot, [string]$DataRoot, [string]$JournalSha256,
    [string]$StageDirectory, [string]$ManifestSha256,
    [ValidateSet('Verification','Production')][string]$Mode = 'Verification',
    [string]$ExpectedStateSha256, [string]$InitializationEvidenceRoot,
    [ValidateSet('Apply','Remove')][string]$InitializationOperation = 'Apply') {
    $root = Resolve-NativeRoot $InstallRoot
    $data = Resolve-NativeRoot $DataRoot
    Assert-NativeSeparate $root $data
    $path = Join-Path $root 'journal.json'
    if ($JournalSha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Trusted journal hash required.' }
    if (Test-Path -LiteralPath ($path + '.new')) { $null = Resolve-NativeRoot ($path + '.new') }
    if (-not (Test-Path -LiteralPath $path) -and (Test-Path -LiteralPath ($path + '.new')) -and
        (Get-NativeHash ($path + '.new')) -cne $JournalSha256) {
        return Restore-NativeInitialization $root $data $JournalSha256 $StageDirectory $ManifestSha256 $Mode $ExpectedStateSha256 $InitializationEvidenceRoot $InitializationOperation
    }
    $source = $path
    if (-not (Test-Path -LiteralPath $path)) { $source = $path + '.new' }
    elseif (Test-Path -LiteralPath ($path + '.new')) { throw 'Unexpected duplicate journal temporary file.' }
    $null = Resolve-NativeRoot $source
    if ($JournalSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-NativeHash $source) -cne $JournalSha256) { throw 'Trusted journal hash required; journal changed or tampered.' }
    $journal = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
    if ($journal.schema -ne 1 -or $journal.installRoot -cne $root -or $journal.dataRoot -cne $data -or $journal.operation -notin @('Apply','Remove')) { throw 'Journal target or operation mismatch.' }
    if ($null -ne $journal.previous) { Assert-NativeState $journal.previous $root $data }
    $statePath = Join-Path $root 'state.json'
    $stateTemporary = $statePath + '.new'
    if (Test-Path -LiteralPath $statePath) {
        $null = Resolve-NativeRoot $statePath
        $current = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $serialized = $current | ConvertTo-Json -Depth 30 -Compress
        if ($serialized -cne ($journal.previous | ConvertTo-Json -Depth 30 -Compress) -and $serialized -cne ($journal.next | ConvertTo-Json -Depth 30 -Compress)) { throw 'Recovery state differs from journal.' }
    }
    $quarantine = Join-Path $root ('quarantine/' + $JournalSha256)
    $pending = Join-Path $root ('copying/' + $JournalSha256)
    $retained = Test-Path -LiteralPath $quarantine
    if ($journal.operation -eq 'Apply') {
        Assert-NativeState $journal.next $root $data
        $candidate = @($journal.next.releases | Where-Object { $_.id -eq $journal.next.current })[0]
        $old = @(); if ($null -ne $journal.previous) { $old = @($journal.previous.releases) }
        if (@($old | Where-Object { $_.id -eq $candidate.id }).Count) { throw 'Candidate already belongs to previous state.' }
        Assert-NativeLayout $root $old $candidate.id -Recovery -TransactionHash $JournalSha256
        if ($null -ne $journal.previous) { Assert-NativeInstalled $root $journal.previous }
        $candidatePath = Join-Path $root ('versions/' + $candidate.id)
        # Only an unpublished, journal-bound staging tree may contain uncertain bytes.
        Assert-NativePayload $candidatePath $candidate.files -Partial
        Assert-NativeUncertainPayload $pending $candidate.files
        if ($retained) {
            foreach ($entry in @(Get-ChildItem -LiteralPath $quarantine -Force)) {
                if ($entry.Name -eq 'copying' -and $entry.PSIsContainer) { Assert-NativeUncertainPayload $entry.FullName $candidate.files }
                elseif ($entry.Name -ne 'state.json.new' -or $entry.PSIsContainer) { throw 'Unknown quarantine entry.' }
            }
        }
    } else {
        if ($null -eq $journal.previous -or $null -ne $journal.next) { throw 'Invalid uninstall journal.' }
        Assert-NativeLayout $root $journal.previous.releases -Recovery -TransactionHash $JournalSha256 -UninstallRecovery
        if (Test-Path -LiteralPath $pending) { throw 'Unexpected uninstall copy staging.' }
        if ($retained -and @(Get-ChildItem -LiteralPath $quarantine -Force | Where-Object { $_.Name -ne 'state.json.new' -or $_.PSIsContainer }).Count) { throw 'Unknown uninstall quarantine entry.' }
        foreach ($release in $journal.previous.releases) {
            $live = Join-Path $root ('versions/' + $release.id)
            $removed = Join-Path $root ('removed/' + $release.id)
            if ((Test-Path -LiteralPath $live) -eq (Test-Path -LiteralPath $removed)) { throw 'Ambiguous uninstall recovery source.' }
            if (Test-Path -LiteralPath $live) { Assert-NativePayload $live $release.files }
            else { Assert-NativePayload $removed $release.files }
        }
    }
    # All roots, active version bytes and entry ownership are checked before moving evidence.
    if ($source -ne $path) { [IO.File]::Move($source, $path) }
    if ((Test-Path -LiteralPath $pending) -or (Test-Path -LiteralPath $stateTemporary)) {
        [IO.Directory]::CreateDirectory($quarantine) | Out-Null
        if (Test-Path -LiteralPath $pending) { [IO.Directory]::Move($pending, (Join-Path $quarantine 'copying')) }
        if (Test-Path -LiteralPath $stateTemporary) { [IO.File]::Move($stateTemporary, (Join-Path $quarantine 'state.json.new')) }
        $retained = $true
    }
    if ($journal.operation -eq 'Apply') {
        if ($null -ne $journal.previous) { Write-NativeJson $statePath $journal.previous }
        elseif (Test-Path -LiteralPath $statePath) { [IO.File]::Delete($statePath) }
        Remove-NativeCandidate $candidatePath $candidate.files
        if ((Test-Path -LiteralPath (Join-Path $root 'copying')) -and -not @(Get-ChildItem -LiteralPath (Join-Path $root 'copying') -Force).Count) { [IO.Directory]::Delete((Join-Path $root 'copying')) }
    } else {
        foreach ($release in $journal.previous.releases) {
            $removed = Join-Path $root ('removed/' + $release.id)
            if (Test-Path -LiteralPath $removed) { [IO.Directory]::Move($removed, (Join-Path $root ('versions/' + $release.id))) }
        }
        if (Test-Path -LiteralPath (Join-Path $root 'removed')) { [IO.Directory]::Delete((Join-Path $root 'removed')) }
        Write-NativeJson $statePath $journal.previous
    }
    if ($null -ne $journal.previous) { Assert-NativeInstalled $root $journal.previous }
    if (-not $retained) { [IO.File]::Delete($path) }
    [pscustomobject]@{ operation = $(if ($retained) { 'RecoveredWithQuarantine' } else { 'Recovered' });
        quarantine = $(if ($retained) { $quarantine } else { $null }); journalRetained = [bool]$retained;
        stateSha256 = $(if (Test-Path -LiteralPath $statePath) { Get-NativeHash $statePath } else { $null }) }
}

function Move-NativePreparedPayload([string]$Source, [string]$Destination, $Files) {
    # Windows scanners/observers may briefly hold the fully copied directory.
    # Retry only the atomic rename, never the transaction or content copy.
    foreach ($delay in @(0, 750, 1750)) {
        if ($delay) {
            Start-Sleep -Milliseconds $delay
            $null = Resolve-NativeRoot $Source
            $null = Resolve-NativeRoot $Destination
            Assert-NativePayload $Source $Files
        }
        try {
            $null = [IO.File]::GetAttributes($Destination)
            throw 'Prepared payload destination already exists; no adoption or overwrite.'
        } catch [IO.FileNotFoundException] {
        } catch [IO.DirectoryNotFoundException] {
        }
        try {
            [IO.Directory]::Move($Source, $Destination)
            return
        } catch {
            $cause = $_.Exception
            while ($null -ne $cause.InnerException) { $cause = $cause.InnerException }
            $code = $cause.HResult -band 65535
            if ($delay -eq 1750 -or $code -notin @(5, 32)) { throw }
            # Both sides must still be known before the next attempt. An ambiguous
            # completed rename is never treated as permission to publish/adopt.
            $null = [IO.File]::GetAttributes($Source)
        }
    }
}

function Get-NativePhaseResult([string]$Operation, [string]$Root, $Journal, [string]$JournalSha256) {
    $statePath = Join-Path $Root 'state.json'
    [pscustomobject]@{
        operation = $Operation
        journalSha256 = $JournalSha256
        previousStateSha256 = $(if ($null -ne $Journal.previous) { Get-NativeJsonHash $Journal.previous } else { $null })
        nextStateSha256 = $(if ($null -ne $Journal.next) { Get-NativeJsonHash $Journal.next } else { $null })
        stateSha256 = $(if (Test-Path -LiteralPath $statePath) { Get-NativeHash $statePath } else { $null })
    }
}

function Invoke-NativePinnedPhase {
    param([string]$Operation, [string]$Root, [string]$Data, [string]$TransactionPhase,
        [string]$JournalSha256, [string]$ExpectedStateSha256, [scriptblock]$TestHook)
    $path = Join-Path $Root 'journal.json'
    $null = Resolve-NativeRoot $path
    if ($JournalSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-NativeHash $path) -cne $JournalSha256) { throw 'Trusted journal hash required; journal changed or tampered.' }
    $journal = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($journal.schema -ne 1 -or $journal.installRoot -cne $Root -or $journal.dataRoot -cne $Data -or $journal.operation -cne $Operation -or $journal.copyProtocol -cne 'staged-v1') { throw 'Journal target, operation or protocol mismatch.' }
    if ($null -ne $journal.previous) { Assert-NativeState $journal.previous $Root $Data }
    if ($Operation -eq 'Apply') {
        Assert-NativeState $journal.next $Root $Data
        $old = @(); if ($null -ne $journal.previous) { $old = @($journal.previous.releases) }
        $candidate = @($journal.next.releases | Where-Object { $_.id -ceq $journal.next.current })[0]
        if (@($old | Where-Object { $_.id -ceq $candidate.id }).Count -or
            ($journal.next.releases | ConvertTo-Json -Depth 30 -Compress) -cne (@($old + @($candidate)) | ConvertTo-Json -Depth 30 -Compress)) { throw 'Journal release transition mismatch.' }
        Assert-NativeLayout $Root $journal.next.releases
        Assert-NativeInstalled $Root $journal.next
    } else {
        if ($null -eq $journal.previous -or $null -ne $journal.next) { throw 'Invalid uninstall journal.' }
        Assert-NativeLayout $Root $journal.previous.releases -UninstallRecovery
    }
    $previousHash = if ($null -ne $journal.previous) { Get-NativeJsonHash $journal.previous } else { $null }
    $nextHash = if ($null -ne $journal.next) { Get-NativeJsonHash $journal.next } else { $null }
    $expected = if ($TransactionPhase -eq 'Finalize' -and $Operation -eq 'Apply') { $nextHash } else { $previousHash }
    if (($null -eq $expected -and $ExpectedStateSha256) -or
        ($null -ne $expected -and ($ExpectedStateSha256 -cnotmatch '^[0-9a-f]{64}$' -or $ExpectedStateSha256 -cne $expected))) { throw 'Expected state hash differs from pinned phase intent.' }
    $statePath = Join-Path $Root 'state.json'
    $actual = if (Test-Path -LiteralPath $statePath) { Get-NativeHash $statePath } else { $null }
    if ($Operation -eq 'Apply') {
        if ($TransactionPhase -eq 'Finalize') {
            if ($actual -cne $nextHash) { throw 'Finalize requires the exact activated next state.' }
        } else {
            if ($actual -cne $previousHash -and $actual -cne $nextHash) { throw 'Activation state differs from pinned journal.' }
            if ($actual -cne $nextHash) { Write-NativeJson $statePath $journal.next $TestHook 'State' }
        }
    } else {
        if ($TransactionPhase -eq 'Finalize') {
            if ($null -ne $actual) { throw 'Finalize Remove requires absent active state.' }
            foreach ($release in $journal.previous.releases) {
                if (Test-Path -LiteralPath (Join-Path $Root ('versions/' + $release.id))) { throw 'Finalize Remove refuses live releases.' }
                Assert-NativePayload (Join-Path $Root ('removed/' + $release.id)) $release.files -Partial
            }
            # Validate the entire surviving subset before deleting anything. Missing known files
            # are an interrupted irreversible cleanup, never evidence of a usable rollback.
            foreach ($release in $journal.previous.releases) {
                $removed = Join-Path $Root ('removed/' + $release.id)
                foreach ($file in $release.files) {
                    $target = Join-Path $removed $file.path
                    if (Test-Path -LiteralPath $target) {
                        [IO.File]::Delete($target)
                        if ($TestHook) { $null = & $TestHook 'DuringFinalizeRemove' $target }
                    }
                }
                foreach ($dir in @(Get-NativeTree $removed | Where-Object PSIsContainer | Sort-Object { $_.FullName.Length } -Descending)) { [IO.Directory]::Delete($dir.FullName) }
                if (Test-Path -LiteralPath $removed) { [IO.Directory]::Delete($removed) }
            }
            if (Test-Path -LiteralPath (Join-Path $Root 'removed')) { [IO.Directory]::Delete((Join-Path $Root 'removed')) }
        } else {
            if ($actual -cne $previousHash -and $null -ne $actual) { throw 'Activation state differs from pinned journal.' }
            foreach ($release in $journal.previous.releases) {
                $live = Join-Path $Root ('versions/' + $release.id)
                $removed = Join-Path $Root ('removed/' + $release.id)
                if ((Test-Path -LiteralPath $live) -eq (Test-Path -LiteralPath $removed)) { throw 'Ambiguous removal source.' }
                if (Test-Path -LiteralPath $live) {
                    if ($null -eq $actual) { throw 'Absent state with live removal payload.' }
                    Assert-NativePayload $live $release.files
                } else { Assert-NativePayload $removed $release.files }
            }
            [IO.Directory]::CreateDirectory((Join-Path $Root 'removed')) | Out-Null
            foreach ($release in $journal.previous.releases) {
                $live = Join-Path $Root ('versions/' + $release.id)
                if (Test-Path -LiteralPath $live) {
                    [IO.Directory]::Move($live, (Join-Path $Root ('removed/' + $release.id)))
                    if ($TestHook) { & $TestHook 'AfterRemoveMove' }
                }
            }
            if ($null -ne $actual) { [IO.File]::Delete($statePath) }
        }
    }
    if ($TransactionPhase -eq 'Activate') {
        if ($TestHook) { $null = & $TestHook 'AfterActivate' $statePath }
        return Get-NativePhaseResult 'Activated' $Root $journal $JournalSha256
    }
    [IO.File]::Delete($path)
    Get-NativePhaseResult 'Finalized' $Root $journal $JournalSha256
}

function Invoke-NativeTransactionCore {
    [CmdletBinding()]
    param(
        [ValidateSet('Apply','Remove')][string]$Operation,
        [string]$InstallRoot, [string]$DataRoot,
        [string]$StageDirectory, [string]$ManifestSha256,
        [ValidateSet('Verification','Production')][string]$Mode = 'Verification',
        [string]$ExpectedStateSha256,
        # The entry script never exposes this hook. Tests can interrupt but cannot bypass guards.
        [scriptblock]$TestHook,
        [ValidateSet('Standalone','Prepare','Activate','Finalize')][string]$TransactionPhase = 'Standalone',
        [string]$JournalSha256
    )
    $root = Resolve-NativeRoot $InstallRoot
    $data = Resolve-NativeRoot $DataRoot
    Assert-NativeSeparate $root $data
    if ($Operation -notin @('Apply','Remove')) { throw 'An explicit Apply or Remove operation is required.' }
    if ($TransactionPhase -in @('Activate','Finalize')) {
        if ($StageDirectory -or $ManifestSha256) { throw 'Pinned phases do not accept source-stage arguments.' }
        return Invoke-NativePinnedPhase $Operation $root $data $TransactionPhase $JournalSha256 $ExpectedStateSha256 $TestHook
    }
    if ($JournalSha256) { throw 'JournalSha256 is valid only for Activate/Finalize.' }
    if ($TransactionPhase -eq 'Prepare' -and $Operation -eq 'Remove' -and ($StageDirectory -or $ManifestSha256)) { throw 'Remove Prepare does not accept source-stage arguments.' }
    if (Test-Path -LiteralPath (Join-Path $root 'journal.json')) { throw 'Unresolved transaction; explicit recovery is required.' }
    $previous = $null
    if (Test-Path -LiteralPath (Join-Path $root 'state.json')) {
        $previous = Read-NativeState $root $data $ExpectedStateSha256
        Assert-NativeLayout $root $previous.releases
        Assert-NativeInstalled $root $previous
        # Schema1 embeds the parsed previous state, not its original byte representation.
        # Phased pins therefore require the existing writer's exact bytes; never normalize input.
        if ($TransactionPhase -eq 'Prepare' -and (Get-NativeJsonHash $previous) -cne $ExpectedStateSha256) { throw 'Phased Prepare requires canonical engine state bytes.' }
    } else {
        $entries = @(Get-NativeTree $root)
        $emptyVersionsOnly = $entries.Count -eq 1 -and $entries[0].PSIsContainer -and $entries[0].FullName -ceq (Join-Path $root 'versions')
        if ($entries.Count -and -not $emptyVersionsOnly) { throw 'Fresh installation requires an empty dedicated root.' }
        if ($ExpectedStateSha256) { throw 'Expected installation state is missing.' }
    }
    $next = $null
    if ($Operation -eq 'Apply') {
        $stage = Resolve-NativeRoot $StageDirectory
        Assert-NativeSeparate $stage $root
        Assert-NativeSeparate $stage $data
        $manifest = Read-NativeStage $stage $ManifestSha256 $Mode
        $releases = @(); if ($null -ne $previous) { $releases = @($previous.releases) }
        if (@($releases | Where-Object { $_.id -eq $ManifestSha256 }).Count) { throw 'Release already installed.' }
        $candidate = [pscustomobject]@{ id = $ManifestSha256; files = @($manifest.files) }
        $next = [pscustomobject]@{ schema = 1; installRoot = $root; dataRoot = $data; current = $ManifestSha256; releases = @($releases) + @($candidate) }
    } elseif ($null -eq $previous) { throw 'Nothing installed.' }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $journal = [pscustomobject]@{ schema = 1; operation = $Operation; installRoot = $root; dataRoot = $data; previous = $previous; next = $next; copyProtocol = 'staged-v1' }
    $journalPath = Join-Path $root 'journal.json'
    $journalHash = Get-NativeJsonHash $journal
    Write-Information "RECOVERY journal=$journalPath sha256=$journalHash" -InformationAction Continue
    Write-NativeJson $journalPath $journal $TestHook 'Journal'
    [IO.Directory]::CreateDirectory((Join-Path $root 'versions')) | Out-Null
    if ($TestHook) { & $TestHook 'AfterJournal' }
    # Failures intentionally retain a journal. No reliance on Inno automatic rollback.
    if ($Operation -eq 'Apply') {
        $destination = Join-Path $root ('copying/' + $journalHash)
        $count = 0
        foreach ($file in $manifest.files) {
            $target = Join-Path $destination $file.path
            [IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
            $inputStream = [IO.File]::OpenRead((Join-Path $stage $file.path))
            try {
                $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try {
                    $buffer = New-Object byte[] 65536
                    while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $outputStream.Write($buffer, 0, $read)
                        $outputStream.Flush()
                        if ($TestHook) { & $TestHook 'DuringCopy' }
                    }
                } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
            $count++
            if ($count -eq 1 -and $TestHook) { & $TestHook 'AfterFirstCopy' }
        }
        Assert-NativePayload $destination $manifest.files
        Move-NativePreparedPayload $destination (Join-Path $root ('versions/' + $ManifestSha256)) $manifest.files
        [IO.Directory]::Delete((Join-Path $root 'copying'))
        if ($TransactionPhase -eq 'Prepare') {
            if ($TestHook) { $null = & $TestHook 'AfterPrepare' $journalPath }
            return Get-NativePhaseResult 'Prepared' $root $journal $journalHash
        }
        if ($TestHook) { & $TestHook 'BeforeCommit' }
        Write-NativeJson (Join-Path $root 'state.json') $next $TestHook 'State'
    } else {
        if ($TransactionPhase -eq 'Prepare') {
            if ($TestHook) { $null = & $TestHook 'AfterPrepare' $journalPath }
            return Get-NativePhaseResult 'Prepared' $root $journal $journalHash
        }
        [IO.Directory]::CreateDirectory((Join-Path $root 'removed')) | Out-Null
        foreach ($release in $previous.releases) {
            [IO.Directory]::Move((Join-Path $root ('versions/' + $release.id)), (Join-Path $root ('removed/' + $release.id)))
            if ($TestHook) { & $TestHook 'AfterRemoveMove' }
        }
        [IO.File]::Delete((Join-Path $root 'state.json'))
    }
    if ($TestHook) { & $TestHook 'AfterCommit' }
    if ($Operation -eq 'Apply') {
        [IO.File]::Delete($journalPath)
        [pscustomobject]@{ operation = 'Applied'; current = $ManifestSha256; stateSha256 = Get-NativeHash (Join-Path $root 'state.json') }
    } else {
        # Retained, verified quarantine is the uninstall recovery point, not active binaries.
        [pscustomobject]@{ operation = 'RemovedWithRecovery'; journalSha256 = $journalHash }
    }
}

function Invoke-NativeLocked([string]$InstallRoot, [scriptblock]$Body) {
    $root = Resolve-NativeRoot $InstallRoot
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToUpperInvariant()))).Replace('-', '') }
    finally { $sha.Dispose() }
    $mutex = [Threading.Mutex]::new($false, ('Local\WinSight.NativeInstaller.' + $key))
    $acquired = $false
    try {
        try { $acquired = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Concurrent installer operation rejected.' }
        & $Body
    } finally {
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Invoke-NativeTransaction {
    [CmdletBinding()]
    param(
        [ValidateSet('Apply','Remove')][string]$Operation,
        [string]$InstallRoot, [string]$DataRoot,
        [string]$StageDirectory, [string]$ManifestSha256,
        [ValidateSet('Verification','Production')][string]$Mode = 'Verification',
        [string]$ExpectedStateSha256, [scriptblock]$TestHook,
        [ValidateSet('Standalone','Prepare','Activate','Finalize')][string]$TransactionPhase = 'Standalone',
        [string]$JournalSha256
    )
    $parameters = $PSBoundParameters
    Invoke-NativeLocked $InstallRoot { Invoke-NativeTransactionCore @parameters }
}

function Invoke-NativeRecovery([string]$InstallRoot, [string]$DataRoot, [string]$JournalSha256,
    [string]$StageDirectory, [string]$ManifestSha256,
    [ValidateSet('Verification','Production')][string]$Mode = 'Verification',
    [string]$ExpectedStateSha256, [string]$InitializationEvidenceRoot,
    [ValidateSet('Apply','Remove')][string]$InitializationOperation = 'Apply') {
    Invoke-NativeLocked $InstallRoot { Invoke-NativeRecoveryCore $InstallRoot $DataRoot $JournalSha256 $StageDirectory $ManifestSha256 $Mode $ExpectedStateSha256 $InitializationEvidenceRoot $InitializationOperation }
}

Export-ModuleMember -Function Get-NativeHash, Resolve-NativeRoot, Assert-NativeSeparate, Read-NativeStage, Invoke-NativeTransaction, Invoke-NativeRecovery
