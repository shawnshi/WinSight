[CmdletBinding()]
param([string]$StageDirectory, [string]$DataDirectory, [string]$EvidenceDirectory, [switch]$RequirePauseProbe)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Resolve-TaskPath([string]$Value, [string]$Default) {
    if ($Value -and -not [IO.Path]::IsPathFullyQualified($Value)) { throw 'Explicit verification paths must be absolute.' }
    $path = [IO.Path]::GetFullPath($(if ($Value) { $Value } else { Join-Path $root $Default })).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $boundary = [IO.Path]::GetFullPath((Join-Path $root '.dev/native-migration'))
    if (-not $path.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Verification paths must remain under the task .dev/native-migration boundary.' }
    for ($parent = $path; $parent.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase); $parent = Split-Path $parent -Parent) {
        if ((Test-Path $parent) -and ((Get-Item $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Refusing verification reparse point: $parent" }
    }
    return $path
}
$stage = Resolve-TaskPath $StageDirectory '.dev/native-migration/release'
$app = Join-Path $stage 'app'
$data = Resolve-TaskPath $DataDirectory '.dev/native-migration/published-ui-smoke'
$logs = Resolve-TaskPath $EvidenceDirectory '.dev/native-migration/verification'
if ($RequirePauseProbe -and (Test-Path $data)) { throw 'The deployment pause probe requires a fresh task data directory.' }
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$manifest = Get-Content (Join-Path $stage 'release-manifest.json') -Raw | ConvertFrom-Json
if (-not $manifest.appOnlyPreview -or $manifest.releaseApproved) { throw 'Expected an explicitly unapproved app-only task preview.' }
foreach ($required in 'WinSight.App.exe','WinSight.App.runtimeconfig.json','coreclr.dll','Views/MainWindow.xbf','WinSight.App.pri') {
    if (-not (Test-Path (Join-Path $app $required) -PathType Leaf)) { throw "Incomplete preview payload: $required" }
}
$unexpected = @($manifest.files | Where-Object { $_.path -match '(?i)(FakeHost|\.Tests\.|swiftCore\.dll|winswiftrt|tauri)' })
if ($unexpected.Count -ne 0) { throw 'Test/legacy payload appeared in the native preview.' }
foreach ($file in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $stage $file.path))
    if (-not $path.StartsWith($stage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release manifest escapes its task directory.' }
    if (-not (Test-Path $path -PathType Leaf) -or (Get-Item $path).Length -ne $file.bytes -or
        (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Payload hash/size mismatch: $($file.path)" }
}
$psi = [Diagnostics.ProcessStartInfo]::new((Join-Path $app 'WinSight.App.exe'))
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.ArgumentList.Add('--data-dir')
$psi.ArgumentList.Add($data)
$psi.ArgumentList.Add('--smoke-test')
$psi.Environment['DOTNET_ROOT'] = Join-Path $root '.dev/native-migration/nonexistent-dotnet-root'
$psi.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$process = [Diagnostics.Process]::Start($psi)
try {
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Published preview smoke timed out; owned process tree terminated.'
    }
    $exit = $process.ExitCode
    $outputText = $stdout.GetAwaiter().GetResult()
    [IO.File]::WriteAllText((Join-Path $logs 'published.smoke.stdout.log'), $outputText)
    [IO.File]::WriteAllText((Join-Path $logs 'published.smoke.stderr.log'), $stderr.GetAwaiter().GetResult())
    $dataUsed = Test-Path (Join-Path $data 'index/index.sqlite')
    if ($exit -ne 0 -or -not $dataUsed) { throw "Published preview smoke failed: exit=$exit requestedDataUsed=$dataUsed" }
    $pausePassed = $outputText.Contains('[SMOKE-PAUSE]')
    $runtimeLines = @($outputText -split '\r?\n' | Where-Object { $_.StartsWith('[SMOKE-RUNTIME] ') })
    $runtime = $null
    $localCore = $null
    $localWinUI = $null
    if ($runtimeLines.Count -eq 1) {
        $runtime = $runtimeLines[0].Substring('[SMOKE-RUNTIME] '.Length) | ConvertFrom-Json
        $prefix = [IO.Path]::GetFullPath($app) + [IO.Path]::DirectorySeparatorChar
        $localCore = $runtime.coreLib.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
            $runtime.modules.'coreclr.dll'.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
        $localWinUI = $runtime.modules.'Microsoft.UI.Xaml.dll'.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($RequirePauseProbe -and (-not $pausePassed -or -not $localCore -or -not $localWinUI)) {
        throw 'Published pause/runtime probe did not prove actual main/tray state and local CoreCLR/WinUI module loading.'
    }
    [pscustomobject]@{
        exitCode = $exit; localCoreClr = $localCore; localWinUI = $localWinUI; pauseResumeProbe = $pausePassed;
        runtimeObserved = $runtime; requestedTaskDataDirectoryUsed = $dataUsed;
        payloadHashCheck = 'passed'; fileCount = $manifest.files.Count; appOnlyPreview = $true;
        realModelsIncluded = $false; releaseApproved = $false;
        cleanMachineInstallationVerified = $false; pixelAcceptance = $false
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $logs 'published.smoke.result.json') -Encoding utf8
}
finally { $process.Dispose() }
Write-Output 'Self-contained preview hash and actual smoke checks passed; not a clean-machine or installer approval.'
