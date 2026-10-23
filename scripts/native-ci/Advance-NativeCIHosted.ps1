#requires -Version 7.0
[CmdletBinding()]
param([switch]$Start)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Hosted advance requires a GitHub-hosted runner.' }
foreach ($name in @('GITHUB_WORKSPACE','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','NUGET_PACKAGES','CARGO_HOME')) {
    if (-not [Environment]::GetEnvironmentVariable($name,'Process')) { throw "Missing hosted input: $name" }
}
$root = Join-Path $env:GITHUB_WORKSPACE ".dev/native-migration/native-ci/run-$($env:GITHUB_RUN_ID)-$($env:GITHUB_RUN_ATTEMPT)"
& (Join-Path $PSScriptRoot 'Invoke-NativeCICheckpoint.ps1') -ArtifactRoot $root -NuGetPackageRoot $env:NUGET_PACKAGES -CargoCacheRoot $env:CARGO_HOME -DependencyMode PublicHosted -Resume:(-not $Start)
$code = $LASTEXITCODE
if ($code -eq 2) {
    # A transport continuation is not a validation pass; the workflow's mandatory RequireComplete step closes acceptance.
    Write-Output 'INCOMPLETE native-validation checkpoint (controller exit 2). No CI pass; mandatory final completion gate remains.'
    exit 0
}
exit $code
