#requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1')

function Get-NativeCIRetainedOutputs {
    param([string]$Root, [Diagnostics.Stopwatch]$Clock, [int]$BudgetSeconds = 160)
    if (-not $Clock) { $Clock = [Diagnostics.Stopwatch]::StartNew() }
    $files = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($relative in @('workspace','candidate','empty-feed','NuGet.Config','candidate-inventory.json')) {
        $path = Join-Path $Root $relative
        if (Test-Path -LiteralPath $path) { $pending.Enqueue($path) }
    }
    $fixtures = Join-Path $Root 'fixtures'
    if (Test-Path -LiteralPath $fixtures) {
        foreach ($directory in Get-ChildItem -LiteralPath $fixtures -Directory -Filter 'media-*') {
            $receipt = Join-Path $directory.FullName 'results.json'
            if (Test-Path -LiteralPath $receipt) { $pending.Enqueue($receipt) }
        }
    }
    $buffer = [byte[]]::new(1MB)
    while ($pending.Count) {
        if ($Clock.Elapsed.TotalSeconds -ge $BudgetSeconds) { throw 'Retained-output validation exceeded the invocation deadline.' }
        $path = $pending.Dequeue()
        $null = Assert-NativeCIPlainPath $path
        $attributes = [IO.File]::GetAttributes($path)
        if ($attributes -band [IO.FileAttributes]::Directory) {
            foreach ($childPath in [IO.Directory]::EnumerateFileSystemEntries($path)) { $pending.Enqueue($childPath) }
            continue
        }
        $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        $stream = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            while (($read = $stream.Read($buffer,0,$buffer.Length)) -gt 0) {
                if ($Clock.Elapsed.TotalSeconds -ge $BudgetSeconds) { throw 'Retained-output hashing exceeded the invocation deadline.' }
                $hash.AppendData($buffer,0,$read)
            }
            $digest = [Convert]::ToHexString($hash.GetHashAndReset())
            $files.Add([pscustomobject]@{path=[IO.Path]::GetRelativePath($Root,$path).Replace('\','/');bytes=$stream.Length;sha256=$digest})
        }
        finally { $stream.Dispose(); $hash.Dispose() }
    }
    [pscustomobject]@{schema=1;files=@($files | Sort-Object path)}
}
function Assert-NativeCIRetainedOutputs {
    param([string]$Root, $Expected, [Diagnostics.Stopwatch]$Clock)
    if ($Expected.schema -ne 1) { throw 'Retained-output receipt missing/invalid; old checkpoints cannot resume.' }
    $actual = Get-NativeCIRetainedOutputs $Root -Clock $Clock
    $left = $Expected.files | ConvertTo-Json -Depth 5 -Compress
    $right = $actual.files | ConvertTo-Json -Depth 5 -Compress
    if ($left -cne $right) { throw 'Retained outputs deleted/mutated/added; checkpoint refused without rebuild.' }
}
function Assert-NativeCIFinalInventory {
    param([string]$Root, $Receipt)
    $path = Join-Path $Root 'candidate-inventory.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Completed checkpoint has no candidate inventory.' }
    $inventory = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($inventory.schema -ne 1 -or -not $inventory.modelFree -or $inventory.installerReady -or $inventory.releaseApproved) { throw 'Completed candidate inventory has invalid acceptance flags.' }
    $actual = @($Receipt.files | Where-Object { $_.path.StartsWith('candidate/',[StringComparison]::Ordinal) } | ForEach-Object {
        [pscustomobject]@{path=$_.path.Substring(10);bytes=$_.bytes;sha256=$_.sha256.ToUpperInvariant()}
    } | Sort-Object path)
    $listed = @($inventory.files | ForEach-Object { [pscustomobject]@{path=$_.path;bytes=$_.bytes;sha256=$_.sha256.ToUpperInvariant()} } | Sort-Object path)
    if ($actual.Count -eq 0 -or (($actual | ConvertTo-Json -Depth 5 -Compress) -cne ($listed | ConvertTo-Json -Depth 5 -Compress))) { throw 'Completed candidate inventory does not match retained candidate outputs.' }
}
Export-ModuleMember -Function Get-NativeCIRetainedOutputs,Assert-NativeCIRetainedOutputs,Assert-NativeCIFinalInventory
