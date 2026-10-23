#requires -Version 7.0
param([string]$OutputDirectory, [ValidateSet("win-x64", "win-arm64")][string]$RuntimeIdentifier)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $IsWindows) { throw 'Windows with .NET 10, Rust and matching MSVC Build Tools is required.' }
if (-not $RuntimeIdentifier) {
    $RuntimeIdentifier = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
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

# No restore, tool installation, models, provider downloads, execution probes or installer steps.
# A missing cached dependency/restore asset is a hard error, not permission to fetch it.
$native = Join-Path $root 'native/winsight-tokenizer'
& cargo build --manifest-path (Join-Path $native 'Cargo.toml') --target-dir (Join-Path $native 'target') --target $rustTarget --offline --locked --release
if ($LASTEXITCODE -ne 0) { throw "Native tokenizer build failed with exit $LASTEXITCODE" }
Push-Location (Join-Path $root 'inference')
try {
    & dotnet publish 'WinSight.InferenceHost/WinSight.InferenceHost.csproj' --configuration Release --runtime $RuntimeIdentifier --self-contained false --no-restore -p:PublishTrimmed=false --output $out
    if ($LASTEXITCODE -ne 0) { throw "Inference host publish failed with exit $LASTEXITCODE; no restore was attempted" }
} finally { Pop-Location }

foreach ($relative in @('WinSight.InferenceHost.exe', 'model-manifest.json', "runtimes/$RuntimeIdentifier/native/winsight_tokenizer.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $out $relative) -PathType Leaf)) { throw "Missing staged file: $relative" }
}
Copy-Item -LiteralPath (Join-Path $root 'docs/media-attribution.md') -Destination (Join-Path $out 'MEDIA-ATTRIBUTION.md')
@'
STAGED WINDOWS ML HOST — BUILD OUTPUT IS NOT RUNTIME ACCEPTANCE
This framework-dependent host requires .NET 10. Windows ML native libraries are app-local;
WindowsAppSDKSelfContained does not mean the .NET runtime is bundled.
No model weights are included. No staged host/model/EP was launched by this script.
Swift integration, numerical media alignment, recall, accelerator execution, migration,
license/distribution closure and clean-machine installation remain separate gates.
The WinSight package includes this host as its sole inference backend. Runtime acceptance remains separate.
'@ | Set-Content -Encoding utf8 (Join-Path $out 'RUNTIME-REQUIREMENTS.txt')
Write-Output "Staged development host at $out. This is not runtime or installation acceptance."
