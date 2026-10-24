# Native image EXIF/OCR contract — historical v1

> The source candidate now uses [RapidOCR protocol2](rapidocr-candidate-contract.md), with scoped38-case image integration passed; full upgrade qualification remains BLOCKED. The Windows OCR behavior/evidence below describes the earlier v1 snapshot only; its35 passes are not new-engine acceptance.

## Scope and owners

`DesktopBackend` supplies its existing per-run `NativeMediaProcessClient` to `ContentExtractor`. The model-free `WinSight.InferenceHost.exe inspect-image ABSOLUTE_REQUEST_JSON` operation runs before model/provider initialization. There is no new OCR package, download, language-pack installation or embedding-profile change.

The request is a closed protocol-1 object: `inputPath` and `kind="image"`. The helper validates local fixed-drive paths and holds the existing pinned source/request leases. The desktop client retains the existing suspended-born Job Object, stdio byte bounds, timeout, cancellation, physical-settlement and admission-fence behavior. Successful inspections also remove their task directory. This does not claim control over Windows system services or their caches.

## Extraction

- Compressed input: at most 32 MiB; decoded dimensions: positive, at most 32768 per edge and 40 million pixels.
- Decode the first frame through Windows imaging, respecting all eight EXIF orientations. OCR pixels use BGRA8/sRGB and a maximum edge of `min(2600, OcrEngine.MaxImageDimension)`; the original path still supplies the existing visual embedding.
- Run installed `en-US` and `zh-Hans-CN` OCR recognizers, in that order. If neither is installed, try at most one installed recognizer. No language pack is installed implicitly.
- Retain repeated text within a recognizer result. Deduplicate only identical whole recognizer passes. OCR text is bounded to 8000 UTF-16 characters without splitting a surrogate pair.
- Read only standardized Windows properties for camera manufacturer/model, date taken, exposure time, F-number, ISO speed, focal length, image description (`System.Title`) and software. Values are invariant-formatted, control-character-stripped and capped to 256 characters. These are Windows' property values, not a forensic/raw EXIF dump or proof of the original timestamp timezone.
- Do not query GPS, camera serial numbers, people tags, arbitrary XMP or identity fields. This is a property whitelist, not a guarantee that free-form captions/OCR contain no private content.
- Store dimensions, orientation, frame count, whitelisted properties, OCR languages/dimensions/count and `native_image_revision="native-image-exif-ocr-v1"` in the existing file-info JSON. No SQLite schema, tokenizer, native marker or model identity changes.
- Compact only spaces/tabs between adjacent common Han glyphs in OCR text (not Latin words, numbers or line boundaries), because Windows OCR emits glyph-spaced Chinese. Feed OCR and whitelisted metadata through normal text segments (1800-character chunks, 200-character overlap) alongside the original visual segment. The existing private-key marker exclusion also applies to image text/metadata. This narrow marker check is not general secret detection.
- Unchanged images lacking the current extraction revision are refreshed; old `done` entries are not silently treated as OCR-complete. No existing user index is automatically migrated by this implementation task.

## Truthful states

`done` means a single-frame image decoded and at least one installed OCR recognizer completed, with no recorded inspection failure or OCR truncation. A successfully inspected blank image can be `done` with no text segments. Missing optional EXIF tags are not errors.

Multi-frame input is explicitly `partial` (first-frame coverage only). Unavailable/failed OCR, an EXIF inspection failure or OCR truncation also remains `partial` with its reason. Malformed/corrupt, over-limit or unauthorized inputs fail explicitly; invalid native responses never become `no_data`. Calling the parameterless extractor without a configured native client remains an explicit `partial`, not an invented OCR success.

This does not establish OCR accuracy for arbitrary layouts, handwriting, other languages, all codecs/HEIC/TIFF variants, video parity, GPU/NPU behavior or semantic relevance calibration. Existing short-audio sampling policy is unchanged.

## Evidence and release boundary

`desktop/tests/WinSight.Image.Tests` consumes the actual Infrastructure/Core assemblies and exercises the actual model-free Host using synthetic images. Current image evidence: `.dev/image-exif-ocr-01/image-tests-05/results.json` (35 passing cases), including English/Chinese OCR, all eight orientations, embedded-GPS exclusion, blank/corrupt/animated inputs, scaling and pixel/text limits, strict protocol rejection, source identity, cleanup, timeout and cancellation of an owned process tree. Earlier failing runs are retained.

Real-model/public-backend integration and candidate installation evidence belong to `.dev/image-exif-ocr-01/`, not to the previous frozen candidate. Static tests are not independent approval or installation evidence. `WS-REVIEW-01` remains open: the previous three full-scope reviews timed out; no further retry or owner risk acceptance is inferred from this implementation request. Active installation is not approved by this document.
