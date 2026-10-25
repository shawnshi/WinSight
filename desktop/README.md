# WinSight native desktop candidate

> 当前仅维护原生预览路线。Swift/Tauri 及旧 CLI 兼容已按产品取舍退役；源码清理不代表完整行为对齐、安装或发布验收通过。详见 `docs/native-migration/legacy-retirement.md`。

This is the WinUI 3/.NET 11 RC2 migration candidate, not a completed production cutover. The public standard-installer/recovery sources are unmaterialized, fail-closed templates; private roots and historical qualification artifacts are deliberately not published. The old Swift/Tauri runtime and CLI compatibility were explicitly abandoned under the approved source-only retirement; their source/build paths are removed without claiming full migration acceptance. See `../docs/native-migration/plan.md` and `command-inventory.json`.

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

The CLI now declares the win-x64 RID explicitly. App, CLI, InferenceHost, and OCR host passed task-local locked restore and self-contained publish with unchanged dependency locks; the App includes Windows App SDK runtime files. These are build-boundary checks, not full CLI behavior acceptance. A separate private local package also completed standard installation and a bounded responsive-main-window startup; the public installer remains fail-closed. See [local installation and startup scope](../docs/native-migration/local-install-validation.md). The SearchPage synthetic preview/overlay/keyboard evidence below remains distinct from installed-GUI startup, all-DPI, hardware-keyboard, Office, and whole-product acceptance.

The graphical app defaults to `%LOCALAPPDATA%\WinSight\NativePreview`, never automatically imports legacy settings or indexes, and supports `--data-dir ABSOLUTE_PATH` for isolated verification. No folders are inferred. Background indexing and autostart are not authorized by defaults. Configure a folder explicitly before indexing; choose only synthetic files for development.

The CLI's keyword mode does not start the model. Semantic/index/model-info operations require a properly built inference host and the exact model manifest artifacts. Missing model files, identity mismatch or runtime errors remain visible failures, not empty successful data. Equal dimensions do not imply equal model profiles.

The repository owns the keyword-only filename segment. Extractors preserve the full chunk/page/line body in `IndexSegment.Excerpt`; the UI may shorten presentation but must not truncate before FTS storage.

## Structured document search — local candidate

Current source adds PPTX slide title/body/table/notes text, XLSX worksheet/header/cached-cell/row context, and DOCX paragraph/table anchors. Word comments and header/footer parts are opt-in through `includeWordComments` and `includeWordHeadersFooters` in settings, both defaultfalse; no new settings checkbox is claimed. Search results carry/display slide, worksheet/range or paragraph labels. Ordinary Open remains unchanged. Explicit backend APIs now provide fingerprint-checked previews and bounded read-only Word paragraph / Excel range / PPT slide navigation; SearchPage now includes explicit preview and navigation actions, with a separately scoped synthetic GUI acceptance below. See [source-integration scope](../docs/native-migration/source-integration-status.md).

Chinese search uses NFKC width normalization, Windows `LCMapStringEx` character simplification, mixed-script boundaries and bounded CJK bigrams/substring recall; original excerpts remain intact. This is not a linguistic dictionary or context-aware traditional/simplified conversion. Literal project/model/version tokens preserve punctuation. Filters include `project:PRJ-2026-001 amount:128000.50`, `sheet:"项目清单" cell:B2`, `date:2026-10-23`, `version:v2.5.1`, and `folder1:政策 folder2:2026 folder3:华东`. Directory levels are relative to the root that indexed the file, not arbitrary absolute path components. Amounts match the stored unit's decimal value; no currency/unit conversion or date-role inference is performed.

Source anchors/fields are versioned optional `files.info` metadata keyed by actual segment IDs, committed in the same transaction; no schema5 table/column/marker or FP16/model-profile change. Old anchors/normalized keyword bodies require a new authorized indexing pass; no real user index is rebuilt by this source change. Word extraction options participate in the extraction revision so unchanged files are not falsely current after an options change.

Independent keyword/name/folder and per-source semantic channels use bounded reciprocal-rank fusion, deduplicating repeated chunks within each channel/source unit. Body, OCR and pagevisual vectors are never averaged. `SearchHit.Score` is now normalized RRF ranking score, not the previous additive number or a probability. Two old test assertions were deliberately retargeted: fixed lookup score4 → exact normalized RRF/channel assertion; flattened DOCX text → separate paragraph/control/anchor assertions. Original failures remain in task evidence.

Current synthetic document48, Storage27 and Indexing29 cases pass; Core/Infrastructure/App builds pass. Tests use fake vectors, not real OCR/models/PowerPoint or GUI rendering. Independent follow-up `ad0eba9e-46d6-4d2d-ad68-c9e81ff580d9` approved LOCAL_DOCUMENT_SEARCH_PHASE1_SOURCE and closed DS01-R01..05 after inspecting the10-case red/green and prior regression artifacts; the reviewer executed no tests. Keyword and field caps now count distinct source units, coverage cannot be assembled from unrelated units, normalized header aliases establish context from one typed column, text-PDF fields are page-local, and numeric IDs/zeros are exact across body/name/folder channels. PPTX whole-slide visual rendering is now a separately approved local PowerPoint source integration; bounded read-only Office exact-position navigation is separately accepted at the non-GUI API boundary; scanned-PDF pages/OCR text-line geometry are implemented in the separately approved local-source slice below; embedded PPTX pictures are not advertised as rendered slides. ZIP/XML expansion, DTD, macros, malformed input, hidden sheets/rows, formula-cache freshness and content/segment bounds fail or report partial explicitly; formulas, macros and external links are not executed.

## Native PDF pages / OCR geometry — local source approved

This candidate renders PDF pages through Windows.Data.Pdf, normalizes DIPs to canonical pixels, and pins each PNG until embedding finishes. Short/scanned pages receive real RapidOCR text-line rectangles linked to recognized text. Results persist page IDs and separate body/OCR/pagevisual vectors; they do not average modalities or persist temporary PNG paths. Limits: first32 pages,1600pixel long edge, one180s page budget; incomplete coverage is partial. Only the new render Job has a512MiB commit cap, not sandbox isolation.

Independent review `97becc9f-044f-45c6-850f-ad38a85ae81d` approved LOCAL_DOCUMENT_PAGES_SOURCE with no new issues; the reviewer executed no tests. Parent-produced native cases34/34 (including raster-only and existing-text-layer scans), document48, original-review10, Storage27, Indexing29 and parent-lifecycle5 pass as separate/overlapping suites, not a product-acceptance total. Actual raster-only PDF rendering/OCR/indexing and one real CPU embedding smoke are recorded in `.dev/document-pages-01/`. The smoke predates the render-only memory cap and raster/text-layer classification enhancement; the latest34-case native run covers those changes without claiming a repeated model smoke. Image protocol3, inner OCR2 and extractionv3 replace the geometry-free candidate. Old38-image acceptance was not rerun or applied to this new protocol. The later bounded GUI covers synthetic previews/boxes and callbacks, not live Office navigation from the UI or a broad document corpus. The user selected installed PowerPoint and accepted idle-only exclusive rendering; the local source integration below has independent approval, not full-product acceptance.

## PowerPoint whole-slide integration — local source approved

`IndexRequest.AllowPowerPointAutomation` defaultsfalse: background indexing never starts Office, retains text and records manual-index-required partial coverage. Manual Index Now opts in and displays a keep-PowerPoint-closed warning. Already-running PowerPoint/renderer contention yields deferred visual coverage, never attaches to an existing instance; unchanged busy/manual-required/permanently-unsupported partials can skip repeat embedding until conditions change. Do not open PowerPoint during the explicit manual interval; PowerPoint MultiUse is not isolated from a manual join in that interval.

The helper uses one explicitly suspended-born Job-owned, cache-only Microsoft-signature-verified PowerPoint process per deck; sole process/PID and COM installation path are checked. It opens only a read-only untitled task copy with macros disabled. Direct Slide.Export includes hidden slides and canonicalizes palette PNGs to RGB via native WIC plus bounded PNG encoding. Up to32 slides,1600pixel edge,64MiB accepted PNGs,180s page stage; render worker50s under client60s. Source and all accepted PNGs stay pinned through embedding; uncertain consumers retain batch material. ActiveX/OLE/embedded packages, SVG, macros and any external relationship are unsupported visual content and explicitly partial, not silently stripped. Office itself may update profile/cache/MRU; this is not a credential/network/OS sandbox.

Read-only independent review `bc10d66e-822e-4362-9a4f-f878a8497abd` approved `APPROVE_LOCAL_POWERPOINT_PAGES_SOURCE` with three retainedP2notes: Office side effects/exclusive interval, transient native-export disk use, and LSP diagnostics. The reviewer ran no tests. The64MiB limit is accepted-PNG payload, NOT an OS disk quota; no strict32MiB native-write overshoot proof is claimed. Actual seven builds pass, but OCRWorker01 retains8 vendor nullable warnings;12-path LSP58 is not declared clean.

`.dev/powerpoint-pages-01/`: new PPT boundary38/38 and indexing17/17 use real local PowerPoint/RapidOCR with own ordinary/image-only/hidden/unsafe fixtures; embedding vectors are synthetic768D, not a real-model relevance benchmark. Busy uses a task-owned sleeping POWERPNT-name fixture; in-flight cancellation reaches and settles real owned Office. The new38 count is NOT the exhausted old38-image matrix, which was not run. PDF34, document48, original-review10, Storage27, Indexing29 and parent lifecycle5 also pass separately. Direct-PDF prototype failures and the failed palette/DLR/error-oracle runs remain retained. The later bounded SearchPage GUI and non-GUI exact-navigation acceptance are distinct from these indexing cases. Broad slide/font/SmartArt fidelity, end-to-end live Office GUI paths, real user indexes, installer and Release remain unqualified.

## SearchPage source preview — bounded GUI accepted

SearchPage includes explicit preview buttons, Space from a result, Escape close/focus restoration and Down from the query to the selected result. Enter retains ordinary-open authorization. The preview switches between inline and overlay layouts, decodes the page image and aligns optional indexed OCR line boxes in the same pixel frame. Source mismatch, invalid geometry and stale completions clear or reject raster/exact-open state; preview leases are released on replacement, close and unload.

The source-preview harness uses the SDK ApplicationDefinition/IXamlMetadataProvider path with its original explicit Main, x64 application-local Windows App SDK and .NET framework-dependent deployment. At the measured 200% DPI, synthetic owned-window light/dark, 1000x720 and 360x720 DIP cases pass: visual43/43, keyboard26/26, lifecycle6/6 and 13/13 fixture leases released. Twelve original control captures were manually checked; an opaque theme-backed test host avoids transparent full-page capture artifacts without modifying production page styles. SearchBox uses PreviewKeyDown because ordinary bubbling KeyDown can already be class-handled.

Keys are targeted only to the owned foreground HWND/child/PID. Reverse Tab uses a saved/restored calling-UI-thread Shift state and requires the target thread to match; it is not physical hardware Shift+Tab. Runtime-internal attached input queues are not an OS sandbox. This acceptance does not cover QuickSearchWindow redesign, all DPI, formal accessibility certification, broad real Office GUI navigation, installation or Release. Ordinary Open, previous non-GUI Office boundaries and retained P2 risks remain separate. See [source-integration scope](../docs/native-migration/source-integration-status.md).

## Session pause candidate

The current source implements real session-only pause/resume for indexing, shared by the main window and tray. It cancels active/queued work, waits for quiescence before reporting Paused, and leaves search and saved background authorization unchanged. Backend and non-render UI tests pass. The earlier separate pause-UI harness record retains its exhausted three-attempt rendered-startup blocker; the later source-preview harness startup and bounded GUI acceptance above do not retrospectively change that record. A newly deployed production App subsequently passed cold-start, actual pause/resume button automation and two basic viewport checks; this does not close independent review or complete visual acceptance. See `../docs/native-migration/deployment-verification.md` and the earlier `reviews/p4-pause-integration.md`.

The previously staged `.dev/native-migration/release/app` and first deployment candidate remain unchanged. The latest isolated candidate is under `.dev/native-migration/deployment-checks/n2b-native-20261019/candidate/app`; its sibling `Launch-Preview.cmd` fixes an explicit task data directory. It includes the model-free native helper but no real models. Actual cold-start, pause/resume UI Automation and helper metadata checks passed; see `../docs/native-migration/n2b-deployment-verification.md`. No external distribution or legacy cutover is approved.

## Native image EXIF/OCR

The native backend now runs whitelisted photo metadata inspection in the bounded media host and fixed-model RapidOcrNet.WinSight4.2.0-winsight.1 in its separate bounded OCR worker. This is no longer the historical model-free WindowsOCR implementation. English/Chinese OCR, all eight EXIF orientations, GPS exclusion, explicit blank/partial/failure semantics and physical cancellation have synthetic acceptance evidence. OCR/metadata text joins the existing visual input without changing the model profile or SQLite schema. See `../docs/native-migration/image-exif-ocr-contract.md` for limits, evidence and the still-open independent-review/install gate. No language pack or model is downloaded.

## Native media leaf command

The inference host now accepts `extract-media ABSOLUTE_REQUEST_JSON` before any model/provider initialization. It extracts one explicit audio interval (PCM16, mono, 16 kHz, at most 30 seconds) or one bounded video frame using WinRT. N1 has bounded independent approval and 111 passing suite cases, including actual-host EOF checks and mutation-sensitive tail validation. See `../docs/native-migration/reviews/native-media-recheck.md`.

Native audio/video sampling is now connected to the candidate DesktopBackend through an independently approved per-run controlled client and typed artifact leases. N2b-1 sampling/index consumption has 102 passing synthetic cases; N2b-2 has 29 public-backend cases (fake embeddings, including real model-free WAV decoding), eight settlement-guard cases and five bounded-reader checks. See `../docs/native-migration/reviews/n2b-runtime-review-state.json`. Audio uses 30-second windows/25-second hop; video frames and soundtrack preserve original-source locators and explicit partial/failure semantics. Native shutdown failure keeps admission fenced rather than falsely Paused. This is **not real-model or all-format parity**; the no-model preview cannot demonstrate model-driven media semantic indexing. WinRT cancellation alone is not a hard stop. Use only explicit synthetic task files/directories; no model, codec or provider is downloaded.

## Retrieval and desktop policies (WS-01–WS-06)

Keyword candidate limits apply to distinct files after ordinary/CJK candidate metadata is merged and ranked by query-term coverage; only selected excerpts are hydrated. CJK substring lookup uses a connection-local `fts5vocab` view of existing tokens; the persistent schema5 layout, tokenizer, native marker and read-only legacy-index contract remain unchanged. No live-index migration or rebuild is performed. CJK lookup scans the token vocabulary, not a dedicated substring index.

Semantic acceptance now calibrates independent source channels, with repeated chunks collapsed to their strongest source-unit score. At least five units use a within-channel Z gate; smaller sets retain the explicit cosine0.5 fallback and small-sample notice. RRF compares resulting channel ranks, not raw image/text cosine magnitudes. This is a deterministic policy, not a claim of real-model relevance calibration.

Keyword search may read file headers/source metadata across the scoped corpus for directory/field recall, but hydrates keyword excerpts only for selected candidates. It does not load every full text body into the result list. Semantic search caches vector-bearing segments and their associated excerpts for a stable SQLite snapshot; local writes invalidate the cache, and `PRAGMA data_version` invalidates it after external commits. All authorized roots are searched together, with authorization checked before candidate/result limits. Cold semantic queries still load the vector corpus, retain its associated text, and perform linear cosine scoring; this is not an ANN index or a constant-memory guarantee.

Hotkeys require a modifier and exactly one supported key. Invalid tokens, empty components, duplicate modifiers and ambiguous chords are rejected rather than becoming Space. Binding replacement uses a spare registration ID before releasing the working binding. Registration conflicts preserve the previous chord. Both ordinary Save and Apply Hotkey use the same serialized registration/persistence path; a binding revision prevents an older failed save from rolling back a newer binding. Failed preference saves attempt to restore only the binding still owned by that save.

The historical `processorOnly` preference now means **automatic indexing on AC power only**, not CPU throttling or execution-provider selection. Battery/unknown power defers automatic work; a power-read error is reported and fails closed. The coordinator polls policy every 30 seconds, cancels active automatic work when AC/schedule permission is lost, waits for it to settle and reconciles on permission restoration. Explicit manual indexing remains available. Power-unrelated regression fixtures opt out explicitly so their results do not depend on the test host's battery state.

Regression entry points are `WinSight.Storage.Tests`, `WinSight.Runtime.Tests`, `WinSight.DesktopPolicy.Tests`, `WinSight.Indexing.Tests`, `WinSight.Pause.Tests` and `WinSight.IndexingSettlement.Tests` under `desktop/tests`. The new desktop-policy suite links the actual parser/service/settings view-model with a controlled registrar; it does not claim real OS hotkey-conflict acceptance. Runtime manual-index integration needs the built `WinSight.Inference.Tests/FakeHost` executable (or its absolute path as the first argument). Pause tests require that fake-host path plus a new absolute evidence directory; settlement tests require one new absolute evidence directory. Use the task evidence logs for actual pass counts, not this list of entry points.

## Dependencies

Direct versions are pinned in each project and restore lockfiles are retained. The new implementation uses official `PdfPig [0.1.16]` (Apache-2.0), not the old blueprint's `UglyToad.PdfPig 1.7.0-custom-5`, whose package lacks explicit license/provenance metadata. The namespace remains `UglyToad.PdfPig`; see the official [package metadata](https://api.nuget.org/v3-flatcontainer/pdfpig/0.1.16/pdfpig.nuspec) and [project](https://github.com/UglyToad/PdfPig).

`audit-native-packages.py` requires the already installed `defusedxml` module. It checks restored production package graphs against cached archives, sources and license metadata. Raw signed-archive SHA-512 and NuGet content hashes are different values; each is checked against its own authority. This metadata audit is not signature verification, a vulnerability check or distribution approval. Those checks are separate receipts.

## Distribution is gated

`Prepare-NativeRelease.ps1 -AppOnlyPreview` can stage an incomplete self-contained development candidate. Without that switch, explicitly supply the model directory; every model byte is checked against the repository manifest. The script does not discover private model caches, install the app, modify PATH, register clients, sign or publish anything. Its release manifest always states that distribution is not yet approved.

A staged folder is not an installer acceptance result. The approved source-only abandonment is distinct from the original full-parity P5 and distribution P6 gates, which remain unaccepted. Current media/search/config/UI/installation/recovery and full-product review omissions must stay documented; deleting the old implementation does not close them.
