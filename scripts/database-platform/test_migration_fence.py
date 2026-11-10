import importlib.util
from pathlib import Path
import subprocess
import unittest
spec = importlib.util.spec_from_file_location('fence', Path(__file__).with_name('migration-fence.py'))
fence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fence)

class FenceTests(unittest.TestCase):
    def test_fences_target_before_terminating_sessions_without_touching_neighbors(self):
        calls = []
        def run(args, **kwargs):
            calls.append((args, kwargs))
            output = b'/var/lib/postgresql/data/pg_hba.conf\n' if args[-1] == 'SHOW hba_file' else b'0\n'
            return subprocess.CompletedProcess(args, 0, output, b'')
        result = fence.execute({'container':'minicloud-source-db-1','databaseName':'orders','operationId':'dbop_test','action':'fence'}, run)
        self.assertEqual('fenced', result['status'])
        writes = [kwargs['input'] for args, kwargs in calls if 'input' in kwargs]
        self.assertEqual([b'host orders all 0.0.0.0/0 reject\nhost orders all ::/0 reject\n'], writes)
        self.assertIn("datname='orders'", calls[-1][0][-1])
        self.assertIn('pg_reload_conf()', calls[-2][0][-1])

    def test_invalid_binding_never_runs_a_command(self):
        def forbidden(*args, **kwargs):
            raise AssertionError('No command should run')
        with self.assertRaises(ValueError):
            fence.execute({'container':'bad;command','databaseName':'orders','operationId':'dbop_test','action':'fence'}, forbidden)

if __name__ == '__main__':
    unittest.main()
