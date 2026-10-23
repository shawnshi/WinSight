[CmdletBinding(DefaultParameterSetName='Legacy')]
param(
    [Parameter(Mandatory,ParameterSetName='Legacy')][string]$ControlDirectory,
    [Parameter(ParameterSetName='Standard')][switch]$StandardLive,
    [Parameter(Mandatory,ParameterSetName='Standard')][ValidateSet('Install','Uninstall')][string]$Action,
    [Parameter(Mandatory,ParameterSetName='Standard')][ValidateNotNullOrEmpty()][string]$BindingPath,
    [Parameter(Mandatory,ParameterSetName='Standard')][ValidatePattern('^[0-9a-f]{64}$')][string]$BindingSha256,
    [Parameter(ParameterSetName='Standard')][ValidateRange(1000,160000)][int]$TimeoutMilliseconds = 160000,
    [Parameter(ParameterSetName='Standard')][switch]$Quiet
)
$ErrorActionPreference = 'Stop'
if ($PSCmdlet.ParameterSetName -ceq 'Standard') {
    Set-StrictMode -Version Latest
    $tick = [long][Environment]::TickCount -band 4294967295L
    if (($PSBoundParameters.ContainsKey('StandardLive') -and -not $StandardLive) -or
        ($PSBoundParameters.ContainsKey('Quiet') -and ($Action -cne 'Uninstall' -or -not $Quiet))) { throw 'B.StandardArguments: false selectors/Quiet outside Uninstall rejected.' }
    $expectedControl='__WINSIGHT_PRIVATE_CONTROL_ROOT__'
    if ($expectedControl.StartsWith('__')) { throw 'B.UnconfiguredPreviewSource: standard operations require a materialized, re-pinned private graph.' }
    if ($Action -cnotin @('Install','Uninstall') -or $BindingSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $BindingPath -cne ($expectedControl+'\bindings\'+$BindingSha256+'.json')) { throw 'B.StandardArguments: exact operation/content-addressed binding required.' }
    if ($PSVersionTable.PSEdition -cne 'Desktop' -or $PSVersionTable.PSVersion.ToString() -cne '5.1.29683.1000' -or -not [Environment]::Is64BitProcess) { throw 'B.Host: exact x64 Windows PowerShell 5.1.29683.1000 required.' }
    # Frozen bootstrap modules are checked before import; binding never chooses executable code.
    foreach ($slot in @(
        @('NativeDeploymentJob.psm1','1f5600efadac9afc1f5afb2a7f6158643fcb25aeaf9400f58022674c69763331'),
        @('NativeStandardLiveShell.psm1','7aa3a888133ed4792c3e1e923ff7e11b4ddf7465af6cecbc634fea5121410485')
    )) {
        $path = Join-Path $PSScriptRoot $slot[0]
        for ($ancestor=$path; $ancestor; $ancestor=[IO.Path]::GetDirectoryName($ancestor)) {
            try { $attributes=[IO.File]::GetAttributes($ancestor) }
            catch [IO.FileNotFoundException] { throw 'B.Bootstrap: missing sibling.' }
            catch [IO.DirectoryNotFoundException] { throw 'B.Bootstrap: missing ancestor.' }
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'B.Bootstrap: reparse rejected.' }
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $slot[1]) { throw 'B.Bootstrap: frozen sibling mismatch.' }
    }
    Import-Module (Join-Path $PSScriptRoot 'NativeDeploymentJob.psm1') -ErrorAction Stop
    $adapter=Import-Module (Join-Path $PSScriptRoot 'NativeStandardLiveShell.psm1') -PassThru -ErrorAction Stop
    $binding=Read-NativeLiveBinding -BindingPath $BindingPath -BindingSha256 $BindingSha256 -StartTick $tick -DeadlineMilliseconds $TimeoutMilliseconds
    & $adapter {
        param($B,$Path,$Hash,$Runtime,$Start,$Deadline)
        if ($Path -cne ($B.roots.controlRoot+'\bindings\'+$Hash+'.json')) { throw 'B.Binding: exact content-addressed binding required.' }
        $sources=Read-NLGraphObject $B.sources $B.roots.controlRoot $Start $Deadline
        Assert-NLSources $sources
        if ($Runtime -cne ($B.roots.controlRoot+'\runtime\'+$B.sources.sha256)) { throw 'B.Runtime: controller must be the co-located pinned runtime.' }
        foreach ($source in $sources.files) {
            $pin=[pscustomobject]@{path=($Runtime+'\'+$source.name);bytes=$source.bytes;sha256=$source.sha256}
            Assert-NLPhysicalPin $pin $B.roots.controlRoot $Start $Deadline
        }
    } $binding $BindingPath $BindingSha256 $PSScriptRoot $tick $TimeoutMilliseconds
    $parameters=@{ Command='RunOwnedShell'; Operation=$(if($Action -ceq 'Install'){'Apply'}else{'Remove'}); BindingPath=$BindingPath; BindingSha256=$BindingSha256;
        TransactionId=([Guid]::NewGuid().ToString('N')); StartTick=$tick; DeadlineMilliseconds=$TimeoutMilliseconds }
    # Core Prepare, actual producer ownership and postsettlement commits all remain in A.
    & (Join-Path $PSScriptRoot 'Invoke-NativeInstallCoordinator.ps1') @parameters
    exit $LASTEXITCODE
}
Import-Module (Join-Path $PSScriptRoot 'NativeDeploymentJob.psm1') -Force
$request = Get-Content -LiteralPath (Join-Path $ControlDirectory 'request.json') -Raw | ConvertFrom-Json
while (-not (Test-Path -LiteralPath (Join-Path $ControlDirectory 'assigned'))) {
    if ((Get-NativeDeadlineRemaining $request.startTick $request.deadlineMilliseconds) -le 0) { throw 'Worker assignment deadline expired.' }
    Start-Sleep -Milliseconds 20
}
$parameters = @{}
foreach ($property in $request.parameters.PSObject.Properties) { $parameters[$property.Name] = $property.Value }
$global:LASTEXITCODE = 0
& $request.engine @parameters
exit $LASTEXITCODE
