[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$OfflinePackageSource,
    [string]$InferenceHost,
    [switch]$FixturesOnly,
    [switch]$TailOnly,
    [ValidateRange(10,160)][int]$DeadlineSeconds = 150
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot) -or (Test-Path -LiteralPath $ArtifactRoot)) {
    throw 'ArtifactRoot must be an explicit new absolute task directory.'
}
if (-not [IO.Path]::IsPathFullyQualified($OfflinePackageSource) -or -not (Test-Path -LiteralPath $OfflinePackageSource -PathType Container)) {
    throw 'Supply an explicit existing offline public package source. No network restore is allowed.'
}
if ($InferenceHost -and (-not [IO.Path]::IsPathFullyQualified($InferenceHost) -or -not (Test-Path -LiteralPath $InferenceHost -PathType Leaf))) {
    throw 'InferenceHost must point to the actual built host executable.'
}
if (-not $FixturesOnly -and -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot '../../../inference/WinSight.InferenceHost/NativeMediaCommand.cs'))) {
    throw 'Pending integration dependency: actual NativeMediaCommand.cs is absent. No stub or blind build retry is permitted.'
}
if ($FixturesOnly -and $TailOnly) { throw 'Choose FixturesOnly or TailOnly, not both.' }
if ($TailOnly -and -not $InferenceHost) { throw 'TailOnly requires the actual host executable; linked extraction alone is insufficient.' }
if (-not $FixturesOnly -and -not $TailOnly) {
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../../inference/WinSight.InferenceHost/NativeMediaCommand.cs'))
    if (-not $source.Contains('ExecuteWithDriveClassifierAsync') -or -not $source.Contains('RunRequestFileWithDriveClassifierAsync')) {
        throw 'Pending NM-N1R-01 integration: required internal classifier seams are absent. No stub or blind build retry is permitted; TailOnly can validate NM-N1R-02 independently.'
    }
}
$root = [IO.Path]::GetFullPath($ArtifactRoot)
[void][IO.Directory]::CreateDirectory($root)
$receipts = [Collections.Generic.List[object]]::new()
$overall = [Diagnostics.Stopwatch]::StartNew()
function Invoke-BoundedProcess([string]$Name, [string]$File, [string[]]$Arguments, [int]$Seconds) {
    $Seconds = [Math]::Min($Seconds, [int][Math]::Floor(170 - $overall.Elapsed.TotalSeconds - 5))
    if ($Seconds -le 0) { throw 'Overall 170-second command budget exhausted; no new process started.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File
    # Resolve desktop/global.json rather than an unrelated SDK chosen from the fixture directory.
    $start.WorkingDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $output = $process.StandardOutput.ReadToEndAsync()
    $errorOutput = $process.StandardError.ReadToEndAsync()
    $timedOut = -not $process.WaitForExit($Seconds * 1000)
    if ($timedOut) {
        $process.Kill($true)
        if (-not $process.WaitForExit(5000)) { throw "Process exit unconfirmed: $Name. Retain task directories; do not clean native output." }
    }
    $stdout = $output.GetAwaiter().GetResult()
    $stderr = $errorOutput.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $root "$Name.stdout.log"), $stdout)
    [IO.File]::WriteAllText((Join-Path $root "$Name.stderr.log"), $stderr)
    $code = $process.ExitCode
    $receipts.Add([pscustomobject]@{ name=$Name; executable=$File; arguments=$Arguments; exitCode=$code; timedOut=$timedOut })
    [IO.File]::WriteAllText((Join-Path $root 'commands.json'), (ConvertTo-Json -InputObject @($receipts.ToArray()) -Depth 8))
    Write-Output $stdout
    if ($stderr) { [Console]::Error.WriteLine($stderr) }
    $process.Dispose()
    if ($timedOut) { throw "External process deadline exceeded: $Name. Process tree exited; artifacts retained. WinRT token is NOT a hard-stop guarantee." }
    if ($code -ne 0) { throw "$Name failed with exit $code. See $root" }
}
try {
    $project = if ($FixturesOnly) { Join-Path $PSScriptRoot 'Fixtures/WinSight.NativeMedia.Fixtures.csproj' } elseif ($TailOnly) { Join-Path $PSScriptRoot 'TailProbe/WinSight.NativeMedia.TailProbe.csproj' } else { Join-Path $PSScriptRoot 'WinSight.NativeMedia.Tests.csproj' }
    $artifacts = Join-Path $root 'build'
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    Invoke-BoundedProcess 'restore' $dotnet @('restore', $project, '--source', $OfflinePackageSource, '--packages', (Join-Path $root 'packages'), '--artifacts-path', $artifacts, '-p:NuGetAudit=false', '-p:RestoreIgnoreFailedSources=false', '--disable-build-servers') 120
    Invoke-BoundedProcess 'build' $dotnet @('build', $project, '--no-restore', '--artifacts-path', $artifacts, '-p:UseSharedCompilation=false', '--disable-build-servers', '-nr:false') 120
    $assembly = if ($FixturesOnly) { 'WinSight.NativeMedia.Fixtures' } elseif ($TailOnly) { 'WinSight.NativeMedia.TailProbe' } else { 'WinSight.NativeMedia.Tests' }
    $executables = @(Get-ChildItem -LiteralPath (Join-Path $artifacts "bin/$assembly") -Filter "$assembly.exe" -Recurse -File -ErrorAction Stop)
    if ($executables.Count -ne 1) { throw "Expected one test executable, found $($executables.Count)." }
    $arguments = @('--artifact-root', (Join-Path $root 'data'))
    if ($InferenceHost -and -not $FixturesOnly) { $arguments += @('--inference-host', $InferenceHost) }
    Invoke-BoundedProcess 'tests' $executables[0].FullName $arguments $DeadlineSeconds
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
