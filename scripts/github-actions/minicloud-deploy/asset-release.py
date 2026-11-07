#!/usr/bin/env python3
"""Validate stopped-image exports and atomically publish current app assets."""
import base64
import hashlib
import fcntl
import errno
import time
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import signal
import stat
import subprocess
import sys
import tarfile
import tempfile
import zipfile

RUNTIMES = {'osx-arm64', 'osx-x64', 'linux-arm64', 'linux-x64', 'win-x64'}
MAX_BYTES = 2 * 1024 ** 3


def safe_name(value):
    return isinstance(value, str) and re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,100}', value) and value not in ('.', '..')


def safe_path(value):
    return isinstance(value, str) and '\\' not in value and not value.startswith('/') and all(p not in ('', '.', '..') for p in value.split('/'))


def digest(path):
    with path.open('rb') as stream:
        result = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            result.update(chunk)
        return result.hexdigest()


def validate(directory, wanted_digest=None):
    for path in [directory, *directory.parents]:
        if path.is_symlink():
            raise ValueError('Symlinked asset directory')
    manifest_path = directory / 'manifest.json'
    if manifest_path.is_symlink() or manifest_path.stat().st_size > 16384:
        raise ValueError('Invalid asset manifest')
    if wanted_digest and digest(manifest_path) != wanted_digest:
        raise ValueError('Asset manifest digest mismatch')
    manifest = json.loads(manifest_path.read_text())
    if manifest.get('protocolVersion') != 1 or not isinstance(manifest.get('version'), str):
        raise ValueError('Unsupported asset protocol')
    packages = manifest.get('packages', [])
    if len(packages) != 5 or {p.get('runtimeIdentifier') for p in packages} != RUNTIMES:
        raise ValueError('Asset must include exactly five supported runtimes')
    names, total = {'manifest.json'}, 0
    if (directory / '.input-sha256').exists():
        names.add('.input-sha256')
    for package in packages:
        name = package['fileName']
        if not safe_name(name) or name in names:
            raise ValueError('Invalid or duplicate asset filename')
        names.add(name)
        path = directory / name
        if path.is_symlink() or not path.is_file() or type(package.get('sizeBytes')) is not int or package['sizeBytes'] <= 0:
            raise ValueError('Invalid package file or size')
        total += path.stat().st_size
        if total > MAX_BYTES or path.stat().st_size != package['sizeBytes'] or digest(path) != package['sha256']:
            raise ValueError('Asset package digest or size mismatch')
        with zipfile.ZipFile(path) as archive:
            entries = archive.infolist()
            if len(entries) > 10000 or sum(e.file_size for e in entries) > 4 * 1024 ** 3:
                raise ValueError('Unbounded package archive')
            for entry in entries:
                if not safe_path(entry.filename.rstrip('/')) or stat.S_ISLNK(entry.external_attr >> 16):
                    raise ValueError('Unsafe package archive path')
            executable = 'si.exe' if package['runtimeIdentifier'] == 'win-x64' else 'si'
            info = archive.getinfo(executable)
            if executable == 'si' and not info.external_attr >> 16 & 0o111:
                raise ValueError('Package executable permissions missing')
            if archive.testzip() is not None:
                raise ValueError('Package CRC failure')
    if {p.name for p in directory.iterdir()} != names:
        raise ValueError('Unexpected asset output files')
    return manifest


def location(app, name, release):
    if not safe_name(name) or not safe_name(release):
        raise ValueError('Invalid asset identity')
    return app / 'assets' / name / release


def verify_mounts(app, payload):
    for service in payload['services']:
        for mount in service.get('asset_mounts', []):
            if mount.get('release_id'):
                directory = location(app, mount['name'], mount['release_id'])
            else:
                current = location(app, mount['name'], 'current')
                release = os.readlink(current)
                if not safe_name(release):
                    raise ValueError('Invalid current asset identity')
                directory = location(app, mount['name'], release)
            manifest = validate(directory, mount['manifest_sha256'])
            if manifest['protocolVersion'] != mount['protocol_version']:
                raise ValueError('Asset protocol mismatch')


def publish(app, payload):
    def interrupted(signum, frame):
        raise InterruptedError('Asset publication interrupted')
    signal.signal(signal.SIGTERM, interrupted)
    asset = payload['asset_publication']
    target = location(app, asset['name'], asset['release_id'])
    for parent in [target, *target.parents]:
        if parent.is_symlink():
            raise ValueError('Symlinked asset storage')
    target.parent.mkdir(parents=True, exist_ok=True, mode=0o755)
    # Consumers bind this parent and may run under a different non-root UID.
    target.parent.chmod(0o755)
    # The caller holds the host/app deployment locks, so no prior exporter remains active.
    for abandoned in sorted(target.parent.glob('.publishing-*'))[:8]:
        if abandoned.is_symlink() or not abandoned.is_dir() or abandoned.stat().st_uid != os.getuid():
            raise ValueError('Unsafe abandoned staging directory')
        abandoned.chmod(0o700)
        shutil.rmtree(abandoned)
    existing = [p for p in target.parent.iterdir() if p.is_dir() and not p.is_symlink() and not p.name.startswith('.')]
    all_storage = [release for name in (app / 'assets').iterdir() if name.is_dir() and not name.is_symlink()
                   for release in name.iterdir() if release.is_dir() and not release.is_symlink()]
    all_releases = [release for name in (app / 'assets').iterdir() if name.is_dir() and not name.is_symlink()
                    for release in name.iterdir() if release.is_dir() and not release.is_symlink() and not release.name.startswith('.')]
    if not target.exists() and (len(existing) >= 32 or len(all_releases) >= 128 or sum(f.stat().st_size for p in all_storage for f in p.iterdir() if f.is_file()) > 8 * 1024 ** 3 - MAX_BYTES):
        raise ValueError('Asset retention quota reached')
    if target.exists():
        # A retry may recover only the exact immutable bytes originally published.
        identity = target / '.input-sha256'
        if identity.is_symlink() or identity.read_text() != asset['input_sha256']:
            raise ValueError('Asset release identity mismatch')
        manifest = validate(target)
    else:
        staging = Path(tempfile.mkdtemp(prefix='.publishing-', dir=target.parent))
        container = None
        try:
            scope = hashlib.sha256(str(app.absolute()).encode()).hexdigest()[:24]
            abandoned = subprocess.check_output(['docker', 'ps', '-aq', '--filter', 'label=dev.minicloud.asset-export=' + scope,
                                                 '--filter', 'status=created'], text=True).splitlines()
            for previous in abandoned[:8]:
                if not re.fullmatch(r'[a-f0-9]{12,64}', previous):
                    raise ValueError('Invalid export container identity')
                subprocess.run(['docker', 'rm', previous], check=True, stdout=subprocess.DEVNULL)
            container = subprocess.check_output(['docker', 'create', '--network', 'none', '--label',
                'dev.minicloud.asset-export=' + scope, asset['image']], text=True).strip()
            output = asset['output_path']
            if not output.startswith('/') or any(p in ('', '.', '..') for p in output[1:].split('/')):
                raise ValueError('Unsafe image output path')
            process = subprocess.Popen(['docker', 'cp', container + ':' + output + '/.', '-'], stdout=subprocess.PIPE)
            try:
                total, names = 0, set()
                with tarfile.open(fileobj=process.stdout, mode='r|*') as archive:
                    for member in archive:
                        name = member.name.removeprefix('./')
                        if member.isdir() and name in ('', '.'):
                            continue
                        if not member.isfile() or not safe_name(name) or name in names:
                            raise ValueError('Unsafe image export path or type')
                        names.add(name)
                        total += member.size
                        if len(names) > 6 or total > MAX_BYTES or member.size < 0:
                            raise ValueError('Asset export exceeds bounds')
                        with archive.extractfile(member) as source, (staging / name).open('xb') as destination:
                            shutil.copyfileobj(source, destination)
                if process.wait(timeout=30):
                    raise ValueError('Asset export failed')
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait()
                process.stdout.close()
            manifest = validate(staging)
            for path in staging.iterdir():
                path.chmod(0o444)
            identity = staging / '.input-sha256'
            identity.write_text(asset['input_sha256'])
            identity.chmod(0o444)
            staging.chmod(0o555)
            lease = Path(payload['worker_lease_file'])
            with Path(str(lease) + '.lock').open('a') as fence:
                fcntl.flock(fence, fcntl.LOCK_EX)
                if lease.read_text().strip() != payload['ownership_token'] or time.time() - lease.stat().st_mtime > 5:
                    raise ValueError('Publication ownership lost')
                os.rename(staging, target)
        finally:
            if container:
                subprocess.run(['docker', 'rm', '-f', container], stdout=subprocess.DEVNULL, check=False)
            if staging.exists():
                staging.chmod(0o700)
                shutil.rmtree(staging)
    protected = set(asset.get('retained_release_ids', [])) | {asset['release_id']}
    unreferenced = sorted((p for p in existing if p.name not in protected), key=lambda p: p.stat().st_mtime, reverse=True)
    removed = []
    for old in unreferenced[2:10]:
        if old.is_symlink() or time.time() - old.stat().st_mtime < 7 * 86400:
            continue
        lease = Path(payload['worker_lease_file'])
        with Path(str(lease) + '.lock').open('a') as fence:
            fcntl.flock(fence, fcntl.LOCK_EX)
            if lease.read_text().strip() != payload['ownership_token'] or time.time() - lease.stat().st_mtime > 5:
                raise ValueError('Retention ownership lost')
            old.chmod(0o700)
            shutil.rmtree(old)
            removed.append(old.name)
    result = dict(manifest=manifest, manifestBase64=base64.b64encode((target / 'manifest.json').read_bytes()).decode(), manifestSha256=digest(target / 'manifest.json'), removedReleaseIds=removed)
    # Mount the stable asset parent so existing consumers observe this atomic switch.
    lease = Path(payload['worker_lease_file'])
    with Path(str(lease) + '.lock').open('a') as fence:
        fcntl.flock(fence, fcntl.LOCK_EX)
        if lease.read_text().strip() != payload['ownership_token'] or time.time() - lease.stat().st_mtime > 5:
            raise ValueError('Publication ownership lost')
        current = target.parent / 'current'
        pending = target.parent / ('.current-' + asset['release_id'])
        if current.exists() and not current.is_symlink():
            raise ValueError('Invalid current asset identity')
        if pending.is_symlink():
            pending.unlink()
        try:
            pending.symlink_to(target.name, target_is_directory=True)
            os.replace(pending, current)
        finally:
            if pending.is_symlink():
                pending.unlink()
    print('__MINICLOUD_ASSET_MANIFEST__=' + base64.b64encode(json.dumps(result).encode()).decode())


if __name__ == '__main__':
    try:
        mode, app_dir, payload_file = sys.argv[1:]
        payload = json.loads(Path(payload_file).read_text())
        if mode == 'publish':
            publish(Path(app_dir), payload)
        elif mode == 'verify':
            verify_mounts(Path(app_dir), payload)
        else:
            raise ValueError('Invalid asset operation')
    except Exception as error:
        # Emit bounded reason codes without payload, filesystem paths or credential values.
        reasons = {
            'Asset retention quota reached': 'storage_quota',
            'Publication ownership lost': 'ownership_lost',
            'Retention ownership lost': 'ownership_lost',
            'Asset export exceeds bounds': 'export_limit',
            'Asset package digest or size mismatch': 'package_integrity',
            'Asset manifest digest mismatch': 'manifest_integrity',
            'Unsupported asset protocol': 'unsupported_protocol',
            'Asset protocol mismatch': 'unsupported_protocol',
            'Asset release identity mismatch': 'immutable_identity',
        }
        reason = 'disk_full' if isinstance(error, OSError) and error.errno == errno.ENOSPC else reasons.get(str(error), 'invalid_output')
        print('ERROR: asset_release_invalid: reason=' + reason + '; consumer bindings unchanged.', file=sys.stderr)
        sys.exit(1)
