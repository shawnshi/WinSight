#requires -Version 7.0
Set-StrictMode -Version Latest

function Assert-NativeCIPlainPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    for ($cursor = $full; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        try { $attributes = [IO.File]::GetAttributes($cursor) }
        catch [IO.FileNotFoundException] { continue }
        catch [IO.DirectoryNotFoundException] { continue }
        # Check every ancestor on every call: no stale path cache or skipped guard.
        # Only genuine absence is permitted; access/I/O failures still propagate.
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse points are not allowed: $cursor"
        }
    }
    return $full
}

function New-NativeCIArtifactRoot {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][string]$ArtifactRoot)
    if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot)) { throw 'ArtifactRoot must be absolute.' }
    $boundary = Assert-NativeCIPlainPath (Join-Path $RepositoryRoot '.dev/native-migration/native-ci')
    $full = Assert-NativeCIPlainPath $ArtifactRoot
    if (-not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ArtifactRoot must be strictly below this repository .dev/native-migration/native-ci/.'
    }
    if (Test-Path -LiteralPath $full) { throw 'ArtifactRoot already exists; choose a fresh task root.' }
    $null = New-Item -ItemType Directory -Path $full
    return $full
}

function Invoke-NativeCIProcess {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$LogPrefix,
        [hashtable]$Environment = @{},
        [ValidateRange(1,170)][int]$TimeoutSeconds = 160,
        [int[]]$AllowedExitCodes = @(0)
    )
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $totalMs = $TimeoutSeconds * 1000
    $start = [Diagnostics.ProcessStartInfo]::new($FilePath)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $WorkingDirectory
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $stdout = $null; $stderr = $null; $timedOut = $false; $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw "Could not start $FilePath" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        # Execution, output drain and cleanup share one deadline, rather than each obtaining a new timeout.
        $executionMs = [Math]::Max(1, $totalMs - $clock.ElapsedMilliseconds - [Math]::Min(10000, $totalMs / 2))
        if (-not $process.WaitForExit([int]$executionMs)) {
            $timedOut = $true
            $process.Kill($true)
            $remaining = [Math]::Max(1, [Math]::Min(3000, $totalMs - $clock.ElapsedMilliseconds))
            if (-not $process.WaitForExit([int]$remaining)) { throw 'Owned process tree did not terminate before the invocation deadline.' }
        }
        foreach ($task in @($stdout,$stderr)) {
            $remaining = [Math]::Max(1, [Math]::Min(2000, $totalMs - $clock.ElapsedMilliseconds))
            if (-not $task.Wait([int]$remaining)) { throw 'Owned process output did not close before the shared deadline; invocation failed, not checkpointed.' }
        }
        [IO.File]::WriteAllText("$LogPrefix.stdout.log", $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText("$LogPrefix.stderr.log", $stderr.GetAwaiter().GetResult())
        if ($timedOut) { throw "Process timed out within the shared $TimeoutSeconds-second budget; owned tree terminated. See $LogPrefix.*.log" }
        if ($process.ExitCode -notin $AllowedExitCodes) { throw "Process exited $($process.ExitCode). See $LogPrefix.*.log" }
        return [pscustomobject]@{ exitCode = $process.ExitCode; stdout = "$LogPrefix.stdout.log"; stderr = "$LogPrefix.stderr.log" }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            $remaining = [Math]::Max(1, [Math]::Min(3000, $totalMs - $clock.ElapsedMilliseconds))
            if (-not $process.WaitForExit([int]$remaining)) { throw 'Owned process cleanup exceeded the shared deadline; no successful checkpoint.' }
        }
        # Closing our pipe readers also cancels pending reads; no unbounded GetResult/pipe drain in finally.
        if ($started) { $process.StandardOutput.Dispose(); $process.StandardError.Dispose() }
        $process.Dispose()
    }
}

function Get-NativeCISuiteInventory {
    param([Parameter(Mandatory)][string]$RepositoryRoot)
    $selected = @('WinSight.Storage.Tests', 'WinSight.Indexing.Tests', 'WinSight.Inference.Tests', 'FakeHost', 'WinSight.Runtime.Tests', 'WinSight.Pause.Tests',
        'WinSight.MediaBackend.Tests','CompositeHost','WinSight.MediaProcess.Tests','FakeMediaHost','WinSight.MediaIndexing.Tests','FakeNativeWorker','WinSight.MediaMetadata.Tests','FakeMediaOpsHost','WinSight.NativeMedia.Tests')
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot 'desktop/tests') -Filter '*.csproj' -Recurse -File) {
        [xml]$xml = [IO.File]::ReadAllText($project.FullName)
        $name = $project.BaseName
        $packages = @($xml.SelectNodes('//PackageReference') | ForEach-Object { "$($_.Include) $($_.Version)" })
        $references = @($xml.SelectNodes('//ProjectReference') | ForEach-Object { $_.Include })
        $locked = Test-Path -LiteralPath (Join-Path $project.DirectoryName 'packages.lock.json')
        $reason = if ($selected -contains $name) { 'Required synthetic CI or test-only dependency; absence/failure is not skipped-success. No direct PackageReference does not waive SDK/framework/runtime pack requirements.' }
            elseif ($name -eq 'WinSight.Ui.Harness') { 'NotRun: interactive UI/accessibility is a separate current-machine gate.' }
            elseif ($project.FullName -match '[\\/]WinSight\.[^\\/]*Media') { 'NotRun: media suites/helpers are outside this bounded CI slice; no previous media result is reasserted.' }
            else { 'NotRun: deployment/settlement probe is outside this bounded CI slice.' }
        if (-not $locked -and ($packages.Count -gt 0 -or $references -match 'WinSight.Infrastructure')) {
            $reason += ' Blocked for CI inclusion: third-party dependency closure lacks a frozen suite lock; no lock generation authorized.'
        }
        [pscustomobject]@{ project = [IO.Path]::GetRelativePath($RepositoryRoot, $project.FullName).Replace('\','/');
            selected = $selected -contains $name; lockPresent = $locked; packageReferences = $packages;
            projectReferences = $references; reason = $reason }
    }
}

Export-ModuleMember -Function Assert-NativeCIPlainPath,New-NativeCIArtifactRoot,Invoke-NativeCIProcess,Get-NativeCISuiteInventory
