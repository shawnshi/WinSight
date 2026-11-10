#requires -Version 7.0
<#
.SYNOPSIS
Builds the per-user WinSight installer from a staged payload (see Prepare-NativeRelease.ps1).

.DESCRIPTION
Canonical mainline installer builder for WinSight.
Uses desktop/installer/WinSight.iss: creates a self-contained per-user installer that installs
directly into %LOCALAPPDATA%\Programs\WinSight with direct Exe shortcuts, optional PATH task,
full process termination handling, and uninstallation cleanup.
The product version comes from desktop/Directory.Build.props.
Optional Authenticode signing runs Sign-NativeArtifacts.ps1 on the payload binaries first and then
signs the installer executable itself through an Inno Setup "SignTool" definition.
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
$root = Split-Path $PSScriptRoot -Parent

$payload = Join-Path $StageDirectory 'app'
$allRequired = @(
    'WinSight.App.exe',
    'winsight-native.exe',
    'bridge\winsight-mcp.exe',
    'inference\WinSight.InferenceHost.exe',
    'inference\ocr\WinSight.OcrHost.exe',
    'inference\winml\WinSight.WinMlHost.exe'
)
foreach ($required in $allRequired) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
        throw "Payload is incomplete, missing required host: $required"
    }
}
if (-not (Get-ChildItem -LiteralPath $payload -Recurse -Filter winsight_segmenter.dll -File)) {
    throw 'Payload lacks winsight_segmenter.dll; indexing would fail at run time.'
}

# Calculate uncompressed payload size for Inno Setup EstimatedSize metadata
$payloadFiles = @(Get-ChildItem -LiteralPath $payload -Recurse -File)
$totalPayloadBytes = ($payloadFiles | Measure-Object -Property Length -Sum).Sum
$estimatedKb = [math]::Ceiling($totalPayloadBytes / 1KB)

[xml]$props = Get-Content -LiteralPath (Join-Path $root 'desktop/Directory.Build.props') -Raw
$prefix = $props.Project.PropertyGroup.VersionPrefix | Where-Object { $_ } | Select-Object -First 1
$suffix = $props.Project.PropertyGroup.VersionSuffix | Where-Object { $_ } | Select-Object -First 1
if ($prefix -notmatch '^\d+\.\d+\.\d+$') { throw 'VersionPrefix in desktop/Directory.Build.props must be major.minor.patch.' }
$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }

$iscc = $CompilerPath
if (-not $iscc) {
    $candidates = @(
        'C:\Program Files\Inno Setup 7\ISCC.exe',
        "$env:ProgramW6432\Inno Setup 7\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        "$env:ProgramW6432\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "C:\Users\shich\AppData\Local\Programs\Antigravity IDE\resources\app\node_modules\innosetup\bin\ISCC.exe"
    )
    $iscc = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'ISCC.exe (Inno Setup 6) was not found; install it or pass -CompilerPath.' }

if (-not $OutputDirectory) { $OutputDirectory = Join-Path $StageDirectory 'installer' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$signing = $CertificateThumbprint -or $PfxPath
if ($signing) {
    & (Join-Path $PSScriptRoot 'Sign-NativeArtifacts.ps1') -Path $payload -CertificateThumbprint $CertificateThumbprint -PfxPath $PfxPath
}

$issFile = Join-Path $root 'desktop/installer/WinSight.iss'
if (-not (Test-Path -LiteralPath $issFile)) { $issFile = Join-Path $root 'desktop/installer/WinSight.Simple.iss' }

$arguments = @(
    "/DPayloadDir=$payload",
    "/DAppVersion=$version",
    "/DNumericVersion=$prefix.0",
    "/DOutputDir=$OutputDirectory"
# Estimated size is automatically calculated by Inno Setup from [Files]
)
if ($CertificateThumbprint) {
    $signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Source
    if (-not $signtool) { throw 'signtool.exe must be on PATH to sign the installer itself.' }
    $arguments += @('/DSignToolName=winsightsign', "/Swinsightsign=`"$signtool`" sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /sha1 $CertificateThumbprint `$f")
}
& $iscc @arguments $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC compilation failed (exit $LASTEXITCODE)." }

$installer = Get-ChildItem -LiteralPath $OutputDirectory -Filter "WinSight-$version-x64-setup.exe" | Select-Object -First 1
if (-not $installer) { throw 'The installer was not produced.' }
$hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$hashContent = "$hash  $($installer.Name)`r`n"
Get-ChildItem -LiteralPath $OutputDirectory -Filter "WinSight-$version-x64-setup*.bin" | ForEach-Object {
    $binHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashContent += "$binHash  $($_.Name)`r`n"
}
Set-Content -LiteralPath ($installer.FullName + '.sha256') -Value $hashContent.TrimEnd() -Encoding ascii
Write-Output "Built $($installer.FullName) ($([math]::Round($installer.Length / 1MB, 1)) MiB, sha256 $hash, uncompressed $estimatedKb KB)$(if (-not $signing) { '; UNSIGNED' })"
