#!/usr/bin/env python3
"""Local filesystem and optional real Docker export checks for asset releases."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import zipfile

SPEC = importlib.util.spec_from_file_location('asset_release', Path(__file__).with_name('asset-release.py'))
ASSETS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ASSETS)


def packages(root):
    root.mkdir(parents=True, exist_ok=True)
    manifest = dict(protocolVersion=1, version='1.0.0+fixture', packages=[])
    for runtime in sorted(ASSETS.RUNTIMES):
        name = 'si-' + runtime + '.zip'
        path = root / name
        with zipfile.ZipFile(path, 'w') as archive:
            executable = zipfile.ZipInfo('si.exe' if runtime == 'win-x64' else 'si')
            executable.external_attr = 0o100755 << 16
            archive.writestr(executable, '#!/bin/sh\necho fixture\n')
        manifest['packages'].append(dict(runtimeIdentifier=runtime, fileName=name, sizeBytes=path.stat().st_size,
                                         sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    (root / 'manifest.json').write_text(json.dumps(manifest))
    return manifest


class AssetReleaseTests(unittest.TestCase):
    def test_agent_host_verifier_is_identical_and_valid_python(self):
        root = Path(__file__).resolve().parents[3]
        source = (root / 'modules/worker/Pipeline/Steps/RuntimeAgentVerification.cs').read_text()
        verifier = source.split('internal const string Script = """\n', 1)[1].split('\n""";', 1)[0]
        remote = Path(__file__).with_name('remote-deploy.sh').read_text().split("<<'PY_AGENT_VERIFY'\n", 1)[1].split('\nPY_AGENT_VERIFY', 1)[0]
        self.assertEqual(verifier, remote)
        compile(verifier, 'agent-verification', 'exec')

    def test_manifest_tampering_protocol_and_symlinks_fail_closed(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            manifest = packages(root)
            ASSETS.validate(root)
            pin = ASSETS.digest(root / 'manifest.json')
            manifest['protocolVersion'] = 2
            (root / 'manifest.json').write_text(json.dumps(manifest))
            with self.assertRaises(ValueError): ASSETS.validate(root, pin)
            with self.assertRaises(ValueError): ASSETS.validate(root)
            manifest = packages(root)
            package = root / manifest['packages'][0]['fileName']
            package.unlink()
            package.symlink_to('/etc/passwd')
            with self.assertRaises(ValueError): ASSETS.validate(root)

    def test_archive_traversal_is_rejected_even_with_correct_checksum(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            manifest = packages(root)
            package = manifest['packages'][0]
            path = root / package['fileName']
            with zipfile.ZipFile(path, 'a') as archive: archive.writestr('../escape', 'unsafe')
            package.update(sizeBytes=path.stat().st_size, sha256=ASSETS.digest(path))
            (root / 'manifest.json').write_text(json.dumps(manifest))
            with self.assertRaises(ValueError): ASSETS.validate(root)

    def test_manual_publication_switches_existing_mount_without_restart(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            app = root / 'app'
            lease = root / 'worker.lease'
            lease.write_text('owner')
            asset = dict(name='cli', release_id='asset_first', input_sha256='a' * 64)
            payload = dict(worker_lease_file=str(lease), ownership_token='owner', asset_publication=asset)
            for name, version in [('asset_first', '1.0'), ('asset_second', '2.0')]:
                directory = ASSETS.location(app, 'cli', name)
                manifest = packages(directory)
                manifest['version'] = version
                (directory / 'manifest.json').write_text(json.dumps(manifest))
                (directory / '.input-sha256').write_text('a' * 64)
            # Existing private publication roots must be traversable in consumer mounts.
            (app / 'assets/cli').chmod(0o700)
            ASSETS.publish(app, payload)
            self.assertEqual(0o755, (app / 'assets/cli').stat().st_mode & 0o777)
            current = app / 'assets/cli/current'
            self.assertEqual('1.0', json.loads((current / 'manifest.json').read_text())['version'])
            asset['release_id'] = 'asset_second'
            lease.touch()
            ASSETS.publish(app, payload)
            self.assertEqual('2.0', json.loads((current / 'manifest.json').read_text())['version'])
            ASSETS.verify_mounts(app, dict(services=[dict(asset_mounts=[dict(name='cli', release_id=None,
                manifest_sha256=ASSETS.digest(current / 'manifest.json'), protocol_version=1)])]))
            # A corrupt package or lost lease cannot change the current release.
            asset['release_id'] = 'asset_first'
            (ASSETS.location(app, 'cli', 'asset_first') / 'si-linux-x64.zip').write_bytes(b'bad')
            with self.assertRaises(ValueError): ASSETS.publish(app, payload)
            self.assertEqual('asset_second', os.readlink(current))
            asset['release_id'] = 'asset_second'
            payload['ownership_token'] = 'wrong-owner'
            with self.assertRaises(ValueError): ASSETS.publish(app, payload)
            self.assertEqual('asset_second', os.readlink(current))
            # Traversal in the current pointer is rejected before validation.
            current.unlink()
            current.symlink_to('../outside')
            with self.assertRaises(ValueError): ASSETS.verify_mounts(app, dict(services=[dict(asset_mounts=[
                dict(name='cli', release_id=None, manifest_sha256='', protocol_version=1)])]))

    @unittest.skipUnless(os.environ.get('MINICLOUD_TEST_DOCKER') == '1', 'Set MINICLOUD_TEST_DOCKER=1 for real Docker mount')
    def test_running_readonly_consumer_observes_publication_without_restart(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            app = root / 'app'
            lease = root / 'worker.lease'
            lease.write_text('owner')
            asset = dict(name='cli', release_id='asset_first', input_sha256='a' * 64)
            payload = dict(worker_lease_file=str(lease), ownership_token='owner', asset_publication=asset)
            for name, version in [('asset_first', '1.0'), ('asset_second', '2.0')]:
                directory = ASSETS.location(app, 'cli', name)
                manifest = packages(directory)
                manifest['version'] = version
                (directory / 'manifest.json').write_text(json.dumps(manifest))
                (directory / '.input-sha256').write_text('a' * 64)
            (app / 'assets/cli').chmod(0o700)
            ASSETS.publish(app, payload)
            container = subprocess.check_output(['docker', 'run', '-d', '--user', '1654:1654', '--network', 'none', '--mount',
                'type=bind,source=' + str(app / 'assets/cli') + ',target=/published,readonly',
                '--entrypoint', '/bin/sh', 'mcr.microsoft.com/dotnet/runtime:10.0', '-c', 'sleep 120'], text=True).strip()
            try:
                def catalog():
                    return json.loads(subprocess.check_output(['docker', 'exec', container, 'cat', '/published/current/manifest.json'], text=True))
                started = subprocess.check_output(['docker', 'inspect', '-f', '{{.State.StartedAt}}', container], text=True)
                self.assertEqual('1.0', catalog()['version'])
                lease.touch()
                asset['release_id'] = 'asset_second'
                ASSETS.publish(app, payload)
                # Docker Desktop host sharing may delay symlink invalidation.
                deadline = time.monotonic() + 5
                while catalog()['version'] != '2.0' and time.monotonic() < deadline:
                    time.sleep(0.05)
                self.assertEqual('2.0', catalog()['version'])
                self.assertEqual(started, subprocess.check_output(['docker', 'inspect', '-f', '{{.State.StartedAt}}', container], text=True))
                self.assertNotEqual(0, subprocess.run(['docker', 'exec', container, 'touch', '/published/forbidden'], capture_output=True).returncode)
            finally:
                subprocess.run(['docker', 'rm', '-f', container], check=True, stdout=subprocess.DEVNULL)

    @unittest.skipUnless(os.environ.get('MINICLOUD_TEST_DOCKER') == '1', 'Set MINICLOUD_TEST_DOCKER=1 for real Docker export')
    def test_stopped_image_exports_atomically_without_running_cmd(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            packages(root / 'cli-releases')
            (root / 'Dockerfile').write_text("""FROM scratch
COPY cli-releases /cli-releases
LABEL dev.minicloud.asset-mounts='{"cli":"/app/cli-releases"}'
CMD ["/must-never-execute"]
""")
            image = 'minicloud-asset-export:test-' + str(os.getpid())
            subprocess.run(['docker', 'build', '-t', image, str(root)], check=True, stdout=subprocess.DEVNULL)
            try:
                source = Path(__file__).with_name('remote-deploy.sh').read_text()
                function = 'verify_image_asset_requirements() {' + source.split('verify_image_asset_requirements() {', 1)[1].split('\nverify_runtime_agent_plan()', 1)[0]
                def requirement(mounts):
                    service = dict(image=image, asset_mounts=mounts)
                    script = function + '\nSERVICES_JSON=' + "'" + json.dumps([service]) + "'" + '\nverify_image_asset_requirements\n'
                    return subprocess.run(['bash', '-c', script], stdout=subprocess.PIPE, stderr=subprocess.PIPE).returncode
                self.assertNotEqual(0, requirement([]))
                self.assertEqual(0, requirement([dict(name='cli', mount_path='/app/cli-releases', protocol_version=1)]))
                self.assertNotEqual(0, requirement([dict(name='cli', mount_path='/wrong', protocol_version=1)]))
                lease = root / 'worker.lease'
                lease.write_text('fixture-owner')
                payload = dict(worker_lease_file=str(lease), ownership_token='fixture-owner', asset_publication=dict(
                    name='cli', release_id='asset_fixture', input_sha256='a' * 64, image=image, output_path='/cli-releases'))
                ASSETS.publish(root / 'app', payload)
                output = root / 'app/assets/cli/asset_fixture'
                self.assertEqual(5, len(ASSETS.validate(output)['packages']))
                self.assertEqual(0o555, output.stat().st_mode & 0o777)
                self.assertTrue(all(p.stat().st_mode & 0o222 == 0 for p in output.iterdir()))
                lease.touch()
                ASSETS.publish(root / 'app', payload)
                self.assertEqual([], list(output.parent.glob('.publishing-*')))
                payload['asset_publication']['input_sha256'] = 'b' * 64
                with self.assertRaises(ValueError): ASSETS.publish(root / 'app', payload)
            finally:
                subprocess.run(['docker', 'image', 'rm', image], check=True, stdout=subprocess.DEVNULL)
                # Restore directory write permission for temporary fixture cleanup.
                for directory in root.rglob('*'):
                    if directory.is_dir(): directory.chmod(0o755)


if __name__ == '__main__': unittest.main()
