#requires -Version 7.0
<#
.SYNOPSIS
Builds the per-user WinSight installer (alias for Build-NativeInstaller.ps1).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageDirectory,
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$CertificateThumbprint,
    [string]$PfxPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& (Join-Path $PSScriptRoot 'Build-NativeInstaller.ps1') @PSBoundParameters
exit $LASTEXITCODE
