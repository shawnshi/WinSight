#requires -Version 7.0
<#
.SYNOPSIS
Authenticode-signs the first-party WinSight binaries (and, optionally, the installer).

.DESCRIPTION
Signs only files whose name starts with "WinSight"/"winsight" (Microsoft and third-party binaries keep their own
signatures), timestamps with RFC 3161 and verifies the result. The certificate is selected by store thumbprint or by a PFX
file; a PFX password is read from the WINSIGHT_SIGN_PFX_PASSWORD environment variable and is never accepted as a parameter,
so it cannot end up in a command line or a log. Use -WhatIf to list what would be signed without needing a certificate.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$Path,
    [string]$CertificateThumbprint,
    [string]$PfxPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$SignToolPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $Path)) { throw "Path not found: $Path" }
$item = Get-Item -LiteralPath $Path
$candidates = if ($item.PSIsContainer) {
    Get-ChildItem -LiteralPath $item.FullName -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' -and $_.Name -match '^(?i)winsight' }
} else { @($item) }
if (-not @($candidates).Count) { throw 'No first-party WinSight binaries found to sign.' }

if (-not $CertificateThumbprint -and -not $PfxPath -and -not $WhatIfPreference) {
    throw 'Supply -CertificateThumbprint (certificate store) or -PfxPath (password in WINSIGHT_SIGN_PFX_PASSWORD).'
}
if ($PfxPath -and -not (Test-Path -LiteralPath $PfxPath)) { throw "PFX not found: $PfxPath" }
if ($TimestampUrl -notmatch '^https?://') { throw 'TimestampUrl must be an http(s) RFC 3161 service.' }

function Find-SignTool {
    if ($SignToolPath) { return $SignToolPath }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = if (Test-Path $kits) { Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1 }
    if (-not $found) { throw 'signtool.exe was not found; install the Windows SDK or pass -SignToolPath.' }
    $found.FullName
}

$signtool = if ($WhatIfPreference) { 'signtool.exe' } else { Find-SignTool }
$signed = 0
foreach ($file in $candidates) {
    if (-not $PSCmdlet.ShouldProcess($file.FullName, 'Authenticode sign')) { Write-Output "would sign $($file.FullName)"; continue }
    $arguments = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256')
    if ($CertificateThumbprint) { $arguments += @('/sha1', $CertificateThumbprint) }
    else {
        $arguments += @('/f', $PfxPath)
        if ($env:WINSIGHT_SIGN_PFX_PASSWORD) { $arguments += @('/p', $env:WINSIGHT_SIGN_PFX_PASSWORD) }
    }
    $arguments += $file.FullName
    & $signtool @arguments *> $null
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $($file.Name) (exit $LASTEXITCODE)." }
    & $signtool verify /pa $file.FullName *> $null
    if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $($file.Name)." }
    $signed++
}
if ($WhatIfPreference) { Write-Output "WhatIf: $(@($candidates).Count) file(s) listed, none signed." } else { Write-Output "Signed and verified $signed file(s)." }
