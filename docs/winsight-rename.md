# WinSight rename and supplied icon

## Scope

User request: rename the project to WinSight and use the supplied `WinSight.ico` as its icon.

The original `DigUp-Windows` folder was locked by another process. It was not forcibly closed or deleted. Work continues in the sibling `WinSight/` copy, verified against all 164 snapshotted files and the original dirty Git status. Earlier edits, including Rust formatting already present before this request, are preserved.

Recovery snapshot: `.dev/winsight-rename-backup/`; manifest: `docs/winsight-rename-baseline.json`. These represent the current pre-rename tree, not merely upstream HEAD. Do not use `git reset` to recover this task, because that would lose the earlier cleanup and existing edits.

## Changes authorized by the rename

- Rename the Swift project/module/CLI targets and corresponding source/test directories; align build scripts and developer commands.
- Rename Rust package, Windows product/executable names, visible UI strings, application identity and default data directories to WinSight.
- Do not migrate, delete or open existing DigUp or WinSight user data. Identity/configuration changes are source-only until a build is run.
- Preserve upstream copyright/license text, provenance links, historical cleanup receipts, model checksums, vector identity, index format and search behavior.
- Import the supplied ICO unchanged; derive PNG/window/setup/tray assets locally from its existing icon frames. No image-generation service is used. Preserve distinct normal/dimmed/paused tray states.
- Do not reuse the upstream application's public update identity for the renamed product. Disable updates until a WinSight endpoint and signing key are deliberately configured; report 'not configured', never 'up to date', in that state. No endpoint, signing key, publication or toolchain installation is invented.

## Acceptance and failure modes

Check product/configuration/CLI names and references, asset existence and ICO integrity, fresh Rust compilation/tests, Python/JavaScript/Bash syntax, source/update-feed negative tests, local frontend rendering and interactions where available, and independent read-only review. Missing or malformed icons, stale paths, a renamed app contacting the old update channel, mismatched helper names, missing update configuration and errors reported as empty/up-to-date are failures.

The host lacks Swift; Swift compilation/model runtime/installed Windows application acceptance must remain explicitly unverified. Browser previews and Rust tests do not replace that evidence.

## Status

**Rename and supplied-icon integration complete. Independent review: OK with notes.**

- Historical local rename checkpoint: the repository working copy (machine-specific path omitted in this public summary); the original locked source copy was retained locally. This is not current release or installation approval.
- Windows product/executable name is WinSight; helper CLI is `winsight`; Swift engine module is `WinSightKit`; application identifier is `com.winsight.desktop`. Native setup/settings titles and tray labels/tooltip/IDs are aligned.
- Supplied `WinSight.ico` is preserved byte-for-byte. Window/setup/documentation PNGs and normal/dimmed/paused tray images are derived locally from its frames. Initial source integrity and mocked-Tauri browser preview checks passed.
- Initial full checks passed: 8 source tests and 15 Rust tests. Review identified WR-01 and WR-02, now repaired. Only the new native-branding source regression and model-path Rust regression were executed for that repair; both passed. The Rust test binary was freshly compiled; the latest targeted run executed 1 case and filtered 15, not a 16-case full run.
- `storage()` now resolves the same `%LOCALAPPDATA%\\WinSight\\Models` default as the Swift engine, with explicit `--models` taking priority. No user data was migrated or accessed by that resolver.
- `independent_review_completed`: follow-up reviewer run `a3422860-2c23-49f4-8069-ea195b28a8b0` closed both P1 findings with no new P1. Final verdict and evidence: 历史本地审阅 `winsight-review-followup.md`（原文不随源码公开）. The initial blocked report remains historical evidence, not the current verdict.
- Swift compilation, real-model execution, installed native-window/tray behavior and installer end-to-end operation remain unverified. This is source-change completion, not a runnable-installer or release certification.

No WinSight desktop application, installer, updater or model downloader was launched. No toolchain installation, Git commit, push or publication was performed. Existing independent README edits remain untouched. Development commands are in [windows-development.md](windows-development.md).
