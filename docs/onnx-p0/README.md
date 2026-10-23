# EmbeddingGemma 2 / Windows ML — P0

## Status and scope

**P0 complete; the production-backend migration is not complete. Independent P0 review: OK with notes.**

P0 establishes an isolated native Windows ML environment, pinned artifacts, three graph sessions and a synthetic text-token inference path. **It does not replace the production backend. Audio is mandatory, not dropped.**

Only new `inference/`, `scripts/onnx-p0/`, `docs/onnx-p0/` and `.dev/onnx-p0/` files are introduced. The baseline manifest is `.dev/onnx-p0/baseline.json`; the verification suite confirmed all 184 pre-existing files remain byte-identical. No existing model folder, database, index, application setting, root README, helper or UI file was changed.

## Implemented

- `.NET 10` console host with `Microsoft.WindowsAppSDK.ML` **2.1.94**, locked NuGet dependencies and app-local Windows ML deployment. No second `Microsoft.ML.OnnxRuntime.DirectML` package is added.
- Windows ML `ExecutionProviderCatalog` discovery succeeded. The actual app-local ONNX Runtime reports **1.24.6**. The runtime binary includes CPU and DirectML providers; **only CPU execution was tested**. Hardware-specific providers reported NotPresent/NotReady; no provider preparation or download API was called.
- Fixed model repository `onnx-community/embeddinggemma-2-ONNX`, revision `daa72c51243991dfcaf9f9137d2c573d8f7790c0`. All **12** selected files verified against upstream LFS SHA256 or pinned Git blob identity; SHA256 is recorded for every acquired file.
- Text, vision and audio Q4 graph/data pairs and tokenizer/configuration assets total **505,166,357 bytes**. This is file size, not resident memory. Large assets are ignored under `.dev/onnx-p0/models/`.
- Actual CPU graph execution generated finite, normalized **768-dimensional** embeddings for three synthetic Chinese/document/code-query token sequences. ORT profiling contains only `CPUExecutionProvider` node events. The ONNX graph's `sentence_embedding` is consumed without another pooling/normalization pass.
- Nine verification tests passed: artifact integrity, mandatory audio, path/revision allowlisting, corruption rejection, real Windows ML discovery/runtime origin, three graph sessions, missing-audio preflight, CPU text vectors, invalid-token failures and pre-existing-file integrity (some assertions share test cases).

## Verified graph contracts

| Graph | Inputs | Outputs |
|---|---|---|
| `model_q4.onnx` | `input_ids`/`attention_mask`: int64 `[batch, sequence]`; `image_features`, `video_features`, `audio_features`: float32 `[rows,512]` | `sentence_embedding`: float32 `[batch,768]`; token states `[batch,sequence,768]` |
| `vision_encoder_q4.onnx` | `pixel_values`: float32 `[batch,patches,768]`; `pixel_position_ids`: int64 `[batch,patches,2]` | `image_features`: float32 `[rows,512]` |
| `audio_encoder_q4.onnx` | `input_features`: float32 `[batch,frames,128]`; `input_features_mask`: **bool** `[batch,frames]` | `audio_features`: float32 `[rows,512]` |

The encoder feature rows are **not** final searchable embeddings; visual/audio features must enter the common text graph with the proper modality tokens. A successful session load is not proof of correct media preprocessing, full multimodal inference or recall.

## Reproduction

Prerequisites: Windows, .NET SDK 10.0.113 (compatible servicing patch allowed), installed Python development packages `huggingface_hub`, `tokenizers`, and the SDK prerequisites already present on this host. No global toolchain was installed by this task.

```powershell
cd inference
dotnet restore WinSight.InferenceHost/WinSight.InferenceHost.csproj --locked-mode --configfile NuGet.Config
dotnet build WinSight.InferenceHost/WinSight.InferenceHost.csproj -c Release --no-restore
cd ..
# Downloads are explicit, public, credential-free and remain under .dev/onnx-p0/.
python scripts/onnx-p0/acquire_models.py text
python scripts/onnx-p0/acquire_models.py vision
python scripts/onnx-p0/acquire_models.py audio
python scripts/onnx-p0/prepare_text_fixture.py
python scripts/onnx-p0/test_probe.py
```

These commands are development probes, not the production JSONL protocol. Python generates only synthetic token fixtures and tests; no Python inference dependency has been added to the application. The host currently needs the installed .NET 10 runtime: `WindowsAppSDKSelfContained` makes Windows ML app-local, **not** the .NET runtime itself. A clean-machine self-contained .NET publish/installer belongs to P5.

SDK NuGet packages `Microsoft.WindowsAppSDK.ML.2.1.94` and `Microsoft.Windows.AI.MachineLearning.2.1.74` passed native `dotnet nuget verify --all` author/repository signature checks. Standard NuGet restore uses the project-scoped public-source configuration and lockfile; no private feed credentials were inspected.

## Evidence and limits

- `.dev/onnx-p0/restore-locked.log`, `build.log`: successful build, zero warnings/errors.
- `.dev/onnx-p0/environment.json`: actual WinML catalog, app-local runtime path/version and provider discovery.
- `.dev/onnx-p0/session-inspection.json`: actual graph inputs/outputs from created sessions.
- `.dev/onnx-p0/text-inference.json`: original synthetic-vector receipt. Its original SDK-returned profile path was relative; the raw trace was recovered and archived without modifying its contents. `profile-receipts.json` maps the old locations to `.dev/onnx-p0/profiles/recovered/` with SHA256 and CPU event counts.
- `.dev/onnx-p0/tests.txt`: the initial 9-test suite passed. A subsequent profiling-output repair was checked by one targeted regression, not by repeating that suite. `results.json` records both stages separately.
- `.dev/onnx-p0/build-profile-fix.log`, `test-profile-output.txt`, `profile-output-regression.json`: fresh zero-warning/error build and successful output-location regression. The new raw profile remains under its actual requested task directory; no new default profile appeared in the repository root.
- Small upstream source snapshots and provenance are in `sources/`; the runtime and model weight binaries are not tracked in Git.

The first combined artifact fetch exited while starting the tokenizer download without an exception stack. A focused call to the same public Hugging Face API with its official endpoint and `token=False` succeeded; the acquisition script now specifies that endpoint explicitly and all files subsequently verified. No credential was added or authentication gate bypassed.

**Unverified:** production tokenizer equivalence, visual/audio preprocessing, waveform-to-768d output, video sound/frame retrieval, cross-modal recall, GPU/NPU execution, Swift bridge build, index migration and clean-machine installation. The installed Python ORT is 1.20.1 and was not used as an inference reference; cross-runtime/golden numeric equivalence is not claimed. The local timings are profiling-enabled smoke observations, not a benchmark or performance SLO.

P0 host compilation targets SDK 26100 with a probe minimum of build 18362; this is not a decision to raise the main application's supported-OS baseline. Any older-OS fallback must be verified before production integration.

## Next gates — audio retained

1. **P1:** production tokenizer and JSONL/Swift adapter; verify prompt/BOS/EOS and full embedding profile identity. No production index switch.
2. **P2:** vision **and audio** preprocessing/reference equivalence, audio/video-sound temporal windows and cross-modal retrieval. Audio must pass before production cutover; text/vision success cannot waive it.
3. **P3:** qualified EP/device selection and same-profile CPU fallback, measured memory/latency and process cleanup.
4. **P4:** isolated index generation, atomic activation and recoverable rollback; never mix old GGUF and new ONNX vectors or delete the active index on profile mismatch.
5. **P5:** package/runtime/notices cleanup and fresh-machine E2E; retire llama.cpp only after all retained modalities pass.

Current audio behavior to retain: 16 kHz mono PCM16, default 30-second windows/25-second hop, temporal location metadata and per-item errors. The target processor uses Gemma4 audio features; its actual settings are pinned in `sources/processor_config.json`. Align waveform-to-features and bool-mask semantics against a reference, not just the graph shapes. Existing `ffprobe` errors being interpreted as no audio and missing/failed embeddings being marked complete are known caller-level acceptance risks to address at P2, not silently normalized during this P0 probe.

## Diagnostic coverage

Fresh LSP checks did not confirm four paths within the probe budget and reported design-time missing ONNX references in `TextProbe.cs`. These diagnostics disagree with the actual locked Release build and native execution of the same files; they were marked as session false positives without source suppression or adding another ORT runtime. The IDE reference-resolution issue remains a development-environment limitation, not a claim that every language-server path is clean. Fail-fast JSON parsing and Git's SHA1 object-ID comparison are intentional; acquired files also receive SHA256 receipts.

The separation is a task directory/process boundary, not an OS security sandbox or a zero-side-effect guarantee. No global toolchain, application registration or provider package installation was intentionally performed.

## Independent review and profiling repair

`independent_review_completed`: reviewer run `41e3e33b-ca28-4a41-ad1d-12e52172f4ba`, verdict **OK with notes**, no P0/P1 finding. See 历史本地审阅 `review.md`（原文不随源码公开）.

The reviewer found one P2: enabling profiling before assigning the prefix caused native ORT to use its default cwd-relative path. The original raw trace was present and independently confirmed CPU nodes and three model runs; the execution evidence was not fabricated or lost, but the documented path was wrong.

The primary agent repaired the order (prefix first, enable second), made `EndProfiling()` paths absolute and enforced the requested directory. One new targeted regression performs one synthetic token invocation to verify the changed output behavior, file existence, CPU events, one model run and no new root-level profile. It passed after a fresh build. This is primary verification, not an independent follow-up review or a rerun of the full model suite; see 历史本地输出 `profile-output-test.txt`（原文不随源码公开） and [profile-output-regression.json](profile-output-regression.json).

Two exact P0-generated misplaced traces (one at the copy root, one at the parent task root) were copied to `profiles/recovered/`, SHA256-verified, then removed from their original locations after the review finished. Recovery copies and provenance remain in 历史本地回执 `profile-receipts.json`（原文不随源码公开）. No user document or pre-existing production file was removed. The first-review diff is retained as `implementation.diff`; current additive code is in `implementation.final.diff`.
