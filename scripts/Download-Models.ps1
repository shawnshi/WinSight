#requires -Version 7.0
<#
.SYNOPSIS
Downloads and verifies the 11 official EmbeddingGemma-2 ONNX model files for WinSight.

.DESCRIPTION
Fetches the pinned model files from Hugging Face (or hf-mirror.com) without requiring Python or credentials.
Verifies each file's SHA-256 hash against inference/WinSight.InferenceHost/model-manifest.json.
Downloads can be resumed; existing verified files are skipped.
#>
[CmdletBinding()]
param(
    [string]$Destination = 'C:\Program Files\WinSight\models',
    [switch]$UseMirror = $true,
    [string]$CustomEndpoint
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent

$manifestPath = Join-Path $root 'inference/WinSight.InferenceHost/model-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Manifest not found: $manifestPath" }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

$baseUrl = if ($CustomEndpoint) { $CustomEndpoint.TrimEnd('/') }
           elseif ($UseMirror) { 'https://hf-mirror.com' }
           else { 'https://huggingface.co' }

$repoUrl = "$baseUrl/$($manifest.repository)/resolve/$($manifest.revision)"

Write-Output "=== WinSight Model Downloader ==="
Write-Output "Repository : $($manifest.repository) @ $($manifest.revision.Substring(0, 12))"
Write-Output "Endpoint   : $baseUrl"
Write-Output "Destination: $Destination"
Write-Output "Files      : $($manifest.files.Count) files (~505 MiB total)"
Write-Output ""

New-Item -ItemType Directory -Path $Destination -Force | Out-Null

$httpClient = [System.Net.Http.HttpClient]::new()
$httpClient.Timeout = [TimeSpan]::FromMinutes(30)

$downloaded = 0
$skipped = 0

try {
    foreach ($entry in $manifest.files) {
        $target = Join-Path $Destination $entry.path
        $parentDir = Split-Path $target -Parent
        if (-not (Test-Path -LiteralPath $parentDir)) { New-Item -ItemType Directory -Path $parentDir -Force | Out-Null }

        # Check if already present and verified
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $item = Get-Item -LiteralPath $target
            if ($item.Length -eq $entry.size) {
                $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($hash -eq $entry.sha256.ToLowerInvariant()) {
                    Write-Host "[OK] $($entry.path) (already verified)" -ForegroundColor Green
                    $skipped++
                    continue
                }
            }
        }

        $url = "$repoUrl/$($entry.path)"
        Write-Host "[DOWNLOAD] $($entry.path) ($([math]::Round($entry.size / 1MB, 2)) MiB)..." -ForegroundColor Cyan

        $temp = $target + ".tmp." + [Guid]::NewGuid().ToString("N")
        try {
            $response = $httpClient.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $response.EnsureSuccessStatusCode() | Out-Null

            $inStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $outStream = [System.IO.File]::Create($temp)

            try {
                $inStream.CopyTo($outStream)
            } finally {
                $outStream.Dispose()
                $inStream.Dispose()
            }

            # Verify downloaded file hash
            $hash = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne $entry.sha256.ToLowerInvariant()) {
                throw "SHA-256 verification failed for $($entry.path): expected $($entry.sha256), got $hash"
            }

            Move-Item -LiteralPath $temp -Destination $target -Force
            Write-Host "[OK] $($entry.path) verified." -ForegroundColor Green
            $downloaded++
        } finally {
            if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
        }
    }

    Write-Output ""
    Write-Host "Success: All $($manifest.files.Count) model files are verified and ready in $Destination!" -ForegroundColor Green
    Write-Output "Restart WinSight or click 'Refresh models' in Settings to load the embedding engine."
} finally {
    $httpClient.Dispose()
}
