# Native installer source status

This directory contains the controlled installer kernel and **unmaterialized,
fail-closed standard-install/recovery templates**, not a ready-to-run installer.

The original development copies carried private source/run roots, interpreter
paths, machine identities and frozen bindings. Public source replaces these
capabilities with explicit unconfigured markers and rejects the standard
entrypoints before using the private graph. Do not remove those guards and
assume the result is qualified. Environment variables or caller JSON must not
select executable code or silently adopt another target.

The source includes the bounded atomic-directory publication retry, bounded
worker-error diagnostics and callback clock initialization fixes. Fixed sibling
hashes have been updated for these source bytes. No previous bundle, binding,
journal, installed-state record or approval is reused by this publication.

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
