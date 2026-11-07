#!/usr/bin/env python3
"""Host maintenance serialized with deployments; never collect application data."""
import argparse
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time

APPS = Path('/opt/minicloud/apps')
TEMP = Path('/tmp')
LOCK = Path('/opt/minicloud/locks/docker.deploy.lock')
GRACE_SECONDS = 3600


def command(*args):
    return subprocess.check_output(args, text=True, timeout=120)


def inspect_runtime(run=command):
    ids = run('docker', 'ps', '--all', '--quiet').split()
    containers = json.loads(run('docker', 'inspect', *ids)) if ids else []
    return ({c['Image'] for c in containers},
            [Path(m['Source']) for c in containers for m in c.get('Mounts', []) if m.get('Source')])


def process_paths(proc=Path('/proc')):
    paths = []
    for entry in proc.iterdir():
        if not entry.name.isdigit():
            continue
        try:
            paths.append((entry / 'cwd').resolve(strict=True))
            for arg in (entry / 'cmdline').read_bytes().decode(errors='replace').split('\0'):
                for match in re.finditer(r'/+(?:tmp/minicloud-deploy-|opt/minicloud/apps/)[^\s\x00\"\']*', arg):
                    paths.append(Path(match.group()))
            for fd in (entry / 'fd').iterdir():
                try:
                    paths.append(fd.resolve(strict=True))
                except (OSError, RuntimeError):
                    pass
        except (FileNotFoundError, ProcessLookupError, PermissionError):
            pass
    return [p for p in paths if str(p).startswith("/tmp/minicloud-deploy-") or
            (str(p).startswith("/opt/minicloud/apps/") and "/builds/" in str(p))]


def protected(path, paths):
    return any(path == p or path in p.parents or p in path.parents for p in paths)


def collect_directories(apps, temp, paths, now):
    removed = []
    if apps.is_symlink() or temp.is_symlink():
        raise ValueError('Maintenance roots must not be symlinks')
    for app in apps.iterdir():
        if app.is_symlink() or not app.is_dir() or not re.fullmatch(r'[a-z0-9][a-z0-9_-]*', app.name):
            continue
        builds = app / 'builds'
        if builds.is_symlink() or not builds.is_dir():
            continue
        for candidate in builds.iterdir():
            if re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,199}', candidate.name) and not protected(candidate, paths):
                if not candidate.is_symlink() and candidate.is_dir():
                    shutil.rmtree(candidate)
                    removed.append(str(candidate))
    for candidate in temp.glob('minicloud-deploy-*'):
        if candidate.is_symlink() or not candidate.is_dir() or protected(candidate, paths):
            continue
        # Uploads may be staged before their script obtains the host lock.
        newest = max([candidate.stat().st_mtime] + [p.lstat().st_mtime for p in candidate.iterdir()])
        if newest > now - GRACE_SECONDS:
            continue
        shutil.rmtree(candidate)
        removed.append(str(candidate))
    return removed


def configured_images(apps):
    images = set()
    for app in apps.iterdir():
        if app.is_symlink() or not app.is_dir():
            continue
        for name in ('app.compose.json', 'infra.compose.json', 'observability.compose.json'):
            config = app / name
            if config.is_symlink():
                raise ValueError('Compose configuration must not be a symlink')
            if config.exists():
                images.update(s['image'] for s in json.loads(config.read_text()).get('services', {}).values() if s.get('image'))
    return images


def collect_images(apps, protected_ids, run=command):
    for ref in configured_images(apps):
        # A configured recovery image need not currently have a container.
        result = json.loads(run('docker', 'image', 'inspect', ref))
        protected_ids.update(image['Id'] for image in result)
    removed = []
    for image_id in set(run('docker', 'image', 'ls', '--quiet', '--no-trunc').split()):
        if image_id in protected_ids:
            continue
        image = json.loads(run('docker', 'image', 'inspect', image_id))[0]
        refs = (image.get('RepoTags') or []) + (image.get('RepoDigests') or [])
        for ref in refs or [image_id]:
            # No force: Docker refuses to remove images newly referenced by a container.
            try:
                run('docker', 'image', 'rm', ref)
                removed.append(ref)
            except subprocess.CalledProcessError:
                # The final tag may already have removed its digest reference.
                if ref == image_id or not refs:
                    raise
    return removed


def maintain(apps=APPS, temp=TEMP, preserve=(), files_only=False, run=command, proc=Path('/proc')):
    images, mounts = inspect_runtime(run)
    paths = mounts + list(preserve) + process_paths(proc)
    removed = collect_directories(apps, temp, paths, time.time())
    if not files_only:
        collect_images(apps, images, run)
        run('docker', 'builder', 'prune', '--all', '--force', '--filter', 'until=48h')
        run('docker', 'builder', 'prune', '--all', '--force', '--keep-storage', '4294967296')
        run('journalctl', '--rotate', '--vacuum-time=2days')
    print(json.dumps({'maintenance': 'complete', 'directories_removed': len(removed)}))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--lock-held', action='store_true')
    parser.add_argument('--files-only', action='store_true')
    parser.add_argument('--preserve', action='append', default=[])
    args = parser.parse_args()
    if args.lock_held:
        # Descriptor 7 is inherited from remote-deploy.sh, which owns the host lock.
        if not os.path.samefile('/proc/self/fd/7', LOCK):
            raise ValueError('Missing deployment lock descriptor')
        lock_fd = 7
    else:
        lock_fd = os.open(LOCK, os.O_WRONLY | os.O_NOFOLLOW)
    try:
        fcntl.flock(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        if not args.lock_held:
            os.close(lock_fd)
        print('Maintenance skipped: deployment owns the host lock')
        return
    try:
        maintain(preserve=[Path(p) for p in args.preserve], files_only=args.files_only)
    finally:
        if not args.lock_held:
            os.close(lock_fd)


if __name__ == '__main__':
    main()
