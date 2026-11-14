#requires -Version 7.0
<#
.SYNOPSIS
Builds the per-machine WinSight installers (alias for Build-NativeInstaller.ps1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageDirectory,
    [ValidateSet('all', 'amd', 'intel')][string[]]$Vendor = @('all'),
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$CertificateThumbprint,
    [string]$PfxPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'Build-NativeInstaller.ps1') @PSBoundParameters
exit $LASTEXITCODE
