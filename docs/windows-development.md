# Windows native development

The maintained implementation is the .NET/WinUI native preview. Swift, Tauri, FFmpeg/vendor build scripts and their old CLI/JSONL entry points are retired; historical source is available at commit `400c5b501e664f7aaaf2c2ec5e1c270cf0425a87`. See [the retirement contract](native-migration/legacy-retirement.md). Removal does not imply complete legacy parity or a stable Windows installer.

## Required development components

- Windows 11 x64, .NET SDK selected by `desktop/global.json` and `inference/global.json`.
- Rust/MSVC toolchain and Windows SDK for the independently required tokenizer DLL.
- PowerShell 7 for development scripts; it does not replace the exact Windows PowerShell 5.1 host required by the separate standard-install contract.
- Python 3.11+ for source checks, with the existing tooling dependency `defusedxml==0.7.1`.
- Pillow is optional, only for explicit icon regeneration. It is not an application runtime dependency.

No Swift, Tauri, FFmpeg, PDFium or old Vendor acquisition is required for the native build. The Windows image/OCR/media stack and the locked native packages remain in use.

## Safe source checks

After preparing the development dependencies, from the repository root:

```powershell
python scripts/check-windows-source.py
$env:WINSIGHT_TEST_TOKENIZER = (Resolve-Path -LiteralPath 'desktop/tests/Fixtures/Tokenizer/abi-tokenizer.json').Path
cargo test --locked --manifest-path native/winsight-tokenizer/Cargo.toml
```

Dependency acquisition can use the network; these commands are not authority to install tools or scan private data. Add Cargo `--offline` when validating against an existing cache. The source checker reads repository metadata/assets and uses temporary files for malformed-input tests; it does not open model weights or real indexes.

The ordinary CI workflow runs these source/tokenizer checks. It does not establish .NET binary, UI/model or installation acceptance.

## Native build and data boundaries

Use [desktop/README.md](../desktop/README.md), [build-inference-host.ps1](../scripts/build-inference-host.ps1) and [Prepare-NativeRelease.ps1](../scripts/Prepare-NativeRelease.ps1) according to their declared parameters. Use fresh task-owned output/data directories; do not replay old `.dev` evidence paths or copy private binding graphs.

The native CLI currently has a separately documented RID/lock restore issue. Do not remove locked checks or rewrite package locks to pretend it passed. App/Core/Infrastructure tests do not prove that CLI publication works.

Only acquire model files from the pinned [manifest](../inference/WinSight.InferenceHost/model-manifest.json) after explicit authorization. There is no automatic migration of legacy data or permission to index real folders. GPU/NPU/provider options do not mean those devices are qualified.

Shared icon input is [Resources/AppIcon/WinSight.ico](../Resources/AppIcon/WinSight.ico); binary reference inputs and provenance are under [desktop/tests/Fixtures](../desktop/tests/Fixtures). Their byte preservation is separate from rendered UI and model quality acceptance.

Standard installer/recovery sources remain unmaterialized fail-closed templates. Source cleanup is not installation approval and does not reset the exhausted physical-install retry budget. Refer to the [current README](../README.md), [migration state](native-migration/status.json) and [release acceptance contract](native-migration/release-acceptance-contract.md) for outstanding gaps.
