[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][string]$DataRoot,
    [Parameter(Mandatory)][ValidateSet('Verification','Production')][string]$Mode,
    [Parameter(Mandatory)][string]$ManifestSha256,
    [string]$CompilerPath,
    [string]$CompilerSha256,
    [switch]$ValidateOnly,
    [ValidateSet('None','AfterJournal','AfterFirstCopy','BeforeCommit','AfterCommit')][string]$VerificationFailurePoint = 'None'
)
$ErrorActionPreference = 'Stop'
$engine = Join-Path (Split-Path $PSScriptRoot -Parent) 'desktop/installer'
Import-Module (Join-Path $engine 'NativeInstaller.psm1') -Force
$stage = Resolve-NativeRoot $StageDirectory
$output = Resolve-NativeRoot $OutputDirectory
$install = Resolve-NativeRoot $InstallRoot
$data = Resolve-NativeRoot $DataRoot
$roots = @($stage, $output, $install, $data)
for ($i = 0; $i -lt $roots.Count; $i++) {
    for ($j = $i + 1; $j -lt $roots.Count; $j++) { Assert-NativeSeparate $roots[$i] $roots[$j] }
}
if ($Mode -eq 'Production' -and $VerificationFailurePoint -ne 'None') { throw 'Production cannot contain failure injection.' }
$manifest = Read-NativeStage $stage $ManifestSha256 $Mode
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must be a new dedicated task directory.' }
if ($ValidateOnly) {
    [pscustomobject]@{ validated = $true; compiled = $false; mode = $Mode; manifestSha256 = $ManifestSha256; files = @($manifest.files).Count; installRoot = $install; dataRoot = $data } | ConvertTo-Json
    return
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Official Inno compiler is required; no compiler is downloaded or discovered.' }
$compiler = Resolve-NativeRoot $CompilerPath
if ($CompilerSha256 -cnotmatch '^[0-9a-f]{64}$' -or (Get-NativeHash $compiler) -cne $CompilerSha256) { throw 'Compiler hash does not match the independently approved input.' }
$owned = @('NativeInstaller.psm1','Invoke-NativeInstaller.ps1','WinSight.Native.iss','Invoke-NativeCompilerProcess.ps1',
    'Invoke-NativeDeployment.ps1','Invoke-NativeDeployWorker.ps1','NativeDeploymentJob.psm1')
$sourceHashes = @{}
foreach ($name in $owned) { $sourceHashes[$name] = Get-NativeHash (Join-Path $engine $name) }
[IO.Directory]::CreateDirectory($output) | Out-Null
$config = Join-Path $output 'candidate-config.iss'
$include = Join-Path $output 'payload-files.iss'
$definitions = [ordered]@{
    CandidateMode = $Mode; BoundInstallRoot = $install; BoundDataRoot = $data;
    CandidateOutput = $output; EngineDirectory = $engine;
    ReleaseManifest = (Join-Path $stage 'release-manifest.json'); ManifestHash = $ManifestSha256;
    PayloadInclude = $include; FailurePoint = $VerificationFailurePoint
}
$lines = foreach ($key in $definitions.Keys) { '#define {0} "{1}"' -f $key, $definitions[$key] }
[IO.File]::WriteAllLines($config, [string[]]$lines, [Text.UTF8Encoding]::new($false))
$payload = foreach ($file in $manifest.files) {
    $source = Join-Path $stage $file.path
    $relativeDirectory = [IO.Path]::GetDirectoryName($file.path.Replace('/', '\'))
    'Source: "{0}"; DestDir: "{{tmp}}\winsight-stage\{1}"; Flags: ignoreversion' -f $source, $relativeDirectory
}
[IO.File]::WriteAllLines($include, [string[]]$payload, [Text.UTF8Encoding]::new($false))
$log = Join-Path $output 'compile.log'
# No command fallback, no install, no dependency resolution. Caller supplies official ISCC.
$compilerArguments = '/DGeneratedConfig="' + $config + '" "' + (Join-Path $engine 'WinSight.Native.iss') + '"'
$null = & (Join-Path $engine 'Invoke-NativeCompilerProcess.ps1') -CompilerPath $compiler -CompilerArguments $compilerArguments -LogPath $log
$null = Read-NativeStage $stage $ManifestSha256 $Mode
if ((Get-NativeHash $compiler) -cne $CompilerSha256) { throw 'Compiler changed during compilation.' }
foreach ($name in $owned) { if ((Get-NativeHash (Join-Path $engine $name)) -cne $sourceHashes[$name]) { throw 'Installer source changed during compilation.' } }
$setup = Join-Path $output "WinSight-Native-$Mode-Candidate.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Compiler did not produce the expected candidate.' }
[pscustomobject]@{ schema = 1; releaseApproved = $false; mode = $Mode; installRoot = $install; dataRoot = $data;
    setupSha256 = Get-NativeHash $setup; manifestSha256 = $ManifestSha256; compilerSha256 = $CompilerSha256; sourceHashes = $sourceHashes; verificationFailurePoint = $VerificationFailurePoint
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'installer-build.json') -Encoding utf8
Write-Output "Candidate compiled only; no setup executed: $setup"
