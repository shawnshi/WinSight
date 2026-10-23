[CmdletBinding()]
param([Parameter(Mandatory)][string]$TaskRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$engine = Split-Path $PSScriptRoot -Parent
$repository = Split-Path (Split-Path $engine -Parent) -Parent
Import-Module (Join-Path $engine 'NativeInstaller.psm1') -Force
$taskRootFull = Resolve-NativeRoot $TaskRoot
$boundary = Resolve-NativeRoot (Join-Path $repository '.dev/native-migration')
if (-not $taskRootFull.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $taskRootFull)) { throw 'A new lane .dev/native-migration task root is required.' }
[IO.Directory]::CreateDirectory($taskRootFull) | Out-Null
Start-Transcript -LiteralPath (Join-Path $taskRootFull 'tests.log') -NoClobber | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "ASSERTION FAILED: $Name" }
    $checks.Add($Name)
    Write-Output "PASS $Name"
}
$hostExecutable = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$helper = Join-Path $engine 'Invoke-NativeCompilerProcess.ps1'
$fixture = Join-Path $taskRootFull 'compiler-fixture.ps1'
$descendant = Join-Path $taskRootFull 'pipe-holder.ps1'
$pidFile = Join-Path $taskRootFull 'pipe-holder.pid'
$fixtureText = @'
param([string]$Mode, [string]$ChildScript, [string]$PidFile)
$ErrorActionPreference = 'Stop'
switch ($Mode) {
    'Flood' {
        for ($i = 0; $i -lt 512; $i++) {
            [Console]::Out.WriteLine('OUT-' + $i + '-' + ('o' * 256))
            [Console]::Error.WriteLine('ERR-' + $i + '-' + ('e' * 256))
        }
        exit 0
    }
    'Nonzero' { [Console]::Out.WriteLine('nonzero-before-exit'); [Console]::Error.WriteLine('native-error-17'); exit 17 }
    'Timeout' { [Console]::Out.WriteLine('before-timeout'); Start-Sleep -Seconds 10; exit 0 }
    'InheritedPipe' {
        $info = [Diagnostics.ProcessStartInfo]::new()
        $info.FileName = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $info.Arguments = '-NoProfile -NonInteractive -File "' + $ChildScript + '"'
        $info.UseShellExecute = $false
        $child = [Diagnostics.Process]::Start($info)
        try { [IO.File]::WriteAllText($PidFile, $child.Id.ToString()) }
        finally { $child.Dispose() }
        [Console]::Out.WriteLine('parent-exited-with-inherited-pipe'); exit 0
    }
}
'@
[IO.File]::WriteAllText($fixture, $fixtureText, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($descendant, 'Start-Sleep -Seconds 4', [Text.UTF8Encoding]::new($false))
function Invoke-Fixture([string]$Mode, [int]$Timeout = 10000, [int]$Drain = 1000) {
    $log = Join-Path $taskRootFull ($Mode + '.log')
    $arguments = '-NoProfile -NonInteractive -File "' + $fixture + '" -Mode ' + $Mode + ' -ChildScript "' + $descendant + '" -PidFile "' + $pidFile + '"'
    $errorRecord = $null
    try { $null = & $helper -CompilerPath $hostExecutable -CompilerArguments $arguments -LogPath $log -ProcessTimeoutMilliseconds $Timeout -DrainTimeoutMilliseconds $Drain }
    catch { $errorRecord = $_ }
    [pscustomobject]@{ errorRecord = $errorRecord; receipt = (Get-Content -LiteralPath ($log + '.process.json') -Raw | ConvertFrom-Json); log = $log }
}
try {
    foreach ($path in @($helper, $PSCommandPath, (Join-Path $repository 'scripts/Build-NativeInstaller.ps1'))) {
        $tokens = $null; $errors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        Check (@($errors).Count -eq 0) ('syntax ' + [IO.Path]::GetFileName($path))
    }
    $normal = Invoke-Fixture Flood
    Check ($null -eq $normal.errorRecord -and $normal.receipt.exitCode -eq 0) 'both-pipe flood exits successfully'
    $text = [IO.File]::ReadAllText($normal.log)
    Check ([regex]::Matches($text, '(?m)^OUT-').Count -eq 512 -and [regex]::Matches($text, '(?m)^ERR-').Count -eq 512) 'both redirected pipes are completely drained without deadlock'
    Check ($normal.receipt.processSettled -and $normal.receipt.readersSettled -and $normal.receipt.drained) 'successful process and readers are settled'
    $nonzero = Invoke-Fixture Nonzero
    Check ($null -ne $nonzero.errorRecord -and $nonzero.receipt.exitCode -eq 17 -and $nonzero.receipt.processSettled) 'native nonzero exit preserved and settled'
    Check ([IO.File]::ReadAllText($nonzero.log).Contains('native-error-17')) 'stderr retained on native failure'
    $timeout = Invoke-Fixture Timeout 1500
    Check ($null -ne $timeout.errorRecord -and $timeout.receipt.timedOut -and $timeout.receipt.processSettled) 'timeout terminates and settles only owned process'
    Check ($timeout.receipt.elapsedMilliseconds -lt 10000 -and $timeout.receipt.readersSettled) 'timeout cleanup has a bounded settlement and reader path'
    $live = $null
    try { $live = [Diagnostics.Process]::GetProcessById($timeout.receipt.processId); Check $live.HasExited 'timed-out owned PID is no longer running' }
    catch [ArgumentException] { Check $true 'timed-out owned PID no longer exists' }
    finally { if ($null -ne $live) { $live.Dispose() } }
    $pipe = Invoke-Fixture InheritedPipe 10000 200
    Check ($null -ne $pipe.errorRecord -and $pipe.receipt.processSettled -and $pipe.receipt.drainTimedOut) 'inherited pipe cannot turn exited process into an unbounded drain'
    Check ($pipe.receipt.elapsedMilliseconds -lt 7000) 'inherited-pipe failure returns within cleanup budget'
    # This known fixture owns the descendant; let it finish, never terminate unrelated processes.
    $holder = $null
    try {
        $holder = [Diagnostics.Process]::GetProcessById([int][IO.File]::ReadAllText($pidFile))
        Check ($holder.WaitForExit(6000)) 'owned pipe-holder fixture settles naturally'
    } catch [ArgumentException] { Check $true 'owned pipe-holder fixture already settled' }
    finally { if ($null -ne $holder) { $holder.Dispose() } }
    $missingLog = Join-Path $taskRootFull 'missing.log'
    $missing = $null
    try { $null = & $helper -CompilerPath (Join-Path $taskRootFull 'does-not-exist.exe') -CompilerArguments 'unused' -LogPath $missingLog }
    catch { $missing = $_ }
    $missingReceipt = Get-Content -LiteralPath ($missingLog + '.process.json') -Raw | ConvertFrom-Json
    Check ($null -ne $missing -and -not $missingReceipt.started -and $null -ne $missingReceipt.error) 'start failure preserved without invented exit success'
    [pscustomobject]@{ passed = $true; checks = @($checks); helperSha256 = Get-NativeHash $helper;
        testSha256 = Get-NativeHash $PSCommandPath; runtime = $PSVersionTable.PSVersion.ToString(); actualCompilerExecuted = $false; actualSetupExecuted = $false
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskRootFull 'summary.json') -Encoding utf8
    Write-Output "PASS $($checks.Count) compiler-process checks. No setup executed."
} finally { Stop-Transcript | Out-Null }
