# Native installer source status

This directory contains the controlled installer kernel and **unmaterialized,
fail-closed standard-install/recovery templates**, not a ready-to-run installer.

The original development copies carried private source/run roots, interpreter
paths, machine identities and frozen bindings. Public source replaces these
capabilities with explicit unconfigured markers and rejects the standard
entrypoints before using the private graph. Do not remove those guards and
assume the result is qualified. Environment variables or caller JSON must not
select executable code or silently adopt another target.

The source includes bounded atomic-directory publication retry, callback clock
initialization, early nonzero-producer failure reporting, buffered-pipe peer-close
settlement, Inno 6.7.3 callback-output logging, and case-only shortcut Target
comparison by simultaneous volume/128-bit FileID. Arguments and working directory
remain exact. Fixed sibling hashes close over these source bytes; no previous
bundle, binding, journal, installed-state record or approval is reused.

A separately materialized private package completed a real local standard install
and responsive GUI startup. This is maintainer-local evidence, not qualification
of a fresh public checkout or a downloadable release. See
[local validation scope](../../docs/native-migration/local-install-validation.md).
Portable component tests are `tests/Test-NativeInstallFixes.ps1` and
`tests/Invoke-OwnedPipeFixture.ps1`; run them in x64 Desktop PowerShell 5.1 with a
fresh owned output directory. They use synthetic pipes and file/LNK fixtures, not
native owner/NoCommit capabilities or live program/registry/menu writes.

`NativeInstaller.psm1` remains the library for explicitly scoped, synthetic kernel
tests. `NativeInstallCoordinator.psm1` can be parsed and its isolated clock/guard
helpers tested without an installation. Public standard-live/shell launch and
build entrypoints, and historical frozen qualification scripts, intentionally
refuse use until a separately reviewed private graph is materialized and all
source, manifest, runtime, compiler, shell, bundle and binding pins are rebuilt.

`PreparedAbortCopy.py` and `InvokePreparedAbortCopy.py` are hash-referenced helper
sources and are retained; their interpreter capability is unconfigured. Unused
one-off recovery scripts and raw local qualification artifacts are not published.

A future installation task still requires explicit target ownership, a recovery
point, physical process settlement, real callback/NoCommit checks, installation,
upgrade/uninstall acceptance and all applicable approval/retry gates. Source
publication is not installation or release approval, and does not authorize an
additional installation attempt.

See [the root README](../../README.md),
[standard installation contract](../../docs/native-migration/standard-per-user-installer-contract.md)
and [release acceptance contract](../../docs/native-migration/release-acceptance-contract.md).
