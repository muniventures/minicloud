#!/usr/bin/env python3
"""Run only inside an ephemeral privileged Linux test container; no provider access."""
import importlib.util
import os
from pathlib import Path
import subprocess
import tempfile

if os.environ.get('MINICLOUD_STORAGE_LOOPBACK_TEST') != '1':
    raise SystemExit('Requires an ephemeral test container with MINICLOUD_STORAGE_LOOPBACK_TEST=1')
spec = importlib.util.spec_from_file_location('storage', Path(__file__).with_name('storage-volume.py'))
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)


def run(*args):
    return subprocess.check_output(args, text=True).strip()


with tempfile.TemporaryDirectory() as directory:
    base = Path(directory)
    image = base / 'volume.img'
    with image.open('wb') as output:
        output.truncate(32 * 1024 * 1024)
    loop = run('losetup', '--find', '--show', str(image))
    device = Path('/dev/disk/by-id/scsi-0HC_Volume_901')
    device.parent.mkdir(parents=True, exist_ok=True)
    if device.exists() or device.is_symlink():
        run('losetup', '--detach', loop)
        raise RuntimeError('Test device identity already exists')
    device.symlink_to(loop)
    root = base / 'storage'
    target = root / '901'
    fstab = base / 'fstab'
    fstab.write_text('# isolated test fstab\n')
    mount = dict(volume_id='901', uid=1000, gid=1000)
    try:
        run('mkfs.ext4', '-F', loop)
        storage.prepare(mount, root=root, fstab=fstab)
        sentinel = target / 'data/customer-upload.txt'
        sentinel.write_text('durable customer data')
        before = os.statvfs(target).f_blocks
        assert (target / 'data').stat().st_uid == 1000
        run('umount', str(target))
        storage.prepare(mount, root=root, fstab=fstab)
        assert sentinel.read_text() == 'durable customer data'
        with image.open('r+b') as output:
            output.truncate(64 * 1024 * 1024)
        run('losetup', '--set-capacity', loop)
        storage.prepare(mount, root=root, fstab=fstab)
        assert os.statvfs(target).f_blocks > before
        assert sentinel.read_text() == 'durable customer data'
        assert fstab.read_text().count('UUID=') == 1
        run('umount', str(target))
        device.unlink()
        try:
            storage.prepare(mount, root=root, fstab=fstab, wait=lambda _: None)
        except ValueError as error:
            assert 'missing' in str(error)
        else:
            raise AssertionError('Missing disk did not fail')
        assert not (target / 'data').exists(), 'Must never create a fallback local data directory'
        print('PASS: real ext4 mount, ownership, remount persistence, online growth and missing-disk refusal')
    finally:
        subprocess.run(['umount', str(target)], check=False, stderr=subprocess.DEVNULL)
        if device.is_symlink():
            device.unlink()
        run('losetup', '--detach', loop)
