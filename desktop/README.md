# WinSight native desktop candidate

This is the WinUI 3/.NET 10 migration candidate, not a completed production cutover. The public standard-installer/recovery sources are unmaterialized, fail-closed templates; private roots and historical qualification artifacts are deliberately not published. The existing Swift/Tauri chain remains until the migration acceptance gates pass. See `../docs/native-migration/plan.md` and `command-inventory.json`.

## Boundaries

- `src/WinSight.Core`: UI-independent contracts and validation.
- `src/WinSight.Infrastructure`: SQLite/FTS5, search/index/extraction, settings and one managed inference-host client.
- `src/WinSight.App`: native XAML windows, tray, hotkey and application lifecycle.
- `src/WinSight.Cli`: `winsight-native`, an explicit preview CLI, **not** a compatible replacement for every legacy command.
- `tests`: synthetic, isolated executable suites. Fake model hosts never ship with production source.

The candidate does not use Swift, Tauri or a WebView UI. Its embedding tokenizer still uses a native Rust DLL. The existing separate .NET inference process is intentionally retained rather than consolidating UI and model lifetimes during the same migration.

## Safe development

Use the pinned SDK from `global.json`. No global tools are installed by these scripts.

```powershell
# From the inner WinSight repository:
pwsh -NoProfile -File scripts/Test-NativeDesktop.ps1 -IncludeApp
python scripts/check-native-candidate.py
python scripts/audit-native-packages.py

# Run only against an explicit task-owned directory:
dotnet run --project desktop/src/WinSight.Cli -- help
dotnet run --project desktop/src/WinSight.Cli -- status --data-dir C:\TaskFixtures\WinSightNative
```

The graphical app defaults to `%LOCALAPPDATA%\WinSight\NativePreview`, never automatically imports legacy settings or indexes, and supports `--data-dir ABSOLUTE_PATH` for isolated verification. No folders are inferred. Background indexing and autostart are not authorized by defaults. Configure a folder explicitly before indexing; choose only synthetic files for development.

The CLI's keyword mode does not start the model. Semantic/index/model-info operations require a properly built inference host and the exact model manifest artifacts. Missing model files, identity mismatch or runtime errors remain visible failures, not empty successful data. Equal dimensions do not imply equal model profiles.

The repository owns the keyword-only filename segment. Extractors preserve the full chunk/page/line body in `IndexSegment.Excerpt`; the UI may shorten presentation but must not truncate before FTS storage.

## Session pause candidate

The current source implements real session-only pause/resume for indexing, shared by the main window and tray. It cancels active/queued work, waits for quiescence before reporting Paused, and leaves search and saved background authorization unchanged. Backend and non-render UI tests pass. The separate UI harness still has an exhausted three-attempt rendered-startup blocker. A newly deployed production App subsequently passed cold-start, actual pause/resume button automation and two basic viewport checks; this does not close independent review or complete visual acceptance. See `../docs/native-migration/deployment-verification.md` and the earlier `reviews/p4-pause-integration.md`.

The previously staged `.dev/native-migration/release/app` and first deployment candidate remain unchanged. The latest isolated candidate is under `.dev/native-migration/deployment-checks/n2b-native-20261019/candidate/app`; its sibling `Launch-Preview.cmd` fixes an explicit task data directory. It includes the model-free native helper but no real models. Actual cold-start, pause/resume UI Automation and helper metadata checks passed; see `../docs/native-migration/n2b-deployment-verification.md`. No external distribution or legacy cutover is approved.

## Native image EXIF/OCR

The native backend now runs model-free Windows OCR and whitelisted photo metadata inspection inside the existing bounded media process. English/Chinese OCR, all eight EXIF orientations, GPS exclusion, explicit blank/partial/failure semantics and physical cancellation have synthetic acceptance evidence. OCR/metadata text joins the existing visual input without changing the model profile or SQLite schema. See `../docs/native-migration/image-exif-ocr-contract.md` for limits, evidence and the still-open independent-review/install gate. No language pack or model is downloaded.

## Native media leaf command

The inference host now accepts `extract-media ABSOLUTE_REQUEST_JSON` before any model/provider initialization. It extracts one explicit audio interval (PCM16, mono, 16 kHz, at most 30 seconds) or one bounded video frame using WinRT. N1 has bounded independent approval and 111 passing suite cases, including actual-host EOF checks and mutation-sensitive tail validation. See `../docs/native-migration/reviews/native-media-recheck.md`.

Native audio/video sampling is now connected to the candidate DesktopBackend through an independently approved per-run controlled client and typed artifact leases. N2b-1 sampling/index consumption has 102 passing synthetic cases; N2b-2 has 29 public-backend cases (fake embeddings, including real model-free WAV decoding), eight settlement-guard cases and five bounded-reader checks. See `../docs/native-migration/reviews/n2b-runtime-review-state.json`. Audio uses 30-second windows/25-second hop; video frames and soundtrack preserve original-source locators and explicit partial/failure semantics. Native shutdown failure keeps admission fenced rather than falsely Paused. This is **not real-model or all-format parity**; the no-model preview cannot demonstrate model-driven media semantic indexing. WinRT cancellation alone is not a hard stop. Use only explicit synthetic task files/directories; no model, codec or provider is downloaded.

## Retrieval and desktop policies (WS-01–WS-06)

Keyword candidate limits apply to distinct files after ordinary/CJK candidate metadata is merged and ranked by query-term coverage; only selected excerpts are hydrated. CJK substring lookup uses a connection-local `fts5vocab` view of existing tokens; the persistent schema5 layout, tokenizer, native marker and read-only legacy-index contract remain unchanged. No live-index migration or rebuild is performed. CJK lookup scans the token vocabulary, not a dedicated substring index.

Semantic search retains the existing modality-centered Z gate when a modality has at least five calibration segments. Smaller calibration sets use an explicit cosine floor of 0.5 and return a small-sample notice. This is a deterministic fallback policy, not a claim of real-model relevance calibration.

Keyword search loads matching file headers and evidence only. Semantic search caches vector-bearing segments and their associated excerpts for a stable SQLite snapshot; local writes invalidate the cache, and `PRAGMA data_version` invalidates it after external commits. All authorized roots are searched together, with authorization checked before candidate/result limits. Cold semantic queries still load the vector corpus, retain its associated text, and perform linear cosine scoring; this is not an ANN index or a constant-memory guarantee.

Hotkeys require a modifier and exactly one supported key. Invalid tokens, empty components, duplicate modifiers and ambiguous chords are rejected rather than becoming Space. Binding replacement uses a spare registration ID before releasing the working binding. Registration conflicts preserve the previous chord. Both ordinary Save and Apply Hotkey use the same serialized registration/persistence path; a binding revision prevents an older failed save from rolling back a newer binding. Failed preference saves attempt to restore only the binding still owned by that save.

The historical `processorOnly` preference now means **automatic indexing on AC power only**, not CPU throttling or execution-provider selection. Battery/unknown power defers automatic work; a power-read error is reported and fails closed. The coordinator polls policy every 30 seconds, cancels active automatic work when AC/schedule permission is lost, waits for it to settle and reconciles on permission restoration. Explicit manual indexing remains available. Power-unrelated regression fixtures opt out explicitly so their results do not depend on the test host's battery state.

Regression entry points are `WinSight.Storage.Tests`, `WinSight.Runtime.Tests`, `WinSight.DesktopPolicy.Tests`, `WinSight.Indexing.Tests`, `WinSight.Pause.Tests` and `WinSight.IndexingSettlement.Tests` under `desktop/tests`. The new desktop-policy suite links the actual parser/service/settings view-model with a controlled registrar; it does not claim real OS hotkey-conflict acceptance. Runtime manual-index integration needs the built `WinSight.Inference.Tests/FakeHost` executable (or its absolute path as the first argument). Pause tests require that fake-host path plus a new absolute evidence directory; settlement tests require one new absolute evidence directory. Use the task evidence logs for actual pass counts, not this list of entry points.

## Dependencies

Direct versions are pinned in each project and restore lockfiles are retained. The new implementation uses official `PdfPig [0.1.16]` (Apache-2.0), not the old blueprint's `UglyToad.PdfPig 1.7.0-custom-5`, whose package lacks explicit license/provenance metadata. The namespace remains `UglyToad.PdfPig`; see the official [package metadata](https://api.nuget.org/v3-flatcontainer/pdfpig/0.1.16/pdfpig.nuspec) and [project](https://github.com/UglyToad/PdfPig).

`audit-native-packages.py` requires the already installed `defusedxml` module. It checks restored production package graphs against cached archives, sources and license metadata. Raw signed-archive SHA-512 and NuGet content hashes are different values; each is checked against its own authority. This metadata audit is not signature verification, a vulnerability check or distribution approval. Those checks are separate receipts.

## Distribution is gated

`Prepare-NativeRelease.ps1 -AppOnlyPreview` can stage an incomplete self-contained development candidate. Without that switch, explicitly supply the model directory; every model byte is checked against the repository manifest. The script does not discover private model caches, install the app, modify PATH, register clients, sign or publish anything. Its release manifest always states that distribution is not yet approved.

A staged folder is not an installer acceptance result. No P5 source retirement or P6 release claim is allowed until media/search/config/UI/installation/recovery and independent-review gates are satisfied. Current omissions must remain documented, not hidden by deleting the old implementation.
