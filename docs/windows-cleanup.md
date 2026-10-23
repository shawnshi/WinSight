# Windows-only source cleanup

## Contract and baseline

- User request: copy DigUp into a folder, then retain only Windows development content.
- Source: https://github.com/ARahim3/DigUp, commit `7db9537712995d14e50e0e44baa00f721b98f1c6` (224 tracked files).
- Copy: independent local repository on `windows-only`; upstream Git objects are the recovery point. `upstream-baseline.json` records original file hashes. No upstream push or publication is authorized or performed.
- Only files inside this copy may change. Existing tracked deletions in the parent WinSight repository must remain byte-for-byte equivalent in Git status.

## Invariants

Preserve the Windows frontend, Rust/Swift JSON-lines protocols, SQLite format/migrations, vector identity, model checksums, native-library pins, Windows helper lifecycle, installed-data locations, and Windows tests/fixtures. No changes to search ranking or model inference. Do not replace shared implementations merely to remove harmless conditional compilation.

## Write set and semantic changes

- Remove the SwiftUI macOS app, Apple-only extractors, non-Windows llama module maps, Sparkle dependency/lockfile, macOS signing/release assets and Linux package/config/update-feed files.
- Retain Windows icons and shared tray assets, source/tests/fixtures, evaluation query files and Windows-native dependency/license tooling.
- Narrow SwiftPM, Cargo direct dependencies, vendor/llama/FFmpeg scripts, notices, workflows and developer documentation to Windows x64.
- Remove the Apple MLX Python reference worker and its internal CLI override; the shipped llama.cpp worker remains.
- Remove the Wayland implementation and use the existing Windows value (`false`) for its persisted UI compatibility flag. Other shared platform guards remain where removing them would unnecessarily alter Windows logic or persisted structures.
- Keep upstream updater identity/endpoints unchanged; this is a local development copy, not a rebranded distribution. A fork must configure its own identity, update endpoint and signing key before distributing builds. No updater, model downloader, native dependency fetch/build script or installer was run. The Rust shell was compiled and unit-tested offline.

## Acceptance and failure modes

1. Required Windows entry points, assets, tests, native-library pins and dependencies must be present; dedicated deleted-platform targets must be absent.
2. Missing DLL/include paths, stale executable script references and broken manifests are failures, not empty results.
3. Vendor/build scripts must reject unsupported hosts before deletion/download/build; this negative path must be exercised safely.
4. Parse JSON/TOML/YAML/Python, syntax-check JavaScript/Bash, inspect Cargo metadata offline/locked, and compare preserved files with upstream hashes.
5. Independently review the final diff and actual validation evidence. Review is read-only and cannot expand scope.
6. Full Swift build/tests, Tauri compilation and installer/UI tests require the Windows Swift/MSVC/native dependencies. If unavailable, explicitly report them as unverified; static checks are not a successful build.

## Recovery

The original upstream commit remains HEAD; no commits are created by this cleanup. Restore a specific modified/deleted tracked path with `git restore --source=7db9537712995d14e50e0e44baa00f721b98f1c6 -- <path>` inside this repository. Remove only cleanup-created files if discarding them. Never reset or clean the parent WinSight repository. Review `git diff` before restoring any path that has subsequently been edited.

## Validation status

**Source cleanup complete; full application build and runtime acceptance remain unverified.**

- Removed 72 upstream tracked files; modified 24 retained upstream files; 128 upstream files remain byte-identical. The original baseline has 224 tracked files. New documentation and the source-check script are additional files, not included in those counts.
- `python scripts/check-windows-source.py`: **7 tests passed**, exit 0. Covers layout, manifests, compile-time assets, Python/JavaScript/Bash syntax, safe unsupported-host preflight, Windows update-feed fixtures and notices platform rejection. First-run failures and their repairs are retained in `source-checks.first-failure.txt`; the successful rerun is in `source-checks.txt`.
- `cargo test --offline --locked` from `shell/src-tauri`: **13 tests passed**, exit 0, after actual Windows Rust compilation. See `cargo-tests.txt`. No application or installer was launched.
- Windows-filtered Cargo metadata, both no-dependencies and full dependency resolution: exit 0 offline/locked. All 550 locked package identities, versions and checksums match upstream; only four non-Windows direct dependency edges were removed.
- SHA-256 comparisons confirmed the 49 selected critical files are unchanged, including all existing `Tests/` and `shell/ui/` files, search ranking, SQLite schema/database implementation, model identity/checksum definitions, llama runtime/model location logic, embedding patch and root license.
- Both retained GitHub workflows parse and target only Windows runners. JSON/TOML/Python and shell/frontend syntax checks passed; `git diff --check` passed. Ruff import-order checks passed with Python 3.11 as the target.
- A credential-pattern scan found three expected upstream synthetic private-key headers in the unchanged code tests/fixture generator, not introduced credentials. No new matching credential patterns were found. This is not a comprehensive security audit.
- Fresh LSP probing did not confirm six Swift/Rust paths within its wait budget; do not interpret that as clean compiler diagnostics. Python fail-fast JSON checks were reviewed as intentional test/packaging failure behavior. The actual Rust compile/test result is separate evidence.
- `independent_review_completed`: reviewer run `71ca0e9c-978c-4c59-b93d-5d89991ab0fb`, verdict **OK with notes**, no P0/P1/P2 issues found. See 历史本地审阅 `independent-review.md`（原文不随源码公开）. Review was read-only and did not rerun the tests.
- **Not verified:** Swift compilation/tests (Swift toolchain absent), real-model reference-vector execution, DLL loading through the application, installer end-to-end behavior and desktop UI operation. Do not describe this copy as a tested runnable installer or independent release.

## Parent-worktree observation

Only this copied repository was intentionally modified. A final status comparison found the parent `README.md` independently restored/edited during the task (`D` became `M`, file timestamp 2026-10-19 11:39:30 local); no tool in this cleanup wrote that parent file. It was left untouched. All other tracked parent status entries still matched `parent-status.before.txt`. The observed parent README SHA-256 was `13ee4a6da374d891360f11f4b607eea6fcb66de0c7cbaba76114e74b8ff754a5`; no restoration/reset/cleanup was applied to parent changes.

No Git commit, push, release, toolchain installation or GitHub Actions run was performed.
