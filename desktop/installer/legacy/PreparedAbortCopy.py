"""File-copy-only abort qualifier. No live/native recovery or authority provider.

Original pending/journal/Intent/events/data stay byte-exact at their copied paths.
Only the complete unpublished version moves into retained evidence. The resulting
AbortedCopyFenced state deliberately cannot authorize install, launch or recovery.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import ntpath
import os
from pathlib import Path
import re
import stat
import time
from typing import cast

LC = Path(__file__).resolve().parents[2]
ALLOWED_BASE = LC / '.dev/prepared-abort-copy-v1/cases'
ACTUAL_TARGET = LC.parents[1] / '.dev/native-migration/release-acceptance/standard-install-checks/run-01'
WITNESS_FIELDS = {'protocol', 'kind', 'declaredTaskRoot', 'phase', 'pendingSha256', 'files', 'directories'}
PENDING_FIELDS = {'protocol', 'bindingSha256', 'intentSha256', 'transactionId', 'operation', 'previousReceiptSha256', 'journalSha256'}
INTENT_FIELDS = {'protocol', 'bindingSha256', 'transactionId', 'operation', 'previousReceiptSha256', 'stageDirectory', 'manifestSha256', 'shellSpecSha256', 'journal', 'journalSha256', 'previousStateSha256', 'nextStateSha256'}
JOURNAL_FIELDS = {'schema', 'operation', 'installRoot', 'dataRoot', 'previous', 'next', 'copyProtocol'}


class AbortRefused(ValueError):
    pass


class InjectedInterruption(RuntimeError):
    pass


def require(condition: bool, reason: str) -> None:
    if not condition:
        raise AbortRefused(reason)


class Budget:
    def __init__(self, seconds: float = 155):
        self.end = time.monotonic() + seconds

    def check(self) -> None:
        if time.monotonic() >= self.end:
            raise TimeoutError('Copy-only shared filesystem budget exceeded')


def encoded(value: object) -> bytes:
    return (json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=True) + '\n').encode('ascii')


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result: dict = {}
    for key, value in pairs:
        require(key not in result, 'Duplicate JSON key')
        result[key] = value
    return result


def load_json(path: Path) -> tuple[dict, bytes]:
    require(path.stat().st_size <= 8 * 1024 * 1024, 'Oversized JSON')
    raw = path.read_bytes()
    value = json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique_object)
    require(type(value) is dict, 'Concrete JSON object required')
    return value, raw


def fields(value: dict, expected: set[str]) -> None:
    require(type(value) is dict and set(value) == expected, 'Closed JSON fields required')


def pin(value: object, width: int = 64) -> str:
    require(type(value) is str and re.fullmatch('[0-9a-f]{%d}' % width, value) is not None, 'Lowercase hex pin required')
    return cast(str, value)


def relative(value: object) -> str:
    require(type(value) is str and bool(value) and '\\' not in value and ':' not in value and not value.startswith('/'), 'Relative copy path required')
    value = cast(str, value)
    require(all(part not in ('', '.', '..') and not part.endswith((' ', '.')) for part in value.split('/')), 'Noncanonical copy path')
    return value


def node(path: Path) -> os.stat_result:
    info = path.lstat()
    require(not stat.S_ISLNK(info.st_mode) and not getattr(info, 'st_file_attributes', 0) & 0x400, 'Reparse/symlink refused')
    require(stat.S_ISDIR(info.st_mode) or stat.S_ISREG(info.st_mode), 'Special filesystem node refused')
    if stat.S_ISREG(info.st_mode):
        require(info.st_nlink == 1, 'Hardlinked copy refused')
    return info


def ancestry(path: Path) -> None:
    for part in reversed((path, *path.parents)):
        node(part)


def case_boundary(case_root: Path) -> Path:
    require(os.name == 'nt', 'This Windows file-copy qualifier has no non-Windows provider')
    root = Path(os.path.abspath(case_root))
    base = Path(os.path.abspath(ALLOWED_BASE))
    require(root != ACTUAL_TARGET and root.is_relative_to(base) and root.parent == base, 'Outside explicit copy-case boundary')
    ancestry(root)
    require(root.is_dir(), 'Existing physical copy root required')
    return root


def file_digest(path: Path, budget: Budget) -> tuple[int, str]:
    info = node(path)
    require(stat.S_ISREG(info.st_mode), 'Expected regular copy file')
    size = 0
    h = hashlib.sha256()
    with path.open('rb') as stream:
        while True:
            budget.check()
            block = stream.read(1048576)
            if not block:
                break
            size += len(block)
            h.update(block)
    after = node(path)
    require(size == info.st_size == after.st_size and info.st_mtime_ns == after.st_mtime_ns, 'Copy file changed during read')
    return size, h.hexdigest()


def raise_walk_error(error: OSError) -> None:
    raise error


def tree(root: Path, budget: Budget) -> tuple[dict[str, tuple[int, str]], set[str]]:
    node(root)
    files: dict[str, tuple[int, str]] = {}
    dirs: set[str] = set()
    seen: set[str] = set()
    for parent, children, names in os.walk(root, followlinks=False, onerror=raise_walk_error):
        budget.check()
        for name in children + names:
            path = Path(parent) / name
            rel = relative(path.relative_to(root).as_posix())
            require(rel.casefold() not in seen, 'Case-colliding copy paths')
            seen.add(rel.casefold())
            info = node(path)
            if stat.S_ISDIR(info.st_mode):
                dirs.add(rel)
            else:
                files[rel] = file_digest(path, budget)
    require(len(files) <= 10000, 'Copy file-count bound exceeded')
    return files, dirs


def rows_map(rows: object) -> dict[str, tuple[int, str]]:
    require(type(rows) is list and 0 < len(rows) <= 10000, 'Bounded file array required')
    result: dict[str, tuple[int, str]] = {}
    folded: set[str] = set()
    for item in cast(list, rows):
        fields(item, {'path', 'bytes', 'sha256'})
        name = relative(item['path'])
        require(name.casefold() not in folded, 'Duplicate file record')
        folded.add(name.casefold())
        require(type(item['bytes']) is int and 0 <= item['bytes'] <= 2147483647, 'Raw bounded integer file length required')
        result[name] = item['bytes'], pin(item['sha256'])
    return result


def new_file(path: Path, raw: bytes, budget: Budget) -> None:
    budget.check()
    node(path.parent)
    with path.open('xb') as stream:
        stream.write(raw)
        stream.flush()
        os.fsync(stream.fileno())
    require(path.read_bytes() == raw, 'Create-new record readback mismatch')


def admission(root: Path, witness_sha: str, budget: Budget) -> dict:
    require({p.name for p in root.iterdir()} <= {'task', 'witness.json', 'abort-meta', 'retained'}, 'Unknown case-root node')
    for entry in root.iterdir():
        node(entry)
    node(root / 'witness.json')
    w, raw = load_json(root / 'witness.json')
    require(digest(raw) == pin(witness_sha), 'Witness byte identity mismatch')
    fields(w, WITNESS_FIELDS)
    require(w['protocol'] == 'PreparedAbortCopyWitness/1' and w['kind'] in ('ExactFailedSnapshot', 'SyntheticFixture') and w['phase'] == 'Prepared', 'Unsupported witness scope/phase')
    require(type(w['declaredTaskRoot']) is str and ntpath.isabs(w['declaredTaskRoot']), 'Declared source root required, never an IO target')
    expected = rows_map(w['files'])
    require(type(w['directories']) is list and all(type(x) is str for x in w['directories']), 'Directory array required')
    expected_dirs = {relative(x) for x in w['directories']}
    require(len(expected_dirs) == len(w['directories']) == len({x.casefold() for x in expected_dirs}), 'Duplicate directory record')
    current, dirs = tree(root / 'task', budget)
    pending_path = root / 'task/control/pending.json'
    p, pending_raw = load_json(pending_path)
    fields(p, PENDING_FIELDS)
    require(p['protocol'] == 'StandardPending/1' and p['operation'] == 'Apply' and p['previousReceiptSha256'] is None, 'Only first Apply pending is admissible')
    require(digest(pending_raw) == pin(w['pendingSha256']), 'Pending byte mismatch')
    transaction = pin(p['transactionId'], 32)
    intent_sha, binding_sha, journal_sha = pin(p['intentSha256']), pin(p['bindingSha256']), pin(p['journalSha256'])
    intent_path = root / ('task/control/intents/' + intent_sha + '.json')
    i, intent_raw = load_json(intent_path)
    fields(i, INTENT_FIELDS)
    require(digest(intent_raw) == intent_sha and i['protocol'] == 'StandardIntent/1', 'Intent identity mismatch')
    require(i['operation'] == 'Apply' and i['previousReceiptSha256'] is None and i['previousStateSha256'] is None, 'Unsupported intent/prior state')
    require(i['transactionId'] == transaction and i['bindingSha256'] == binding_sha and i['journalSha256'] == journal_sha, 'Intent/pending mismatch')
    binding_path = root / ('task/control/bindings/' + binding_sha + '.json')
    require(file_digest(binding_path, budget)[1] == binding_sha, 'Binding byte mismatch')
    j, journal_raw = load_json(root / 'task/install/deployment/journal.json')
    fields(j, JOURNAL_FIELDS)
    require(digest(journal_raw) == journal_sha and encoded(j) == encoded(i['journal']), 'Journal/intent mismatch')
    require(type(j['schema']) is int and j['schema'] == 1 and j['operation'] == 'Apply' and j['previous'] is None and j['copyProtocol'] == 'staged-v1', 'Only unpublished first Prepare is supported')
    original = ntpath.normpath(w['declaredTaskRoot'])
    require(j['installRoot'] == ntpath.join(original, 'install', 'deployment') and j['dataRoot'] == ntpath.join(original, 'data'), 'Journal declared-root mismatch')
    nxt = j['next']
    fields(nxt, {'schema', 'installRoot', 'dataRoot', 'current', 'releases'})
    require(type(nxt['schema']) is int and nxt['schema'] == 1 and nxt['installRoot'] == j['installRoot'] and nxt['dataRoot'] == j['dataRoot'], 'Next-state root/schema mismatch')
    require(type(nxt['releases']) is list and len(nxt['releases']) == 1, 'Exactly one unpublished candidate required')
    release = nxt['releases'][0]
    fields(release, {'id', 'files'})
    candidate = pin(release['id'])
    require(candidate == nxt['current'] == i['manifestSha256'], 'Candidate mismatch')
    pin(i['nextStateSha256']); pin(i['shellSpecSha256'])
    manifest_base = 'control/stage/' + candidate
    require(i['stageDirectory'] == ntpath.join(original, 'control', 'stage', candidate), 'Declared stage mismatch')
    manifest, manifest_raw = load_json(root / ('task/' + manifest_base + '/release-manifest.json'))
    fields(manifest, {'schema', 'runtime', 'selfContained', 'appOnlyPreview', 'releaseApproved', 'files'})
    require(digest(manifest_raw) == candidate and type(manifest['schema']) is int and manifest['schema'] == 1 and manifest['runtime'] == 'win-x64' and manifest['selfContained'] is True and manifest['appOnlyPreview'] is False and manifest['releaseApproved'] is False, 'Manifest identity/contract mismatch')
    payload = rows_map(release['files'])
    require(payload == rows_map(manifest['files']), 'Journal/manifest payload mismatch')
    source_rel = 'install/deployment/versions/' + candidate
    source = root / ('task/' + source_rel)
    retained = root / ('retained/' + candidate)
    ledger_rel = 'control/aborted-copy/' + intent_sha + '.json'
    ledger = root / ('task/' + ledger_rel)
    require(source.exists() != retained.exists(), 'Candidate must exist at exactly one known location')
    if retained.exists():
        archive_files, archive_dirs = tree(retained, budget)
        require(archive_files == payload, 'Retained payload drift')
        for name, value in archive_files.items():
            key = source_rel + '/' + name
            require(key not in current, 'Source/retained ambiguity')
            current[key] = value
        dirs.add(source_rel)
        dirs.update(source_rel + '/' + x for x in archive_dirs)
    candidate_files = {name[len(source_rel) + 1:]: value for name, value in current.items() if name.startswith(source_rel + '/')}
    require(candidate_files == payload, 'Incomplete/unknown/changed unpublished payload')
    require(all(current.get(manifest_base + '/' + name) == value for name, value in payload.items()), 'Stage payload mismatch')
    if ledger.exists():
        current.pop(ledger_rel)
        dirs.discard('control/aborted-copy')
    require(current == expected and dirs == expected_dirs, 'Complete copy snapshot drift or unknown nodes')
    require('install/deployment/state.json' not in current and not any(x.startswith(('control/receipts/', 'install/shell/')) for x in current), 'Selected/committed/shell state refused')
    require(not any(x == 'control/receipts' or x == 'install/shell' for x in dirs), 'Selected shell/receipt directory refused')
    if (root / 'retained').exists():
        require({x.name for x in (root / 'retained').iterdir()} == {candidate}, 'Unknown retained evidence')
    if (root / 'abort-meta').exists():
        require({x.name for x in (root / 'abort-meta').iterdir()} == {'plan.json'}, 'Unknown/partial abort metadata')
        node(root / 'abort-meta/plan.json')
    return {'witness': w, 'pending': p, 'intent': i, 'candidate': candidate, 'source': source, 'retained': retained, 'ledger': ledger, 'ledgerRel': ledger_rel, 'files': expected, 'directories': expected_dirs, 'pendingRawSha256': digest(pending_raw), 'journalRawSha256': digest(journal_raw), 'intentRawSha256': digest(intent_raw)}


def abort_copy(case_root: Path, witness_sha: str, fault_at: str | None = None, seconds: float = 155) -> dict:
    require(fault_at in (None, 'after_plan', 'after_move', 'before_receipt'), 'Unknown copy-only interruption')
    budget = Budget(seconds)
    root = case_boundary(case_root)
    state = admission(root, witness_sha, budget)
    plan = {'protocol': 'PreparedAbortCopyPlan/1', 'scope': 'FileCopyQualificationOnly', 'caseRoot': str(root), 'witnessSha256': witness_sha, 'transactionId': state['pending']['transactionId'], 'intentSha256': state['intentRawSha256'], 'pendingSha256': state['pendingRawSha256'], 'journalSha256': state['journalRawSha256'], 'candidate': state['candidate'], 'snapshotSha256': digest(encoded(state['witness']['files'])), 'nativeAuthorityResumed': False}
    plan_raw = encoded(plan)
    receipt = {'protocol': 'PreparedAbortCopyReceipt/1', 'scope': 'FileCopyQualificationOnly', 'state': 'AbortedCopyFenced', 'planSha256': digest(plan_raw), 'transactionId': plan['transactionId'], 'intentSha256': plan['intentSha256'], 'pendingSha256': plan['pendingSha256'], 'journalSha256': plan['journalSha256'], 'retainedCandidate': state['candidate'], 'originalFileCount': len(state['files']), 'snapshotSha256': plan['snapshotSha256'], 'nativeAdmissionAllowed': False, 'nativeAuthorityResumed': False, 'installed': False, 'liveRecovered': False, 'pendingAndJournalRetainedAtCopiedPaths': True}
    receipt_raw = encoded(receipt)
    meta = root / 'abort-meta'
    if state['ledger'].exists():
        require(meta.exists() and (meta / 'plan.json').read_bytes() == plan_raw, 'Abort receipt has no exact root-bound plan')
        require(state['retained'].exists() and state['ledger'].read_bytes() == receipt_raw, 'Abort receipt drift/partial record')
        return dict(receipt, result='AlreadyAbortedCopyFenced')
    if meta.exists():
        require((meta / 'plan.json').read_bytes() == plan_raw, 'Abort plan drift/foreign-root/partial record')
    else:
        meta.mkdir()
        new_file(meta / 'plan.json', plan_raw, budget)
    if fault_at == 'after_plan':
        raise InjectedInterruption('After immutable copy plan')
    if state['source'].exists():
        # Recheck complete bytes before same-case rename; never delete or overwrite.
        state = admission(root, witness_sha, budget)
        (root / 'retained').mkdir(exist_ok=True)
        budget.check()
        state['source'].rename(state['retained'])
    if fault_at == 'after_move':
        raise InjectedInterruption('After original payload retained by directory rename')
    state = admission(root, witness_sha, budget)
    if fault_at == 'before_receipt':
        raise InjectedInterruption('Before create-new abort receipt')
    if not state['ledger'].parent.exists():
        state['ledger'].parent.mkdir()
    new_file(state['ledger'], receipt_raw, budget)
    verified = admission(root, witness_sha, budget)
    require(verified['ledger'].read_bytes() == receipt_raw and verified['retained'].exists(), 'Post-abort readback failed')
    return dict(receipt, result='AbortedCopyFenced')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--copy-root', type=Path, required=True)
    parser.add_argument('--witness-sha256', required=True)
    args = parser.parse_args()
    print(json.dumps(abort_copy(args.copy_root, args.witness_sha256), indent=2))
