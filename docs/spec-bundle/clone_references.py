#!/usr/bin/env python3
"""Read-only reference checkouts: clone public sources; never install or run them.

Requires Python 3.10+ and Git. Existing checkouts are inspected, not updated.
Resolved commit hashes are locked under <root>/_index/source-lock.json.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from typing import Any

SLUG = re.compile(r'[a-z0-9][a-z0-9-]{0,63}\Z')
REMOTE = re.compile(r'https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+?)(?:\.git)?\Z')
SHA = re.compile(r'[0-9a-f]{40}\Z')


def canonical_remote(value: str) -> str:
    if not isinstance(value, str):
        raise ValueError('Repository remote must be a string')
    match = REMOTE.fullmatch(value.rstrip('/'))
    if not match:
        raise ValueError('Expected an explicit public https://github.com/owner/repository remote')
    return '/'.join(match.groups()).lower()


def validate_manifest(data: Any) -> list[dict[str, Any]]:
    if not isinstance(data, dict) or data.get('schema') != 'dcu.reference-repositories.v1':
        raise ValueError('Unsupported reference manifest schema')
    sources = data.get('repositories')
    if not isinstance(sources, list) or not sources:
        raise ValueError('Manifest must contain a nonempty repositories array')
    seen: set[str] = set()
    seen_ids: set[str] = set()
    for source in sources:
        if not isinstance(source, dict):
            raise ValueError('Every source must be an object')
        sid = source.get('id')
        if not isinstance(sid, str) or not re.fullmatch(r'S[0-9]{2}', sid) or sid in seen_ids:
            raise ValueError('Missing, invalid or duplicated source ID')
        seen_ids.add(sid)
        slug = source.get('directory', '')
        if not isinstance(slug, str) or not SLUG.fullmatch(slug) or slug in seen:
            raise ValueError(f'Unsafe or duplicated source directory: {slug!r}')
        seen.add(slug)
        canonical_remote(source.get('repository', ''))
        anchors = source.get('read_first', [])
        if not isinstance(anchors, list) or any(not isinstance(a, str) or not a for a in anchors):
            raise ValueError('read_first must be an array of nonempty strings')
        for anchor in anchors:
            p = Path(anchor)
            if p.is_absolute() or '..' in p.parts or '\\' in anchor or ':' in anchor:
                raise ValueError(f'Unsafe code-reading path: {anchor!r}')
    return sources


def write_json_atomic(path: Path, value: Any) -> None:
    fd, temporary = tempfile.mkstemp(prefix=path.name + '.', suffix='.tmp', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8', newline='\n') as handle:
            json.dump(value, handle, indent=2, ensure_ascii=False)
            handle.write('\n')
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def git(arguments: list[str], hooks: Path, timeout: int = 300) -> str:
    env = dict(os.environ, GIT_TERMINAL_PROMPT='0', GIT_LFS_SKIP_SMUDGE='1')
    result = subprocess.run(
        ['git', '-c', f'core.hooksPath={hooks}', '-c', 'submodule.recurse=false',
         '-c', 'protocol.file.allow=never', *arguments],
        check=False, capture_output=True, text=True, encoding='utf-8', errors='replace',
        env=env, timeout=timeout,
    )
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or f'Git exited with status {result.returncode}')
    return result.stdout.strip()


def inspect_or_clone(source: dict[str, Any], root: Path, hooks: Path,
                     previous: dict[str, Any] | None) -> dict[str, Any]:
    destination = root / source['directory']
    expected_remote = canonical_remote(source['repository'])
    if previous is not None and not isinstance(previous, dict):
        raise ValueError('Invalid source-lock repository record')
    if previous is not None and not isinstance(previous.get('repository'), str):
        raise ValueError('Source-lock repository record is missing its original remote')
    desired_sha = previous.get('commit') if previous else None
    if desired_sha is not None and (not isinstance(desired_sha, str) or not SHA.fullmatch(desired_sha)):
        raise ValueError('Existing source lock contains an invalid commit hash')
    if previous and canonical_remote(previous['repository']) != expected_remote:
        raise ValueError('Manifest remote differs from the locked remote; review it explicitly')
    if destination.is_symlink():
        raise ValueError('Refusing a symlinked source checkout')
    if not destination.exists():
        git(['clone', '--filter=blob:none', '--depth', '1', '--no-recurse-submodules',
             '--no-checkout', source['repository'], str(destination)], hooks)
        if desired_sha:
            git(['-C', str(destination), 'fetch', '--depth', '1', 'origin', desired_sha], hooks)
        git(['-C', str(destination), 'checkout', '--detach', desired_sha or 'HEAD'], hooks)
    # Never pull, reset, clean or checkout over a pre-existing directory.
    if not (destination / '.git').exists():
        raise ValueError('Existing directory is not a Git checkout; it was left unchanged')
    origin = git(['-C', str(destination), 'remote', 'get-url', 'origin'], hooks)
    if canonical_remote(origin) != expected_remote:
        raise ValueError('Existing checkout has a different origin; it was left unchanged')
    if git(['-C', str(destination), 'status', '--porcelain', '--untracked-files=normal'], hooks):
        raise ValueError('Existing checkout has local changes; it was left unchanged')
    commit = git(['-C', str(destination), 'rev-parse', 'HEAD^{commit}'], hooks)
    if not SHA.fullmatch(commit):
        raise ValueError('Git did not return a full commit hash')
    if desired_sha and commit != desired_sha:
        raise ValueError('Existing checkout differs from its locked commit; it was left unchanged')
    tracked = git(['-C', str(destination), 'ls-files'], hooks).splitlines()
    licence_paths = [p for p in tracked if re.match(r'(?i)^(license|licence|copying|notice)(\.|$)', Path(p).name)]
    return {
        'id': source['id'], 'directory': source['directory'],
        'repository': source['repository'], 'commit': commit,
        'checked_at_utc': datetime.now(timezone.utc).isoformat(),
        'working_tree_clean': True, 'license_review': 'not_reviewed',
        'license_paths': licence_paths,
        'read_first_present': {p: (destination / p).exists() for p in source.get('read_first', [])},
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', type=Path, default=Path(__file__).with_name('reference-repositories.json'))
    parser.add_argument('--root', type=Path, default=None, help='Absolute checkout root; Windows example: C:\\other-software')
    parser.add_argument('--only', help='Comma-separated source directories; omitted means all references')
    parser.add_argument('--dry-run', action='store_true', help='Print destinations without writes or network access')
    args = parser.parse_args(argv)
    try:
        data = json.loads(args.manifest.read_text(encoding='utf-8'))
        sources = validate_manifest(data)
        if args.only:
            chosen = {name.strip() for name in args.only.split(',') if name.strip()}
            unknown = chosen - {s['directory'] for s in sources}
            if unknown or not chosen:
                raise ValueError(f'Unknown or empty --only selection: {sorted(unknown)}')
            sources = [s for s in sources if s['directory'] in chosen]
        if args.root is None:
            if os.name == 'nt':
                raise ValueError('On Windows, supply an explicit absolute --root, for example C:\\other-software')
            args.root = Path('/other-software')
        if not args.root.is_absolute():
            raise ValueError('--root must be absolute; no silent fallback directory is used')
        root = args.root.resolve()
        if args.dry_run:
            for source in sources:
                print(f"{source['id']} {source['repository']} -> {root / source['directory']}")
            return 0
        root.mkdir(parents=True, exist_ok=True)
        index = root / '_index'
        if index.is_symlink():
            raise ValueError('Refusing a symlinked reference index')
        index.mkdir(exist_ok=True)
        guard = index / 'checkout-in-progress.lock'
        # Exclusive guard prevents overlapping writers. Never remove another process's guard.
        try:
            with guard.open('x', encoding='utf-8') as handle:
                handle.write(str(os.getpid()))
        except FileExistsError:
            raise ValueError('Another checkout writer or a stale lock exists; inspect it before retrying')
        try:
            lock_path = index / 'source-lock.json'
            lock = json.loads(lock_path.read_text(encoding='utf-8')) if lock_path.exists() else {
                'schema': 'dcu.source-lock.v1', 'logical_root': '/other-software',
                'resolved_root': str(root), 'repositories': {},
            }
            if (not isinstance(lock, dict) or lock.get('schema') != 'dcu.source-lock.v1' or
                    lock.get('resolved_root') != str(root) or not isinstance(lock.get('repositories'), dict)):
                raise ValueError('Existing source lock schema/root is incompatible; it was not rewritten')
            failures: list[dict[str, str]] = []
            # An empty temporary hooks directory prevents inherited checkout hooks.
            with tempfile.TemporaryDirectory(prefix='dcu-empty-git-hooks-') as hooks_dir:
                for source in sources:
                    slug = source['directory']
                    try:
                        record = inspect_or_clone(source, root, Path(hooks_dir), lock['repositories'].get(slug))
                        lock['repositories'][slug] = record
                        write_json_atomic(lock_path, lock)
                        print(f"READY {slug} {record['commit']}")
                    except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as exc:
                        failures.append({'directory': slug, 'error': str(exc)})
                        print(f'BLOCKED {slug}: {exc}', file=sys.stderr)
            report = {'schema': 'dcu.checkout-report.v1', 'selected': len(sources),
                      'ready': len(sources)-len(failures), 'failures': failures,
                      'note': 'No dependencies, model weights, submodules or upstream software were installed/executed.'}
            write_json_atomic(index / 'last-checkout-report.json', report)
            return 1 if failures else 0
        finally:
            guard.unlink()
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(f'ERROR: {exc}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    raise SystemExit(main())
