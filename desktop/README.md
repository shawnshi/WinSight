# WinSight Native Desktop Candidate

> 当前仅维护原生预览路线。Swift/Tauri 及旧 CLI 兼容已按产品取舍退役；源码清理不代表完整行为对齐、安装或发布验收通过。详见 `docs/native-migration/legacy-retirement.md`。

This is the WinUI 3 / .NET 11 desktop application candidate. The candidate does not use Swift, Tauri or a WebView UI. Its embedding tokenizer uses a native Rust DLL. The separate .NET inference process is intentionally retained rather than merged into the UI host so that GPU/NPU execution provider crashes or high memory commit spikes do not destabilize the desktop interface.

## Project Structure
- `src/WinSight.App/`: WinUI 3 desktop client (Mica Alt, DesktopAcrylic HUD, XAML views, ViewModels, tray service).
- `src/WinSight.Cli/`: CLI command-line tool (`winsight-native.exe`).
- `src/WinSight.Bridge/`: MCP bridge for AI agents (`winsight-mcp.exe`).
- `src/WinSight.Core/`: Contracts, logging, diagnostics, update checker.
- `src/WinSight.Infrastructure/`: SQLite storage, FTS5 dual tables, indexing queue, media sampling, and runtime coordination.
- `installer/`: Inno Setup 6 per-user installer (`WinSight.iss`).
- `tests/`: Component test suites and UI harnesses.
