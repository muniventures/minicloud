#!/usr/bin/env python3
"""Mount preformatted Hetzner volumes; never format, detach, or delete storage."""
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import time

ROOT = Path('/var/lib/minicloud/customer-storage')


def run(*args):
    return subprocess.check_output(args, text=True, timeout=180).strip()


def safe_directory(path):
    for parent in [*reversed(path.parents), path]:
        if parent.is_symlink():
            raise ValueError('Storage paths must not contain symlinks')
    path.mkdir(parents=True, exist_ok=True, mode=0o755)


def configure_fstab(uuid, target, fstab=Path('/etc/fstab')):
    lines = fstab.read_text().splitlines()
    source = 'UUID=' + uuid
    for line in lines:
        fields = line.split()
        if not fields or fields[0].startswith('#') or len(fields) < 2:
            continue
        if fields[0] == source or fields[1] == str(target):
            if fields[:3] != [source, str(target), 'ext4']:
                raise ValueError('Conflicting existing fstab entry for storage')
            return
    with fstab.open('a') as output:
        output.write(f'\n{source} {target} ext4 defaults 0 2\n')


def is_block_device(device):
    try:
        return stat.S_ISBLK(device.stat().st_mode)
    except FileNotFoundError:
        return False


def prepare(mount, root=ROOT, fstab=Path('/etc/fstab'), command=run, wait=time.sleep, block_device=is_block_device):
    volume_id = str(mount['volume_id'])
    if not re.fullmatch(r'[1-9][0-9]*', volume_id):
        raise ValueError('Invalid provider volume ID')
    uid, gid = mount['uid'], mount['gid']
    if type(uid) is not int or type(gid) is not int or not 0 <= uid <= 2147483647 or not 0 <= gid <= 2147483647:
        raise ValueError('Invalid storage ownership')
    target = root / volume_id
    safe_directory(target)
    device = Path('/dev/disk/by-id/scsi-0HC_Volume_' + volume_id)
    for attempt in range(60):
        if block_device(device):
            break
        wait(1)
    else:
        raise ValueError('Expected Hetzner block device is missing')
    if command('blkid', '-s', 'TYPE', '-o', 'value', str(device)) != 'ext4':
        raise ValueError('Storage must already contain ext4; refusing to format')
    uuid = command('blkid', '-s', 'UUID', '-o', 'value', str(device))
    if not re.fullmatch(r'[0-9a-fA-F-]{36}', uuid):
        raise ValueError('Invalid filesystem UUID')
    try:
        source = command('findmnt', '--noheadings', '--output', 'SOURCE', '--mountpoint', str(target))
    except subprocess.CalledProcessError:
        source = ''
    if source:
        if os.path.realpath(source) != os.path.realpath(device):
            raise ValueError('Storage path is mounted from an unexpected device')
    else:
        if any(target.iterdir()):
            raise ValueError('Refusing to hide existing files beneath a storage mount')
        command('mount', '-t', 'ext4', str(device), str(target))
        source = command('findmnt', '--noheadings', '--output', 'SOURCE', '--mountpoint', str(target))
        if os.path.realpath(source) != os.path.realpath(device):
            raise ValueError('Storage mount verification failed')
    configure_fstab(uuid, target, fstab)
    command('resize2fs', str(device))
    marker = target / '.minicloud-storage.json'
    expected = dict(volume_id=volume_id, uid=uid, gid=gid)
    if marker.is_symlink():
        raise ValueError('Storage marker must not be a symlink')
    if marker.exists() and json.loads(marker.read_text()) != expected:
        raise ValueError('Storage identity or ownership does not match')
    data = target / 'data'
    if data.is_symlink():
        raise ValueError('Storage data directory must not be a symlink')
    if not data.exists():
        data.mkdir(mode=0o755)
        os.chown(data, uid, gid)
    elif not marker.exists():
        raise ValueError('Unrecognized storage directory; refusing ownership changes')
    if not marker.exists():
        marker.write_text(json.dumps(expected))
        marker.chmod(0o600)


def main(payload):
    mounts = {}
    for service in payload.get('services', []):
        for mount in service.get('storage_mounts', []):
            identity = str(mount['volume_id'])
            if identity in mounts and any(mount[k] != mounts[identity][k] for k in ('uid', 'gid')):
                raise ValueError('Shared storage ownership differs')
            mounts[identity] = mount
    for mount in mounts.values():
        prepare(mount)


if __name__ == '__main__':
    try:
        main(json.loads(Path(sys.argv[1]).read_text()))
    except Exception as error:
        print(f'ERROR: storage_mount_failed: {error}', file=sys.stderr)
        sys.exit(1)
