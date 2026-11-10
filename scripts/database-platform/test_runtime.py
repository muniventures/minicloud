import importlib.util
import json
import io
import os
from pathlib import Path
import sqlite3
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import operation
import worker
import restore
import capacity


class RuntimeTests(unittest.TestCase):
    def test_first_backup_lock_uses_prepared_user_owned_root_without_run_access(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'runtime-locks' / 'db-backups'
            root.parent.mkdir(mode=0o755)
            expected = '/opt/minicloud/locks/db-backups'
            def fixture_path(value):
                self.assertEqual(value, expected)
                return root
            with patch.dict(os.environ, {}, clear=True), patch.object(worker, 'Path', side_effect=fixture_path):
                with worker.source_lock('database_fixture01'):
                    self.assertTrue((root / 'database_fixture01').is_file())
                    self.assertEqual(root.stat().st_uid, os.geteuid())
                    self.assertEqual(root.stat().st_mode & 0o777, 0o700)
                    self.assertEqual((root / 'database_fixture01').stat().st_mode & 0o777, 0o600)
                    with self.assertRaises(BlockingIOError):
                        with worker.source_lock('database_fixture01'):
                            self.fail('Concurrent source lock must not be admitted')
                with worker.source_lock('database_fixture01'):
                    pass

    def test_tmpfs_and_memory_reserves_are_independent(self):
        required = 1024**2
        with self.assertRaises(capacity.BackupCapacityError):
            capacity.require_capacity(required, disk_usage=lambda _: SimpleNamespace(free=required), memory=lambda: 1024**3)
        with self.assertRaises(capacity.BackupCapacityError):
            capacity.require_capacity(required, disk_usage=lambda _: SimpleNamespace(free=1024**3), memory=lambda: required)
        capacity.require_capacity(required, disk_usage=lambda _: SimpleNamespace(free=1024**3), memory=lambda: 1024**3)

    def test_encrypted_workspace_keeps_disk_floor_and_bounds_stream(self):
        with self.assertRaises(capacity.BackupCapacityError):
            capacity.require_disk_capacity('/fixture', 1, disk_usage=lambda _: SimpleNamespace(free=256*1024**2,total=1024**3))
        target=io.BytesIO()
        with patch.object(capacity,'require_disk_capacity',side_effect=capacity.BackupCapacityError):
            with self.assertRaises(capacity.BackupCapacityError):
                capacity.write_encrypted_stream(io.BytesIO(b'ciphertext'),target,'/fixture')
        self.assertEqual(target.getvalue(),b'')
        with patch.object(capacity,'require_disk_capacity'):
            with self.assertRaises(capacity.BackupCapacityError):
                capacity.write_encrypted_stream(io.BytesIO(b'too large'),target,'/fixture',maximum=2)
        self.assertEqual(target.getvalue(),b'')

    def test_restore_rejects_capacity_before_decryption(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'database.dump.enc').write_bytes(b'ciphertext')
            with patch.object(restore,'require_capacity',side_effect=capacity.BackupCapacityError), patch.object(restore,'crypt') as decrypt:
                with self.assertRaises(capacity.BackupCapacityError):
                    restore.restore(root,'k'*64,'db_fixture01',root/'new.db')
                decrypt.assert_not_called()
                self.assertFalse((root/'new.db').exists())

    def test_shared_database_space_preserves_reserve(self):
        def run(args,**kwargs):
            return SimpleNamespace(returncode=0,stdout=b'Filesystem 1024-blocks Used Available Capacity Mounted\nfixture 10485760 9437184 1048576 90% /var/lib/postgresql/data\n')
        with self.assertRaises(capacity.BackupCapacityError):
            operation.require_database_space('db-1',1024,run)

    def test_source_size_uses_same_scoped_container_socket(self):
        calls=[]
        def run(args,**kwargs):
            calls.append((args,kwargs))
            return SimpleNamespace(returncode=0,stdout=b'8388608\n')
        command=['docker','exec','-e','PGHOST','db-1','pg_dump','--format=custom']
        env={'PGHOST':'/var/run/postgresql','PGDATABASE':'source_db'}
        self.assertEqual(worker.postgres_database_size(command,env,run),8388608)
        self.assertEqual(calls[0][0][:6],['docker','exec','-e','PGHOST','db-1','psql'])
        self.assertIs(calls[0][1]['env'],env)
        self.assertIn('current_database()',calls[0][0][-1])

    def test_restore_rejects_invalid_size_or_shared_disk_capacity_before_dump_decryption(self):
        original_directory=tempfile.TemporaryDirectory
        def local_temporary(*args,**kwargs):
            kwargs.pop('dir',None)
            return original_directory(*args,**kwargs)
        with original_directory() as directory:
            root=Path(directory)
            encrypted=root/'database.dump.enc'
            encrypted.write_bytes(b'ciphertext')
            calls=[]
            def run(args,**kwargs):
                calls.append(args)
                return SimpleNamespace(returncode=0,stdout=b'Filesystem 1024-blocks Used Available Capacity Mounted\nfixture 10485760 9437184 1048576 90% /var/lib/postgresql/data\n')
            for size in (None,True,-1,0,'1024',2**63,8*1024**2):
                payload={'databaseId':'db_fixture01','backupId':'backup-fixture','format':'pg_dump-custom','databaseSizeBytes':size}
                with patch.object(restore.tempfile,'TemporaryDirectory',side_effect=local_temporary), patch.object(restore,'require_capacity'), patch.object(restore._checker,'verify_backup',return_value=(payload,encrypted,root/'key')), patch.object(restore,'crypt') as decrypt:
                    with self.assertRaises((ValueError,capacity.BackupCapacityError)):
                        restore.restore(root,'k'*64,'db_fixture01','target_db',container='db-1',target_role='target_user',run=run)
                    decrypt.assert_not_called()
            self.assertEqual(len(calls),1)
            self.assertIn('df',calls[0])

    def test_lifecycle_sql_is_stdin_and_identity_checked(self):
        calls = []
        def run(args, **kw):
            calls.append((args,kw))
            if 'df' in args:
                return SimpleNamespace(returncode=0,stdout=b'Filesystem 1024-blocks Used Available Capacity Mounted\nfixture 10485760 1024 10484736 1% /var/lib/postgresql/data\n')
            return SimpleNamespace(returncode=0)
        request = dict(action='create', databaseName='fixture', username='fixture_user', container='db-1', password="safe-secret-'"*4)
        operation.execute(request, run)
        args, values = next(item for item in calls if 'psql' in item[0])
        self.assertIn('minicloud', args)
        self.assertNotIn(request['password'], ' '.join(args))
        self.assertIn(b'minicloud.database:fixture', values['input'])
        self.assertIn(b'WHERE NOT EXISTS', values['input'])
        self.assertIn(b'REVOKE ALL', values['input'])
        request['databaseName'] = 'x; DROP DATABASE postgres'
        with self.assertRaises(ValueError):
            operation.execute(request, run)
        self.assertEqual(len(calls), 2)

    def test_sqlite_capture_authenticate_restore_and_no_overwrite(self):
        if not Path('/dev/shm').exists():
            self.skipTest('Linux tmpfs required')
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root/'source.db'
            with sqlite3.connect(source) as db:
                db.execute('CREATE TABLE fixture(value TEXT)')
                db.execute("INSERT INTO fixture VALUES ('preserved')")
            artifacts = root/'artifacts'
            artifacts.mkdir()
            def upload(url, token, path):
                (artifacts/path.name).write_bytes(path.read_bytes())
            env = dict(BACKUP_ENGINE='sqlite', SQLITE_PATH=str(source), BACKUP_URL='https://fixture.example',
                       BACKUP_DATABASE_ID='db_fixture01', BACKUP_ENVIRONMENT='org-fixture', BACKUP_TOKEN='t'*48,
                       BACKUP_ENCRYPTION_KEY='k'*64, BACKUP_KEY_ID='key-fixture', BACKUP_LOCK_ROOT=str(root/'locks'))
            with patch.dict(os.environ, env), patch.object(worker,'upload',side_effect=upload):
                result = worker.backup()
            self.assertEqual(result['format'],'sqlite-snapshot')
            with self.assertRaises(ValueError):
                restore.restore(artifacts,env['BACKUP_ENCRYPTION_KEY'],'db_other001',root/'wrong.db')
            target = root/'restored.db'
            restore.restore(artifacts,env['BACKUP_ENCRYPTION_KEY'],'db_fixture01',target)
            with sqlite3.connect(target) as db:
                self.assertEqual(db.execute('SELECT value FROM fixture').fetchone()[0],'preserved')
            with self.assertRaises(FileExistsError):
                restore.restore(artifacts,env['BACKUP_ENCRYPTION_KEY'],'db_fixture01',target)


if __name__ == '__main__':
    unittest.main()
