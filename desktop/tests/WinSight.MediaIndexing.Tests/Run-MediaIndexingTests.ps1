[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NewArtifactRoot,
    [Parameter(Mandatory)][string]$OfflinePackageSource,
    [string]$ActualHost,
    [string]$ActualInfrastructureAssembly,
    [switch]$RequireIntegration
)
$ErrorActionPreference = 'Stop'
$lane = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$prefix = [IO.Path]::GetFullPath((Join-Path $lane '.dev')) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::IsPathFullyQualified($NewArtifactRoot) -or (Test-Path -LiteralPath $NewArtifactRoot) -or -not [IO.Path]::GetFullPath($NewArtifactRoot).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Explicit NEW artifact directory under this lane .dev required.' }
$cache = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
if (-not [IO.Path]::IsPathFullyQualified($OfflinePackageSource) -or -not (Test-Path -LiteralPath $OfflinePackageSource -PathType Container) -or [IO.Path]::GetFullPath($OfflinePackageSource) -ne [IO.Path]::GetFullPath($cache)) { throw 'Only the exact installed public package cache is authorized.' }
foreach ($file in @($ActualHost,$ActualInfrastructureAssembly)) { if ($file -and (-not [IO.Path]::IsPathFullyQualified($file) -or -not (Test-Path -LiteralPath $file -PathType Leaf))) { throw 'Explicit actual Host/Infrastructure DLL must exist.' } }
if ($ActualHost) { $ActualHost = [IO.Path]::GetFullPath($ActualHost) }
if ($ActualInfrastructureAssembly) { $ActualInfrastructureAssembly = [IO.Path]::GetFullPath($ActualInfrastructureAssembly) }
$root = [IO.Path]::GetFullPath($NewArtifactRoot); [void][IO.Directory]::CreateDirectory($root)
$watch = [Diagnostics.Stopwatch]::StartNew(); $commands = [Collections.Generic.List[object]]::new(); $exitCode = 1
function Save-Json([string]$Name, $Value) { [IO.File]::WriteAllText((Join-Path $root $Name), (ConvertTo-Json -InputObject $Value -Depth 12)) }
function Locks { @(Get-ChildItem -LiteralPath (Join-Path $lane 'desktop/src') -Recurse -File -Filter 'packages.lock.json' | ForEach-Object { @{ path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } }) }
function Stop-Identity($id) {
    try { $p=[Diagnostics.Process]::GetProcessById([int]$id.Pid) } catch [ArgumentException] { return }
    try { if (-not $p.HasExited -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$id.CreationUtcTicks) { $p.Kill(); if (-not $p.WaitForExit(3000)) { throw 'Owned exact PID exit unconfirmed' } } } finally { $p.Dispose() }
}
function Invoke-Owned([string]$Name,[string]$Exe,[string[]]$Arguments,[int]$Budget) {
    $budget=[Math]::Min($Budget,[int][Math]::Floor(170-$watch.Elapsed.TotalSeconds-8)); if($budget -le 0){throw '170-second runner budget exhausted'}
    $si=[Diagnostics.ProcessStartInfo]::new($Exe); $si.WorkingDirectory=$lane; $si.UseShellExecute=$false; $si.RedirectStandardOutput=$true; $si.RedirectStandardError=$true
    foreach($a in $Arguments){[void]$si.ArgumentList.Add($a)}
    $p=[Diagnostics.Process]::Start($si); $id=@{Pid=$p.Id;CreationUtcTicks=$p.StartTime.ToUniversalTime().Ticks}; Save-Json "$Name.pid.json" $id
    $stdout=[IO.File]::Create((Join-Path $root "$Name.stdout.log")); $stderr=[IO.File]::Create((Join-Path $root "$Name.stderr.log"))
    $outTask=$p.StandardOutput.BaseStream.CopyToAsync($stdout); $errTask=$p.StandardError.BaseStream.CopyToAsync($stderr)
    try {
        $timedOut=-not $p.WaitForExit($budget*1000)
        if($timedOut){
            $data=Join-Path $root 'data'
            if(Test-Path -LiteralPath $data){ foreach($f in @(Get-ChildItem -LiteralPath $data -Recurse -File -Filter '*.json' | Where-Object {$_.Directory.Name -eq 'p' -or $_.Name.EndsWith('.pid.json')})) {
                $s=[IO.FileStream]::new($f.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::Read -bor [IO.FileShare]::Delete)); $r=[IO.StreamReader]::new($s)
                try{$child=$r.ReadToEnd() | ConvertFrom-Json}finally{$r.Dispose()}; Stop-Identity $child
            } }; Stop-Identity $id
        }
        if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($outTask,$errTask),2500)){throw 'Output EOF unconfirmed'}
        $code=$p.ExitCode; $commands.Add(@{name=$Name;executable=$Exe;arguments=$Arguments;exitCode=$code;timedOut=$timedOut;identity=$id}); Save-Json 'commands.json' @($commands.ToArray()); Write-Output "$Name exit=$code timedOut=$timedOut"
        if($timedOut){throw "$Name deadline exceeded"}; if($code -ne 0){$script:exitCode=$code;throw "$Name nonzero=$code; preserve failed/pending evidence"}
    }finally{$stdout.Dispose();$stderr.Dispose();$p.Dispose()}
}
$before=Locks; Save-Json 'production-locks-before.json' $before
$stagedBefore=@(git -C $lane diff --cached --raw); if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}; Save-Json 'inherited-staged-before.json' $stagedBefore
try {
    $source=@(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Where-Object {$_.Extension -in '.cs','.csproj','.ps1','.md','.props','.targets' -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]'} | ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}}); Save-Json 'test-source-sha256.json' $source
    foreach($entry in @(@{name='actual-host';path=$ActualHost},@{name='actual-infrastructure';path=$ActualInfrastructureAssembly})){if($entry.path){Save-Json ($entry.name+'.json') @{path=$entry.path;sha256=(Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash}}}
    $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $project=Join-Path $PSScriptRoot 'WinSight.MediaIndexing.Tests.csproj'; $build=Join-Path $root 'b'
    # Both projects are NEW test-only executables. No production restore/build and no global lock override.
    Invoke-Owned 'restore' $dotnet @('restore',$project,'-r','win-x64','--source',$OfflinePackageSource,'--packages',(Join-Path $root 'packages'),'--artifacts-path',$build,'-p:NuGetAudit=false','-p:RestoreIgnoreFailedSources=false','--disable-build-servers') 45
    Invoke-Owned 'build' $dotnet @('build',$project,'-r','win-x64','--no-restore','--artifacts-path',$build,'-p:UseSharedCompilation=false','--disable-build-servers','-nr:false') 45
    $tests=@(Get-ChildItem -LiteralPath (Join-Path $build 'bin/WinSight.MediaIndexing.Tests') -Recurse -File -Filter 'WinSight.MediaIndexing.Tests.exe'); $fake=@(Get-ChildItem -LiteralPath (Join-Path $build 'bin/FakeNativeWorker') -Recurse -File -Filter 'FakeNativeWorker.exe')
    if($tests.Count -ne 1 -or $fake.Count -ne 1){throw 'Exactly one test/fake executable required'}
    $arguments=@('--artifact-root',(Join-Path $root 'data'),'--fake-host',$fake[0].FullName)
    if($ActualHost){$arguments+=@('--actual-host',$ActualHost)}; if($ActualInfrastructureAssembly){$arguments+=@('--actual-infrastructure-assembly',$ActualInfrastructureAssembly)}; if($RequireIntegration){$arguments+=@('--require-integration','true')}
    Invoke-Owned 'tests' $tests[0].FullName $arguments 130
    $exitCode=0
}catch{Save-Json 'runner-error.json' @{error=$_.Exception.ToString()}; [Console]::Error.WriteLine($_.Exception.Message)}
finally {
    $after=Locks; Save-Json 'production-locks-after.json' $after
    $drift=@($before | Where-Object {$entry=$_; -not @($after | Where-Object {$_.path -eq $entry.path -and $_.sha256 -eq $entry.sha256}).Count})
    $stagedAfter=@(git -C $lane diff --cached --raw); if($LASTEXITCODE -ne 0){$exitCode=1}; Save-Json 'inherited-staged-after.json' $stagedAfter
    $stagedSame=($stagedBefore -join "`n") -ceq ($stagedAfter -join "`n")
    Save-Json 'preservation.json' @{productionLocksUnchanged=($drift.Count -eq 0);inheritedStagedUnchanged=$stagedSame;stagedCount=$stagedAfter.Count;noNewStagedFiles=$stagedSame;seconds=$watch.Elapsed.TotalSeconds}
    if($drift.Count -gt 0 -or -not $stagedSame){$exitCode=1;[Console]::Error.WriteLine('Preservation violated; no automatic recovery attempted')}
    $artifacts=@(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {$_.FullName -notmatch '[\\/]packages[\\/]' -and $_.Name -ne 'artifact-sha256.json'} | ForEach-Object {@{path=$_.FullName;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}}); Save-Json 'artifact-sha256.json' $artifacts
}
exit $exitCode
