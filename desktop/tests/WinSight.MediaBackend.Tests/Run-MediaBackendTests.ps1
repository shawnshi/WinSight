[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NewArtifactRoot,
    [Parameter(Mandatory)][string]$OfflinePackageSource,
    [Parameter(Mandatory)][string]$ActualInfrastructureAssembly,
    [string]$ActualHost,
    [switch]$RequireIntegration
)
$ErrorActionPreference='Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$lane=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$prefix=[IO.Path]::GetFullPath((Join-Path $lane '.dev'))+[IO.Path]::DirectorySeparatorChar
if(-not [IO.Path]::IsPathFullyQualified($NewArtifactRoot) -or (Test-Path -LiteralPath $NewArtifactRoot) -or -not [IO.Path]::GetFullPath($NewArtifactRoot).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Explicit NEW short lane .dev artifact root required'}
$cache=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
if([IO.Path]::GetFullPath($OfflinePackageSource) -ne [IO.Path]::GetFullPath($cache) -or -not(Test-Path -LiteralPath $cache -PathType Container)){throw 'Exact existing public cache required, not a directory named Public'}
$ActualInfrastructureAssembly=[IO.Path]::GetFullPath($ActualInfrastructureAssembly)
$core=Join-Path ([IO.Path]::GetDirectoryName($ActualInfrastructureAssembly)) 'WinSight.Core.dll'
foreach($file in @($ActualInfrastructureAssembly,$core)){if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw 'Actual production Infrastructure/Core DLLs required'}}
$main=$lane
$approved=Join-Path $main '.dev/native-migration/native-stack-gap/n2b-media-ops/verification/prod-1/b/bin/WinSight.InferenceHost/release_win-x64/WinSight.InferenceHost.exe'
if($ActualHost){$ActualHost=[IO.Path]::GetFullPath($ActualHost);if($ActualHost -ne [IO.Path]::GetFullPath($approved) -or -not(Test-Path -LiteralPath $ActualHost -PathType Leaf)){throw 'Only explicit approved main Host is allowed'}}
# Reuse the ambient existing CLI home. Never redirect first use into a fresh directory.
$cliHome=if($env:DOTNET_CLI_HOME){$env:DOTNET_CLI_HOME}else{$env:USERPROFILE}
if(-not(Test-Path -LiteralPath (Join-Path $cliHome '.dotnet') -PathType Container)){throw 'Existing CLI home required; no first-use home will be created'}
$root=[IO.Path]::GetFullPath($NewArtifactRoot);[void][IO.Directory]::CreateDirectory($root)
$watch=[Diagnostics.Stopwatch]::StartNew();$commands=[Collections.Generic.List[object]]::new();$exitCode=1
function Save([string]$Name,$Value){[IO.File]::WriteAllText((Join-Path $root $Name),(ConvertTo-Json -InputObject $Value -Depth 14))}
function Hash([string]$Path){@{path=$Path;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}}
function Locks { @(Get-ChildItem -LiteralPath (Join-Path $lane 'desktop/src') -Recurse -File -Filter 'packages.lock.json' | ForEach-Object {Hash $_.FullName}) }
function Stop-Identity($id){
    try{$p=[Diagnostics.Process]::GetProcessById([int]$id.Pid)}catch [ArgumentException]{return}
    try{if(-not $p.HasExited -and $p.StartTime.ToUniversalTime().Ticks -eq [long]$id.CreationUtcTicks){$p.Kill();if(-not $p.WaitForExit(3000)){throw 'Exact owned PID exit unconfirmed'}}}finally{$p.Dispose()}
}
function Invoke-Owned([string]$Name,[string]$Exe,[string[]]$Arguments,[int]$Budget){
    $budget=[Math]::Min($Budget,[int][Math]::Floor(170-$watch.Elapsed.TotalSeconds-8));if($budget -le 0){throw '170 second runner budget exhausted'}
    $s=[Diagnostics.ProcessStartInfo]::new($Exe);$s.WorkingDirectory=$lane;$s.UseShellExecute=$false;$s.RedirectStandardOutput=$true;$s.RedirectStandardError=$true
    $s.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE']='false';$s.Environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH']='false';$s.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    foreach($a in $Arguments){[void]$s.ArgumentList.Add($a)}
    $p=[Diagnostics.Process]::Start($s);$id=@{Pid=$p.Id;CreationUtcTicks=$p.StartTime.ToUniversalTime().Ticks};Save "$Name.pid.json" $id
    $o=[IO.File]::Create((Join-Path $root "$Name.stdout.log"));$e=[IO.File]::Create((Join-Path $root "$Name.stderr.log"));$ot=$p.StandardOutput.BaseStream.CopyToAsync($o);$et=$p.StandardError.BaseStream.CopyToAsync($e)
    try{
        $timedOut=-not $p.WaitForExit($budget*1000)
        if($timedOut){
            $data=Join-Path $root 'data'
            if(Test-Path -LiteralPath $data){foreach($f in @(Get-ChildItem -LiteralPath $data -Recurse -File -Filter '*.pid.json')){
                $stream=[IO.FileStream]::new($f.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::Read -bor [IO.FileShare]::Delete));$reader=[IO.StreamReader]::new($stream)
                try{$child=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};Stop-Identity $child
            }};Stop-Identity $id
        }
        if(-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($ot,$et),2500)){throw 'Output EOF unconfirmed'}
        $code=$p.ExitCode;$commands.Add(@{name=$Name;executable=$Exe;arguments=$Arguments;exitCode=$code;timedOut=$timedOut;identity=$id});Save 'commands.json' @($commands.ToArray());Write-Output "$Name exit=$code timedOut=$timedOut"
        $o.Dispose();$e.Dispose()
        $all=[IO.File]::ReadAllText((Join-Path $root "$Name.stdout.log"))+[IO.File]::ReadAllText((Join-Path $root "$Name.stderr.log"))
        if($all -match '(?i)Installed an ASP.NET Core HTTPS development certificate'){throw 'ENV-CERT-RECURRENCE: STOP validation; certificate status unknown, no inspection or cleanup'}
        if($timedOut){throw "$Name exceeded deadline"};if($code -ne 0){$script:exitCode=$code;throw "$Name nonzero=$code; preserved failure, no retry"}
    }finally{$o.Dispose();$e.Dispose();$p.Dispose()}
}
$before=Locks;Save 'production-locks-before.json' $before
$stagedBefore=@(git -C $lane diff --cached --raw);if($LASTEXITCODE -ne 0){exit $LASTEXITCODE};Save 'inherited-staged-before.json' $stagedBefore
Save 'environment-boundary.json' @{cliHome=$cliHome;reuseOnly=$true;certificateStatus='UNKNOWN; no certificate-store inspection';network='none';productionRestore='not run: explicit prebuilt assembly';controls=@{DOTNET_GENERATE_ASPNET_CERTIFICATE='false';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false';DOTNET_CLI_TELEMETRY_OPTOUT='1'}}
try{
    if($stagedBefore.Count -ne 224){throw 'Expected 224 inherited staged deletions; stop for baseline drift'}
    $expected=@{'WinSight.Core'='CD97F644C24BB2CD8C96D92C519036A65133AB22CAFC294D07B527626AD20E84';'WinSight.Infrastructure'='0CFE344C6D6B7ED2F8F6DA0189F2A9DE26565C8325A015696FD18C73F8BAFED5'}
    foreach($n in $expected.Keys){$file=Join-Path $lane "desktop/src/$n/packages.lock.json";if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected[$n]){throw 'Source lock drift: no automatic repair in test lane'}}
    Save 'test-source-sha256.json' @(Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File | Where-Object {$_.Extension -in '.cs','.csproj','.ps1','.props','.md' -and $_.FullName -notmatch '[\/](obj|bin|\.dev)[\/]'} | ForEach-Object {Hash $_.FullName})
    Save 'actual-infrastructure.json' (Hash $ActualInfrastructureAssembly);Save 'actual-core.json' (Hash $core)
    Save 'main-frozen-baseline.json' (Hash (Join-Path $main '.dev/native-migration/native-stack-gap/n2b-runtime/implementation-base/manifest.json'))
    if($ActualHost){Save 'actual-host.json' (Hash $ActualHost)}
    $dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
    $project=Join-Path $PSScriptRoot 'WinSight.MediaBackend.Tests.csproj';$b=Join-Path $root 'b'
    $refs=@("-p:ActualInfrastructureAssembly=$ActualInfrastructureAssembly","-p:ActualCoreAssembly=$core")
    # Only NEW test projects restore; production sources/locks are never restored or built here.
    Invoke-Owned 'restore' $dotnet (@('restore',$project,'-r','win-x64','--source',$cache,'--packages',(Join-Path $root 'packages'),'--artifacts-path',$b,'-p:NuGetAudit=false','-p:RestoreIgnoreFailedSources=false','--disable-build-servers')+$refs) 45
    Invoke-Owned 'build' $dotnet (@('build',$project,'-c','Release','-r','win-x64','--no-restore','--artifacts-path',$b,'-p:UseSharedCompilation=false','--disable-build-servers','-nr:false')+$refs) 50
    $test=@(Get-ChildItem -LiteralPath (Join-Path $b 'bin/WinSight.MediaBackend.Tests') -Recurse -File -Filter 'WinSight.MediaBackend.Tests.exe');$fake=@(Get-ChildItem -LiteralPath (Join-Path $b 'bin/CompositeHost') -Recurse -File -Filter 'CompositeHost.exe')
    if($test.Count -ne 1 -or $fake.Count -ne 1){throw 'Exactly one test/composite executable required'}
    Save 'binary-sha256.json' @(Get-ChildItem -LiteralPath (Join-Path $b 'bin') -Recurse -File | ForEach-Object {Hash $_.FullName})
    $arguments=@('--artifact-root',(Join-Path $root 'data'),'--fake-host',$fake[0].FullName)
    if($ActualHost){$arguments+=@('--actual-host',$ActualHost)};if($RequireIntegration){$arguments+=@('--require-integration','true')}
    Invoke-Owned 'tests' $test[0].FullName $arguments 110
    $exitCode=0
}catch{Save 'runner-error.json' @{error=$_.Exception.ToString()};[Console]::Error.WriteLine($_.Exception.Message)}
finally{
    $after=Locks;Save 'production-locks-after.json' $after
    $same=(ConvertTo-Json -InputObject $before -Compress) -ceq (ConvertTo-Json -InputObject $after -Compress)
    $stagedAfter=@(git -C $lane diff --cached --raw);if($LASTEXITCODE -ne 0){$exitCode=1};Save 'inherited-staged-after.json' $stagedAfter
    $stageSame=($stagedBefore -join "`n") -ceq ($stagedAfter -join "`n")
    Save 'preservation.json' @{productionLocksUnchanged=$same;inheritedStagedUnchanged=$stageSame;stagedCount=$stagedAfter.Count;noNewStagedFiles=$stageSame;seconds=$watch.Elapsed.TotalSeconds}
    if(-not $same -or -not $stageSame){$exitCode=1;[Console]::Error.WriteLine('Preservation violated; no automatic recovery')}
}
exit $exitCode
