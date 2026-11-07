#!/usr/bin/env python3
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('storage', Path(__file__).with_name('storage-volume.py'))
storage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(storage)


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve() / 'volumes'
        self.fstab = Path(self.temp.name) / 'fstab'
        self.fstab.write_text('# original\n')
        self.mounted = False
        self.calls = []
        self.device = '/dev/disk/by-id/scsi-0HC_Volume_901'
        self.mount = dict(volume_id='901', uid=os.getuid(), gid=os.getgid())
        self.fs = 'ext4'
        self.source = self.device

    def command(self, *args):
        self.calls.append(args)
        if args[0] == 'blkid':
            return self.fs if args[2] == 'TYPE' else '12345678-1234-1234-1234-123456789abc'
        if args[0] == 'findmnt':
            if not self.mounted:
                raise subprocess.CalledProcessError(1, args)
            return self.source
        if args[0] == 'mount':
            self.mounted = True
        return ''

    def prepare(self, **kwargs):
        storage.prepare(self.mount, root=self.root, fstab=self.fstab, command=self.command,
                        wait=lambda _: None, block_device=lambda _: True, **kwargs)

    def test_redeploy_preserves_data_and_fstab_is_idempotent(self):
        self.prepare()
        sentinel = self.root / '901/data/upload.txt'
        sentinel.write_text('customer data')
        self.prepare()
        self.assertEqual(sentinel.read_text(), 'customer data')
        self.assertEqual(self.fstab.read_text().count('UUID='), 1)
        self.assertEqual(sum(c[0] == 'mount' for c in self.calls), 1)
        self.assertFalse(any('mkfs' in c[0] for c in self.calls))

    def test_missing_disk_does_not_create_data_or_mount(self):
        with self.assertRaisesRegex(ValueError, 'missing'):
            storage.prepare(self.mount, root=self.root, fstab=self.fstab, command=self.command,
                            wait=lambda _: None, block_device=lambda _: False)
        self.assertFalse((self.root / '901/data').exists())
        self.assertEqual(self.calls, [])

    def test_wrong_filesystem_is_never_formatted(self):
        self.fs = 'xfs'
        with self.assertRaisesRegex(ValueError, 'refusing to format'):
            self.prepare()
        self.assertFalse(self.mounted)

    def test_wrong_mounted_device_is_rejected(self):
        self.mounted = True
        self.source = '/dev/other'
        with self.assertRaisesRegex(ValueError, 'unexpected device'):
            self.prepare()

    def test_local_files_are_not_hidden_by_mount(self):
        target = self.root / '901'
        target.mkdir(parents=True)
        (target / 'existing').write_text('keep')
        with self.assertRaisesRegex(ValueError, 'hide existing files'):
            self.prepare()
        self.assertEqual((target / 'existing').read_text(), 'keep')

    def test_symlink_and_changed_ownership_are_rejected(self):
        self.prepare()
        data = self.root / '901/data'
        data.rmdir()
        data.symlink_to(self.root, target_is_directory=True)
        with self.assertRaisesRegex(ValueError, 'symlink'):
            self.prepare()
        data.unlink()
        self.mount['uid'] += 1
        with self.assertRaisesRegex(ValueError, 'ownership does not match'):
            self.prepare()

    def test_conflicting_fstab_is_not_overwritten(self):
        self.fstab.write_text(f'UUID=other {self.root}/901 ext4 defaults 0 2\n')
        with self.assertRaisesRegex(ValueError, 'Conflicting'):
            self.prepare()
        self.assertIn('UUID=other', self.fstab.read_text())

    def test_duplicate_shared_disk_is_prepared_once(self):
        with patch.object(storage, 'prepare') as prepare:
            storage.main(dict(services=[dict(storage_mounts=[self.mount]), dict(storage_mounts=[self.mount])]))
            prepare.assert_called_once_with(self.mount)


if __name__ == '__main__':
    unittest.main()
