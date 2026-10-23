# N7 source prerequisites and execution limits

## CPU/data entry: source evidence, not a fresh host test

Read the actual App Program.cs, DesktopRuntime.cs, CLI Program.cs, DesktopBackend.cs, InferenceHostClient.cs and Host Program.cs. App uses its existing `--DATA-DIR` option; do NOT invent `--CPU` or change C#.

`desktop/src/WinSight.Infrastructure/Runtime/DesktopBackend.cs:243–247` constructs the host with explicit `automaticProviders: false` and fixed application-relative `inference/WinSight.InferenceHost.exe`, `models`, and `inference/model-manifest.json` absent explicit configured overrides. `InferenceHostClient.cs:126–135` turns this into `--device cpu`, not auto. `WinSight.InferenceHost/Program.cs` routes CPU separately from the Auto provider-registration branch. Thus the existing backend source has an explicit CPU route without a new App argument, provider download or global environment change.

Daily launcher must validate committed release/helper/model/profile and exact task data directory, reject or validate any executable/model/manifest overrides in that task's settings, then launch only after the validator Job/readers settle. No user corpus. Current-machine App/CPU/indexing/search acceptance and correspondence of the frozen published binary to this source remain separate runtime gates. This file does not execute App or mutate data/settings/CAS.

## Inno final-byte ordering: pinned source only

Official Inno6.7.3 source is already frozen under `.dev/native-migration/release-acceptance/n4-current-machine/official-inno-6.7.3`, commit `4adf37ed7f3fd2bd11c6836ba056e3de170fbabf`, previously git-blob-verified. No network/source research was repeated.

`Projects/Src/Setup.Install.pas:2830–2978` shows the normal install path: copy files, create icons/registry entries, rename uninstall executable, create uninstall message file, register ARP info, record uninstall run entries, then `UninstLog.Save(UninstallDataFilename, AppendUninstallData, ...)`, clear/free the log, and return success. This completes before `Setup.MainForm.pas:237–254` calls `SetStep(ssPostInstall, True)` after PerformInstall/ProcessRunEntries/restart handling. Thus the pinned normal path saves Inno uninstall information BEFORE ssPostInstall, not afterward.

This is not a blanket runtime-stability guarantee. The new candidate MUST have no [Run]/postinstall run, app autorestart, associations/environment broadcasts, scheduled/delete-after-reboot behavior or later custom hook that mutates the receipt's shell/uninstaller bytes. Keep logs/attempt/control evidence outside the Inno-owned shell and outside the stable shell digest. Genuine uninstaller optional files and exact paths must be closed/validated rather than guessed or adopted by basename. `ssDone`/Deinitialize cannot become necessary commit points.

Keep the designed ssPostInstall Apply commit, but RE-HASH/re-probe those same resources after actual setup-owned-tree exit/reader EOF before Finalize. Any change after an authoritative anchor is postcommit reconciliation failure: preserve evidence, block Finalize/report ShellRepairRequired; NEVER replace the already anchored receipt, claim rollback or silently normalize new shell bytes. Actual setup exit and byte-stability acceptance remains UNEXECUTED and mandatory.

## Next source seam

Adopt `n7-live-interface-handoff.md` as a proposed implementation contract, not an approval. A owns coordinator/protocol/live authority; B owns actual Inno wrapper and entrypoints, DEPENDS on A's exact interface and therefore follows it serially. No full multi-seam end-to-end writer and no forced concurrent source writers. Preserve N5 FileVerification/Production refusal and readonly schema2 behavior; new live schema3 is distinct.

Source A may implement and qualify pure/isolated typed protocol and existing affected file-only compatibility, using bounded inert fixtures. It must NOT perform actual standard root/HKCU/menu/App/Inno writes or claim live commit qualified before independent source review. Mock observations cannot be accepted by the real production wire. Outer-result JSON or alleged settlement is not physical proof: A must require the supported one-use owned-process assignment/capability and current independent resource observations. If a frozen helper cannot provide needed evidence, STOP that seam with an exact API gap; do not clone process ownership or infer proof from logs/files. No new C#/SDK/helper pin changes without parent re-scoping.

Frozen: NativeInstaller.psm1, Invoke-NativeInstaller.ps1, NativeDeploymentJob.psm1, NativeStandardShell.psm1, Prepare-NativeRelease.ps1, C#/lockfiles/governance/CI. Do not weaken size/type/hash/deadline/resource fences. Binding<=32KiB, controlJSON<=2MiB, records<=4096, native streaming64KiB blocks, new live actual payload not old Verification16/64MiB caps. Full real832-file timings remain unknown; no new940MB assembly or physical CI allowed here.

User already authorized the exact standard per-user targets and current-machine actions. Implementation source/targeted tests are covered. Actual standard setup remains a later independently reviewed gate, not authorized by source tests alone. CI3/UI-harness3 exhausted and never reopened. Original candidate aa1c5f source/outerexit1, original21 license issues and all release/P0-P6 gates retain their attribution.
