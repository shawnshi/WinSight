# WinSight native migration execution contract

Status: original full-parity migration remains incomplete. This document is not a release or cutover receipt.

## 2026-10-23 source-only retirement decision

The owner explicitly approved native-only maintenance and abandonment of the old Swift/Tauri program and CLI/JSONL compatibility. [legacy-retirement.md](legacy-retirement.md) supersedes only the old requirement to keep those source/build paths operational. Shared assets, reference inputs, licenses and native tokenizer remain. This is not proof that the original 47-command parity contract, independent full-product review, NM-P5 or NM-P6 passed. The original stage evidence below remains historical. No production data migration, toolchain uninstall, new physical installation or external client reconfiguration is authorized by this source cleanup.

## Scope and decisions

- User authorized P0–P6 on the staged WinUI 3/.NET migration proposal. Route `/forge`, L3, Refactor.
- The implementation baseline is the CURRENT inner `WinSight/` checkout, not upstream HEAD and not the deleted outer .NET scaffold.
- Preserve existing dirty changes. No staging, commits, reset, push, production install or access to personal indexes/settings/models during this task.
- New source lives under `desktop/`. During validation it is explicitly a NativePreview, using a separate data directory. The original keep-Swift/Tauri-operational condition is superseded by the explicit source-only retirement decision above; full-parity acceptance remains unpassed.
- Target: WinUI 3 + .NET 10; Core contracts, Infrastructure, App, existing managed inference host. No HTML/WebView interface or Swift/Tauri dependency in the candidate.
- Keep the existing managed inference subprocess initially. This intentionally defers the older architecture guide's single-process consolidation.
- Keep the tokenizer's native Rust C ABI. Removing Tauri does not remove an independently required tokenizer.
- Do not combine the UI migration with sqlite-vec, Float32 storage redesign, new lexical tokenization, MCP or automatic external client configuration.

## Frozen baseline and recovery

`.dev/native-migration/p0/manifest.json` records 236 task source/resource files and SHA-256 values. `source/` contains their recovery copies. `status-before.txt` records the original dirty state; excluded paths include `.git`, `.dev`, build artifacts and package caches. The baseline script itself and initial new Core skeleton were included; subsequent task changes are not claimed to be pre-existing source.

Task lane worktrees: `.dev/native-migration/lanes/{ui,storage,indexing,inference}`. Each has its own working directory and Git index, starts from a copied current-source snapshot and must not stage or commit. Parent alone integrates into the main checkout. Their metadata still shares the Git common object store; these are code-directory isolation, not OS sandboxes.

Recovery is file-scoped: compare current bytes with the task diff, restore only task-owned changes from the recovery copy, and preserve changes made after the freeze. Never reset the original checkout. No production database has been migrated, so code rollback must not imply database rollback was tested.

## Protected behavior

`command-inventory.json` maps all 47 registered Tauri commands. Initial parity status is pending, not passed.

- Settings: camelCase fields; index hours are minutes since midnight; legacy Windows default shortcut is Ctrl+Shift+Space.
- SQLite: `index.sqlite` and independent `code.sqlite`; old schema 5; segments carry 768 Float16 values. Never open an unknown/legacy database and silently clear or upgrade it.
- Model handshake: protocol 1, raw task-prompt text, 768-dimensional finite normalized vectors, profile identity. Equal dimension alone is not compatibility.
- Shared metadata names: `profile_id`, `embedding_info`, `indexing_fingerprint`. Force never bypasses identity checks.
- Index states: pending/done/partial/skipped/failed, meaningful detail, safe deletions only after a successful authorized scan.
- Failures must not become empty-data success. Query cancellation and out-of-order replies must not display stale results.
- Preserve media/page/line locators and supported formats, or explicitly block parity/cutover. Do not silently drop audio/video/code support.
- Inventory external CLI/JSONL callers before retiring their executable/protocol. A new preview CLI is not evidence of old CLI compatibility.

## Acceptance gates

| ID | Stage | Required evidence |
|---|---|---|
| NM-P0 | Baseline | Source recovery copy, command inventory, protected contracts, explicit gaps and rollback path |
| NM-P1 | Native shell | Build plus actual Windows launch, single instance, tray/hotkey conflict/cleanup, hide vs exit, DPI/theme/keyboard/accessibility checks |
| NM-P2 | Search slice | Synthetic index -> real search -> result -> safety-checked open/reveal; stale result/cancel/error tests; model-backed evidence separately identified |
| NM-P3 | Engine parity | Storage/search/index/extraction/media/code/profile tests and differential fixtures vs protected source contracts |
| NM-P4 | Integration | Atomic config save/reload, authorized folders, watcher convergence/deletion safety, indexing schedule, startup/editor/clipboard/drag behavior |
| NM-P5 | Original full-parity retirement | All required parity gates and independent full-product review pass; still unaccepted. Separate source-only abandonment removes old paths without claiming this gate passed |
| NM-P6 | Distribution | Self-contained .NET and App SDK publish, correct inference/native/model/licenses payload, dependency audit, clean Windows install/upgrade/uninstall and recovery |

A build, a running process, a static check or a test count cannot alone satisfy the behavioral gates. Do not mark NM-P5/NM-P6 complete if models/media/installation/independent review are missing. Do not use source-only retirement to manufacture full-parity or release acceptance. Recoverable historical source is at commit `400c5b501e664f7aaaf2c2ec5e1c270cf0425a87`; its executable/protocol compatibility is deliberately no longer offered.

## Verification constraints

Use deterministic synthetic fixtures under `.dev/native-migration/` or per-test temporary directories. Do not start watchers on the user's real roots, enable real autostart, change PATH, install the application, download providers or read private indexes during tests. Public build dependencies may be restored into task build outputs with pinned sources/lockfiles; no global toolchain installation. Any further public model acquisition must use the checked manifest/revision and a task-scoped destination; never adopt an unverified cached model as evidence.

Independent reviewer receives original requirements, rules, actual diff and executed evidence, not a generation narrative. Until that review completes record self-checks only, not `independent_review_completed`.
