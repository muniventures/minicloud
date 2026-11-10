import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

class LeaseWatchdogTests(unittest.TestCase):
    def check(self, token, lease_token, modified=None):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            payload=root/'payload.json'
            payload.write_text(json.dumps({'ownership_token':token}))
            lease=root/'worker.lease'
            lease.write_text(lease_token)
            if modified is not None:
                os.utime(lease,(modified,modified))
            script=Path(__file__).with_name('lease-watchdog.py').resolve()
            launcher='import os,sys; os.execv(sys.executable,[sys.executable,sys.argv[1],sys.argv[2],str(os.getpid()),"--once"])'
            return subprocess.run([sys.executable,'-c',launcher,str(script),str(payload)],start_new_session=True,capture_output=True,timeout=5).returncode
    def test_live_matching_lease_allows_execution(self):
        self.assertEqual(0,self.check('owned-token','owned-token'))
    def test_changed_ownership_terminates_owned_operation_group(self):
        self.assertEqual(-15,self.check('owned-token','replacement-token'))
    def test_expired_heartbeat_terminates_owned_operation_group(self):
        self.assertEqual(-15,self.check('owned-token','owned-token',time.time()-10))
    def test_missing_ownership_cannot_run(self):
        self.assertNotEqual(0,self.check(None,''))
