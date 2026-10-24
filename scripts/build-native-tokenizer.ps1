#requires -Version 7.0
param([ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier='win-x64', [string]$TargetDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$native=Join-Path $root 'native/winsight-tokenizer'
$defaultTarget=[IO.Path]::GetFullPath((Join-Path $native 'target'))
if (-not $TargetDirectory) { $TargetDirectory=$defaultTarget }
if (-not [IO.Path]::IsPathFullyQualified($TargetDirectory)) { throw 'TargetDirectory must be absolute.' }
$TargetDirectory=[IO.Path]::GetFullPath($TargetDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$dev=[IO.Path]::GetFullPath((Join-Path $root '.dev'))+[IO.Path]::DirectorySeparatorChar
if ($TargetDirectory -ne $defaultTarget -and -not $TargetDirectory.StartsWith($dev,[StringComparison]::OrdinalIgnoreCase)) { throw 'Native build output must be the project native target or a task directory strictly below .dev.' }
for ($parent=$TargetDirectory; $parent.StartsWith($root,[StringComparison]::OrdinalIgnoreCase); $parent=Split-Path $parent -Parent) {
    if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse native output ancestor is not allowed.' }
}
$target=if($RuntimeIdentifier -eq 'win-arm64'){'aarch64-pc-windows-msvc'}else{'x86_64-pc-windows-msvc'}
# Offline, locked build only. Caller owns toolchain/cache setup; this installs or downloads nothing.
& cargo build --manifest-path (Join-Path $native 'Cargo.toml') --target-dir $TargetDirectory --target $target --offline --locked --release
if($LASTEXITCODE -ne 0){throw "Native tokenizer build failed: $LASTEXITCODE"}
$dll=Join-Path $TargetDirectory "$target/release/winsight_tokenizer.dll"
$inputs=@((Join-Path $native 'Cargo.toml'),(Join-Path $native 'Cargo.lock'),(Join-Path $native 'src/lib.rs'),$dll)
$binding=(@($inputs|ForEach-Object{(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToUpperInvariant()}) -join ':')
$receipt=$dll+'.source.sha256'
$temp=$receipt+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
[IO.File]::WriteAllText($temp,$binding+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
[IO.File]::Move($temp,$receipt,$true)
Write-Output "Built $RuntimeIdentifier tokenizer and source/DLL binding receipt. This is not release approval."
