import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest

spec=importlib.util.spec_from_file_location('repository_control',Path(__file__).with_name('repository-control.py'))
control=importlib.util.module_from_spec(spec)
spec.loader.exec_module(control)


class RepositoryControlTests(unittest.TestCase):
    def request(self):
        return dict(action='purge-source',container='repository-1',expectedRepositoryId='repository-fixture',binding=dict(environment='fixture',databaseId='database_fixture01',token='a'*64,retentionDays=30,minimumComplete=2,enabled=False,keepLastCopies=False,policyRevision=3))

    def test_purge_passes_exact_identity_only_over_private_stdin(self):
        calls=[]
        def run(args,**kwargs):
            calls.append((args,kwargs))
            return SimpleNamespace(returncode=0,stdout=json.dumps(dict(status='purged',purgedBackupIds=['backup-fixture01'],purgedCount=1,remainingCount=0)).encode())
        result=control.configure(self.request(),run)
        self.assertEqual(result['purgedCount'],1)
        args,kwargs=calls[0]
        self.assertNotIn('a'*64,' '.join(args))
        payload=json.loads(kwargs['input'])
        self.assertEqual(payload['action'],'purge-source')
        self.assertEqual(payload['expectedRepositoryId'],'repository-fixture')
        compile(control.PROGRAM,'repository-control-program','exec')

    def test_enabled_or_manual_retention_refuses_before_command(self):
        for field in ('enabled','keepLastCopies'):
            request=self.request()
            request['binding'][field]=True
            with self.assertRaises(ValueError):
                control.configure(request,lambda *a,**kw:self.fail('Purge command must not execute'))
