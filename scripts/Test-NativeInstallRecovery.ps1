[CmdletBinding()]
param([Parameter(Mandatory)][string]$TaskRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent
$engine = Join-Path $repository 'desktop/installer'
Import-Module (Join-Path $engine 'NativeInstaller.psm1') -Force
$task = Resolve-NativeRoot $TaskRoot
$boundary = Resolve-NativeRoot (Join-Path $repository '.dev/native-migration')
if (-not $task.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Synthetic tests require a new .dev/native-migration task root.' }
if (Test-Path -LiteralPath $task) { throw 'Refusing an existing test root.' }
[IO.Directory]::CreateDirectory($task) | Out-Null
Start-Transcript -LiteralPath (Join-Path $task 'test.log') -NoClobber | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Name" }
    $checks.Add($Name)
    Write-Output "PASS $Name"
}
function Reject([scriptblock]$Body, [string]$Pattern, [string]$Name) {
    $caught = $null
    try { & $Body | Out-Null } catch { $caught = $_ }
    Check ($null -ne $caught -and $caught.Exception.Message -match $Pattern) $Name
}
function New-Stage([string]$Name, [string]$Content) {
    $root = Join-Path $task $Name
    [IO.Directory]::CreateDirectory((Join-Path $root 'app')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'app/WinSight.App.exe'), "synthetic-not-executable-$Content")
    [IO.File]::WriteAllText((Join-Path $root 'app/THIRD-PARTY-NOTICES.txt'), 'synthetic-notices')
    $files = @(Get-ChildItem -LiteralPath (Join-Path $root 'app') -File | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{ path = 'app/' + $_.Name; bytes = $_.Length; sha256 = Get-NativeHash $_.FullName }
    })
    [pscustomobject]@{ schema = 1; product = 'WinSight NativePreview'; runtime = 'win-x64'; selfContained = $true;
        appOnlyPreview = $true; releaseApproved = $false; files = $files
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'release-manifest.json') -Encoding utf8
    [pscustomobject]@{ root = $root; hash = Get-NativeHash (Join-Path $root 'release-manifest.json') }
}
function Invoke-Entry([string]$Arguments, [string]$LogName) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $start.Arguments = '-NoProfile -NonInteractive -File "' + (Join-Path $engine 'Invoke-NativeInstaller.ps1') + '" ' + $Arguments
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Entry process did not start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); $process.WaitForExit(); throw 'Owned test entry exceeded 15 seconds.' }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $task $LogName), $text)
        [pscustomobject]@{ code = $process.ExitCode; text = $text }
    } finally { $process.Dispose() }
}
function JournalHash([string]$Root) { Get-NativeHash (Join-Path $Root 'journal.json') }
function DataSnapshot {
    @(Get-ChildItem -LiteralPath $data -Recurse -File | Sort-Object FullName | ForEach-Object { $_.Name + ':' + (Get-NativeHash $_.FullName) }) -join '|'
}
try {
    $parseTargets = @((Join-Path $engine 'NativeInstaller.psm1'), (Join-Path $engine 'Invoke-NativeInstaller.ps1'), (Join-Path $PSScriptRoot 'Build-NativeInstaller.ps1'), $PSCommandPath)
    foreach ($path in $parseTargets) {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        Check (@($errors).Count -eq 0) ("syntax " + [IO.Path]::GetFileName($path))
    }
    $one = New-Stage 'stage-v1' 'v1'
    $two = New-Stage 'stage-v2' 'v2'
    $install = Join-Path $task 'install'
    $data = Join-Path $task 'data'
    [IO.Directory]::CreateDirectory($data) | Out-Null
    foreach ($name in @('schema5.db','profile-768d-float16.json','settings.json','index.bin','models.bin')) {
        [IO.File]::WriteAllText((Join-Path $data $name), "synthetic-preserve-$name")
    }
    $external = Join-Path $task 'external.txt'
    [IO.File]::WriteAllText($external, 'external-do-not-touch')
    $externalHash = Get-NativeHash $external
    $snapshot = DataSnapshot
    $valid = & (Join-Path $PSScriptRoot 'Build-NativeInstaller.ps1') -StageDirectory $one.root -OutputDirectory (Join-Path $task 'validate-output') -InstallRoot $install -DataRoot $data -Mode Verification -ManifestSha256 $one.hash -ValidateOnly | ConvertFrom-Json
    Check ($valid.validated -and -not $valid.compiled) 'build dry-run validates helper-format payload without compiler'
    Reject { & (Join-Path $PSScriptRoot 'Build-NativeInstaller.ps1') -StageDirectory $one.root -OutputDirectory (Join-Path $task 'production-fault-output') -InstallRoot $install -DataRoot $data -Mode Production -ManifestSha256 $one.hash -VerificationFailurePoint AfterFirstCopy -ValidateOnly } 'Production cannot contain failure injection' 'production build rejects compiled fault injection'
    $entryRoot = Join-Path $task 'entry-install'
    $entryArguments = '-Operation Apply -InstallRoot "' + $entryRoot + '" -DataRoot "' + $data + '" -StageDirectory "' + $one.root + '" -ManifestSha256 ' + $one.hash
    $entryFailure = Invoke-Entry ($entryArguments + ' -Mode Verification -VerificationFailurePoint AfterFirstCopy') 'entry-failure.log'
    Check ($entryFailure.code -eq 1 -and $entryFailure.text -match 'Verification-only failure') 'real engine entry surfaces verification-only copy interruption'
    $entryHash = JournalHash $entryRoot
    Check ($entryFailure.text.Contains($entryHash)) 'engine output logs trusted recovery hash before failure'
    $entryRecovery = Invoke-Entry ('-Operation Recover -InstallRoot "' + $entryRoot + '" -DataRoot "' + $data + '" -JournalSha256 ' + $entryHash) 'entry-recovery.log'
    Check ($entryRecovery.code -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $entryRoot 'state.json')) -and -not (Test-Path -LiteralPath (Join-Path $entryRoot 'journal.json'))) 'real engine entry recovers failed fresh install'
    $entryProduction = Invoke-Entry ($entryArguments + ' -Mode Production -VerificationFailurePoint AfterFirstCopy') 'entry-production-guard.log'
    Check ($entryProduction.code -eq 1 -and $entryProduction.text -match 'restricted to Verification') 'production entry rejects failure injection'
    Reject { Read-NativeStage $one.root $one.hash Production } 'full payload' 'production refuses preview payload'
    $guardRoot = Join-Path $task 'guard-install'
    Reject { Invoke-NativeTransaction -Operation Apply -InstallRoot $guardRoot -DataRoot $data -StageDirectory $one.root -ManifestSha256 $one.hash -Mode Production -TestHook { throw 'Hook must not run' } } 'full payload' 'test injection cannot waive production payload guard'
    Check (-not (Test-Path -LiteralPath $guardRoot)) 'production guard rejection performs no install writes'
    Reject { & (Join-Path $PSScriptRoot 'Build-NativeInstaller.ps1') -StageDirectory $one.root -OutputDirectory (Join-Path $task 'no-compiler-output') -InstallRoot $install -DataRoot $data -Mode Verification -ManifestSha256 $one.hash } 'Official Inno compiler' 'missing compiler blocks compile without fallback'
    Check (-not (Test-Path -LiteralPath (Join-Path $task 'no-compiler-output'))) 'missing compiler does not create output'
    Reject { Read-NativeStage $one.root ('0' * 64) Verification } 'manifest hash' 'manifest trust-anchor mismatch rejected'
    Reject { Invoke-NativeTransaction Apply $install $install $one.root $one.hash } 'disjoint' 'install/data overlap rejected'
    Reject { Resolve-NativeRoot 'relative/install' } 'absolute local' 'relative root rejected'
    Reject { Resolve-NativeRoot ($install + '.') } 'Ambiguous Windows' 'Windows trailing-dot alias rejected'
    Reject { Resolve-NativeRoot ($install + ' ') } 'Ambiguous Windows' 'Windows trailing-space alias rejected'
    Reject { Resolve-NativeRoot (Join-Path $task 'CON') } 'Ambiguous Windows' 'Windows device root rejected'
    Reject { Resolve-NativeRoot (Join-Path $task 'INSTAL~1') } 'Ambiguous Windows' 'Windows short-name alias rejected'
    [IO.File]::WriteAllText((Join-Path $one.root 'app/unknown.txt'), 'unknown')
    Reject { Read-NativeStage $one.root $one.hash Verification } 'Unknown or missing' 'unknown payload rejected before install'
    [IO.File]::Delete((Join-Path $one.root 'app/unknown.txt'))
    [IO.File]::WriteAllText((Join-Path $one.root 'app/WinSight.App.exe'), 'tampered')
    Reject { Read-NativeStage $one.root $one.hash Verification } 'Payload mismatch' 'payload tampering rejected'
    [IO.File]::WriteAllText((Join-Path $one.root 'app/WinSight.App.exe'), 'synthetic-not-executable-v1')
    $bad = New-Stage 'bad-manifest' 'bad'
    $badManifest = Get-Content -LiteralPath (Join-Path $bad.root 'release-manifest.json') -Raw | ConvertFrom-Json
    $badManifest.files[0].path = 'app/../../external.txt'
    $badManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bad.root 'release-manifest.json') -Encoding utf8
    $badHash = Get-NativeHash (Join-Path $bad.root 'release-manifest.json')
    Reject { Read-NativeStage $bad.root $badHash Verification } 'Unsafe payload' 'manifest path escape rejected'
    # Junctions need no symlink privilege on Windows; target remains inside this synthetic root.
    $junction = Join-Path $bad.root 'app/link'
    New-Item -ItemType Junction -Path $junction -Target $data | Out-Null
    Reject { Read-NativeStage $bad.root $badHash Verification } 'Unsafe payload|Reparse' 'reparse entry rejected'
    # Use a valid manifest to prove rejection is caused by the physical junction.
    $badManifest.files[0].path = 'app/THIRD-PARTY-NOTICES.txt'
    $badManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bad.root 'release-manifest.json') -Encoding utf8
    $badHash = Get-NativeHash (Join-Path $bad.root 'release-manifest.json')
    Reject { Read-NativeStage $bad.root $badHash Verification } 'Reparse entry' 'valid-manifest junction rejected'
    [IO.Directory]::Delete($junction)
    $first = Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $one.root -ManifestSha256 $one.hash
    $v1State = Get-NativeHash (Join-Path $install 'state.json')
    Check ($first.current -eq $one.hash -and $first.stateSha256 -eq $v1State) 'fresh install commits a verified version and receipt'
    Reject { Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $two.root -ManifestSha256 $two.hash } 'Trusted state hash' 'upgrade requires trusted old-state hash'
    foreach ($point in @('AfterJournal','AfterFirstCopy','BeforeCommit','AfterCommit')) {
        $hook = { param($event); if ($event -eq $point) { throw "synthetic interruption $point" } }.GetNewClosure()
        Reject { Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $two.root -ManifestSha256 $two.hash -ExpectedStateSha256 $v1State -TestHook $hook } 'synthetic interruption' ("upgrade failure injection $point")
        $journalHash = JournalHash $install
        Reject { Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $two.root -ManifestSha256 $two.hash -ExpectedStateSha256 $v1State } 'Unresolved transaction' 'unresolved transaction prevents replay'
        Reject { Invoke-NativeRecovery $install (Join-Path $task 'other-data') $journalHash } 'target' 'recovery data target mismatch rejected'
        Reject { Invoke-NativeRecovery $install $data ('0' * 64) } 'journal hash' 'recovery wrong trust anchor rejected'
        $journalPath = Join-Path $install 'journal.json'
        $original = [IO.File]::ReadAllBytes($journalPath)
        [IO.File]::AppendAllText($journalPath, ' ')
        Reject { Invoke-NativeRecovery $install $data $journalHash } 'journal hash' 'recovery journal tampering rejected'
        [IO.File]::WriteAllBytes($journalPath, $original)
        if ($point -eq 'AfterFirstCopy') {
            $candidateRoot = Join-Path $install ('versions/' + $two.hash)
            $partial = @(Get-ChildItem -LiteralPath $candidateRoot -Recurse -File)[0].FullName
            $copiedBytes = [IO.File]::ReadAllBytes($partial)
            [IO.File]::AppendAllText($partial, 'tampered')
            Reject { Invoke-NativeRecovery $install $data $journalHash } 'Payload mismatch' 'recovery refuses tampered candidate bytes'
            Check (Test-Path -LiteralPath $partial) 'tampered candidate is not deleted'
            [IO.File]::WriteAllBytes($partial, $copiedBytes)
            $unknown = Join-Path $candidateRoot 'app/unknown.txt'
            [IO.File]::WriteAllText($unknown, 'external-in-candidate')
            Reject { Invoke-NativeRecovery $install $data $journalHash } 'Unknown payload' 'recovery refuses unknown candidate files'
            [IO.File]::Delete($unknown)
            $held = [IO.File]::Open($partial, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            try { Reject { Invoke-NativeRecovery $install $data $journalHash } 'another process|being used|cannot access|used by another' 'locked candidate prevents unsafe recovery' }
            finally { $held.Dispose() }
        }
        $null = Invoke-NativeRecovery $install $data $journalHash
        Check ((Get-NativeHash (Join-Path $install 'state.json')) -eq $v1State) ("rollback restores exact v1 receipt after $point")
        Check ((Get-NativeHash (Join-Path $install ('versions/' + $one.hash + '/app/WinSight.App.exe'))) -eq (Get-NativeHash (Join-Path $one.root 'app/WinSight.App.exe'))) 'rollback restores original version bytes'
        Check (-not (Test-Path -LiteralPath (Join-Path $install ('versions/' + $two.hash)))) 'rollback removes only candidate files'
    }
    # Locked-file failure is a real sharing violation, not only an injected exception.
    $locked = [IO.File]::Open((Join-Path $two.root 'app/WinSight.App.exe'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        Reject { Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $two.root -ManifestSha256 $two.hash -ExpectedStateSha256 $v1State } 'another process|being used|cannot access|used by another' 'locked source is rejected before journaling'
        Check (-not (Test-Path -LiteralPath (Join-Path $install 'journal.json'))) 'locked preflight leaves old state untouched'
    } finally { $locked.Dispose() }
    $second = Invoke-NativeTransaction -Operation Apply -InstallRoot $install -DataRoot $data -StageDirectory $two.root -ManifestSha256 $two.hash -ExpectedStateSha256 $v1State
    $v2State = $second.stateSha256
    Check ($second.current -eq $two.hash -and (Test-Path -LiteralPath (Join-Path $install ('versions/' + $one.hash)))) 'upgrade selects v2 and preserves v1 recovery bytes'
    [IO.File]::WriteAllText((Join-Path $install 'external.txt'), 'external-in-install')
    Reject { Invoke-NativeTransaction -Operation Remove -InstallRoot $install -DataRoot $data -ExpectedStateSha256 $v2State } 'Unknown installation' 'uninstall refuses unknown external file without deleting it'
    Check (Test-Path -LiteralPath (Join-Path $install 'external.txt')) 'unknown file remains intact'
    [IO.File]::Delete((Join-Path $install 'external.txt'))
    $hook = { param($event); if ($event -eq 'AfterRemoveMove') { throw 'synthetic uninstall interruption' } }
    Reject { Invoke-NativeTransaction -Operation Remove -InstallRoot $install -DataRoot $data -ExpectedStateSha256 $v2State -TestHook $hook } 'synthetic uninstall interruption' 'partial uninstall interruption retained for recovery'
    $null = Invoke-NativeRecovery $install $data (JournalHash $install)
    Check ((Get-NativeHash (Join-Path $install 'state.json')) -eq $v2State) 'partial uninstall recovery restores exact v2 state'
    $removed = Invoke-NativeTransaction -Operation Remove -InstallRoot $install -DataRoot $data -ExpectedStateSha256 $v2State
    Check ($removed.operation -eq 'RemovedWithRecovery' -and -not (Test-Path -LiteralPath (Join-Path $install 'state.json'))) 'uninstall removes active state and retains explicit recovery receipt'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $install 'versions') -Force).Count -eq 0) 'uninstall removes all active versions into verified quarantine'
    $null = Invoke-NativeRecovery $install $data $removed.journalSha256
    Check ((Get-NativeHash (Join-Path $install 'state.json')) -eq $v2State) 'uninstall recovery restores exact state'
    Check ((DataSnapshot) -ceq $snapshot) 'schema/profile/settings/index/models synthetic data unchanged'
    Check ((Get-NativeHash $external) -ceq $externalHash) 'external sibling untouched'
    $parseTargets += Join-Path $engine 'WinSight.Native.iss'
    $sourceHashes = @{}
    foreach ($path in $parseTargets) { $sourceHashes[[IO.Path]::GetFileName($path)] = Get-NativeHash $path }
    [pscustomobject]@{ schema = 1; passed = $true; checks = @($checks); sourceHashes = $sourceHashes;
        actualSetupExecuted = $false; compilerExecuted = $false; restartTested = $false;
        runtime = $PSVersionTable.PSVersion.ToString(); taskRoot = $task
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $task 'summary.json') -Encoding utf8
    Write-Output "PASS $($checks.Count) assertions. Synthetic engine tests only; no setup/real app executed."
} finally { Stop-Transcript | Out-Null }
