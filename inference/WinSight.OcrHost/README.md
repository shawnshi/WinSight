# Isolated RapidOCR candidate — image suite passed; full qualification pending

This is **RapidOcrNet.WinSight 4.2.0-winsight.1**, not an unmodified RapidOcrNet 4.2.0 package.

- Upstream: BobLd/RapidOcrNet commit `708cae2fcb88720e1d891a81b5ee3e8b2bcc139e`.
- Apache-2.0 source/license/headers retained in `vendor/RapidOcrNet/`.
- The exact behavioral patch is `provenance/winsight-error-propagation.patch`: recognizer, detector and classifier non-cancellation inference failures rethrow instead of returning empty/default values. Recognition algorithms are unchanged.
- The local build project changes assembly identity/packaging; it does not import the stock package's default Latin-model copy targets.
- Exact ORT/Managed 1.29.0, SkiaSharp 3.119.1 and Clipper2 2.0.0 are locked. These assemblies/native libraries must remain under the separate `ocr/` output directory, never in the Windows ML process directory.
- CPU only, one recognition operation, bounded four-thread ORT sessions. `model-manifest.json` binds this runtime setting, the v6 options identity and four Chinese/English model/dictionary pins.
- No startup downloads or installed Windows OCR/language-pack fallback.

## Protocol

`WinSight.OcrHost.exe inspect-ocr ABSOLUTE_REQUEST_JSON`

Request: strict UTF-8 <=64KiB, only `{"protocol":2,"imagePath":"ABSOLUTE_LOCAL_PNG"}`. The pinned PNG must share the request directory: <=32MiB, each edge <=2600 and <=6.76MP. UNC/device/reparse paths, duplicate/unknown keys and invalid images fail.

Success: protocol 2, engine RapidOcrNet, version 4.2.0-winsight.1, pinned modelProfile, languages [zh-Hans-CN,en-US], text <=8000 UTF-16 units and explicit truncated flag; stdout <=128KiB. A valid blank is successful empty text. Invalid input/model/engine operation produces bounded stderr and nonzero exit, never fake blank/no_data.

The parent first applies EXIF orientation and image scaling. It owns a suspended-born Job, explicit stdio, cancellation, a 25s inner deadline and physical-tree settlement; its caller's earlier deadline always wins. Unknown physical settlement retains task material rather than cleaning underneath a possible worker.

The current geometry candidate returns protocol2 with at most96 normalized, axis-aligned **text-line** rectangles. Each rectangle has a line ordinal and a validated UTF-16 start/length into the returned raw OCR text; it is not a per-character/word-box claim. Text remains <=8000UTF-16 units and blank is valid; clipped/omitted geometry is explicit. The Host image wrapper uses protocol3 and may trim regions to its64KiB response cap, marking partial. Model profile and vendor recognition algorithm remain unchanged; image extraction revision advances to v3 so older geometry-free entries are not falsely current. Read-only independent review `97becc9f-044f-45c6-850f-ad38a85ae81d` approved this LOCAL_DOCUMENT_PAGES_SOURCE slice; parent native34-case evidence includes real OCR line geometry, but the reviewer ran no tests; previous38-image acceptance does not qualify the changed protocol, and that exhausted suite was not rerun.

## Build / validation limits

Use the exact SDK in `../global.json`. Compile with an explicitly prepared `-p:OcrModelRoot=ABSOLUTE_ASSET_ROOT`; publish requires the four pinned files. No model download is performed by this project/build helper. The Windows ML helper stages the separate worker via `scripts/build-inference-host.ps1 -OcrModelRoot ...`.

`tests/OcrOracle.csproj` consumes a worker DLL path, a NEW task directory, and the synthetic fixtures directory as three arguments. The optional upstream-source baseline mode is build-time only, via UseUpstream/PreparedSourceRoot, not a runtime engine selector.

The same oracle has a model-free lifecycle mode: `OcrOracle.dll --parent-launch-retention NEW_ABSOLUTE_REPORT_JSON`. It links the production parent caller; process and adjacent partial path helpers are controlled stubs, while WIC PNG preparation and executable FileShare leases are real. The pre-assignment unknown-settlement case failed on the frozen old caller and passed after OCR-LAUNCH-RETENTION-01's retention fix; all5 targeted lifecycle cases passed. No real OCR worker is launched in this mode. The optional `ParentOcrCallerSource` is a test-build baseline selector only. Normal oracle arguments/recognition assertions are unchanged.

Existing component evidence: 22/22 at the initial one-thread component snapshot, including real English/Chinese/blank and deterministic initialized-run failures. This does not validate every later runtime setting.

Earlier Host integration rounds remain37/38,37/38,22/38: the first two dense runs hit15s/25s; the third CPU4 run reached response validation in16.17s but consumed stale Infrastructure and failed model identity before truncation assertions.

The owner then explicitly authorized one further image round. The fresh-layout/current-assembly fourth round passed38/38 in41.45s; dense processing took16.28s and returned exactly8000 UTF-16 units with explicit partial/truncation detail. All four embedded identities matched. Assertions and deadlines were unchanged; no task processes or OCR task directories remained. This is one scoped CPU4 regression run, not a broad OCR quality/latency guarantee, new self-contained/GUI/embedding-model acceptance, independent full-upgrade review, install or Release approval. The tokenizer source-integration manual gate and actual dynamic/profile checks subsequently passed; combined independent review subsequently approved LOCAL COMPONENT SOURCE/INTEGRATION after confirming the startup-retention repair and its5-case regression. See the component status document for remaining broader distribution/qualification gates.
