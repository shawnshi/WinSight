#requires -Version 7.0
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1')
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Outputs.psm1')

function Write-NativeCIAtomicJson {
    param([string]$Path, $Value)
    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 30))
        [IO.File]::Move($temporary, $Path, $true)
    }
    finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
}
function Get-NativeCIDigest([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}
function Get-NativeCISourceBinding {
    param([string]$RepositoryRoot, [hashtable]$Parameters, [Diagnostics.Stopwatch]$Clock, [int]$BudgetSeconds = 165)
    $files = [Collections.Generic.List[object]]::new()
    foreach ($relative in @('desktop','inference','native/winsight-tokenizer','scripts/native-ci','scripts/Test-NativeCI.ps1','.github/workflows/native.yml')) {
        $source = Assert-NativeCIPlainPath (Join-Path $RepositoryRoot $relative)
        $pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($source)
        while ($pending.Count) {
            if ($Clock -and $Clock.Elapsed.TotalSeconds -ge $BudgetSeconds) { throw 'Invocation budget exhausted while validating source binding.' }
            $path = $pending.Dequeue(); $item = Get-Item -LiteralPath $path -Force
            $null = Assert-NativeCIPlainPath $path
            if ($item.PSIsContainer) {
                foreach ($child in Get-ChildItem -LiteralPath $path -Force) {
                    if ($child.Name -notin @('bin','obj','target','.git','models','.artifacts')) { $pending.Enqueue($child.FullName) }
                }
            }
            else {
                if ($item.Length -gt 32MB) { throw 'Unexpected oversized source input; refusing unbounded checkpoint hashing.' }
                $files.Add([pscustomobject]@{path=[IO.Path]::GetRelativePath($RepositoryRoot,$path).Replace('\','/');sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash})
            }
        }
    }
    $orderedParameters = [ordered]@{}
    foreach ($key in @($Parameters.Keys | Sort-Object)) { $orderedParameters[$key] = $Parameters[$key] }
    $binding = [ordered]@{schema=1;repository=[IO.Path]::GetFullPath($RepositoryRoot);parameters=$orderedParameters;files=@($files | Sort-Object path)}
    $json = $binding | ConvertTo-Json -Depth 12 -Compress
    [pscustomobject]@{schema=1;sha256=Get-NativeCIDigest $json;binding=$binding}
}
function Assert-NativeCIBinding {
    param($Expected, $Actual)
    if ($Expected.schema -ne 1 -or $Expected.sha256 -ne $Actual.sha256) { throw 'Checkpoint source/parameter binding changed; resume refused without executing commands.' }
}
function Save-NativeCICheckpoint {
    param([string]$Root, $Report)
    $payload = $Report | ConvertTo-Json -Depth 25 -Compress
    Write-NativeCIAtomicJson (Join-Path $Root 'checkpoint-state.json') ([ordered]@{schema=1;sha256=Get-NativeCIDigest $payload;payload=$payload})
    Write-NativeCIAtomicJson (Join-Path $Root 'native-ci-result.json') $Report
}
function Read-NativeCICheckpoint {
    param([string]$Root, [Diagnostics.Stopwatch]$Clock)
    $envelope = Get-Content -LiteralPath (Join-Path $Root 'checkpoint-state.json') -Raw | ConvertFrom-Json
    if ($envelope.schema -ne 1 -or (Get-NativeCIDigest $envelope.payload) -ne $envelope.sha256) { throw 'Checkpoint checksum/schema invalid; resume refused.' }
    $report = $envelope.payload | ConvertFrom-Json
    if ($report.schema -ne 3 -or $report.releaseApproved -or $report.fullReleaseAcceptance -or $report.state -notin @('incomplete','passed')) { throw 'Failed/invalid checkpoint is not resumable; no automatic retries.' }
    if (@($report.commands | Where-Object state -NE 'Passed').Count -or @($report.gates | Where-Object state -IN @('Running','Failed')).Count) { throw 'Interrupted/failed command or gate is not resumable; reconcile before a new run.' }
    if ($report.state -eq 'passed' -and @($report.gates | Where-Object { $_.requiredForCI -and $_.state -ne 'Passed' }).Count) { throw 'Incomplete required gates cannot be reported as passed.' }
    foreach ($command in $report.commands) {
        foreach ($stream in @('stdout','stderr')) {
            $path = "$($command.logPrefix).$stream.log"
            $null = Assert-NativeCIPlainPath $path
            if (-not $path.StartsWith([IO.Path]::GetFullPath($Root) + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $command.logHashes.$stream) { throw 'Checkpoint command log missing/changed; resume refused.' }
        }
    }
    Assert-NativeCIRetainedOutputs $Root $report.retainedOutputs -Clock $Clock
    if ($report.state -eq 'passed' -and 'ArtifactInventory' -in $report.gates.name) { Assert-NativeCIFinalInventory $Root $report.retainedOutputs }
    return $report
}
function Get-NativeCICommandIdentity {
    param([string]$Gate,[string]$Executable,[string[]]$Arguments,[string]$Directory,[hashtable]$Environment)
    $ordered = [ordered]@{}
    foreach ($key in @($Environment.Keys | Sort-Object)) { $ordered[$key] = $Environment[$key] }
    Get-NativeCIDigest (([ordered]@{gate=$Gate;executable=$Executable;arguments=$Arguments;directory=$Directory;environment=$ordered}) | ConvertTo-Json -Depth 8 -Compress)
}
function Get-NativeCIRequiredGates {
    @('Inputs','Toolchain','TokenizerBuild','TokenizerSyntheticTests','ProductionRestore','ProductionPublish','SyntheticRestore','SyntheticTests','MediaSyntheticRestore','MediaSyntheticTests','ArtifactInventory')
}
function Assert-NativeCIProductionCheckpoint {
    param($Report,[string]$Root,[ValidateSet('Offline','PublicHosted')][string]$DependencyMode = 'Offline')
    $names = @($Report.gates | Where-Object requiredForCI).name
    if (($names -join '|') -ne ((Get-NativeCIRequiredGates) -join '|') -or $Report.mode -ne 'Build' -or $Report.runtime -ne 'win-x64' -or $Report.dependencyMode -ne $DependencyMode -or $Report.artifactRoot -ne $Root) {
        throw 'Checkpoint does not match the required production CI gate/parameter contract.'
    }
}
function New-NativeCIBoundary {
    $exception = [OperationCanceledException]::new('NativeCI invocation checkpoint: no additional command was started.')
    $exception.Data['NativeCICheckpoint'] = $true
    return $exception
}
Export-ModuleMember -Function Write-NativeCIAtomicJson,Get-NativeCIDigest,Get-NativeCISourceBinding,Assert-NativeCIBinding,Save-NativeCICheckpoint,Read-NativeCICheckpoint,Get-NativeCICommandIdentity,Get-NativeCIRequiredGates,Assert-NativeCIProductionCheckpoint,New-NativeCIBoundary
