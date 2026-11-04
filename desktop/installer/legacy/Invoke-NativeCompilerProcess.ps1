[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CompilerPath,
    [Parameter(Mandatory)][string]$CompilerArguments,
    [Parameter(Mandatory)][string]$LogPath,
    [ValidateRange(100,150000)][int]$ProcessTimeoutMilliseconds = 150000,
    [ValidateRange(100,5000)][int]$SettlementTimeoutMilliseconds = 5000,
    [ValidateRange(100,5000)][int]$DrainTimeoutMilliseconds = 5000
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $LogPath) { throw 'Compiler log must be new.' }
$receiptPath = $LogPath + '.process.json'
if (Test-Path -LiteralPath $receiptPath) { throw 'Compiler process receipt must be new.' }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $CompilerPath
$start.Arguments = $CompilerArguments
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $start
$clock = [Diagnostics.Stopwatch]::StartNew()
$started = $false
$settled = $false
$timedOut = $false
$drained = $false
$drainTimedOut = $false
$stdout = $null
$stderr = $null
$failure = $null
$receipt = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$processId = $null
$exitCode = $null
try {
    $started = $process.Start()
    if (-not $started) { throw 'Compiler process failed to start.' }
    $processId = $process.Id
    # Begin both reads before waiting: either pipe can otherwise deadlock the compiler.
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $settled = $process.WaitForExit($ProcessTimeoutMilliseconds)
    if (-not $settled) {
        $timedOut = $true
        $process.Kill()
        $settled = $process.WaitForExit($SettlementTimeoutMilliseconds)
    }
    if (-not $settled) { throw "Owned compiler PID $processId did not settle after termination." }
    $exitCode = $process.ExitCode
    $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), $DrainTimeoutMilliseconds)
    $drainTimedOut = -not $drained
    if ($timedOut) { throw "Compiler exceeded the $ProcessTimeoutMilliseconds millisecond budget; owned PID $processId settled=$settled." }
    if (-not $drained) { throw 'Compiler output pipes did not drain within the bounded budget.' }
    if ($exitCode -ne 0) { throw "Compiler exited with code $exitCode; see $LogPath" }
} catch {
    $failure = $_
} finally {
    # Cleanup is bounded even on a wait/read exception; never kill by name or wait indefinitely.
    if ($started -and -not $settled) {
        try {
            if (-not $process.HasExited) { $process.Kill() }
            $settled = $process.WaitForExit($SettlementTimeoutMilliseconds)
            if ($settled) { $exitCode = $process.ExitCode }
            else { $cleanupErrors.Add("Owned PID $processId remains unsettled.") }
        } catch { $cleanupErrors.Add($_.Exception.ToString()) }
    }
    if ($started) {
        try { $process.StandardOutput.Close() } catch { $cleanupErrors.Add($_.Exception.ToString()) }
        try { $process.StandardError.Close() } catch { $cleanupErrors.Add($_.Exception.ToString()) }
    }
    $readersSettled = $true
    if ($null -ne $stdout -and $null -ne $stderr) {
        try { $readersSettled = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 1000) }
        catch { $readersSettled = $stdout.IsCompleted -and $stderr.IsCompleted; $cleanupErrors.Add($_.Exception.ToString()) }
    }
    $text = ''
    foreach ($task in @($stdout, $stderr)) {
        # Result is never read unless terminal and successful; inherited pipes may stay open after exit.
        if ($null -ne $task -and $task.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) { $text += $task.Result }
    }
    try { $process.Dispose() } catch { $cleanupErrors.Add('Process disposal: ' + $_.Exception.ToString()) }
    $clock.Stop()
    $logStream = $null
    try {
        $logBytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
        $logStream = [IO.File]::Open($LogPath, 'CreateNew', 'Write', 'None')
        $logStream.Write($logBytes, 0, $logBytes.Length)
        $logStream.Flush($true)
    } catch { $cleanupErrors.Add('Log preservation: ' + $_.Exception.ToString()) }
    finally { if ($null -ne $logStream) { try { $logStream.Dispose() } catch { $cleanupErrors.Add('Log disposal: ' + $_.Exception.ToString()) } } }
    $receiptStream = $null
    try {
        $receipt = [pscustomobject]@{
            schema = 1; processId = $processId; started = $started; processSettled = $settled;
            exitCode = $exitCode; timedOut = $timedOut; drained = $drained; drainTimedOut = $drainTimedOut;
            readersSettled = $readersSettled; elapsedMilliseconds = $clock.ElapsedMilliseconds;
            processTimeoutMilliseconds = $ProcessTimeoutMilliseconds; settlementTimeoutMilliseconds = $SettlementTimeoutMilliseconds;
            drainTimeoutMilliseconds = $DrainTimeoutMilliseconds;
            error = $(if ($null -ne $failure) { $failure.Exception.ToString() } else { $null }); cleanupErrors = $cleanupErrors.ToArray()
        }
        # Match Set-Content -Encoding utf8: Desktop uses a BOM; Core does not.
        $receiptEncoding = [Text.UTF8Encoding]::new(($PSVersionTable.PSEdition -ceq 'Desktop'))
        $receiptBytes = $receiptEncoding.GetBytes(($receipt | ConvertTo-Json -Depth 5) + [Environment]::NewLine)
        $preamble = $receiptEncoding.GetPreamble()
        $receiptStream = [IO.File]::Open($receiptPath, 'CreateNew', 'Write', 'None')
        if ($preamble.Length) { $receiptStream.Write($preamble, 0, $preamble.Length) }
        $receiptStream.Write($receiptBytes, 0, $receiptBytes.Length)
        $receiptStream.Flush($true)
    } catch { $cleanupErrors.Add('Receipt preservation: ' + $_.Exception.ToString()) }
    finally { if ($null -ne $receiptStream) { try { $receiptStream.Dispose() } catch { $cleanupErrors.Add('Receipt disposal: ' + $_.Exception.ToString()) } } }
    if ($null -ne $receipt) { $receipt.cleanupErrors = $cleanupErrors.ToArray() }
}
if ($null -ne $failure) { $failure.Exception.Data['NativeCompilerCleanupErrors'] = $cleanupErrors.ToArray(); throw $failure }
if (-not $settled -or -not $drained -or -not $readersSettled -or $cleanupErrors.Count) { throw ('Compiler resource cleanup/preservation incomplete; see ' + $receiptPath + '; ' + ($cleanupErrors -join '; ')) }
$receipt
