[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$OfflinePackageSource,
    [string]$ActualHost,
    [string]$ClientAssembly,
    [switch]$RequireIntegration
)
$ErrorActionPreference = 'Stop'
$lane = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$prefix = [IO.Path]::GetFullPath((Join-Path $lane '.dev')) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::IsPathFullyQualified($ArtifactRoot) -or (Test-Path -LiteralPath $ArtifactRoot) -or
    -not [IO.Path]::GetFullPath($ArtifactRoot).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Explicit NEW artifact directory under this lane .dev required.' }
if (-not [IO.Path]::IsPathFullyQualified($OfflinePackageSource) -or -not (Test-Path -LiteralPath $OfflinePackageSource -PathType Container)) { throw 'Explicit installed PUBLIC offline NuGet cache required.' }
foreach ($file in @($ActualHost, $ClientAssembly)) { if ($file -and (-not [IO.Path]::IsPathFullyQualified($file) -or -not (Test-Path -LiteralPath $file -PathType Leaf))) { throw 'Explicit actual Host/client DLL must exist.' } }
$root = [IO.Path]::GetFullPath($ArtifactRoot)
[void][IO.Directory]::CreateDirectory($root)
$watch = [Diagnostics.Stopwatch]::StartNew()
$commands = [Collections.Generic.List[object]]::new()
function Stop-Identity($owned) {
    try { $p = [Diagnostics.Process]::GetProcessById([int]$owned.Pid) } catch [ArgumentException] { return }
    try {
        if ($p.HasExited -or $p.StartTime.ToUniversalTime().Ticks -ne [long]$owned.CreationUtcTicks) { return }
        $p.Kill()
        if (-not $p.WaitForExit(3000)) { throw "Task PID exit unconfirmed: $($owned.Pid)" }
    } finally { $p.Dispose() }
}
function Invoke-Task([string]$Name, [string]$File, [string[]]$Arguments, [int]$Budget) {
    $budget = [Math]::Min($Budget, [int][Math]::Floor(170 - $watch.Elapsed.TotalSeconds - 8))
    if ($budget -le 0) { throw 'External 170-second total deadline exhausted.' }
    $si = [Diagnostics.ProcessStartInfo]::new($File)
    $si.WorkingDirectory = Join-Path $lane 'desktop'
    $si.UseShellExecute = $false; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
    foreach ($a in $Arguments) { [void]$si.ArgumentList.Add($a) }
    $p = [Diagnostics.Process]::Start($si)
    $identity = @{ Pid=$p.Id; CreationUtcTicks=$p.StartTime.ToUniversalTime().Ticks; Role=$Name }
    [IO.File]::WriteAllText((Join-Path $root "$Name.pid.json"), (ConvertTo-Json $identity))
    $out = [IO.File]::Create((Join-Path $root "$Name.stdout.log")); $err = [IO.File]::Create((Join-Path $root "$Name.stderr.log"))
    $outTask=$p.StandardOutput.BaseStream.CopyToAsync($out); $errTask=$p.StandardError.BaseStream.CopyToAsync($err)
    try {
        $timedOut = -not $p.WaitForExit($budget * 1000)
        if ($timedOut) {
            $data = Join-Path $root 'data'
            if (Test-Path -LiteralPath $data -PathType Container) {
                foreach ($receipt in @(Get-ChildItem -LiteralPath $data -Recurse -File -Filter '*.json' | Where-Object { $_.Directory.Name -eq 'p' -or $_.Name.EndsWith('.pid.json') })) {
                    $stream = [IO.FileStream]::new($receipt.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
                    $reader = [IO.StreamReader]::new($stream)
                    try { $owned = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                    Stop-Identity $owned
                }
            }
            Stop-Identity $identity
        }
        if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask), 2500)) { throw 'Task output EOF unconfirmed; not a pass.' }
        $code = $p.ExitCode
        $commands.Add(@{ name=$Name; executable=$File; arguments=$Arguments; exitCode=$code; timedOut=$timedOut; identity=$identity })
        [IO.File]::WriteAllText((Join-Path $root 'commands.json'), (ConvertTo-Json -InputObject @($commands.ToArray()) -Depth 8))
        Write-Output "$Name exit=$code timedOut=$timedOut"
        if ($timedOut -or $code -ne 0) { throw "$Name exit=$code timedOut=$timedOut. Evidence retained in $root" }
    } finally { $out.Dispose(); $err.Dispose(); $p.Dispose() }
}
try {
    $locks = @(Get-ChildItem -LiteralPath (Join-Path $lane 'desktop/src') -Filter 'packages.lock.json' -Recurse -File | ForEach-Object { @{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    [IO.File]::WriteAllText((Join-Path $root 'production-locks-before.json'), (ConvertTo-Json -InputObject $locks -Depth 4))
    $sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Where-Object { $_.Extension -in '.cs','.csproj','.ps1' -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object { @{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    [IO.File]::WriteAllText((Join-Path $root 'test-source-hashes.json'), (ConvertTo-Json -InputObject $sources -Depth 4))
    $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $project=Join-Path $PSScriptRoot 'WinSight.MediaMetadata.Tests.csproj'
    $build=Join-Path $root 'b'; $packages=Join-Path $root 'packages'
    # Only NEW test projects have no lockfile. Production is never restored/built by this runner.
    Invoke-Task 'restore' $dotnet @('restore',$project,'-r','win-x64','--source',$OfflinePackageSource,'--packages',$packages,'--artifacts-path',$build,'-p:NuGetAudit=false','-p:RestoreIgnoreFailedSources=false','--disable-build-servers') 50
    Invoke-Task 'build' $dotnet @('build',$project,'-r','win-x64','--no-restore','--artifacts-path',$build,'-p:UseSharedCompilation=false','--disable-build-servers','-nr:false') 50
    $exe=@(Get-ChildItem -LiteralPath (Join-Path $build 'bin/WinSight.MediaMetadata.Tests') -Recurse -File -Filter 'WinSight.MediaMetadata.Tests.exe')
    $fake=@(Get-ChildItem -LiteralPath (Join-Path $build 'bin/FakeMediaOpsHost') -Recurse -File -Filter 'FakeMediaOpsHost.exe')
    if ($exe.Count -ne 1 -or $fake.Count -ne 1) { throw 'Exactly one tests/fake executable expected.' }
    $arguments=@('--artifact-root',(Join-Path $root 'data'),'--fake-host',$fake[0].FullName)
    if ($ActualHost) { $arguments+=@('--actual-host',$ActualHost) }
    if ($ClientAssembly) { $arguments+=@('--client-assembly',$ClientAssembly) }
    if ($RequireIntegration) { $arguments+=@('--require-integration','true') }
    Invoke-Task 'tests' $exe[0].FullName $arguments 120
    foreach ($entry in $locks) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Production lock drift: $($entry.path)" } }
    [IO.File]::WriteAllText((Join-Path $root 'production-locks-unchanged.txt'), 'All recorded production lockfile hashes unchanged; no production restore performed.')
    exit 0
} catch {
    [IO.File]::WriteAllText((Join-Path $root 'runner-error.txt'), $_.Exception.ToString())
    [Console]::Error.WriteLine($_.Exception.ToString()); exit 1
}
