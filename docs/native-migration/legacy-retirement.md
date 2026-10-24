# Legacy source retirement: native-only maintenance

Decision date: 2026-10-23. Scope: source-only retirement approved by the owner, not full-parity cutover or release acceptance.

## Target and compatibility

Maintain only `desktop/`, `inference/` and the independent `native/winsight-tokenizer/` runtime. The old Swift program, root Swift tests, Tauri shell and dedicated vendor/build/package/update tooling are retired. No compatibility shim for the old executable, 47-command Tauri interface or CLI/JSONL protocol is offered. The native CLI remains a different interface.

Repository caller inventory found the old Tauri dispatcher, Swift entry point and dedicated scripts in the retired set. Out-of-repository callers were not scanned and no private application/MCP configuration was read or changed. Users of old executable names or protocols must keep their old version or migrate explicitly; this cleanup does not automatically redirect them to the native CLI.

## Preserved contracts and resources

- No changes to native C# business code, NuGet locks, Rust tokenizer source/lock, model manifest/profile or native schema5/768-dimensional FP16 identity.
- Legacy indexes remain read-only/not adopted; no migration, rebuild or user data access occurred.
- Canonical ICO and retained derivatives moved out of the shell into `Resources/AppIcon/`; hashes remain in `docs/winsight-icon-assets.json`.
- Nine binary reference inputs moved into `desktop/tests/Fixtures/`; `provenance.json` records original paths, sizes and hashes. Preservation is not media/model parity acceptance.
- The checked-in `command-inventory.json` and existing historical inventory/freeze receipts remain comparison evidence where available. No missing private receipt is recreated or treated as a pass. Their old source-scanning generators are retired; deleting a generator does not turn pending inventory rows into passed behavior tests.
- Root LICENSE/NOTICE, DigUp MIT and acknowledgements remain. The old Swift license text moved to `licenses/historical/` rather than being discarded. Native dependency/model redistribution licenses still require their own release review.

## Gates and changed meaning

The original migration plan required keeping the old runtime operational until all parity gates passed. The owner instead chose to abandon that compatibility and remove its source/build paths. This decision supersedes that retention requirement only. Original NM-P3/NM-P4 gaps, full-parity NM-P5, full-product independent review and NM-P6 distribution acceptance are not cleared by this decision.

The known native CLI RID/lock mismatch remains unmodified. Its three surfaced attempts are preserved; no further CLI restore/build attempt is part of this cleanup. Installation attempts remain exhausted at three; no Setup, recovery qualification, genuine callback/NoCommit or production entry was run. Public standard-installer templates remain fail-closed.

## CI and verification scope

The push/PR workflow now checks native layout, package/model declarations, immutable shared assets, fixture provenance, rejected XML entities and malformed inputs, then tests `native/winsight-tokenizer` rather than `shell/src-tauri`. Those ABI unit tests use the explicit synthetic WordLevel fixture in `desktop/tests/Fixtures/Tokenizer/abi-tokenizer.json`, not downloaded model assets; original Rust assertions remain unchanged. A separate local run with the pinned model tokenizer must not be conflated with this small CI fixture. The XML checker reuses the project's existing `defusedxml`, explicitly fixed to `0.7.1` in this workflow. This adds no native runtime package or model. Source checks are not a substitute for .NET build, UI/model integration or installation gates.

Local acceptance must retain actual source-check/tokenizer/native regression output and an independent clean-context review of this deletion. Historical Swift/Tauri tests are intentionally retired, not silently reported as passing native tests. No new package/model/toolchain download or genuine installation is needed to prove this source-only change.

## Recovery and non-goals

The pre-retirement source is recoverable at commit `400c5b501e664f7aaaf2c2ec5e1c270cf0425a87` and the local task baseline `.dev/legacy-retirement-01/baseline/source/`. Restore only task-owned paths after checking for later user changes; never reset/clean the repository or apply/drop unrelated stashes.

Ignored caches, models, pending/quarantine recovery evidence, system toolchains and other worktrees are untouched. This task does not stage, commit, push, create a tag/Release, uninstall software or publish an installer. Local source removal does not claim those external actions happened.
