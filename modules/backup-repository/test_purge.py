import hashlib
import io
import json
import os
from unittest.mock import patch
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

from server import Repository,Settings,Rejected


class PurgeTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory()
        self.root=Path(self.temporary.name)
        self.token='a'*64
        self.database='database_fixture01'
        self.settings=Settings(self.root,[dict(environment='fixture',databaseId=self.database,token=self.token,policyRevision=3)],'repository-fixture',trusted_proxies=['127.0.0.1/32'])
        self.repo=Repository(self.settings,mount_check=lambda _:True,disk_usage=lambda _:SimpleNamespace(free=1024**3))
        for name in ('database.dump.enc','manifest.json.enc'):
            data=b'encryptedfixture'
            self.repo.put(('fixture',self.database,'backup-fixture01'),name,io.BytesIO(data),len(data),hashlib.sha256(data).hexdigest())
        self.settings.policies[('fixture',self.database)]['enabled']=False

    def tearDown(self): self.temporary.cleanup()

    def purge(self,revision=3,repository='repository-fixture'):
        return self.repo.purge_source('fixture',self.database,revision,repository)

    def test_disabled_source_cannot_append_after_purge(self):
        self.purge()
        data=b'encryptedfixture'
        with self.assertRaises(Rejected) as caught:
            self.repo.put(('fixture',self.database,'backup-fixture02'),'database.dump.enc',io.BytesIO(data),len(data),hashlib.sha256(data).hexdigest())
        self.assertEqual(caught.exception.status,403)
        self.assertFalse((self.root/'fixture'/self.database/'backup-fixture02').exists())

    def test_exact_disabled_source_purges_and_replay_is_safe(self):
        result=self.purge()
        self.assertEqual(result,dict(status='purged',purgedBackupIds=['backup-fixture01'],purgedCount=1,remainingCount=0))
        self.assertFalse((self.root/'fixture'/self.database/'backup-fixture01').exists())
        self.assertEqual(self.purge()['purgedCount'],0)

    def test_enabled_manual_retained_stale_revision_wrong_repository_fail_closed(self):
        policy=self.settings.policies[('fixture',self.database)]
        policy['enabled']=True
        with self.assertRaises(Rejected): self.purge()
        policy['enabled']=False
        policy['keepLastCopies']=True
        with self.assertRaises(Rejected): self.purge()
        policy['keepLastCopies']=False
        with self.assertRaises(Rejected): self.purge(revision=2)
        with self.assertRaises(Rejected): self.purge(repository='other-repository')
        self.assertTrue((self.root/'fixture'/self.database/'backup-fixture01'/'database.dump.enc').exists())

    def test_unknown_file_prevents_any_deletion(self):
        unknown=self.root/'fixture'/self.database/'backup-fixture01'/'unknown'
        unknown.write_bytes(b'preserve')
        with self.assertRaises(Rejected): self.purge()
        self.assertEqual(unknown.read_bytes(),b'preserve')
        self.assertTrue(unknown.with_name('database.dump.enc').exists())

    def test_symlink_is_never_followed_or_deleted(self):
        source=self.root/'fixture'/self.database/'backup-fixture01'/'database.dump.enc'
        outside=self.root/'outside'
        outside.write_bytes(b'preserve')
        source.unlink()
        source.symlink_to(outside)
        with self.assertRaises((Rejected,OSError)): self.purge()
        self.assertEqual(outside.read_bytes(),b'preserve')
        self.assertTrue(source.is_symlink())

    def test_foreign_metadata_is_rejected_before_artifact_deletion(self):
        path=self.root/'fixture'/self.database/'backup-fixture01'/'.backup-meta.json'
        value=json.loads(path.read_text())
        value['databaseId']='database_foreign01'
        path.write_text(json.dumps(value))
        with self.assertRaises(Rejected): self.purge()
        self.assertTrue(path.with_name('database.dump.enc').exists())

    def test_retry_removes_empty_directory_after_interrupted_rmdir(self):
        original=os.rmdir
        def interrupted(path,*args,**kwargs):
            if path=='backup-fixture01':
                raise OSError('Injected interruption after metadata removal')
            return original(path,*args,**kwargs)
        with patch('server.os.rmdir',side_effect=interrupted):
            with self.assertRaises(OSError):
                self.purge()
        directory=self.root/'fixture'/self.database/'backup-fixture01'
        self.assertTrue(directory.is_dir())
        self.assertEqual(list(directory.iterdir()),[])
        self.assertEqual(self.purge()['purgedBackupIds'],['backup-fixture01'])
        self.assertFalse(directory.exists())

    def test_nonempty_directory_without_metadata_fails_closed(self):
        directory=self.root/'fixture'/self.database/'backup-fixture01'
        (directory/'.backup-meta.json').unlink()
        with self.assertRaises((Rejected,OSError)):
            self.purge()
        self.assertTrue((directory/'database.dump.enc').exists())
        self.assertTrue((directory/'manifest.json.enc').exists())
