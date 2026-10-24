[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [switch]$IncludeApp,
    [switch]$SkipRestore
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$desktop = Join-Path $root 'desktop'
$output = Join-Path $root '.dev/native-migration/verification'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
$started = [DateTime]::UtcNow.ToString('o')
$expected = if ($IncludeApp) { 6 } else { 5 }
function Save-Verification {
    $failures = @($results | Where-Object { $_.exitCode -ne 0 }).Count
    $state = if ($failures -ne 0) { 'failed' } elseif ($results.Count -eq $expected) { 'completed' } else { 'incomplete' }
    [pscustomobject]@{ startedUtc = $started; timeUtc = [DateTime]::UtcNow.ToString('o'); state = $state;
        expectedChecks = $expected; completedChecks = $results.Count; suites = $results.ToArray();
        scope = 'Synthetic fixtures and optional compilation; not model/media/UI/installer parity';
        releaseApproved = $false } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $output 'verification.json') -Encoding utf8
}
Save-Verification
Push-Location $desktop
try {
    $projects = @(
        'tests/WinSight.Storage.Tests/WinSight.Storage.Tests.csproj',
        'tests/WinSight.Indexing.Tests/WinSight.Indexing.Tests.csproj',
        'tests/WinSight.Inference.Tests/WinSight.Inference.Tests.csproj',
        'tests/WinSight.Runtime.Tests/WinSight.Runtime.Tests.csproj',
        'tests/WinSight.Pause.Tests/WinSight.Pause.Tests.csproj'
    )
    foreach ($project in $projects) {
        $name = [IO.Path]::GetFileNameWithoutExtension($project)
        if (-not (Test-Path $project)) { throw "Missing required suite: $project" }
        $log = Join-Path $output "$name.log"
        $arguments = @('run', '--project', $project, '--configuration', $Configuration)
        if ($SkipRestore) { $arguments += '--no-restore' }
        if ($name -eq 'WinSight.Pause.Tests') {
            $fakeHost = Join-Path $desktop "tests/WinSight.Inference.Tests/FakeHost/bin/$Configuration/net11.0/FakeHost.exe"
            if (-not (Test-Path $fakeHost)) { throw "Required explicit synthetic host missing after inference suite: $fakeHost" }
            $fixtures = Join-Path $output ('pause-' + [Guid]::NewGuid().ToString('N'))
            $arguments += @('--', $fakeHost, $fixtures)
        }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @arguments *> $log
        $exit = $LASTEXITCODE
        $results.Add([pscustomobject]@{ name = $name; exitCode = $exit; seconds = $clock.Elapsed.TotalSeconds; log = $log })
        Save-Verification
        if ($exit -ne 0) { throw "Suite $name failed ($exit). See $log" }
    }
    if ($IncludeApp) {
        $log = Join-Path $output 'WinSight.App.build.log'
        $arguments = @('build', 'src/WinSight.App/WinSight.App.csproj', '--configuration', $Configuration, '--nologo')
        if ($SkipRestore) { $arguments += '--no-restore' }
        & dotnet @arguments *> $log
        $exit = $LASTEXITCODE
        $results.Add([pscustomobject]@{ name = 'App build (not UI behavior)'; exitCode = $exit; log = $log })
        Save-Verification
        if ($exit -ne 0) { throw "App build failed ($exit). See $log" }
    }
}
finally {
    Pop-Location
    Save-Verification
}
Write-Output "Synthetic suites passed. Evidence: $output"
