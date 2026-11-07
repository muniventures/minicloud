#!/usr/bin/env python3
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('maintenance', Path(__file__).with_name('runtime-maintenance.py'))
maintenance = importlib.util.module_from_spec(spec)
spec.loader.exec_module(maintenance)


class MaintenanceTests(unittest.TestCase):
    def test_deletes_builds_and_stale_uploads_but_keeps_live_data_and_recent_uploads(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            apps, temp = root / 'apps', root / 'tmp'
            builds = apps / 'club' / 'builds'
            builds.mkdir(parents=True)
            temp.mkdir()
            old, current, mounted = [builds / n for n in ('dep_old', 'dep_current', 'dep_mounted')]
            for directory in (old, current, mounted):
                directory.mkdir()
                (directory / 'input').write_text('source')
            data = apps / 'club' / 'data'
            data.mkdir()
            (data / 'database').write_text('persistent')
            (builds / 'dep_link').symlink_to(data, target_is_directory=True)
            stale, uploading, leased = [temp / n for n in ('minicloud-deploy-old', 'minicloud-deploy-uploading', 'minicloud-deploy-leased')]
            for directory in (stale, uploading, leased):
                directory.mkdir()
                os.utime(directory, (1, 1))
            (uploading / 'input.zip').write_text('in flight')
            (leased / 'worker.lease').write_text('active')
            removed = maintenance.collect_directories(apps, temp, [current / 'input', mounted], time.time())
            self.assertEqual({str(old), str(stale)}, set(removed))
            self.assertTrue((data / 'database').exists())
            self.assertTrue(uploading.exists())
            self.assertTrue(leased.exists())
            self.assertTrue(current.exists())
            self.assertTrue(mounted.exists())
            self.assertTrue((builds / 'dep_link').is_symlink())

    def test_images_referenced_by_containers_and_compose_recovery_survive(self):
        with tempfile.TemporaryDirectory() as work:
            apps = Path(work)
            app = apps / 'club'
            app.mkdir()
            (app / 'app.compose.json').write_text(json.dumps({'services': {'api': {'image': 'app:recovery'}}}))
            calls = []
            def run(*args):
                calls.append(args)
                if args == ('docker', 'image', 'inspect', 'app:recovery'):
                    return '[{"Id":"sha256:recovery"}]'
                if args == ('docker', 'image', 'ls', '--quiet', '--no-trunc'):
                    return 'sha256:running\nsha256:recovery\nsha256:old\nsha256:dangling\n'
                if args == ('docker', 'image', 'inspect', 'sha256:old'):
                    return '[{"Id":"sha256:old","RepoTags":["app:old"],"RepoDigests":[]}]'
                if args == ('docker', 'image', 'inspect', 'sha256:dangling'):
                    return '[{"Id":"sha256:dangling","RepoTags":null,"RepoDigests":null}]'
                if args in [('docker', 'image', 'rm', 'app:old'), ('docker', 'image', 'rm', 'sha256:dangling')]:
                    return ''
                raise AssertionError(args)
            self.assertEqual({'app:old', 'sha256:dangling'}, set(maintenance.collect_images(apps, {'sha256:running'}, run)))
            self.assertNotIn('--force', [arg for call in calls for arg in call])

    def test_inspection_failure_prevents_deletion(self):
        def fail(*args):
            raise RuntimeError('Docker unavailable')
        with patch.object(maintenance, 'collect_directories') as collect:
            with self.assertRaises(RuntimeError):
                maintenance.maintain(run=fail)
            collect.assert_not_called()

    def test_proc_root_cwd_does_not_protect_all_builds(self):
        with tempfile.TemporaryDirectory() as work:
            proc = Path(work)
            process = proc / '123'
            process.mkdir()
            (process / 'cwd').symlink_to('/')
            (process / 'cmdline').write_bytes(b'python3\0/tmp/minicloud-deploy-active/remote-deploy.sh\0')
            (process / 'fd').mkdir()
            paths = maintenance.process_paths(proc)
            self.assertNotIn(Path('/'), paths)
            self.assertIn(Path('/tmp/minicloud-deploy-active/remote-deploy.sh'), paths)

    def test_journal_and_cache_maintenance_are_bounded_without_volume_prune(self):
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            apps, temp, proc = root / 'apps', root / 'tmp', root / 'proc'
            for directory in (apps, temp, proc):
                directory.mkdir()
            calls = []
            def run(*args):
                calls.append(args)
                return ''
            maintenance.maintain(apps, temp, run=run, proc=proc)
            self.assertIn(('journalctl', '--rotate', '--vacuum-time=2days'), calls)
            self.assertIn(('docker', 'builder', 'prune', '--all', '--force', '--filter', 'until=48h'), calls)
            self.assertFalse(any('volume' in call or 'system' in call or 'container' in call for call in calls))

    def test_installation_renders_hourly_timer_and_two_day_journald_policy(self):
        import subprocess
        with tempfile.TemporaryDirectory() as work:
            root = Path(work)
            bundle = root / 'bundle'
            bundle.mkdir()
            (bundle / 'runtime-maintenance.py').write_text('import sys; print("maintenance fixture", sys.argv[1:])')
            source = Path(__file__).with_name('remote-deploy.sh').read_text()
            function = source[source.index('install_runtime_maintenance() {'):source.index('prepare_paths() {')]
            function = function.replace('/opt/minicloud', str(root / 'host')).replace('/etc/systemd', str(root / 'systemd')).replace('-o root -g root ', '')
            calls = root / 'calls'
            (root / 'systemd/system').mkdir(parents=True)
            shell = 'sudo() { if [ "$1" = systemctl ]; then echo "$*" >> "$CALLS"; else "$@"; fi; }\n'
            result = subprocess.run(['bash', '-euo', 'pipefail', '-c', shell + function + '\ninstall_runtime_maintenance'],
                env={**os.environ, 'PAYLOAD_FILE': str(bundle / 'payload.json'), 'APP_DIR': str(root / 'app'), 'DEPLOYMENT_ID': 'dep_current', 'CALLS': str(calls)},
                capture_output=True, text=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('OnUnitActiveSec=1h', (root / 'systemd/system/minicloud-maintenance.timer').read_text())
            self.assertIn('MaxRetentionSec=2day', (root / 'systemd/journald.conf.d/minicloud-retention.conf').read_text())
            self.assertIn('--preserve', result.stdout)
            self.assertNotIn('restart docker', calls.read_text())
            self.assertIn('enable --now minicloud-maintenance.timer', calls.read_text())

    def test_active_deployment_lock_skips_timer_without_work(self):
        import fcntl
        with tempfile.TemporaryDirectory() as work:
            lock = Path(work) / 'docker.deploy.lock'
            with lock.open('w') as held:
                fcntl.flock(held, fcntl.LOCK_EX)
                with patch.object(maintenance, 'LOCK', lock), patch('sys.argv', ['maintenance']), patch.object(maintenance, 'maintain') as maintain:
                    maintenance.main()
                    maintain.assert_not_called()


if __name__ == '__main__':
    unittest.main()
