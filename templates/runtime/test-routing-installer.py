import importlib.util
import fcntl
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('routing', Path(__file__).with_name('apply-minicloud-routing.py'))
routing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(routing)


class RoutingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for directory in ['locks', 'apps/demo', 'caddy/apps']:
            (self.root / 'opt/minicloud' / directory).mkdir(parents=True)
        self.compose = self.root / 'opt/minicloud/apps/demo/app.compose.json'
        self.compose.write_text(json.dumps({'services': {'api': {'ports': ['127.0.0.1:18080:8080']}, 'worker': {}}}))
        self.live = self.root / 'opt/minicloud/caddy/apps/demo.caddy'
        self.live.write_text('previous routes')
        self.config = {'appId': 'app_1', 'composeProject': 'minicloud-demo'}
        self.route = {'hostname': 'demo.app.muni.dev', 'pathPrefix': '/', 'serviceName': 'api', 'servicePort': 8080}
        self.request = dict(self.config, routes=[self.route])
        self.calls = []

    def run_command(self, args, **kwargs):
        self.calls.append(args)
        if args[0] == 'caddy':
            self.assertEqual('previous routes', self.live.read_text())
            candidate = Path(args[-1]).parent / self.live.name
            self.assertIn('127.0.0.1:18080', candidate.read_text())
        return subprocess.CompletedProcess(args, 0)

    def test_validates_before_activation_and_skips_unchanged_reload(self):
        routing.apply(self.config, self.request, self.root, self.run_command)
        self.assertIn('127.0.0.1:18080', self.live.read_text())
        self.assertEqual(['caddy', 'systemctl'], [call[0] for call in self.calls])
        self.calls.clear()
        routing.apply(self.config, self.request, self.root, self.run_command)
        self.assertEqual([['systemctl', 'is-active', '--quiet', 'caddy']], self.calls)

    def test_invalid_or_private_target_preserves_live_routes(self):
        for change in [{'serviceName': 'worker'}, {'servicePort': 9999}, {'hostname': 'x\\n} evil'}, {'pathPrefix': '/other'}]:
            with self.subTest(change=change):
                request = dict(self.request, routes=[dict(self.route, **change)])
                with self.assertRaises(ValueError):
                    routing.apply(self.config, request, self.root, self.run_command)
                self.assertEqual('previous routes', self.live.read_text())
                self.assertEqual([], self.calls)

    def test_custom_apex_and_subdomain(self):
        request = dict(self.request, routes=[dict(self.route, hostname=host) for host in ['example.com', 'api.example.co.uk']])
        routing.apply(self.config, request, self.root, self.run_command)
        self.assertIn('example.com {', self.live.read_text())
        self.assertIn('api.example.co.uk {', self.live.read_text())

    def test_invalid_custom_hostnames_preserve_routes(self):
        for host in ['*.example.com', 'example.com:443', 'https://example.com', 'example.com/path', '127.0.0.1', 'example.com\\n{', '-bad.example.com', 'bad..example.com']:
            with self.subTest(host=host), self.assertRaises(ValueError):
                routing.apply(self.config, dict(self.request, routes=[dict(self.route, hostname=host)]), self.root, self.run_command)
            self.assertEqual('previous routes', self.live.read_text())

    def test_missing_compose_preserves_routes(self):
        self.compose.unlink()
        with self.assertRaises(FileNotFoundError):
            routing.apply(self.config, self.request, self.root, self.run_command)
        self.assertEqual('previous routes', self.live.read_text())

    def test_validate_and_reload_failures_restore_previous_file(self):
        for fail in ['caddy', 'systemctl']:
            with self.subTest(fail=fail):
                def run(args, **kwargs):
                    return subprocess.CompletedProcess(args, 1 if args[0] == fail else 0)
                with self.assertRaises(ValueError):
                    routing.apply(self.config, self.request, self.root, run)
                self.assertEqual('previous routes', self.live.read_text())

    def test_empty_routes_removes_public_access(self):
        routing.apply(self.config, dict(self.request, routes=[]), self.root, lambda args, **kw: subprocess.CompletedProcess(args, 0))
        self.assertEqual('', self.live.read_text())

    def test_deployment_lock_prevents_route_replacement(self):
        with open(self.root / 'opt/minicloud/locks/demo.deploy.lock', 'a') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with patch.object(routing.time, 'monotonic', side_effect=[0, 21]):
                with self.assertRaisesRegex(ValueError, 'Deployment in progress'):
                    routing.apply(self.config, self.request, self.root, self.run_command)
        self.assertEqual('previous routes', self.live.read_text())
        self.assertEqual([], self.calls)

    def test_cross_app_request_is_rejected(self):
        with self.assertRaises(ValueError):
            routing.apply(self.config, dict(self.request, appId='other'), self.root, self.run_command)
        self.assertEqual('previous routes', self.live.read_text())


if __name__ == '__main__':
    unittest.main()
