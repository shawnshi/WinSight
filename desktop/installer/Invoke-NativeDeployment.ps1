[CmdletBinding(DefaultParameterSetName='Legacy')]
param(
    [Parameter(Mandatory,ParameterSetName='Legacy')][string]$EnginePath,
    [Parameter(Mandatory,ParameterSetName='Legacy')][string]$ControlDirectory,
    [Parameter(Mandatory,ParameterSetName='Legacy')][string]$InstallRoot,
    [Parameter(Mandatory,ParameterSetName='Legacy')][string]$DataRoot,
    [Parameter(ParameterSetName='Legacy')][string]$StageDirectory,
    [Parameter(ParameterSetName='Legacy')][string]$ManifestSha256,
    [Parameter(ParameterSetName='Legacy')][ValidateSet('Verification','Production')][string]$Mode = 'Verification',
    [Parameter(ParameterSetName='Legacy')][string]$ExpectedStateSha256,
    [Parameter(ParameterSetName='Legacy')][ValidateSet('None','AfterJournal','AfterFirstCopy','BeforeCommit','AfterCommit')][string]$VerificationFailurePoint = 'None',
    [Parameter(ParameterSetName='Legacy')][ValidateRange(1000,140000)][int]$DeadlineMilliseconds = 140000,
    [Parameter(ParameterSetName='Legacy')][long]$StartTick = -1,
    [Parameter(ParameterSetName='Standard')][switch]$StandardLive,
    [Parameter(Mandatory,ParameterSetName='Standard')][ValidateSet('Install','Uninstall')][string]$Action,
    [Parameter(Mandatory,ParameterSetName='Standard')][ValidateNotNullOrEmpty()][string]$BindingPath,
    [Parameter(Mandatory,ParameterSetName='Standard')][ValidatePattern('^[0-9a-f]{64}$')][string]$BindingSha256,
    [Parameter(ParameterSetName='Standard')][ValidateRange(1000,160000)][int]$TimeoutMilliseconds = 160000,
    [Parameter(ParameterSetName='Standard')][switch]$Quiet
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSCmdlet.ParameterSetName -ceq 'Standard') {
    if (($PSBoundParameters.ContainsKey('StandardLive') -and -not $StandardLive) -or
        ($PSBoundParameters.ContainsKey('Quiet') -and ($Action -cne 'Uninstall' -or -not $Quiet))) { throw 'B.StandardArguments: false selectors/Quiet outside Uninstall rejected.' }
    # This is an in-process adapter to the existing coordinator, not a second owner engine.
    $standard = @{ StandardLive=$true; Action=$Action; BindingPath=$BindingPath; BindingSha256=$BindingSha256; TimeoutMilliseconds=$TimeoutMilliseconds }
    if ($PSBoundParameters.ContainsKey('Quiet')) { $standard.Quiet=$Quiet }
    & (Join-Path $PSScriptRoot 'Invoke-NativeDeployWorker.ps1') @standard
    exit $LASTEXITCODE
}
Import-Module (Join-Path $PSScriptRoot 'NativeInstaller.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'NativeDeploymentJob.psm1') -Force
if ($StartTick -eq -1) { $StartTick = Get-NativeTick }
if ($StartTick -lt 0 -or $StartTick -gt 4294967295L) { throw 'Invalid shared start tick.' }
$control = Resolve-NativeRoot $ControlDirectory
$engine = Resolve-NativeRoot $EnginePath
if (Test-Path -LiteralPath $control) { throw 'Dispatcher control root must be new.' }
[IO.Directory]::CreateDirectory($control) | Out-Null
$parameters = @{ Operation='Apply'; InstallRoot=$InstallRoot; DataRoot=$DataRoot; StageDirectory=$StageDirectory; ManifestSha256=$ManifestSha256; Mode=$Mode; VerificationFailurePoint=$VerificationFailurePoint }
if ($ExpectedStateSha256) { $parameters.ExpectedStateSha256 = $ExpectedStateSha256 }
[pscustomobject]@{ engine=$engine; parameters=$parameters; startTick=$StartTick; deadlineMilliseconds=$DeadlineMilliseconds } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $control 'request.json') -Encoding utf8
$job = [IntPtr]::Zero
$process = [Diagnostics.Process]::new()
$started = $false
$assigned = $false
$settled = $false
$drained = $false
$readersSettled = $false
$timedOut = $false
$pidOwned = $null
$exitCode = $null
$failure = $null
$cleanupErrors = [Collections.Generic.List[string]]::new()
$stdout = $null
$stderr = $null
$active = $null
try {
    $job = New-NativeDeploymentJob
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $start.Arguments = '-NoProfile -NonInteractive -File "' + (Join-Path $PSScriptRoot 'Invoke-NativeDeployWorker.ps1') + '" -ControlDirectory "' + $control + '"'
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process.StartInfo = $start
    if ((Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds) -le 0) { throw 'Dispatcher startup consumed shared deadline.' }
    $started = $process.Start()
    if (-not $started) { throw 'Deployment worker did not start.' }
    $pidOwned = $process.Id
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not [WinSight.DeploymentJobApi]::AssignProcessToJobObject($job, $process.Handle)) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    $assigned = $true
    # The worker cannot invoke the engine or spawn its descendants before job assignment.
    [IO.File]::WriteAllText((Join-Path $control 'assigned'), 'assigned')
    $reserve = [int][Math]::Min(10000, [Math]::Max(400, $DeadlineMilliseconds / 4))
    $execution = [Math]::Max(0, (Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds) - $reserve)
    $settled = $process.WaitForExit([int]$execution)
    if ($settled) { $exitCode = $process.ExitCode }
    $active = Get-NativeJobActiveProcesses $job
    if (-not $settled -or $active -ne 0) {
        $timedOut = $true
        if (-not [WinSight.DeploymentJobApi]::TerminateJobObject($job, 124)) { throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    }
    $remaining = Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds
    if (-not $settled) { $settled = $process.WaitForExit($remaining); if ($settled) { $exitCode = $process.ExitCode } }
    while ((Get-NativeJobActiveProcesses $job) -gt 0 -and (Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds) -gt 0) { Start-Sleep -Milliseconds 10 }
    $active = Get-NativeJobActiveProcesses $job
    $remaining = Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds
    $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr), $remaining)
    if ($timedOut) { throw 'Deployment execution or descendant lifetime exceeded shared budget; owned job terminated.' }
    if (-not $settled -or $active -ne 0 -or -not $drained) { throw 'Deployment process/drain cleanup incomplete within shared deadline.' }
    if ($exitCode -ne 0) { throw "Deployment engine exited $exitCode; journal preserved for explicit recovery." }
} catch { $failure = $_ }
finally {
    if ($job -ne [IntPtr]::Zero) {
        try {
            if ((Get-NativeJobActiveProcesses $job) -gt 0) { $null = [WinSight.DeploymentJobApi]::TerminateJobObject($job, 124) }
            while ((Get-NativeJobActiveProcesses $job) -gt 0 -and (Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds) -gt 0) { Start-Sleep -Milliseconds 10 }
            $active = Get-NativeJobActiveProcesses $job
        } catch { $cleanupErrors.Add($_.Exception.ToString()) }
        if (-not [WinSight.DeploymentJobApi]::CloseHandle($job)) { $cleanupErrors.Add('Job CloseHandle failed.') }
    }
    if ($started) {
        if (-not $assigned) {
            try { if (-not $process.HasExited) { $process.Kill() } } catch { $cleanupErrors.Add($_.Exception.ToString()) }
        }
        try { $settled = $process.WaitForExit((Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds)); if ($settled) { $exitCode = $process.ExitCode } }
        catch { $cleanupErrors.Add($_.Exception.ToString()) }
        try { $process.StandardOutput.Close(); $process.StandardError.Close() } catch { $cleanupErrors.Add($_.Exception.ToString()) }
    }
    if ($null -ne $stdout -and $null -ne $stderr) {
        try { $readersSettled = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout,$stderr), (Get-NativeDeadlineRemaining $StartTick $DeadlineMilliseconds)) }
        catch { $readersSettled = $stdout.IsCompleted -and $stderr.IsCompleted; $cleanupErrors.Add($_.Exception.ToString()) }
    }
    $text = ''
    foreach ($task in @($stdout,$stderr)) { if ($null -ne $task -and $task.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) { $text += $task.Result } }
    [IO.File]::WriteAllText((Join-Path $control 'engine.log'), $text, [Text.UTF8Encoding]::new($false))
    $process.Dispose()
    $elapsed = ((Get-NativeTick) - $StartTick + 4294967296L) % 4294967296L
    $result = [pscustomobject]@{ schema=1; workerPid=$pidOwned; started=$started; processSettled=$settled; activeOwnedProcesses=$active;
        drained=$drained; readersSettled=$readersSettled; engineExitCode=$exitCode; timedOut=$timedOut; elapsedMilliseconds=$elapsed; deadlineMilliseconds=$DeadlineMilliseconds;
        error=$(if($null -ne $failure){$failure.Exception.ToString()}else{$null}); cleanupErrors=@($cleanupErrors) }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $control 'result.json') -Encoding utf8
}
if ($null -ne $failure) { Write-Error -ErrorRecord $failure -ErrorAction Continue; exit 1 }
if (-not $settled -or -not $drained -or -not $readersSettled -or $active -ne 0 -or $cleanupErrors.Count) { Write-Error 'Dispatcher settlement incomplete.' -ErrorAction Continue; exit 1 }
exit 0
