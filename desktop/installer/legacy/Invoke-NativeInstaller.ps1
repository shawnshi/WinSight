[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Apply','Remove','Recover')][string]$Operation,
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][string]$DataRoot,
    [string]$StageDirectory,
    [string]$ManifestSha256,
    [ValidateSet('Verification','Production')][string]$Mode = 'Verification',
    [string]$ExpectedStateSha256,
    [string]$JournalSha256,
    [ValidateSet('Standalone','Prepare','Activate','Finalize')][string]$TransactionPhase = 'Standalone',
    [string]$InitializationEvidenceRoot,
    [ValidateSet('Apply','Remove')][string]$InitializationOperation = 'Apply',
    [ValidateSet('None','AfterJournal','AfterFirstCopy','BeforeCommit','AfterCommit','AfterRemoveMove')][string]$VerificationFailurePoint = 'None'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'NativeInstaller.psm1') -Force
try {
    if ($Operation -eq 'Recover' -and $TransactionPhase -ne 'Standalone') { throw 'Recover supports only Standalone; use explicit trusted recovery arguments.' }
    if ($Operation -ne 'Recover' -and ($InitializationEvidenceRoot -or $PSBoundParameters.ContainsKey('InitializationOperation'))) { throw 'Initialization recovery arguments are valid only for explicit Recover.' }
    if ($VerificationFailurePoint -ne 'None' -and ($Mode -ne 'Verification' -or $Operation -eq 'Recover')) { throw 'Failure injection is restricted to Verification Apply/Remove.' }
    $testHook = $null
    if ($VerificationFailurePoint -ne 'None') {
        $point = $VerificationFailurePoint
        $testHook = { param($event); if ($event -eq $point) { throw "Verification-only failure at $point" } }.GetNewClosure()
    }
    if ($Operation -eq 'Recover') {
        Invoke-NativeRecovery -InstallRoot $InstallRoot -DataRoot $DataRoot -JournalSha256 $JournalSha256 -StageDirectory $StageDirectory -ManifestSha256 $ManifestSha256 -Mode $Mode -ExpectedStateSha256 $ExpectedStateSha256 -InitializationEvidenceRoot $InitializationEvidenceRoot -InitializationOperation $InitializationOperation | ConvertTo-Json -Depth 5
    } else {
        Invoke-NativeTransaction -Operation $Operation -InstallRoot $InstallRoot -DataRoot $DataRoot -StageDirectory $StageDirectory -ManifestSha256 $ManifestSha256 -Mode $Mode -ExpectedStateSha256 $ExpectedStateSha256 -TestHook $testHook -TransactionPhase $TransactionPhase -JournalSha256 $JournalSha256 | ConvertTo-Json -Depth 5
    }
} catch {
    Write-Error -ErrorRecord $_ -ErrorAction Continue
    exit 1
}
