#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ArtifactRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'NativeCI.Common.psm1') -Force
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$out = New-NativeCIArtifactRoot -RepositoryRoot $repository -ArtifactRoot $ArtifactRoot
$pwsh = (Get-Process -Id $PID).Path
$runner = Join-Path $repository 'scripts/Test-NativeCI.ps1'
$results = [Collections.Generic.List[object]]::new()
function Check([bool]$Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function ExpectFailure([scriptblock]$Action,[string]$Pattern) {
    $caught = $null
    try { & $Action } catch { $caught = $_.Exception.Message }
    Check ($null -ne $caught -and $caught -match $Pattern) "Expected failure matching '$Pattern'; got '$caught'"
}
function Test([string]$Name,[scriptblock]$Action) {
    try { & $Action; $results.Add([pscustomobject]@{name=$Name;state='Passed'}); Write-Output "PASS $Name" }
    catch { $results.Add([pscustomobject]@{name=$Name;state='Failed';error=$_.Exception.Message}); throw }
}
try {
    Test 'PowerShell parser: all new CI scripts' {
        foreach ($file in @($runner,(Join-Path $PSScriptRoot 'NativeCI.Common.psm1'),$PSCommandPath)) {
            $tokens = $null; $errors = $null
            $null = [Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
            Check ($errors.Count -eq 0) "Parser errors in $file"
        }
    }
    Test 'Reject relative, boundary and existing roots without overwriting' {
        ExpectFailure { New-NativeCIArtifactRoot $repository 'relative' } 'absolute'
        ExpectFailure { New-NativeCIArtifactRoot $repository (Join-Path $repository '.dev/native-migration/native-ci') } 'strictly below'
        $sentinel = Join-Path $out 'sentinel.txt'
        [IO.File]::WriteAllText($sentinel,'keep')
        ExpectFailure { New-NativeCIArtifactRoot $repository $out } 'already exists'
        Check ([IO.File]::ReadAllText($sentinel) -eq 'keep') 'Existing task root was changed.'
    }
    Test 'Native nonzero exit is not success; stdout/stderr retained' {
        $prefix = Join-Path $out 'exit-23'
        ExpectFailure { Invoke-NativeCIProcess $pwsh @('-NoProfile','-NonInteractive','-Command',"[Console]::Out.WriteLine('real stdout'); [Console]::Error.WriteLine('real stderr'); exit 23") $repository $prefix } 'exited 23'
        Check ([IO.File]::ReadAllText("$prefix.stdout.log").Contains('real stdout')) 'stdout missing'
        Check ([IO.File]::ReadAllText("$prefix.stderr.log").Contains('real stderr')) 'stderr missing'
    }
    Test 'ArgumentList preserves spaces, quotes and shell metacharacters' {
        $fixture = Join-Path $out 'argument fixture.ps1'
        [IO.File]::WriteAllText($fixture,'param([string]$Value) [Console]::Write($Value)')
        $value = 'literal space & "quoted" 中文'
        $prefix = Join-Path $out 'argument'
        $null = Invoke-NativeCIProcess $pwsh @('-NoProfile','-NonInteractive','-File',$fixture,'-Value',$value) $repository $prefix
        Check ([IO.File]::ReadAllText("$prefix.stdout.log") -eq $value) 'Arguments were interpreted by a shell.'
    }
    Test 'Timeout terminates only owned child tree and records failure' {
        $pidPath = Join-Path $out 'owned-child.pid'
        $fixture = Join-Path $out 'timeout-fixture.ps1'
        [IO.File]::WriteAllText($fixture,'param($Pwsh,$PidPath) $p=Start-Process -FilePath $Pwsh -ArgumentList @(''-NoProfile'',''-NonInteractive'',''-Command'',''Start-Sleep 60'') -PassThru; [IO.File]::WriteAllText($PidPath,[string]$p.Id); Start-Sleep 60')
        ExpectFailure { Invoke-NativeCIProcess -FilePath $pwsh -Arguments @('-NoProfile','-NonInteractive','-File',$fixture,$pwsh,$pidPath) -WorkingDirectory $repository -LogPrefix (Join-Path $out 'timeout') -TimeoutSeconds 3 } 'timed out'
        Check (Test-Path -LiteralPath $pidPath) 'Owned descendant was not created; timeout test is inconclusive.'
        $childId = [int][IO.File]::ReadAllText($pidPath)
        $alive = Get-Process -Id $childId -ErrorAction SilentlyContinue
        Check ($null -eq $alive) 'Owned descendant survived timeout.'
    }
    Test 'Plan exits 2 and cannot claim required skipped gates passed' {
        $plan = Join-Path $out 'plan'
        ExpectFailure { Invoke-NativeCIProcess $pwsh @('-NoProfile','-NonInteractive','-File',$runner,'-ArtifactRoot',$plan,'-Mode','Plan') $repository (Join-Path $out 'plan-process') } 'exited 2'
        $report = Get-Content -LiteralPath (Join-Path $plan 'native-ci-result.json') -Raw | ConvertFrom-Json
        Check ($report.state -eq 'incomplete' -and -not $report.releaseApproved) 'Plan claimed success/release approval.'
        Check (@($report.gates | Where-Object { $_.requiredForCI -and $_.state -ne 'NotRun' }).Count -eq 0) 'Plan fabricated gate results.'
        Check ($report.commands.Count -eq 0) 'Plan launched build commands.'
        Check (@($report.suiteInventory | Where-Object { -not $_.selected }).Count -gt 0) 'Omitted suites were hidden.'
        Check (($report.gates | Where-Object name -EQ 'ModelParity').state -eq 'Blocked') 'Missing models were hidden.'
        Check (($report.gates | Where-Object name -EQ 'InstallerRecovery').state -eq 'Blocked') 'Installer gate was hidden.'
    }
    Test 'Missing explicit caches hard fails before any compiler invocation' {
        $failed = Join-Path $out 'missing-cache'
        ExpectFailure { Invoke-NativeCIProcess $pwsh @('-NoProfile','-NonInteractive','-File',$runner,'-ArtifactRoot',$failed) $repository (Join-Path $out 'missing-cache-process') } 'exited 1'
        $report = Get-Content -LiteralPath (Join-Path $failed 'native-ci-result.json') -Raw | ConvertFrom-Json
        Check ($report.state -eq 'failed' -and $report.commands.Count -eq 0) 'Missing caches became skipped-success or invoked a compiler.'
        Check (($report.gates | Where-Object name -EQ 'Inputs').state -eq 'Failed') 'Required failed input gate was lost.'
        Check (($report.gates | Where-Object name -EQ 'ProductionPublish').state -eq 'Blocked') 'Dependent publish gate did not close.'
        Check (-not (Test-Path -LiteralPath (Join-Path $failed 'workspace'))) 'Source copy occurred before preflight.'
    }
    Test 'Public dependency provisioning is rejected outside hosted runner' {
        $failed = Join-Path $out 'not-hosted'
        ExpectFailure { Invoke-NativeCIProcess -FilePath $pwsh -Arguments @('-NoProfile','-NonInteractive','-File',$runner,'-ArtifactRoot',$failed,'-DependencyMode','PublicHosted') -WorkingDirectory $repository -LogPrefix (Join-Path $out 'not-hosted-process') -Environment @{GITHUB_ACTIONS='false';RUNNER_ENVIRONMENT='local'} } 'exited 1'
        $report = Get-Content -LiteralPath (Join-Path $failed 'native-ci-result.json') -Raw | ConvertFrom-Json
        Check (($report.gates | Where-Object name -EQ 'Inputs').detail -match 'restricted') 'PublicHosted restriction was not enforced.'
        Check ($report.commands.Count -eq 0) 'Local public provisioning ran a command.'
    }
    Test 'Pinned action/trigger/security contract has no release path' {
        $workflow = [IO.File]::ReadAllText((Join-Path $repository '.github/workflows/native.yml'))
        $uses = [regex]::Matches($workflow,'uses:\s+(\S+)')
        Check ($uses.Count -eq 4) 'Unexpected action count.'
        foreach ($use in $uses) { Check ($use.Groups[1].Value -match '^actions/(checkout|setup-dotnet|upload-artifact)@[0-9a-f]{40}$') 'Action not official/commit pinned.' }
        Check ($workflow -match 'persist-credentials: false' -and $workflow -match 'contents: read') 'Least privilege is missing.'
        Check ($workflow -notmatch '(?i)secrets\.|pull_request_target|contents: write|packages: write|gh release|git push|continue-on-error') 'Release/secret/bypass path found.'
        Check ($workflow -match 'workflow_dispatch:' -and $workflow -match 'push:\s+paths:') 'Required bounded triggers missing.'
    }
    Write-Output 'CI script contract passed; no dotnet/cargo/model/media/UI/installer execution was performed.'
}
finally {
    [pscustomobject]@{schema=1;state=$(if (@($results|Where-Object state -EQ 'Failed').Count) {'failed'} else {'passed'});
        tests=$results.ToArray(); productBuildExecuted=$false; remoteActionsExecuted=$false; releaseApproved=$false} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'contract-result.json') -Encoding utf8
}
