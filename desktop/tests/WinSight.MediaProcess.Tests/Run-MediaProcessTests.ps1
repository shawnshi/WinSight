[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$OfflinePackageSource,
    [string]$InferenceHost,
    [switch]$FixturesOnly,
    [string]$Case,
    [ValidateRange(10,150)][int]$DeadlineSeconds = 120
)
$ErrorActionPreference = 'Stop'
$lane = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $lane '.dev')) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot) -or (Test-Path -LiteralPath $ArtifactRoot) -or
    -not ([IO.Path]::GetFullPath($ArtifactRoot).StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'ArtifactRoot must be an explicit new directory under this lane .dev.'
}
if (-not [IO.Path]::IsPathFullyQualified($OfflinePackageSource) -or -not (Test-Path -LiteralPath $OfflinePackageSource -PathType Container)) {
    throw 'Explicit existing PUBLIC offline NuGet source required; no remote source/audit/download.'
}
if ($InferenceHost -and (-not [IO.Path]::IsPathFullyQualified($InferenceHost) -or -not (Test-Path -LiteralPath $InferenceHost -PathType Leaf))) {
    throw 'InferenceHost must be the explicitly supplied actual N1 executable.'
}
$root = [IO.Path]::GetFullPath($ArtifactRoot)
[void][IO.Directory]::CreateDirectory($root)
$receipts = [Collections.Generic.List[object]]::new()
$clock = [Diagnostics.Stopwatch]::StartNew()
function Stop-Recorded([int]$ProcessId, [long]$CreationUtcTicks) {
    try { $p = [Diagnostics.Process]::GetProcessById($ProcessId) } catch [ArgumentException] { return }
    try {
        if ($p.StartTime.ToUniversalTime().Ticks -ne $CreationUtcTicks -or $p.HasExited) { return }
        $p.Kill()
        if (-not $p.WaitForExit(5000)) { throw "Owned PID exit unconfirmed: $ProcessId" }
    } finally { $p.Dispose() }
}
function Invoke-Bounded([string]$Name, [string]$File, [string[]]$Arguments, [int]$Seconds) {
    $Seconds = [Math]::Min($Seconds, [int][Math]::Floor(170 - $clock.Elapsed.TotalSeconds - 8))
    if ($Seconds -le 0) { throw 'Overall 170-second budget exhausted; no further process started.' }
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.UseShellExecute = $false
    $info.WorkingDirectory = Join-Path $lane 'desktop'
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    foreach ($arg in $Arguments) { [void]$info.ArgumentList.Add($arg) }
    $p = [Diagnostics.Process]::Start($info)
    $identity = @{ Pid=$p.Id; CreationUtcTicks=$p.StartTime.ToUniversalTime().Ticks }
    [IO.File]::WriteAllText((Join-Path $root "$Name.identity.json"), (ConvertTo-Json $identity))
    $stdout = [IO.File]::Create((Join-Path $root "$Name.stdout.log"))
    $stderr = [IO.File]::Create((Join-Path $root "$Name.stderr.log"))
    $outTask = $p.StandardOutput.BaseStream.CopyToAsync($stdout)
    $errTask = $p.StandardError.BaseStream.CopyToAsync($stderr)
    try {
        $timedOut = -not $p.WaitForExit($Seconds * 1000)
        if ($timedOut) {
            # Only identities in this new task-owned root; never process names, snapshots of unrelated PIDs, or Kill(tree).
            $data = Join-Path $root 'data'
            if (Test-Path -LiteralPath $data -PathType Container) {
                foreach ($receipt in @(Get-ChildItem -LiteralPath $data -Filter '*.json' -Recurse -File -ErrorAction Stop | Where-Object { $_.Directory.Name -eq 'observations' })) {
                    $owned = [IO.File]::ReadAllText($receipt.FullName) | ConvertFrom-Json
                    Stop-Recorded $owned.Pid $owned.CreationUtcTicks
                }
            }
            Stop-Recorded $identity.Pid $identity.CreationUtcTicks
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask), 3000)) { throw "Pipe EOF not confirmed for $Name; output files retained, not normalized to empty." }
        $code = $p.ExitCode
        $receipts.Add([pscustomobject]@{ name=$Name; executable=$File; arguments=$Arguments; exitCode=$code; timedOut=$timedOut; pid=$identity.Pid; creationUtcTicks=$identity.CreationUtcTicks })
        [IO.File]::WriteAllText((Join-Path $root 'commands.json'), (ConvertTo-Json -InputObject @($receipts.ToArray()) -Depth 8))
        Write-Output "$Name exit=$code timedOut=$timedOut"
        if ($timedOut) { throw "External deadline: $Name. All evidence retained; this is NOT a lifecycle pass." }
        if ($code -ne 0) { throw "$Name failed with exit $code; see $root" }
    } finally { $stdout.Dispose(); $stderr.Dispose(); $p.Dispose() }
}
try {
    $hashes = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Where-Object { $_.Extension -in '.cs','.csproj','.ps1' -and $_.FullName -notmatch '[\\/](bin|obj|\.artifacts)[\\/]' } | ForEach-Object {
        @{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [IO.File]::WriteAllText((Join-Path $root 'source-hashes.json'), (ConvertTo-Json -InputObject $hashes -Depth 5))
    if (-not $FixturesOnly -and -not (Test-Path -LiteralPath (Join-Path $lane 'desktop/src/WinSight.Infrastructure/Media'))) {
        [IO.File]::WriteAllText((Join-Path $root 'integration-pending.txt'), 'Actual Infrastructure.Media API absent. No production stub and no blind compilation attempted.')
        throw 'N2-ENV-01: integration pending actual Infrastructure.Media source.'
    }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $artifacts = Join-Path $root 'build'
    $packages = Join-Path $root 'packages'
    $projects = if ($FixturesOnly) { @('FakeMediaHost/FakeMediaHost.csproj','Fixtures/WinSight.MediaProcess.Fixtures.csproj') } else { @('WinSight.MediaProcess.Tests.csproj') }
    $n = 0
    foreach ($project in $projects) {
        $path = Join-Path $PSScriptRoot $project
        Invoke-Bounded "restore-$n" $dotnet @('restore',$path,'--runtime','win-x64','--source',$OfflinePackageSource,'--packages',$packages,'--artifacts-path',$artifacts,'-p:NuGetAudit=false','-p:RestoreIgnoreFailedSources=false','--disable-build-servers') 65
        Invoke-Bounded "build-$n" $dotnet @('build',$path,'--runtime','win-x64','--no-restore','--artifacts-path',$artifacts,'-p:UseSharedCompilation=false','--disable-build-servers','-nr:false') 65
        $n++
    }
    $assembly = if ($FixturesOnly) { 'WinSight.MediaProcess.Fixtures' } else { 'WinSight.MediaProcess.Tests' }
    $exe = @(Get-ChildItem -LiteralPath (Join-Path $artifacts "bin/$assembly") -Recurse -File -Filter "$assembly.exe")
    $fake = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin/FakeMediaHost') -Recurse -File -Filter 'FakeMediaHost.exe')
    if ($exe.Count -ne 1 -or $fake.Count -ne 1) { throw 'Expected exactly one test and fake executable.' }
    $arguments = @('--artifact-root',(Join-Path $root 'data'),'--fake-host',$fake[0].FullName)
    if ($InferenceHost -and -not $FixturesOnly) { $arguments += @('--inference-host',$InferenceHost) }
    if ($Case) { $arguments += @('--case',$Case) }
    Invoke-Bounded 'tests' $exe[0].FullName $arguments $DeadlineSeconds
    exit 0
} catch {
    [IO.File]::WriteAllText((Join-Path $root 'runner-error.txt'), $_.Exception.ToString())
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
