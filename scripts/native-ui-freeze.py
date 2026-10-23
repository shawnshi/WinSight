"""Freeze task-scoped source without staging or modifying either Git index."""
from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {'.git', '.dev', 'bin', 'obj', 'target', 'node_modules', '__pycache__', '.ruff_cache'}

def run(*args: str) -> str:
    return subprocess.check_output(args, cwd=ROOT, text=True, encoding='utf-8')

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', default='.dev/native-migration/p0')
    parser.add_argument('--lanes', nargs='*', default=[])
    args = parser.parse_args()
    output = (ROOT / args.output).resolve()
    if not output.is_relative_to(ROOT / '.dev'):
        raise SystemExit('Baseline must remain under the task .dev directory')
    if output.exists():
        raise SystemExit(f'Refusing to overwrite baseline: {output}')
    names = set(run('git', 'ls-files', '-z', '--cached', '--others', '--exclude-standard').split('\0'))
    files = sorted(ROOT / name for name in names if name and (ROOT / name).is_file()
                   and not any(part in EXCLUDED for part in Path(name).parts))
    output.mkdir(parents=True)
    records = []
    for source in files:
        relative = source.relative_to(ROOT)
        target = output / 'source' / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        records.append({'path': relative.as_posix(), 'bytes': source.stat().st_size,
                        'sha256': hashlib.sha256(source.read_bytes()).hexdigest()})
    status = run('git', 'status', '--porcelain=v1', '--untracked-files=all')
    (output / 'status-before.txt').write_text(status, encoding='utf-8')
    (output / 'manifest.json').write_text(json.dumps({
        'createdUtc': datetime.now(timezone.utc).isoformat(), 'root': str(ROOT),
        'head': run('git', 'rev-parse', 'HEAD').strip(), 'files': records,
        'exclusions': sorted(EXCLUDED), 'scope': 'source and fixtures, not user indexes/models/credentials',
        'recovery': 'Restore only task-modified files from source/ after checking later user edits; never reset the worktree.'
    }, ensure_ascii=False, indent=2), encoding='utf-8')
    for lane in args.lanes:
        if not lane.isidentifier():
            raise SystemExit('Lane names must be identifiers')
        target = ROOT / '.dev' / 'native-migration' / 'lanes' / lane
        subprocess.run(['git', 'worktree', 'add', '--detach', '--no-checkout', str(target), 'HEAD'],
                       cwd=ROOT, check=True, timeout=30)
        shutil.copytree(output / 'source', target, dirs_exist_ok=True)
    print(json.dumps({'baseline': str(output), 'fileCount': len(records),
                      'bytes': sum(r['bytes'] for r in records), 'lanes': args.lanes}))

if __name__ == '__main__':
    main()
