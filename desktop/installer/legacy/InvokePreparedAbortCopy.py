"""Fixed isolated file-copy bridge for the existing authoritative coordinator.
No live adapter, native authority, subprocess, or caller-selected implementation.
"""
import sys
sys.dont_write_bytecode = True
import argparse
import hashlib
import importlib.util
import re
from pathlib import Path

LIBRARY = Path(__file__).resolve().with_name('PreparedAbortCopy.py')
LIBRARY_SHA256 = '6c1e0188c313228181168c64e610c1250fe56a51c036f8a22e7b4dfb5502828d'


WIRE_FIELDS = (
    ('protocol', 'S'), ('scope', 'S'), ('state', 'S'), ('planSha256', 'S'),
    ('transactionId', 'S'), ('intentSha256', 'S'), ('pendingSha256', 'S'),
    ('journalSha256', 'S'), ('retainedCandidate', 'S'), ('originalFileCount', 'I'),
    ('snapshotSha256', 'S'), ('nativeAdmissionAllowed', 'B'),
    ('nativeAuthorityResumed', 'B'), ('installed', 'B'), ('liveRecovered', 'B'),
    ('pendingAndJournalRetainedAtCopiedPaths', 'B'), ('result', 'S'),
)


def encode_wire(result):
    # Separate fixed bridge ABI, not N5's same-host canonical JSON serializer.
    if type(result) is not dict or set(result) != {name for name, _ in WIRE_FIELDS}:
        raise ValueError('Closed bridge receipt fields required')
    lines = ['NativePreparedAbortCopyWire/1']
    for name, kind in WIRE_FIELDS:
        value = result[name]
        if kind == 'S':
            if type(value) is not str or re.fullmatch(r'[\x20-\x7e]{1,128}', value) is None:
                raise ValueError('Bridge raw bounded ASCII string required')
            word = value
        elif kind == 'I':
            if type(value) is not int or not 1 <= value <= 10000:
                raise ValueError('Bridge raw bounded integer required')
            word = str(value)
        else:
            if type(value) is not bool:
                raise ValueError('Bridge raw boolean required')
            word = 'true' if value else 'false'
        lines.append(name + '\t' + kind + '\t' + word)
    return ('\n'.join(lines) + '\n').encode('ascii')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--copy-root', type=Path, required=True)
    parser.add_argument('--witness-sha256', required=True)
    parser.add_argument('--budget-milliseconds', type=int, required=True)
    args = parser.parse_args()
    if not 1000 <= args.budget_milliseconds <= 120000:
        raise ValueError('Coordinator copy budget bound')
    if hashlib.sha256(LIBRARY.read_bytes()).hexdigest() != LIBRARY_SHA256:
        raise ValueError('Fixed qualified copy library identity mismatch')
    spec = importlib.util.spec_from_file_location('coordinator_qualified_abort_copy', LIBRARY)
    if spec is None or spec.loader is None:
        raise ImportError('Fixed copy library loader unavailable')
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    result = module.abort_copy(args.copy_root, args.witness_sha256, seconds=args.budget_milliseconds / 1000)
    sys.stdout.buffer.write(encode_wire(result))
    sys.stdout.buffer.flush()


if __name__ == '__main__':
    main()
