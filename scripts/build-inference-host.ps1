#requires -Version 7.0
param([string]$OutputDirectory, [ValidateSet("win-x64", "win-arm64")][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$OcrModelRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $IsWindows) { throw 'Windows with the pinned .NET 11 RC2 SDK, Rust and matching MSVC Build Tools is required.' }
if (-not $RuntimeIdentifier) {
    $RuntimeIdentifier = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
}
if ($RuntimeIdentifier -ne 'win-x64') { throw 'The isolated RapidOCR candidate is x64-only; ARM64 is not accepted.' }
$ocrRoot = [IO.Path]::GetFullPath($OcrModelRoot)
$ocrManifest = Get-Content -Raw -LiteralPath (Join-Path $root 'inference/WinSight.OcrHost/model-manifest.json') | ConvertFrom-Json
foreach ($pin in $ocrManifest.files) {
    $asset = Join-Path $ocrRoot $pin.path
    if ((Get-Item -LiteralPath $asset).Length -ne $pin.bytes -or
        (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.sha256) {
        throw "Pinned OCR model/dictionary mismatch: $($pin.path)"
    }
}
$rustTarget = if ($RuntimeIdentifier -eq 'win-arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
$dev = Join-Path $root '.dev'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $dev ('winml-host-' + [Guid]::NewGuid().ToString('N')) }
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $out.StartsWith($dev + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Development output must be strictly under this project .dev directory, never an application install path.'
}
if (Test-Path -LiteralPath $out) { throw 'Choose a new output directory; existing artifacts are not overwritten.' }
$null = Get-Command cargo -ErrorAction Stop
$null = Get-Command dotnet -ErrorAction Stop

# No restore, tool installation, model/provider downloads, execution probes or installer steps.
# Only the caller-supplied four pinned OCR assets are read/copied; embedding weights are excluded.
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# A missing cached dependency/restore asset is a hard error, not permission to fetch it.
$native = Join-Path $root 'native/winsight-tokenizer'
& (Join-Path $PSScriptRoot 'build-native-tokenizer.ps1') -RuntimeIdentifier $RuntimeIdentifier
Push-Location (Join-Path $root 'inference')
try {
    & dotnet publish 'WinSight.InferenceHost/WinSight.InferenceHost.csproj' --configuration Release --runtime $RuntimeIdentifier --self-contained false --no-restore -p:PublishTrimmed=false --output $out
    if ($LASTEXITCODE -ne 0) { throw "Inference host publish failed with exit $LASTEXITCODE; no restore was attempted" }
    & dotnet publish 'WinSight.OcrHost/WinSight.OcrHost.csproj' --configuration Release --runtime win-x64 --self-contained false --no-restore -p:PublishTrimmed=false "-p:OcrModelRoot=$ocrRoot" --output (Join-Path $out 'ocr')
    if ($LASTEXITCODE -ne 0) { throw "Isolated OCR publish failed with exit $LASTEXITCODE; no restore was attempted" }
} finally { Pop-Location }

foreach ($relative in @('WinSight.InferenceHost.exe', 'model-manifest.json', 'ocr/WinSight.OcrHost.exe', 'ocr/model-manifest.json', "runtimes/$RuntimeIdentifier/native/winsight_tokenizer.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $out $relative) -PathType Leaf)) { throw "Missing staged file: $relative" }
}
Copy-Item -LiteralPath (Join-Path $root 'docs/media-attribution.md') -Destination (Join-Path $out 'MEDIA-ATTRIBUTION.md')
$tokenizerNotices = Join-Path $out 'licenses/tokenizer'
$null = New-Item -ItemType Directory -Path $tokenizerNotices -Force
Copy-Item -LiteralPath (Join-Path $native 'licenses') -Destination (Join-Path $tokenizerNotices 'texts') -Recurse
Copy-Item -LiteralPath (Join-Path $native 'source-provenance.json') -Destination $tokenizerNotices
@'
STAGED WINDOWS ML HOST — BUILD OUTPUT IS NOT RUNTIME ACCEPTANCE
This framework-dependent host requires exactly .NET 11.0.0-rc.2.26504.105. Windows ML native libraries are app-local;
WindowsAppSDKSelfContained does not mean the .NET runtime is bundled.
Four verified OCR model/dictionary assets are included in the isolated ocr directory.
Embedding model weights are not included. No staged host/model/EP was launched by this script.
The scoped RapidOCR image suite passed; full product qualification is separate and this is not a release.
Numerical media alignment, recall, accelerator execution, migration,
license/distribution closure and clean-machine installation remain separate gates.
The WinSight package includes this host as its sole inference backend. Runtime acceptance remains separate.
'@ | Set-Content -Encoding utf8 (Join-Path $out 'RUNTIME-REQUIREMENTS.txt')
Write-Output "Staged development host at $out. This is not runtime or installation acceptance."
