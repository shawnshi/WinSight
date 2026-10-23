[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('ui','storage','indexing','inference','backend_pause','ui_pause','native_media','native_media_tests','native_media_guard','native_media_tail','native_media_process','native_media_process_tests','native_media_process_guard','native_media_process_oracles','native_media_ops_host','native_media_ops_client','native_media_ops_tests','native_media_sampling','native_media_index_consumer','native_media_index_tests','native_media_index_gap_tests','native_media_index_redaction','native_media_index_redaction_tests','native_media_runtime','native_media_backend_tests')][string]$Lane)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root ".dev/native-migration/lanes/$Lane"
$paths = switch ($Lane) {
    'ui' { @('desktop/src/WinSight.App','desktop/tests/WinSight.Ui.Harness','desktop/ui-handoff.md') }
    'storage' { @('desktop/src/WinSight.Infrastructure/Storage','desktop/src/WinSight.Infrastructure/Search',
        'desktop/tests/WinSight.Storage.Tests','desktop/storage-handoff.md') }
    'indexing' { @('desktop/src/WinSight.Infrastructure/Indexing','desktop/src/WinSight.Infrastructure/Extraction',
        'desktop/tests/WinSight.Indexing.Tests','desktop/indexing-handoff.md') }
    'inference' { @('desktop/src/WinSight.Infrastructure/Inference','desktop/tests/WinSight.Inference.Tests','desktop/inference-handoff.md') }
    'backend_pause' { @('desktop/src/WinSight.Infrastructure/Runtime','desktop/tests/WinSight.Pause.Tests','desktop/p4-backend-handoff.md') }
    'ui_pause' { @('desktop/src/WinSight.App','desktop/tests/WinSight.Ui.Harness','desktop/p4-ui-handoff.md') }
    'native_media' { @('inference/WinSight.InferenceHost/Program.cs','inference/WinSight.InferenceHost/NativeMediaCommand.cs','inference/native-media-handoff.md') }
    'native_media_tests' { @('desktop/tests/WinSight.NativeMedia.Tests','desktop/native-media-tests-handoff.md') }
    'native_media_guard' { @('inference/WinSight.InferenceHost/NativeMediaCommand.cs','inference/WinSight.InferenceHost/NativeMediaDrive*.cs','inference/native-media-guard-handoff.md') }
    'native_media_tail' { @('desktop/tests/WinSight.NativeMedia.Tests','desktop/native-media-review-tests-handoff.md') }
    'native_media_process' { @('desktop/src/WinSight.Infrastructure/Media','desktop/n2-process-handoff.md') }
    'native_media_process_tests' { @('desktop/tests/WinSight.MediaProcess.Tests','desktop/n2-process-tests-handoff.md') }
    'native_media_process_guard' { @('desktop/src/WinSight.Infrastructure/Media','desktop/n2-process-guard-handoff.md') }
    'native_media_process_oracles' { @('desktop/tests/WinSight.MediaProcess.Tests','desktop/n2-process-review-tests-handoff.md') }
    'native_media_ops_host' { @('inference/WinSight.InferenceHost/Program.cs','inference/WinSight.InferenceHost/NativeMediaCommand.cs','inference/WinSight.InferenceHost/NativeMediaInspect.cs','inference/WinSight.InferenceHost/NativeMediaSoundtrack.cs','inference/media-ops-handoff.md') }
    'native_media_ops_client' { @('desktop/src/WinSight.Infrastructure/Media','desktop/media-ops-client-handoff.md') }
    'native_media_ops_tests' { @('desktop/tests/WinSight.MediaMetadata.Tests','desktop/media-ops-tests-handoff.md') }
    'native_media_sampling' { @('desktop/src/WinSight.Infrastructure/Media/Sampling','desktop/media-sampling-handoff.md') }
    'native_media_index_consumer' { @('desktop/src/WinSight.Infrastructure/Indexing/IndexingService.cs','desktop/src/WinSight.Infrastructure/Runtime/TestAccess.cs','desktop/media-index-consumer-handoff.md') }
    'native_media_index_tests' { @('desktop/tests/WinSight.MediaIndexing.Tests','desktop/media-index-tests-handoff.md') }
    'native_media_index_gap_tests' { @('desktop/tests/WinSight.MediaIndexing.Tests','desktop/media-index-gap-tests-handoff.md') }
    'native_media_index_redaction' { @('desktop/src/WinSight.Infrastructure/Indexing/IndexingService.cs','desktop/media-index-redaction-handoff.md') }
    'native_media_index_redaction_tests' { @('desktop/tests/WinSight.MediaIndexing.Tests','desktop/media-index-redaction-tests-handoff.md') }
    'native_media_runtime' { @('desktop/src/WinSight.Infrastructure/Runtime/DesktopBackend.cs','desktop/media-runtime-handoff.md') }
    'native_media_backend_tests' { @('desktop/tests/WinSight.MediaBackend.Tests','desktop/media-backend-tests-handoff.md') }
}
$output = Join-Path $root '.dev/native-migration/integration'
$receipt = Join-Path $output "$Lane.json"
$previous = @{}
if (Test-Path $receipt) {
    (Get-Content $receipt -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('backend_pause','ui_pause')) {
    $baseline = Join-Path $root '.dev/native-migration/p4-pause/baseline/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'P4 pre-change baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media','native_media_tests')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/implementation-base/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'Native-media pre-change baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media_guard','native_media_tail')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n1-review-remediation/baseline/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N1 review-remediation baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media_process','native_media_process_tests')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n2-process/baseline/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N2 process pre-change baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media_process_guard','native_media_process_oracles')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n2-process/review-remediation/baseline/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N2 review-remediation baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media_ops_host','native_media_ops_client','native_media_ops_tests')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n2b-media-ops/implementation-base/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N2b media-ops baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media_sampling','native_media_index_consumer','native_media_index_tests')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n2b-indexing/implementation-base/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N2b indexing baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -eq 'native_media_index_gap_tests') {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n2b-indexing/test-completion/baseline/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N2b test-completion baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media_index_redaction','native_media_index_redaction_tests')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n2b-indexing/review-remediation/implementation-base/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N2b redaction-remediation baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
} elseif ($Lane -in @('native_media_runtime','native_media_backend_tests')) {
    $baseline = Join-Path $root '.dev/native-migration/native-stack-gap/n2b-runtime/implementation-base/manifest.json'
    if (-not (Test-Path $baseline)) { throw 'N2b runtime baseline missing; refusing integration.' }
    (Get-Content $baseline -Raw | ConvertFrom-Json).files | ForEach-Object { $previous[$_.path] = $_.sha256 }
}
$history = Join-Path $output ("history/$Lane-" + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffff'))
$selected = [System.Collections.Generic.List[object]]::new()
foreach ($relative in $paths) {
    $path = Join-Path $source $relative
    if (-not (Test-Path $path)) { continue }
    $files = if ((Get-Item $path).PSIsContainer) { @(Get-ChildItem $path -File -Recurse) } else { @(Get-Item $path) }
    foreach ($file in $files) {
        $name = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\','/')
        if ($name -match '/(bin|obj|TestResults|synthetic-runs|\.?artifacts)/' -or $name -in @('desktop/src/WinSight.App/DesktopRuntime.cs','desktop/src/WinSight.App/WinSight.App.csproj','desktop/tests/WinSight.Ui.Harness/ui-harness-report.json')) { continue }
        if ($Lane -in @('backend_pause','ui_pause','native_media_tests','native_media_tail','native_media_process_tests','native_media_process_oracles','native_media_ops_tests','native_media_index_tests','native_media_index_gap_tests','native_media_index_redaction_tests','native_media_backend_tests') -and ($file.Extension -in @('.log','.tmp','.bak') -or $file.Name -like 'p4-*.json')) { continue }
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refusing reparse-point source: $name" }
        $target = Join-Path $root $name
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $current = $null
        if (Test-Path $target) {
            $current = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($current -ne $hash -and (-not $previous.ContainsKey($name) -or $current -ne $previous[$name])) {
                throw "Integration conflict, preserving existing bytes not matching the last lane receipt: $name"
            }
        }
        $selected.Add([pscustomobject]@{ path = $name; sha256 = $hash; previousSha256 = $current; source = $file.FullName; target = $target })
    }
}
if ($selected.Count -eq 0) { throw "No task-owned source delivered for $Lane" }
New-Item -ItemType Directory -Path $history -Force | Out-Null
if (Test-Path $receipt) { Copy-Item $receipt (Join-Path $history 'previous-receipt.json') }
foreach ($item in $selected) {
    if (Test-Path $item.target) {
        if ((Get-FileHash $item.target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.previousSha256) {
            throw "Target drifted after preflight; previous versions are retained in $history : $($item.path)"
        }
        $backup = Join-Path $history $item.path
        New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
        Copy-Item $item.target $backup
    } elseif ($null -ne $item.previousSha256) { throw "Target disappeared after preflight: $($item.path)" }
    New-Item -ItemType Directory -Path (Split-Path $item.target -Parent) -Force | Out-Null
    Copy-Item $item.source $item.target
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
[pscustomobject]@{ lane = $Lane; timeUtc = [DateTime]::UtcNow.ToString('o'); files = @($selected | Select-Object path,sha256);
    acceptance = 'Source integration only, not component or release acceptance' } | ConvertTo-Json -Depth 6 |
    Set-Content (Join-Path $output "$Lane.json") -Encoding utf8
Write-Output "Integrated $($selected.Count) task-owned files from $Lane; prior-receipt changes backed up to $history; unrelated differing files were not overwritten."
