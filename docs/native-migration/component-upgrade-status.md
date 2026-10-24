# Component upgrade — local source/integration complete and independently approved

## Exact implemented targets

| Component | Current local source identity |
|---|---|
| SDK / runtime | SDK11.0.100-rc.2.26504.105; runtime11.0.0-rc.2.26504.105; roll-forward disabled |
| Host / App BuildTools | 10.0.26100.9169 |
| OCR | Explicit RapidOcrNet.WinSight4.2.0-winsight.1 source revision; ORT/Managed1.29.0 isolated under ocr/ |
| Tokenizer | Official tagv0.23.3 -> commit301277072263fb3830eb66eec7e47adadf76f1dc; actual Rustcrate0.23.2, wrapper0.1.0, unchanged ABI1 |

**APPROVE_LOCAL_COMPONENT_UPGRADE_SOURCE** — independent follow-up `e10f8ce4-81f9-4ac1-bcca-826583d2929f` approved the four components' local source/integration scope. This is not deployment, full product qualification or Release approval. The vendor OCR patch changes only three inference error-to-default paths into rethrows; recognition algorithms remain upstream. Four pinned Chinese/English assets, CPU4 profile and strict image protocol2 are unchanged since the approved image round. No startup downloads or Windows OCR fallback.

## Tokenizer: independent source gate and actual runtime

Independent manual report3 (`0d2995b9-9be8-4471-9aba-f7ba9b0a548a`) returned **APPROVE_TOKENIZER_DEPENDENCY_FOR_INTEGRATION**, closing SUPPLY-EVIDENCE-01. ABI-EVIDENCE-01 was addressed by actual PE inventories: all168 exports unchanged, including5 application exports and inherited Oniguruma. Both changed packages have no custom build entrypoint; graph82->82 changes only tokenizers and daachorse1.0.1->3.0.3.

The old automated validator failed three times and remains failed. It was not repaired, rerun or replaced. Manual review covered20 entrypoints, referenced helpers, attributable partial integrity evidence, actual changed-package licenses and82 declarations. Google search was unavailable due location; direct public OSV queries reported no matches for the pinned Git source/version, not a no-vulnerability guarantee. Existing paste1.0.15 remains informational-unmaintained. Final distribution/legal notice closure is separate;9 selected native/Unicode/license texts are preserved.

A fresh offline/locked release build passed using task-local cache writes and existing public Git objects referenced read-only; this is not OS isolation. Original3 Rust tests passed with both synthetic and pinned real tokenizer JSON. Actual C# P/Invoke checks passed87 on old DLL and87 on the freshly built new DLL, with16 representative token sequences identical. These checks exercise handles, ID buffers, error strings and bounded cycles, not sanitizer/OOM safety.

The embedding profile honestly changed:
- Old: `ff5d2da5a51033ffa9f45c2ffdd58380a30f688d5ace863afaebde4acea807f2`
- New: `6d3f989520ddb56f3c3449417a56725db0626b117cde2a321532ebf5a3244566`

The actual production InferenceHostClient and fresh Host negotiated that transition with Windows ML ORT1.24.6 / CPU. Five English/Chinese/repeated-text/image/audio inputs produced finite normalized768-D vectors, representable as1536-byte FP16; repeated text difference0. No real user index/settings/media were opened. All11 public embedding artifacts remain fixed.

`scripts/build-native-tokenizer.ps1` emits a four-hash Cargo.toml/Cargo.lock/lib.rs/DLL binding. Host builds reject absent/stale bindings and changed DLLs;4 positive/negative guard tests passed. This prevents accidental old-DLL/new-profile mixes; it is not a signed attestation or same-user security boundary. Existing indices with another profile must use a separate empty native index for a rebuild; Force never mixes/reset metadata. No schema5 marker/dimension or real data ownership was changed.

## Validation snapshots — do not add overlapping counts

- Earlier .NET11 Windows-OCR/old-tokenizer snapshot: C#226. Not current whole-product acceptance.
- RapidOCR initial worker snapshot:22/22 before later CPU4 configuration.
- Image integration:37/38,37/38,22/38 retained. Owner explicitly authorized round4:38/38,41.45s total, dense16.28s/exact8000 UTF-16/explicitpartial, unchanged25s inner and30s dense caller deadlines, no residual task processes/directories. No fifth image run.
- Current focused regression: Inference48, Storage27, Indexing29. Initial inference47/48 used dotnet instead of the apphost required by its forced-owner-exit oracle; fixed only the task launcher. Initial Indexing build exposed missing shared OCR identity/resource links in the sliced test project; links restored, original assertions unchanged.
- Fresh framework-dependent Host build and real three-modality/profile smoke passed. App/Core/Infrastructure/OCR .NET11 build evidence is retained separately; no new GUI/self-contained qualification.
- Source checks17. Publishing/CI scripts are syntax/static checked only, not executed end to end. Hosted CI remains unverified; model-free CI is not a full OCR/model package.

## Remaining boundaries

The initial combined review (f49d3cad-f236-4e02-825b-ede9bc1a233a) blocked on P1 OCR-LAUNCH-RETENTION-01: Start could throw before the worker was assigned, and unknown settlement then released the executable pin/task material. The caller now retains on either MediaTerminationException or an unsettled returned worker, matching the existing outer client. Targeted baseline4/5 versus patched5/5 demonstrates the pre-assignment failure plus confirmed/unconfirmed run failures and success; a fresh Host build passed. WIC PNG preparation and FileShare protection are real; process/path helpers are controlled test stubs, not a genuine unconfirmed-OS-termination run. Earlier test-harness build/dependency failures are retained. No image/installer/CLI/tokenizer-audit round was added. Independent follow-up `e10f8ce4-81f9-4ac1-bcca-826583d2929f` inspected the actual source, raw red/green evidence and scope addendum, found no issues, and confirmed the P1 resolved. Its approval retains the initial four-component scope and all broader qualification exclusions. The reviewer performed no test execution. LSP still reports linked-project/dependency context findings; passing actual builds does not establish all-LSP-clean.

Native CLI is static-retarget-only: original RID/lock failures stopped at3; no new CLI restore/build. Genuine installation stopped at3; no additional install/Release. No Git stage/commit/push or real data migration. Prior SDK first-use output claimed a development-certificate installation; actual creation is unproven without a before baseline; no private-key/deletion/trust operation occurred, and later SDK commands disable generation.

All earlier failures, SDK/retirement work, recovery journals, models and stashes are preserved. Current task baseline/evidence: `.dev/component-upgrade-04/`; previous image receipts: `.dev/component-upgrade-03/image-acceptance-04/`. Recovery is file-scoped from the already-dirty baseline after checking later changes, never reset/clean or stash manipulation.
