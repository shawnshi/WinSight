# WinSight Installer Documentation

## Mainline Installer (per-machine by default)

The maintained, canonical installation path for WinSight is **`WinSight.iss`** (aliased by `WinSight.Simple.iss`), compiled via **`scripts/Build-NativeInstaller.ps1`** (or `scripts/Build-SimpleInstaller.ps1`) using Inno Setup 7 (Inno Setup 6 still works).

### Key Architectural Characteristics
- **One installer per CPU vendor**: `WinSight-<version>-x64-amd-setup.exe` ships `models\RyzenAI`, `WinSight-<version>-x64-intel-setup.exe` ships `models\IntelOpenVINO`; both carry the same common payload. `InitializeSetup` refuses to run on the other vendor's CPU (`/IGNORECPUVENDOR` bypasses this for tests). Build one with `-Vendor amd` or `-Vendor intel`; the default builds both.
- **Shared private .NET runtime**: every host (`WinSight.App`, `winsight-native`, `bridge`, `inference`, `inference\ocr`) is published framework-dependent with `AppHostRelativeDotNet`, so all of them load the single runtime in `{app}\runtime\` (`host\fxr` + `shared\Microsoft.NETCore.App\<version>`) and never a global .NET install. Host directories and launch paths are unchanged. On upgrade, generated `[InstallDelete]` entries remove the per-host runtime copies and the retired `inference\winml\` host left by earlier releases.
- **Payload storage**: the `[Files]` list is generated per file; content-identical files are stored once, and model weights are LZMA2-compressed (no `nocompression`).
- **Scope & Privileges**: `PrivilegesRequired=admin` with `PrivilegesRequiredOverridesAllowed=commandline`. The default install is per-machine and shows one UAC prompt; `WinSight-<version>-x64-<vendor>-setup.exe /CURRENTUSER` installs for the current user only (no elevation).
- **Binary Installation Directory**: `{autopf64}\WinSight\` (normally `C:\Program Files\WinSight\`), read-only application binaries and model weights. A `/CURRENTUSER` install resolves `{autopf64}` to `%LOCALAPPDATA%\Programs\`.
- **Mutable User Data Separation**: `%LOCALAPPDATA%\WinSight\NativePreview\` of each Windows user (SQLite database, settings, logs, crash reports, MCP bridge exchange). The `NativePreview` folder name is kept on purpose so existing indexes are not orphaned. Binaries and mutable state are isolated.
  - Caveat: the uninstall prompt that offers to delete this folder resolves `%LOCALAPPDATA%` for the account that runs the uninstaller. If an administrator account other than the daily user performs the uninstall, the daily user's data is neither touched nor offered for deletion.
- **Shortcuts & Shell Integration**:
  - Start Menu shortcut points directly to `WinSight.App.exe` (native WinUI 3 process, zero console popup or launcher script delays).
  - Desktop shortcut is opt-in (unchecked by default).
  - Optional Task: `addtopath` adds the install directory to `PATH` for CLI use: the system `PATH` (`HKLM`) for a per-machine install, the user `PATH` (`HKCU`) for `/CURRENTUSER`.
- **Process Termination**: Leverages Windows Restart Manager (`CloseApplications=yes`) with `CloseApplicationsFilter` covering all 7 WinSight execution roles:
  `WinSight.App.exe, winsight-native.exe, winsight-mcp.exe, WinSight.InferenceHost.exe, WinSight.OcrHost.exe, WinSight.WinMlHost.exe, WinSight.FoundryHost.exe`.
- **Uninstall Hygiene**:
  - Automatically cleans up the login item (`HKCU\...Run` of the account running the uninstaller).
  - Automatically purges `{app}` from the `PATH` it was added to.
  - Prompts interactively before removing index/settings/logs (defaulting to keep user data safe; silent uninstall preserves data).

### How to Build
```powershell
# 1. Prepare stage payload (platform models are required for the vendor installers)
./scripts/Prepare-NativeRelease.ps1 -StageDirectory <stage-dir> -IncludePlatformModels -OcrModelDirectory <ocr-dir>

# 2. Build the AMD and Intel installers with optional Authenticode signing
./scripts/Build-NativeInstaller.ps1 -StageDirectory <stage-dir> [-Vendor amd|intel] [-CompilerPath "C:\Program Files\Inno Setup 7\ISCC.exe"] [-CertificateThumbprint <sha1>]
```

---

## Historical Coordinator Archive (`legacy/`)

The `legacy/` subfolder contains archived templates from an earlier experimental multi-stage transaction coordinator (`NativeInstallCoordinator.psm1`, `Start-WinSightNative.ps1`, etc.). These files are preserved exclusively to satisfy frozen regression checks (`desktop/installer/tests/Test-NativeInstallFixes.ps1`). They are not active deployment code. Do not base new deployment work on them.
