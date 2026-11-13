import argparse
import errno
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest import mock
import urllib.error


ROOT = Path(__file__).resolve().parents[2]
BUNDLE = ROOT / "packaging/server/bundle"
ARTIFACTS = ROOT / ".local/artifacts/t43/tests"
SPEC = importlib.util.spec_from_file_location("server_install", BUNDLE / "server_install.py")
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class FixtureHost:
    def __init__(self):
        self.dns_ready = True
        self.conflict = False

    def tools(self):
        return {"DockerRootDir": str(ARTIFACTS)}

    def resources(self, *args):
        pass

    def conflicts(self, state):
        installer.require(not self.conflict, "Existing populated volume.")
        return set()

    def ports(self, owned):
        pass

    def dns(self, config, addresses):
        return self.dns_ready


class FixtureInstaller(installer.Installer):
    """Simulated command adapter: never starts, pulls or mutates a real container."""

    def __init__(self, args, host):
        super().__init__(args, host)
        self.events = []
        self.fail_migration = False
        self.bootstrap_state = "required"

    def pull_verified_images(self):
        self.events.append("verify-and-pull")

    def openbao(self):
        self.events.append("openbao")

    def compose(self, *arguments, **kwargs):
        self.events.append(arguments)
        if "api-migrate" in arguments and self.fail_migration:
            raise installer.InstallError("Synthetic incomplete migration.")
        if "api-bootstrap" in arguments and "maintenance" in arguments:
            operation = arguments[arguments.index("maintenance") + 1]
            self.maintenance_input = json.loads(kwargs["input"])
            return json.dumps({"operation": operation, "result": "ok", "authority": {
                "state": "open", "epoch": 1, "localHostAuthorized": True}}).encode()
        if "api-bootstrap" in arguments:
            operation = arguments[arguments.index("bootstrap") + 1]
            if operation in ("prepare", "rotate-token"):
                name = arguments[-1].split("/")[-1]
                output = self.root / "secrets/bootstrap/output" / name
                if operation == "rotate-token" or not output.exists():
                    installer.exclusive(output, b"synthetic-token-never-logged")
            return json.dumps({"state": self.bootstrap_state}).encode()
        return b""

    def readiness(self):
        return {"state": "ready" if self.bootstrap_state == "ready" else "awaiting_operator"}


class InstallerTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        ignored = subprocess.run(["git", "check-ignore", str(ARTIFACTS)], cwd=ROOT, capture_output=True)
        self.assertEqual(0, ignored.returncode, "Generated fixtures must be ignored.")
        self.temp = tempfile.TemporaryDirectory(prefix="installer-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        os.chmod(self.base, 0o700)
        # The shared workspace itself is group-writable. Simulate a dedicated host's
        # protected ancestor boundary without changing shared filesystem permissions.
        patcher = mock.patch.object(installer, "path_chain", side_effect=self.fixture_chain)
        patcher.start()
        self.addCleanup(patcher.stop)
        original_stat = Path.stat

        def fixture_stat(path, *args, **kwargs):
            info = original_stat(path, *args, **kwargs)
            if path.is_relative_to(self.base):
                fields = list(info)
                fields[4] = 0
                return os.stat_result(fields)
            return info

        owner = mock.patch.object(Path, "stat", fixture_stat)
        owner.start()
        self.addCleanup(owner.stop)
        self.assets = self.base / "assets"
        self.recovery = self.base / "recovery"
        self.assets.mkdir(mode=0o700)
        self.recovery.mkdir(mode=0o700)
        self.config = self.base / "input.json"
        self.write(self.config, json.dumps({
            "apiUrl": "https://api.cloud.example.com", "consoleUrl": "https://cloud.example.com",
            "apiHostname": "api.cloud.example.com", "consoleHostname": "cloud.example.com",
            "appDomain": "apps.example.com",
        }).encode())
        self.payload = {
            "images.env": b"synthetic\n", "compose.yml": b"services: {}\n",
            "server_install.py": b"class InstallError(Exception): pass\ndef main(args): return 0\n",
        }
        archive = self.assets / "minicloud-server-0.1.0.tar.gz"
        with tarfile.open(archive, "w:gz") as target:
            for name, data in self.payload.items():
                member = tarfile.TarInfo(name)
                member.size = len(data)
                target.addfile(member, io.BytesIO(data))
        os.chmod(archive, 0o600)
        self.manifest = {
            "releaseTag": "server-v0.1.0", "serverVersion": "0.1.0", "sourceRevision": "a" * 40,
            "images": {"api": "example.test/api@sha256:" + "a" * 64},
            "dependencies": {"postgres": "example.test/postgres@sha256:" + "b" * 64},
            "configurationSchemaVersion": 1, "identitySchemaVersion": 1, "applicationMigration": "synthetic",
            "bundle": {"name": archive.name, "sha256": hashlib.sha256(archive.read_bytes()).hexdigest()},
            "files": {name: hashlib.sha256(data).hexdigest() for name, data in self.payload.items()},
        }
        self.write(self.assets / "server-manifest.json", json.dumps(self.manifest).encode())
        self.args = argparse.Namespace(
            tag="server-v0.1.0", assets_dir=self.assets, root=self.base / "installation",
            config=self.config, recovery_dir=self.recovery, public_address=["8.8.8.8"],
            command="install", token_output_name=None,
        )
        self.host = FixtureHost()

    @staticmethod
    def write(path, data):
        installer.exclusive(path, data)

    def fixture_chain(self, path):
        return tuple(parent for parent in (path, *path.parents) if parent.is_relative_to(self.base))

    def subject(self):
        return FixtureInstaller(self.args, self.host)

    def test_explicit_partial_source_refresh_preserves_identity_and_credential_bytes(self):
        spec = importlib.util.spec_from_file_location("partial_local_source", ROOT / "scripts/self-hosting/local_source.py")
        local = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(local)
        self.manifest.update(artifactTrust=local.TRUST, sourceArchiveSha256="a" * 64)
        (self.assets / "server-manifest.json").write_text(json.dumps(self.manifest))
        self.args.command = "prepare"
        subject = self.subject()
        subject.execute()
        original_state = dict(subject.state)
        credentials = {path: path.read_bytes() for path in (self.args.root / "secrets").rglob("*") if path.is_file()}
        selected = dict(self.manifest, sourceRevision="b" * 40, sourceArchiveSha256="b" * 64)
        # The fixture bundle need not change to test the controlled authority transition.
        local.refresh_partial(subject, selected, "a" * 64, installer)
        self.assertEqual(original_state["installationId"], subject.state["installationId"])
        self.assertEqual(original_state["operationId"], subject.state["operationId"])
        self.assertEqual(credentials, {path: path.read_bytes() for path in credentials})
        self.assertTrue((self.args.root / ("bundle.previous-" + "a" * 64)).is_dir())
        self.assertFalse((self.args.root / "local-source-refresh.pending").exists())
        self.assertEqual("b" * 64, subject.state["sourceArchiveSha256"])
        subject.state["baoInitStarted"] = True
        with self.assertRaisesRegex(ValueError, "before OpenBao"):
            local.refresh_partial(subject, dict(selected, sourceRevision="c" * 40), "b" * 64, installer)

    def test_explicit_deferred_dns_initializes_but_does_not_claim_https_ready(self):
        self.host.dns_ready = False
        subject = self.subject()
        self.assertEqual("awaiting_dns_tls", subject.execute()["state"])
        self.assertEqual([], subject.events)
        self.assertFalse(self.args.root.exists())
        self.args.defer_dns = True
        subject = self.subject()
        with mock.patch.object(subject, "readiness", return_value={"state": "awaiting_dns_tls"}):
            self.assertEqual("awaiting_dns_tls", subject.execute()["state"])
        self.assertTrue(subject.state["credentialsComplete"])
        self.assertIn("verify-and-pull", subject.events)
        self.assertIn(("up", "-d", "caddy"), subject.events)
        self.assertEqual("awaiting_dns_tls", subject.state["phase"])

    def test_pending_or_unknown_maintenance_blocks_install_before_any_host_operation(self):
        self.args.root.mkdir(mode=0o700)
        for record in ({"state": "started"}, {"state": "upgraded", "pendingUpgrade": {}},
                       {"state": "upgraded", "pendingRestore": {}}, {"state": "upgraded", "restoreInProgress": {}},
                       {"state": "upgraded", "heldFence": {}}, {"state": "future"},
                       {"state": "upgraded", "lastFailure": {"fenceReleased": False}}):
            with self.subTest(record=record):
                installer.atomic_json(self.args.root / "maintenance.json", record)
                subject = self.subject()
                with mock.patch.object(subject, "preflight", side_effect=AssertionError("No host action")):
                    with self.assertRaises(installer.InstallError):
                        subject.execute()
                self.assertEqual([], subject.events)
                self.assertEqual(record, installer.read_json(self.args.root / "maintenance.json"))

    def test_interrupted_maintenance_write_blocks_install_and_completed_maintenance_allows_preflight(self):
        self.args.root.mkdir(mode=0o700)
        pending = self.args.root / "maintenance.json.pending"
        self.write(pending, b"{")
        with self.assertRaises(installer.InstallError):
            self.subject().execute()
        self.assertEqual(b"{", pending.read_bytes())
        pending.unlink()
        installer.atomic_json(self.args.root / "maintenance.json", {"state": "upgraded", "lastSuccessfulUpgrade": {}})
        subject = self.subject()
        self.args.command = "preflight"
        with mock.patch.object(subject, "preflight", return_value={"state": "preflight_passed"}) as preflight:
            self.assertEqual({"state": "preflight_passed"}, subject.execute())
            preflight.assert_called_once()

    def test_maintenance_dispatch_requires_and_verifies_signed_helper_before_execution(self):
        helper = BUNDLE / "server_maintenance.py"
        self.payload[helper.name] = helper.read_bytes()
        archive = self.assets / self.manifest["bundle"]["name"]
        with tarfile.open(archive, "w:gz") as target:
            for name, data in self.payload.items():
                member = tarfile.TarInfo(name)
                member.size = len(data)
                target.addfile(member, io.BytesIO(data))
        self.manifest["files"][helper.name] = hashlib.sha256(helper.read_bytes()).hexdigest()
        self.manifest["bundle"]["sha256"] = hashlib.sha256(archive.read_bytes()).hexdigest()
        (self.assets / "server-manifest.json").write_text(json.dumps(self.manifest))
        self.subject().execute()
        self.args.command = "maintenance"
        self.args.maintenance_operation = "status"
        subject = self.subject()
        with mock.patch.object(self.host, "tools", side_effect=AssertionError("No Docker call")):
            result = subject.execute()
        self.assertEqual(("maintenance_idle", "open"), (result["state"], result["authority"]["state"]))
        self.assertEqual([("run", "--rm", "-T", "--no-deps", "api-bootstrap", "maintenance", "status", "--stdin")],
                         subject.events)
        self.assertEqual({"installationId": subject.state["installationId"]}, subject.maintenance_input)
        installed_helper = self.args.root / "bundle/server_maintenance.py"
        installed_helper.write_text("raise AssertionError('untrusted helper executed')")
        with self.assertRaisesRegex(installer.InstallError, "differs from the signed release"):
            self.subject().execute()
        # Upgrade/restore run the selected release's signed helper, never the installed copy.
        self.args.maintenance_operation = "upgrade"
        with self.assertRaisesRegex(installer.InstallError, "already installed"):
            self.subject().execute()
        self.assertFalse(any(path.name == "__pycache__"
                             for path in (self.args.root / "bundle").rglob("*")))

    def test_old_release_without_signed_maintenance_helper_is_not_silently_adopted(self):
        self.subject().execute()
        self.args.command = "maintenance"
        self.args.maintenance_operation = "status"
        with self.assertRaisesRegex(installer.InstallError, "signed maintenance tooling"):
            self.subject().execute()

    def test_maintenance_main_refuses_non_root_before_lock_or_commands(self):
        with mock.patch.object(installer.os, "geteuid", return_value=1000):
            with self.assertRaisesRegex(installer.InstallError, "Run as root"):
                installer.main([
                    "maintenance", "status", "--tag", self.args.tag,
                    "--assets-dir", str(self.assets), "--root", str(self.args.root),
                    "--config", str(self.config), "--recovery-dir", str(self.recovery),
                    "--public-address", "192.0.2.1",
                ])
        self.assertFalse(self.args.root.exists())
        self.assertFalse(self.args.root.with_name(self.args.root.name + ".installation.lock").exists())

    def test_preflight_refuses_foreign_state_and_conflicts_before_creation_or_download(self):
        subject = self.subject()
        self.host.conflict = True
        with self.assertRaisesRegex(installer.InstallError, "populated volume"):
            subject.execute()
        self.assertFalse(self.args.root.exists())
        self.assertEqual([], subject.events)
        self.host.conflict = False
        self.args.root.mkdir(mode=0o700)
        self.write(self.args.root / "foreign-data", b"do not adopt")
        with self.assertRaisesRegex(installer.InstallError, "Unknown installation"):
            self.subject().execute()
        self.assertEqual(b"do not adopt", (self.args.root / "foreign-data").read_bytes())

    def test_dns_failure_preserves_fresh_state_and_does_not_pull_images(self):
        self.host.dns_ready = False
        subject = self.subject()
        self.assertEqual("awaiting_dns_tls", subject.execute()["state"])
        self.assertFalse(self.args.root.exists())
        self.assertEqual([], subject.events)

    def test_unknown_metadata_is_not_adopted_or_rewritten_by_failure_reporting(self):
        self.args.root.mkdir(mode=0o700)
        path = self.args.root / "installation.json"
        original = b'{"schemaVersion":999,"product":"foreign","preserve":"original"}\n'
        self.write(path, original)
        subject = self.subject()
        argv = ["install", "--tag", self.args.tag, "--assets-dir", str(self.assets),
                "--root", str(self.args.root), "--config", str(self.config),
                "--recovery-dir", str(self.recovery), "--public-address", "8.8.8.8"]
        with mock.patch.object(installer, "Installer", return_value=subject), \
             mock.patch.object(os, "geteuid", return_value=0):
            with self.assertRaisesRegex(installer.InstallError, "Unknown installation metadata"):
                installer.main(argv)
        self.assertEqual(original, path.read_bytes())
        self.assertIsNone(subject.state)

    def test_configuration_protects_exact_origins_permissions_and_distinct_paths(self):
        value = json.loads(self.config.read_text())
        value["apiUrl"] += "/"
        self.config.write_text(json.dumps(value))
        with self.assertRaisesRegex(installer.InstallError, "exact HTTPS"):
            self.subject()
        value["apiUrl"] = "https://api.cloud.example.com"
        self.config.write_text(json.dumps(value))
        os.chmod(self.config, 0o644)
        with self.assertRaisesRegex(installer.InstallError, "0600/0400"):
            self.subject()
        os.chmod(self.config, 0o600)
        self.args.recovery_dir = self.assets
        with self.assertRaisesRegex(installer.InstallError, "must not overlap"):
            self.subject()

    def test_symlink_input_and_duplicate_configuration_fail_closed(self):
        link = self.base / "link.json"
        link.symlink_to(self.config)
        self.args.config = link
        with self.assertRaisesRegex(installer.InstallError, "Symlink"):
            self.subject()
        self.args.config = self.config
        self.config.write_text('{"apiUrl":"one","apiUrl":"two"}')
        with self.assertRaisesRegex(installer.InstallError, "Duplicate"):
            self.subject()

    def test_capacity_guard_checks_installation_and_engine_filesystems(self):
        host = installer.Host()
        manifest = {"bundleBytes": 1024}
        with mock.patch.object(installer.Path, "read_text", return_value="MemTotal: 4194304 kB\n"), \
             mock.patch.object(installer.os, "cpu_count", return_value=2), \
             mock.patch.object(installer.shutil, "disk_usage", return_value=argparse.Namespace(free=3 * 1024**3)):
            with self.assertRaisesRegex(installer.InstallError, "10 GiB"):
                host.resources(self.args.root, self.base, manifest)

    def test_existing_volume_requires_original_installation_label_not_only_compose_project(self):
        state = {"installationId": "original-installation", "bundlePath": str(self.base / "bundle")}
        outputs = [
            b"minicloud_postgres\n", b"", b"",
            json.dumps([{"Labels": {"com.docker.compose.project": "minicloud",
                                    "com.minicloud.installation-id": "foreign-installation"}}]).encode(),
        ]
        with mock.patch.object(installer, "run", side_effect=outputs):
            with self.assertRaisesRegex(installer.InstallError, "Unrecognized populated volume"):
                installer.Host().conflicts(state)

    def test_own_caddy_tcp_listener_does_not_hide_conflicting_udp_listener(self):
        with mock.patch.object(installer.socket, "socket") as connection:
            connection.return_value.__enter__.return_value.bind.side_effect = OSError("occupied")
            with self.assertRaisesRegex(installer.InstallError, "Port 443"):
                installer.Host().ports({(80, "tcp"), (443, "tcp")})

    def test_ipv4_only_preflight_accepts_only_confirmed_unsupported_or_disabled_ipv6(self):
        for unsupported in (errno.EAFNOSUPPORT, errno.EPROTONOSUPPORT, None):
            with self.subTest(unsupported=unsupported):
                ipv4 = mock.MagicMock()
                ipv6 = mock.MagicMock()
                ipv6.__enter__.return_value.bind.side_effect = OSError(errno.EADDRNOTAVAIL, "disabled")

                def socket(family, kind):
                    if family == installer.socket.AF_INET6:
                        if unsupported is not None:
                            raise OSError(unsupported, "IPv6 unavailable")
                        return ipv6
                    return ipv4

                with mock.patch.object(installer.socket, "socket", side_effect=socket), \
                     mock.patch.object(installer.Path, "exists", return_value=True), \
                     mock.patch.object(installer.Path, "read_text", return_value="1\n"):
                    installer.Host().ports(set())
                self.assertEqual(3, ipv4.__enter__.return_value.bind.call_count)

    def test_ipv6_listener_conflicts_and_unconfirmed_address_errors_are_never_skipped(self):
        for kind in (installer.socket.SOCK_STREAM, installer.socket.SOCK_DGRAM):
            for error_number, disabled in ((errno.EADDRINUSE, "1\n"), (errno.EACCES, "1\n"),
                                           (errno.EADDRNOTAVAIL, "0\n")):
                with self.subTest(kind=kind, error_number=error_number):
                    ipv4 = mock.MagicMock()
                    ipv6 = mock.MagicMock()
                    ipv6.__enter__.return_value.bind.side_effect = OSError(error_number, "refuse")

                    def socket(family, actual_kind):
                        return ipv6 if family == installer.socket.AF_INET6 else ipv4

                    owned = {(80, "tcp"), (443, "udp" if kind == installer.socket.SOCK_STREAM else "tcp")}
                    with mock.patch.object(installer.socket, "socket", side_effect=socket), \
                         mock.patch.object(installer.Path, "exists", return_value=True), \
                         mock.patch.object(installer.Path, "read_text", return_value=disabled):
                        with self.assertRaisesRegex(installer.InstallError, "Port 443"):
                            installer.Host().ports(owned)

    def test_manual_operations_reject_modified_missing_or_extra_installed_bundle_files(self):
        subject = self.subject()
        subject.initialize_state()
        subject.extract()
        subject.credentials()
        subject.save(phase="initialized")
        for command in ("initialize", "activate", "status", "rotate-token"):
            self.args.command = command
            self.args.token_output_name = "new-token"
            for name in ("compose.yml", "server_install.py"):
                with self.subTest(command=command, name=name):
                    path = self.args.root / "bundle" / name
                    path.write_bytes(b"modified but protected")
                    resumed = self.subject()
                    with self.assertRaisesRegex(installer.InstallError, "Installed bundle"):
                        resumed.execute()
                    self.assertEqual([], resumed.events)
                    self.assertEqual(b"modified but protected", path.read_bytes())
                    path.write_bytes(self.payload[name])
        self.args.command = "initialize"
        for missing in (True, False):
            with self.subTest(missing=missing):
                path = self.args.root / "bundle/compose.yml" if missing else self.args.root / "bundle/extra.sh"
                if missing:
                    path.unlink()
                else:
                    self.write(path, b"unlisted")
                resumed = self.subject()
                with self.assertRaisesRegex(installer.InstallError, "Installed bundle"):
                    resumed.execute()
                self.assertEqual([], resumed.events)
                if missing:
                    self.write(path, self.payload["compose.yml"])
                else:
                    path.unlink()
        for phase in ("initializing", "prepared"):
            subject.save(phase=phase)
            for command in ("install", "prepare"):
                with self.subTest(phase=phase, command=command):
                    self.args.command = command
                    path = self.args.root / "bundle/server_install.py"
                    path.write_bytes(b"modified during extraction")
                    resumed = self.subject()
                    with self.assertRaisesRegex(installer.InstallError, "Installed bundle content"):
                        resumed.execute()
                    self.assertEqual([], resumed.events)
                    self.assertEqual(b"modified during extraction", path.read_bytes())
                    path.write_bytes(self.payload["server_install.py"])

    def test_bundle_path_cannot_redirect_ownership_or_runtime_checks(self):
        subject = self.subject()
        subject.initialize_state()
        subject.extract()
        subject.save(bundlePath=str(self.assets))
        with self.assertRaisesRegex(installer.InstallError, "bundle path"):
            self.subject().load_state()

    def test_interrupted_initial_extraction_resumes_only_through_install_or_prepare(self):
        subject = self.subject()
        subject.initialize_state()
        bundle = self.args.root / "bundle"
        bundle.mkdir(mode=0o700)
        self.write(bundle / "images.env", self.payload["images.env"])
        for command in ("initialize", "activate", "status", "rotate-token"):
            with self.subTest(command=command):
                self.args.command = command
                with self.assertRaisesRegex(installer.InstallError, "Installed bundle"):
                    self.subject().execute()
        self.args.command = "prepare"
        resumed = self.subject()
        self.assertEqual("prepared", resumed.execute()["state"])
        self.assertEqual(subject.state["installationId"], resumed.state["installationId"])
        self.assertEqual(subject.state["operationId"], resumed.state["operationId"])
        for name, data in self.payload.items():
            self.assertEqual(data, (bundle / name).read_bytes())
        self.args.command = "install"
        self.assertEqual("awaiting_operator", self.subject().execute()["state"])
        self.args.root = self.base / "second-installation"
        interrupted = self.subject()
        interrupted.initialize_state()
        self.assertEqual("awaiting_operator", self.subject().execute()["state"])
        self.assertEqual(interrupted.state["operationId"], installer.read_json(interrupted.state_path)["operationId"])

    def test_incomplete_migration_replays_original_operation_without_regenerating_credentials(self):
        subject = self.subject()
        subject.fail_migration = True
        with self.assertRaisesRegex(installer.InstallError, "incomplete migration"):
            subject.execute()
        original = installer.read_json(subject.state_path)
        credentials = {path.relative_to(self.args.root): path.read_bytes()
                       for path in (self.args.root / "secrets").rglob("*") if path.is_file()}
        recovery = {path.relative_to(self.recovery): path.read_bytes() for path in self.recovery.rglob("*") if path.is_file()}
        resumed = self.subject()
        self.assertEqual("awaiting_operator", resumed.execute()["state"])
        self.assertEqual(original["installationId"], resumed.state["installationId"])
        self.assertEqual(original["operationId"], resumed.state["operationId"])
        for path, data in credentials.items():
            self.assertEqual(data, (self.args.root / path).read_bytes())
        for path, data in recovery.items():
            self.assertEqual(data, (self.recovery / path).read_bytes())
        self.assertEqual(0o600, (self.args.root / "secrets/bootstrap/output/setup-token-initial").stat().st_mode & 0o777)
        ready = self.subject()
        ready.bootstrap_state = "ready"
        self.assertEqual("ready", ready.execute()["state"])
        finished = self.subject()
        with self.assertRaisesRegex(installer.InstallError, "explicit upgrade"):
            finished.execute()
        self.assertEqual([], finished.events)

    def test_missing_established_credentials_never_regenerate(self):
        subject = self.subject()
        subject.initialize_state()
        subject.extract()
        subject.credentials()
        missing = self.args.root / "secrets/app-db-password"
        missing.unlink()
        with self.assertRaisesRegex(installer.InstallError, "never regenerate"):
            subject.credentials()
        self.assertFalse(missing.exists())

    def test_installer_and_manual_steps_use_identical_initialization_and_bootstrap(self):
        self.args.command = "prepare"
        subject = self.subject()
        self.assertEqual("prepared", subject.execute()["state"])
        self.assertEqual(["verify-and-pull"], subject.events)
        self.args.command = "activate"
        with self.assertRaisesRegex(installer.InstallError, "incomplete"):
            self.subject().execute()
        self.args.command = "initialize"
        subject = self.subject()
        self.assertEqual("initialized", subject.execute()["state"])
        commands = [event for event in subject.events if isinstance(event, tuple)]
        for service in ("database-initialize", "api-migrate", "kratos-migrate"):
            self.assertTrue(any(service in event for event in commands))
        bootstrap = [event for event in commands if "api-bootstrap" in event]
        self.assertEqual(["status", "prepare"], [event[event.index("bootstrap") + 1] for event in bootstrap])
        self.assertTrue(all("/run/minicloud-bootstrap/input/request.json" in event for event in bootstrap))
        self.args.command = "activate"
        self.assertEqual("awaiting_operator", self.subject().execute()["state"])

    def test_persistent_lock_serializes_operations_without_replacing_inode(self):
        lock = self.args.root.with_name(self.args.root.name + ".installation.lock")
        with installer.installation_lock(self.args.root):
            inode = lock.stat().st_ino
            with self.assertRaisesRegex(installer.InstallError, "holds the installation lock"):
                with installer.installation_lock(self.args.root):
                    self.fail("Concurrent lock was acquired.")
        with installer.installation_lock(self.args.root):
            self.assertEqual(inode, lock.stat().st_ino)

    def test_compose_uses_pinned_existing_images_and_excludes_ambient_secret_overrides(self):
        subject = self.subject()
        subject.initialize_state()
        subject.extract()
        with mock.patch.dict(os.environ, {"COMPOSE_FILE": "foreign.yml", "DOCKER_HOST": "ssh://foreign",
                                          "DOCKER_AUTH_CONFIG": "synthetic-credential", "BAO_TOKEN": "synthetic-root"}), \
             mock.patch.object(installer, "run", return_value=b"") as command:
            installer.Installer.compose(subject, "up", "-d", "api")
            arguments = command.call_args.args[0]
            env = command.call_args.kwargs["env"]
            self.assertEqual(["up", "--pull", "never", "-d", "api"], arguments[-5:])
            self.assertEqual("unix:///var/run/docker.sock", env["DOCKER_HOST"])
            for key in ("COMPOSE_FILE", "DOCKER_AUTH_CONFIG", "BAO_TOKEN", "OPENBAO_ROOT_TOKEN_FILE"):
                self.assertNotIn(key, env)
            self.assertEqual(subject.state["installationId"], env["MINICLOUD_INSTALLATION_ID"])
            self.assertEqual(str(self.args.root / "secrets"), env["MINICLOUD_SECRETS_DIR"])
            command.reset_mock()
            (self.args.root / "bundle/compose.yml").write_bytes(b"modified after preflight")
            with self.assertRaisesRegex(installer.InstallError, "Installed bundle content"):
                installer.Installer.compose(subject, "up", "-d", "api")
            command.assert_not_called()

    def test_uncertain_initialized_openbao_never_reinitializes_or_loses_recovery(self):
        subject = self.subject()
        subject.initialize_state()
        subject.credentials()
        subject.openbao = installer.Installer.openbao.__get__(subject)
        subject.bao_status = mock.Mock(return_value={"initialized": True, "sealed": True})
        subject.bao = mock.Mock()
        with self.assertRaisesRegex(installer.InstallError, "no independent recovery"):
            subject.openbao()
        subject.bao.assert_not_called()
        self.assertTrue(subject.state_path.exists())

    def test_openbao_initialization_keeps_original_recovery_and_stdin_unseal_across_resume(self):
        subject = self.subject()
        subject.initialize_state()
        subject.credentials()
        subject.openbao = installer.Installer.openbao.__get__(subject)
        original_compose = subject.compose
        response = {"unseal_keys_b64": ["synthetic-share-1", "synthetic-share-2", "synthetic-share-3"],
                    "root_token": "synthetic-root-token-never-logged"}

        def compose(*arguments, **kwargs):
            if "openbao-configure" in arguments:
                for name in ("role-id", "secret-id"):
                    path = self.args.root / "secrets/openbao-approle" / name
                    if not path.exists():
                        self.write(path, ("synthetic-" + name).encode())
                self.assertEqual(response["root_token"], kwargs["root_token"].read_text())
            return original_compose(*arguments, **kwargs)

        subject.compose = compose
        subject.bao_status = mock.Mock(side_effect=[
            {"initialized": False, "sealed": True}, {"initialized": True, "sealed": False},
            {"initialized": True, "sealed": False}, {"initialized": True, "sealed": False},
        ])
        subject.bao = mock.Mock(return_value=json.dumps(response).encode())
        subject.openbao()
        material = self.recovery / subject.state["installationId"] / "openbao-recovery.json"
        self.assertEqual(response, installer.read_json(material))
        self.assertTrue(subject.state["baoConfigured"])
        unseal = [call for call in subject.bao.call_args_list if call.args[:1] == ("write",)]
        self.assertEqual(2, len(unseal))
        for call, share in zip(unseal, response["unseal_keys_b64"]):
            self.assertNotIn(share, str(call.args))
            self.assertEqual({"key": share}, json.loads(call.kwargs["input"]))
        count = subject.bao.call_count
        before = material.read_bytes()
        subject.openbao()
        self.assertEqual(count, subject.bao.call_count, "Resume must neither initialize nor unseal an already unsealed server.")
        self.assertEqual(before, material.read_bytes())
        (self.args.root / "secrets/openbao-approle/secret-id").unlink()
        subject.bao_status = mock.Mock(return_value={"initialized": True, "sealed": False})
        with self.assertRaisesRegex(installer.InstallError, "never regenerate"):
            subject.openbao()

    def test_readiness_distinguishes_tls_waiting_http_failure_and_real_ready(self):
        subject = self.subject()
        subject.initialize_state()
        subject.readiness = installer.Installer.readiness.__get__(subject)
        subject.service_readiness = mock.Mock()
        with mock.patch.object(installer, "https_json", side_effect=AssertionError("not reached")), \
             mock.patch.object(installer.urllib.request, "build_opener") as opener:
            opener.return_value.open.side_effect = urllib.error.URLError("certificate pending")
            self.assertEqual("awaiting_dns_tls", subject.readiness()["state"])
            opener.return_value.open.side_effect = urllib.error.HTTPError("https://example", 503, "failed", {}, None)
            with self.assertRaisesRegex(installer.InstallError, "HTTP 503"):
                subject.readiness()
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.status = 200
        response.read.return_value = b"Healthy"
        response.headers.get_content_type.return_value = "text/html"
        with mock.patch.object(installer.urllib.request, "build_opener") as opener, \
             mock.patch.object(installer, "https_json", side_effect=[
                 {"serverVersion": "0.1.0", "sourceRevision": "a" * 40}, {"state": "ready"},
                 {"apiBaseUrl": "https://api.cloud.example.com", "appDomain": "apps.example.com",
                  "auth": {"origin": "https://api.cloud.example.com",
                           "capabilitiesUrl": "https://api.cloud.example.com/api/account/capabilities"}}]):
            opener.return_value.open.return_value = response
            subject.bootstrap_state = "ready"
            self.assertEqual("ready", subject.readiness()["state"])

    def test_https_success_cannot_hide_a_failed_worker_or_unhealthy_secret_agent(self):
        subject = self.subject()
        services = [{"Service": name, "State": "running", "Health": "healthy"}
                    for name in ("postgres", "openbao", "openbao-agent", "kratos", "api", "dashboard", "worker", "caddy")]
        services[-2]["State"] = "exited"
        subject.compose = mock.Mock(return_value="\n".join(json.dumps(value) for value in services).encode())
        with self.assertRaisesRegex(installer.InstallError, "worker is not running"):
            subject.service_readiness()
        services[-2]["State"] = "running"
        services[2]["Health"] = "unhealthy"
        subject.compose.return_value = json.dumps(services).encode()
        with self.assertRaisesRegex(installer.InstallError, "openbao-agent is not healthy"):
            subject.service_readiness()

    def bootstrap_namespace(self):
        code = (BUNDLE / "install-server.sh").read_text().split("<<'PY'\n", 1)[1]
        code = code.rsplit("\nPY", 1)[0].split("\ntry:\n    sys.exit(main())", 1)[0]
        namespace = {"__name__": "installer_bootstrap_test"}
        exec(compile(code, "signed-installer-fixture", "exec"), namespace)
        namespace["path_chain"] = self.fixture_chain
        root = mock.patch.object(os, "geteuid", return_value=0)
        root.start()
        self.addCleanup(root.stop)
        return namespace

    def test_unprivileged_installer_refuses_before_verification_or_host_commands(self):
        argv = self.signed_fixture()
        bootstrap = self.bootstrap_namespace()
        with mock.patch("sys.argv", argv), mock.patch.object(os, "geteuid", return_value=1000), \
             mock.patch.object(subprocess, "run") as command:
            with self.assertRaisesRegex(ValueError, "Run as root"):
                bootstrap["main"]()
            command.assert_not_called()

    def signed_fixture(self):
        for name in ("install-server.sh", "verify-server-release.py", "checksums.txt.sigstore.json"):
            self.write(self.assets / name, b"synthetic fixture only")
        names = ("install-server.sh", "verify-server-release.py", "server-manifest.json",
                 "minicloud-server-0.1.0.tar.gz")
        inventory = "".join(hashlib.sha256((self.assets / name).read_bytes()).hexdigest() + "  " + name + "\n"
                            for name in names)
        self.write(self.assets / "checksums.txt", inventory.encode())
        return ["install-server.sh", "--tag", self.args.tag, "--assets-dir", str(self.assets),
                "preflight", "--config", str(self.config)]

    def test_untrusted_or_tampered_downloaded_verifier_is_never_executed(self):
        argv = self.signed_fixture()
        bootstrap = self.bootstrap_namespace()
        with mock.patch("sys.argv", argv), mock.patch.object(subprocess, "run") as command:
            command.side_effect = subprocess.CalledProcessError(1, ["cosign"])
            with self.assertRaises(subprocess.CalledProcessError):
                bootstrap["main"]()
            self.assertEqual(["cosign"], [call.args[0][0] for call in command.call_args_list])
        (self.assets / "verify-server-release.py").write_bytes(b"tampered code")
        with mock.patch("sys.argv", argv), mock.patch.object(subprocess, "run") as command:
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                bootstrap["main"]()
            self.assertEqual(["cosign"], [call.args[0][0] for call in command.call_args_list])

    def test_verified_bootstrap_uses_exact_workflow_and_no_latest_or_signature_bypass(self):
        argv = self.signed_fixture()
        bootstrap = self.bootstrap_namespace()
        with mock.patch("sys.argv", argv), mock.patch.object(subprocess, "run") as command:
            self.assertEqual(0, bootstrap["main"]())
            calls = [call.args[0] for call in command.call_args_list]
            self.assertIn("https://github.com/muniventures/minicloud/.github/workflows/server-release.yml@refs/tags/server-v0.1.0",
                          calls[0])
            self.assertEqual("cosign", calls[0][0])
            self.assertEqual(["docker", "info"], calls[1][:2])
            self.assertIn(str(self.assets / "verify-server-release.py"), calls[2])
            self.assertNotIn("--anonymous-pulls", calls[2], "No image pulls before host preflight.")
            self.assertNotIn("latest", str(calls))
            self.assertNotIn("--insecure-ignore", str(calls))

    def test_failed_download_retains_only_partial_asset_and_executes_nothing(self):
        bootstrap = self.bootstrap_namespace()
        argv = ["install-server.sh", "--tag", self.args.tag, "--assets-dir", str(self.assets), "--download", "install"]
        with mock.patch("sys.argv", argv), mock.patch.object(subprocess, "run") as command, \
             mock.patch.object(installer.urllib.request, "build_opener") as opener:
            opener.return_value.open.side_effect = urllib.error.URLError("release absent")
            with self.assertRaises(urllib.error.URLError):
                bootstrap["main"]()
            command.assert_not_called()
        self.assertTrue((self.assets / "checksums.txt.partial").exists())
        self.assertFalse((self.assets / "checksums.txt").exists())

    def test_download_promotion_never_overwrites_an_asset_created_concurrently(self):
        bootstrap = self.bootstrap_namespace()
        argv = self.signed_fixture() + ["--download"]
        path = self.assets / "checksums.txt"
        path.unlink()
        response = mock.MagicMock()
        response.__enter__.return_value = response
        reads = iter((b"downloaded-by-this-invocation", b""))

        def read(size):
            if not path.exists():
                self.write(path, b"concurrent-owner-asset")
            return next(reads)

        response.read.side_effect = read
        with mock.patch("sys.argv", argv), mock.patch.object(subprocess, "run") as command, \
             mock.patch.object(installer.urllib.request, "build_opener") as opener:
            opener.return_value.open.return_value = response
            with self.assertRaises(FileExistsError):
                bootstrap["main"]()
            command.assert_not_called()
        self.assertEqual(b"concurrent-owner-asset", path.read_bytes())
        self.assertEqual(b"downloaded-by-this-invocation", (self.assets / "checksums.txt.partial").read_bytes())


if __name__ == "__main__":
    unittest.main()
