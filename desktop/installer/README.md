# WinSight Installer Documentation

## Mainline Per-User Installer

The maintained, canonical installation path for WinSight is **`WinSight.iss`** (aliased by `WinSight.Simple.iss`), compiled via **`scripts/Build-NativeInstaller.ps1`** (or `scripts/Build-SimpleInstaller.ps1`) using Inno Setup 6.

### Key Architectural Characteristics
- **Scope & Privileges**: Standard Windows per-user installation (`PrivilegesRequired=lowest`). Runs entirely in standard user token; zero UAC administrative prompts or system-wide modifications.
- **Binary Installation Directory**: `%LOCALAPPDATA%\Programs\WinSight\` (read-only application binaries and model weights).
- **Mutable User Data Separation**: `%LOCALAPPDATA%\WinSight\NativePreview\` (SQLite database, settings, logs, crash reports, MCP bridge exchange). Binaries and mutable state are strictly isolated.
- **Shortcuts & Shell Integration**:
  - Start Menu shortcut points directly to `WinSight.App.exe` (native WinUI 3 process, zero console popup or launcher script delays).
  - Desktop shortcut is opt-in (unchecked by default).
  - Optional Task: `addtopath` allows users to optionally add `%LOCALAPPDATA%\Programs\WinSight` to the current user's `PATH` (`HKCU\Environment\Path`) for convenient CLI invocation.
- **Process Termination**: Leverages Windows Restart Manager (`CloseApplications=yes`) with `CloseApplicationsFilter` covering all 7 WinSight execution roles:
  `WinSight.App.exe, winsight-native.exe, winsight-mcp.exe, WinSight.InferenceHost.exe, WinSight.OcrHost.exe, WinSight.WinMlHost.exe, WinSight.FoundryHost.exe`.
- **Uninstall Hygiene**:
  - Automatically cleans up the user's `HKCU\...\Run` startup key.
  - Automatically purges `{app}` from user `PATH`.
  - Prompts interactively before removing index/settings/logs (defaulting to keep user data safe; silent uninstall preserves data).

### How to Build
```powershell
# 1. Prepare stage payload
./scripts/Prepare-NativeRelease.ps1 -StageDirectory <stage-dir> -ModelDirectory <models-dir> -OcrModelDirectory <ocr-dir>

# 2. Build installer with optional Authenticode signing
./scripts/Build-NativeInstaller.ps1 -StageDirectory <stage-dir> [-CompilerPath "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"] [-CertificateThumbprint <sha1>]
```

---

## Historical Coordinator Archive (`legacy/`)

The `legacy/` subfolder contains archived templates from an earlier experimental multi-stage transaction coordinator (`NativeInstallCoordinator.psm1`, `Start-WinSightNative.ps1`, etc.). These files are preserved exclusively to satisfy frozen regression checks (`desktop/installer/tests/Test-NativeInstallFixes.ps1`). They are not active deployment code. Do not base new deployment work on them.
