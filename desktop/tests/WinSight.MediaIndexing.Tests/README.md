# N2b-1 candidate test lane

Synthetic-only console test project. This is NOT a production sampling implementation, default activation, deployment or independent review.

## Run

From the lane root, with PowerShell 7 `-NoProfile -NonInteractive`:

```powershell
./desktop/tests/WinSight.MediaIndexing.Tests/Run-MediaIndexingTests.ps1 `
  -NewArtifactRoot "<absolute lane>/.dev/n2b-index-tests/<NEW name>" `
  -OfflinePackageSource (Join-Path $env:USERPROFILE '.nuget/packages') `
  -ActualInfrastructureAssembly '<explicit integrated production WinSight.Infrastructure.dll>' `
  -ActualHost '<explicit read-only N2b-0 WinSight.InferenceHost.exe>' `
  -RequireIntegration
```

Omit both actual paths for fixture-only validation. Exit 0 in that mode means fixture validation only; inspect `data/results.json` for pending production work. `-RequireIntegration` exits **3** if anything is pending; actual assertion failures exit **1**. Pending is not success. Execution stops further cases after the first failed assertion, preserving original exception and avoiding cascading retries. The runner restores/builds ONLY the two new test projects; it never builds production or Host. Production restore, if separately done by the integrator, must retain `-r win-x64 --locked-mode` and the approved offline/scoped paths.

## Surfaces

- `Synthetic.cs`: independent integer/sample-count expectations, PCM RIFF chunk reader, temporal tone markers, RGB PNG filter/CRC fixture writer, atomic owned-PID receipts. This oracle is NOT a production API shim.
- `FakeNativeWorker`: actual inspect-media/extract-media/extract-soundtrack JSON shape, exact fields/nulls, bounded requested intervals, normalized Windows paths, scenario-specific metadata, real WAV/PNG artifacts, controlled silence/98/99/filter/duplicate/decode-error/hang. Scenario files are test-owned, never real media. Request and PID receipts are atomically published.
- `ActualApi.cs`: loads ONLY an explicitly provided real Infrastructure assembly and sibling Core. Fixed sealed/internal API checks and internal reflection invocation; no generated sampler/consumer fallback. DispatchProxy implements the **real loaded Core interfaces** for in-memory repository and fake embedding.
- `SamplerCases.cs`, `ConsumerCases.cs`: authored candidate assertions; not production evidence until executed against the integrated DLL.
- `NativeCases.cs`: optional Native Host + generated WAV/MP4 + fake embedding E2E. The video is three nonuniform synthetic scenes plus temporal audio, not real/private media or models.

## Safety/evidence

All fixtures, scoped packages and binaries go under explicit **NEW** lane `.dev` roots. Source directories contain no generated `bin`/`obj`. Each wrapper records commands/exit codes, source and artifact SHA-256, production lock hashes before/after, and inherited staged entries before/after. No package feeds/network, model providers, real settings/indexes, git mutation or default backend hook. One total wrapper deadline <=170 seconds; child cancellation/deadlines use exact recorded PID + creation-time identity, not process-name kills. No arbitrary sleeps are used as assertions; bounded request-receipt polling and TaskCompletionSource barriers establish lifecycle observation points.

Known pending oracles are deliberately reported even with a candidate DLL: direct pure production helper tests (double threshold equality, small RGB/integer hash and 5000 policy), isolated code-unchanged/Git scope, source identity race and genuine retention-error injection. Do not remove pending markers merely to obtain exit 0. Existing 59/111/129 suites remain untouched and must be run separately by their owner after integration. AAC/native-trim, FFmpeg slot precision, multi-track/container/rotation/VFR and real-model parity remain unknown.
