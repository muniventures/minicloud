import argparse
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import types
import unittest
from unittest import mock
import uuid


ROOT = Path(__file__).resolve().parents[2]
BUNDLE = ROOT / "packaging/server/bundle"
ARTIFACTS = ROOT / ".local/artifacts/t44/tests"


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


installer = load("verified_installer_tests", BUNDLE / "server_install.py")
maintenance = load("verified_maintenance_tests", BUNDLE / "server_maintenance.py")


class Fixture(unittest.TestCase):
    def setUp(self):
        self.assertEqual(0, subprocess.run(
            ["git", "check-ignore", str(ARTIFACTS)], cwd=ROOT, capture_output=True).returncode)
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(prefix="recovery-", dir=ARTIFACTS)
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        os.chmod(self.base, 0o700)
        chain = mock.patch.object(installer, "path_chain", side_effect=lambda path: tuple(
            parent for parent in (path, *path.parents) if parent.is_relative_to(self.base)))
        chain.start()
        self.addCleanup(chain.stop)
        original_stat = Path.stat

        def fixture_stat(path, *args, **kwargs):
            info = original_stat(path, *args, **kwargs)
            if path.is_relative_to(self.base):
                fields = list(info)
                fields[4] = 0
                return os.stat_result(fields)
            return info

        ownership = mock.patch.object(Path, "stat", fixture_stat)
        ownership.start()
        self.addCleanup(ownership.stop)
        self.root = self.directory("installation")
        self.secrets = self.directory("installation/secrets")
        self.assets = self.directory("assets")
        self.recovery = self.directory("recovery")
        self.backups = self.directory("archives")
        self.keys = self.directory("keys")
        self.output = self.directory("output")
        self.state = {
            "installationId": str(uuid.uuid4()), "operationId": str(uuid.uuid4()),
            "releaseTag": "server-v0.1.0", "sourceRevision": "a" * 40, "bundleSha256": "b" * 64,
        }
        self.material = self.directory("recovery/" + self.state["installationId"])
        self.identity = self.keys / "age-identity"
        self.recipients = self.keys / "age-recipients"
        installer.run(["age-keygen", "-o", str(self.identity)])
        self.write(self.recipients, installer.run(["age-keygen", "-y", str(self.identity)]))
        installer.run(["openssl", "req", "-x509", "-newkey", "ed25519", "-nodes", "-days", "1",
                       "-subj", "/CN=synthetic-maintenance-test",
                       "-keyout", str(self.material / "openbao-ca.key"),
                       "-out", str(self.secrets / "openbao-ca.crt")])
        for path in (self.material / "openbao-ca.key", self.secrets / "openbao-ca.crt", self.identity):
            path.chmod(0o600)
        self.write(self.material / "openbao-recovery.json", json.dumps({
            "unseal_keys_b64": ["synthetic-share-1", "synthetic-share-2", "synthetic-share-3"],
            "root_token": "synthetic-root-never-logged",
        }).encode())
        self.write(self.material / "openbao-root-token", b"synthetic-root-never-logged")
        self.args = argparse.Namespace(
            root=self.root, assets_dir=self.assets, recovery_dir=self.recovery, tag="server-v0.1.0",
            config=self.base / "input.json", public_address=["192.0.2.1"],
            maintenance_operation="backup-recovery", backup_dir=self.backups,
            recipients_file=self.recipients, identity_file=self.identity, archive=None,
            output_dir=None, at=None, keep=None, confirm_backup_id=None, previous_assets_dir=None,
            drain_timeout=1800, confirm_replace_live=None, confirm_operation_id=None, keep_fenced=False,
        )
        self.runtime = types.SimpleNamespace(args=self.args, root=self.root, state=self.state,
                                             state_path=self.root / "installation.json")
        self.subject = maintenance.Maintenance(self.runtime, vars(installer))

    def directory(self, name):
        path = self.base / name
        path.mkdir(mode=0o700)
        return path

    @staticmethod
    def write(path, data):
        installer.exclusive(path, data)


class MaintenanceTests(Fixture):
    def capture(self):
        result = self.subject.execute()
        self.args.archive = Path(result["archive"])
        return result

    def rewrite_archive(self, transform):
        original = installer.run(["age", "-d", "-i", str(self.identity), str(self.args.archive)])
        files = {}
        with tarfile.open(fileobj=io.BytesIO(original), mode="r:") as archive:
            for member in archive:
                files[member.name] = archive.extractfile(member).read()
        transform(files)
        result = io.BytesIO()
        with tarfile.open(fileobj=result, mode="w") as archive:
            for name, value in files.items():
                item = tarfile.TarInfo(name)
                item.size = len(value)
                archive.addfile(item, io.BytesIO(value))
        self.args.archive.write_bytes(installer.run(
            ["age", "-e", "-R", str(self.recipients)], input=result.getvalue()))

    def test_real_authenticated_roundtrip_keeps_identity_outside_capture_and_starts_nothing(self):
        captured = self.capture()
        self.assertFalse(captured["fullCoordinatedBackup"])
        self.assertNotIn(b"synthetic-root-never-logged", self.args.archive.read_bytes())
        self.assertEqual(0o600, self.args.archive.stat().st_mode & 0o777)
        self.args.maintenance_operation = "verify-recovery"
        self.assertEqual(captured["backupId"], self.subject.execute()["backupId"])
        self.args.output_dir = self.output / "new-rehearsal"
        self.args.maintenance_operation = "rehearse-recovery"
        result = self.subject.execute()
        self.assertFalse(result["servicesStarted"])
        self.assertEqual(maintenance.RECOVERY_FILES, {
            path.name for path in self.args.output_dir.iterdir()})
        for name in maintenance.RECOVERY_FILES:
            self.assertEqual((self.material / name).read_bytes(),
                             (self.args.output_dir / name).read_bytes())

    def test_tampering_and_truncation_rejected_before_rehearsal_mutation(self):
        self.capture()
        original = self.args.archive.read_bytes()
        self.args.output_dir = self.output / "tampered"
        self.args.maintenance_operation = "rehearse-recovery"
        for content in (original[:-20], original[:-1] + bytes([original[-1] ^ 1])):
            with self.subTest(length=len(content)):
                self.args.archive.write_bytes(content)
                with self.assertRaises(installer.InstallError):
                    self.subject.execute()
                self.assertFalse(self.args.output_dir.exists())
                self.assertEqual("failed", installer.read_json(self.subject.status_path)["state"])

    def test_authenticated_but_incomplete_or_unsafe_archives_are_rejected(self):
        for transform in (
            lambda files: files.pop("openbao-root-token"),
            lambda files: files.update({"../escape": b"not allowed"}),
            lambda files: files.update({"openbao-root-token": b"incorrect root"}),
            lambda files: files.update({"age-identity": self.identity.read_bytes()}),
        ):
            with self.subTest(transform=transform):
                self.args.maintenance_operation = "backup-recovery"
                self.capture()
                self.rewrite_archive(transform)
                self.args.output_dir = self.output / "incomplete"
                self.args.maintenance_operation = "rehearse-recovery"
                with self.assertRaises(installer.InstallError):
                    self.subject.execute()
                self.assertFalse(self.args.output_dir.exists())

    def test_exact_release_and_original_ids_rejected_before_output(self):
        self.capture()
        for field in ("installationId", "operationId", "releaseTag", "sourceRevision", "bundleSha256"):
            original = self.state[field]
            self.state[field] = str(uuid.uuid4()) if field.endswith("Id") else "different"
            self.args.output_dir = self.output / "incompatible"
            self.args.maintenance_operation = "rehearse-recovery"
            with self.subTest(field=field), self.assertRaises(installer.InstallError):
                self.subject.execute()
            self.assertFalse(self.args.output_dir.exists())
            self.state[field] = original

    def test_missing_unknown_or_inconsistent_dependencies_never_publish_success(self):
        token = self.material / "openbao-root-token"
        token.write_bytes(b"wrong")
        with self.assertRaises(installer.InstallError):
            self.subject.execute()
        token.unlink()
        with self.assertRaises(installer.InstallError):
            self.subject.execute()
        self.assertEqual([], list(self.backups.iterdir()))

    def test_key_escrow_inside_destination_or_recovery_is_refused(self):
        for path in (self.backups / "identity", self.material / "identity"):
            self.write(path, self.identity.read_bytes())
            self.args.identity_file = path
            with self.subTest(path=path), self.assertRaises(installer.InstallError):
                self.subject.execute()
        self.assertFalse(list(self.backups.glob("*.age")))

    def test_wrong_recipient_pair_does_not_publish_archive(self):
        other = self.keys / "wrong-identity"
        installer.run(["age-keygen", "-o", str(other)])
        self.args.identity_file = other
        with self.assertRaises(installer.InstallError):
            self.subject.execute()
        self.assertEqual([], list(self.backups.iterdir()))

    def test_failed_attempt_retains_last_success_without_claiming_full_backup(self):
        success = self.capture()
        self.args.maintenance_operation = "stop"
        with self.assertRaises(installer.InstallError):
            self.subject.execute()
        self.args.maintenance_operation = "verify-recovery"
        self.args.archive.write_bytes(b"truncated")
        with self.assertRaises(installer.InstallError):
            self.subject.execute()
        self.args.maintenance_operation = "status"
        with mock.patch.object(self.subject, "authority_ok", side_effect=installer.InstallError("offline")):
            result = self.subject.execute()
        self.assertEqual("authority_unavailable", result["state"])
        status = result["lastOperation"]
        self.assertEqual("failed", status["state"])
        self.assertEqual("verify-recovery", status["lastFailure"]["operation"])
        self.assertEqual(success["backupId"], status["lastSuccessfulRecovery"]["backupId"])
        self.assertFalse(status["lastSuccessfulRecovery"]["fullCoordinatedBackup"])

    def test_retention_plans_only_and_removal_requires_exact_authenticated_target(self):
        first = self.capture()
        self.capture()
        self.args.maintenance_operation = "retention-plan"
        self.args.keep = 1
        result = self.subject.execute()
        self.assertEqual(0, result["removed"])
        self.assertEqual([{"archive": first["archive"], "backupId": first["backupId"]}],
                         result["candidates"])
        self.assertEqual(2, len(list(self.backups.iterdir())))
        self.args.archive = Path(first["archive"])
        self.args.maintenance_operation = "remove-recovery"
        self.args.confirm_backup_id = "wrong"
        with self.assertRaises(installer.InstallError):
            self.subject.execute()
        self.assertTrue(self.args.archive.exists())
        self.args.confirm_backup_id = first["backupId"]
        self.subject.execute()
        self.assertFalse(self.args.archive.exists())
        self.assertTrue(self.material.exists())
        self.assertTrue(self.root.exists())

    def test_schedule_authors_quoted_locked_verified_entrypoint_never_installs(self):
        self.write(self.assets / "install-server.sh", b"synthetic verified entry")
        self.args.config = self.base / "input $HOME %i.json"
        self.args.maintenance_operation = "schedule-recovery"
        self.args.output_dir = self.output
        self.args.at = "03:15"
        result = self.subject.execute()
        self.assertEqual("schedule_authored_not_installed", result["state"])
        service = (self.output / "minicloud-recovery.service").read_text()
        self.assertIn('"maintenance" "backup-recovery"', service)
        self.assertIn('"--tag" "server-v0.1.0"', service)
        self.assertIn("UMask=0077", service)
        self.assertIn("input $$HOME %%i.json", service)
        self.assertNotIn("systemctl", service)
        with self.assertRaisesRegex(installer.InstallError, "never overwrite"):
            self.subject.execute()

    def test_stop_and_remove_are_not_maintenance_operations_and_never_run_commands(self):
        with mock.patch.object(installer, "run", side_effect=AssertionError("No subprocess allowed")):
            for operation in ("stop", "remove"):
                with self.subTest(operation=operation):
                    self.args.maintenance_operation = operation
                    with self.assertRaisesRegex(installer.InstallError, "never removed"):
                        self.subject.execute()
        self.assertEqual([], list(self.backups.iterdir()))

    def test_persistent_installation_lock_serializes_with_installer_and_keeps_inode(self):
        with installer.installation_lock(self.root):
            inode = self.root.with_name("installation.installation.lock").stat().st_ino
            with self.assertRaisesRegex(installer.InstallError, "holds the installation lock"):
                with installer.installation_lock(self.root):
                    self.fail("A concurrent installer/maintenance invocation must not proceed.")
        with installer.installation_lock(self.root):
            self.assertEqual(inode, self.root.with_name("installation.installation.lock").stat().st_ino)



FAKE_DOCKER = """#!/usr/bin/env python3
import hashlib, io, json, sys, tarfile
LOG, FAIL = {log!r}, {fail!r}
args = sys.argv[1:]
consume = "-i" in args or "pg_restore" in args
data = sys.stdin.buffer.read() if consume else b""
try:
    fail = open(FAIL).read().strip()
except FileNotFoundError:
    fail = ""
out = b""
if "pg_dump" in args:
    out = b"PGDMP synthetic " + args[-1].encode() * 4096
elif "-cf" in args:
    volume = args[args.index("--volume") + 1].split(":")[0]
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w") as archive:
        payload = ("synthetic " + volume).encode() * 512
        item = tarfile.TarInfo("./data")
        item.size = len(payload)
        archive.addfile(item, io.BytesIO(payload))
    out = buffer.getvalue()
elif args[:2] == ["ps", "-q"]:
    out = b"c1\\nc2\\n"
with open(LOG, "a") as log:
    log.write(json.dumps({{"args": args, "stdin": hashlib.sha256(data).hexdigest() if consume else None,
                          "stdout": hashlib.sha256(out).hexdigest()}}) + "\\n")
if fail and fail in " ".join(args):
    sys.exit(1)
sys.stdout.buffer.write(out)
"""


class Authority:
    """Synthetic model of `dotnet Minicloud.Api.dll maintenance` (fence/release/transfer semantics of #56)."""

    def __init__(self):
        self.state, self.epoch, self.holder, self.host = "open", 1, None, None
        self.completed, self.drain = set(), []
        self.authorized = True
        self.calls = []

    def view(self):
        return {"state": self.state, "epoch": self.epoch, "holderOperationId": self.holder,
                "authorizedHostId": self.host, "localHostAuthorized": self.authorized}

    def refuse(self, operation, code):
        return {"operation": operation, "result": "refused", "code": code, "message": "synthetic refusal"}

    def __call__(self, operation, payload):
        self.calls.append((operation, payload.get("operationId"), payload.get("hostId")))
        op = payload.get("operationId")
        if operation == "status":
            return {"operation": operation, "result": "ok", "authority": self.view()}
        if operation == "fence":
            if op in self.completed:
                return self.refuse(operation, "maintenance_operation_completed")
            if self.state == "fenced" and self.holder != op:
                return self.refuse(operation, "maintenance_fence_held")
            self.state, self.holder = "fenced", op
            return {"operation": operation, "result": "ok", "authority": self.view()}
        if operation == "release":
            if self.state != "fenced" or self.holder != op:
                return self.refuse(operation, "maintenance_not_holder")
            self.state, self.holder = "open", None
            self.completed.add(op)
            return {"operation": operation, "result": "ok", "authority": self.view()}
        if operation == "transfer":
            if op in self.completed:
                return self.refuse(operation, "maintenance_operation_completed")
            self.epoch += 1
            self.state, self.holder, self.host, self.authorized = "fenced", op, payload["hostId"], True
            return {"operation": operation, "result": "ok", "authority": self.view()}
        if operation == "drain-status":
            if self.holder != op:
                return self.refuse(operation, "maintenance_not_holder")
            result = self.drain.pop(0) if self.drain else "drained"
            work = [] if result == "drained" else [{
                "category": "jobs", "state": "uncertain" if result == "uncertain" else "running",
                "count": 1, "ids": ["job-1"], "blocksDrain": True}]
            return {"operation": operation, "result": "drained" if result == "drained" else "not_drained",
                    "status": {"authority": self.view(), "work": work}}
        raise AssertionError(operation)


class FakeInstaller:
    def __init__(self, test, manifest, state):
        self.test, self.args, self.root = test, test.args, test.root
        self.state_path = test.root / "installation.json"
        self.state, self.manifest, self.installed = state, manifest, manifest
        self.config = state["configuration"]
        self.host = types.SimpleNamespace(tools=lambda: test.events.append(("tools",)))
        self.unseal_failures = 0

    def compose_invocation(self, *args):
        return ["docker", "compose", "--project-name", "minicloud", *args], dict(os.environ)

    def compose(self, *args, input=None, allowed=(0,)):
        line = " ".join(args)
        if "api-bootstrap maintenance" in line:
            self.test.assertNotIn(self.test.state["installationId"], line)
            reply = self.test.authority(args[args.index("maintenance") + 1], json.loads(input))
            return (json.dumps(reply) + "\n").encode()
        self.test.events.append(("compose", *args))
        if self.test.compose_failure and self.test.compose_failure in line:
            raise installer.InstallError("synthetic compose failure")
        return b""

    def docker_environment(self):
        return dict(os.environ)

    def host_id(self):
        return (self.root / "host-id").read_text().strip() if (self.root / "host-id").exists() else ""

    def load_state(self, manifest=None):
        self.installed = manifest or self.manifest

    def save(self, **updates):
        self.state.update(updates)
        installer.atomic_json(self.state_path, self.state)

    def extract(self):
        bundle = self.root / "bundle"
        bundle.mkdir(mode=0o700, exist_ok=True)
        (bundle / "release").write_text(self.manifest["releaseTag"])
        self.test.events.append(("extract", self.manifest["releaseTag"]))

    def verify_installed_bundle(self):
        installer.require((self.root / "bundle/release").read_text() == self.installed["releaseTag"], "differs")

    def pull_verified_images(self):
        self.test.events.append(("pull", self.manifest["releaseTag"]))

    def unseal(self, status=None):
        self.test.events.append(("unseal",))
        if self.unseal_failures:
            self.unseal_failures -= 1
            raise installer.InstallError("synthetic unseal failure")

    def service_readiness(self):
        self.test.events.append(("service_readiness",))

    def readiness(self):
        return {"state": "ready"}


class CoordinatedMaintenanceTests(Fixture):
    def setUp(self):
        super().setUp()
        bin_dir = self.directory("bin")
        self.docker_log, self.docker_fail = self.base / "docker.log", self.base / "docker.fail"
        docker = bin_dir / "docker"
        docker.write_text(FAKE_DOCKER.format(log=str(self.docker_log), fail=str(self.docker_fail)))
        docker.chmod(0o700)
        path = mock.patch.dict(os.environ, {"PATH": f"{bin_dir}:{os.environ['PATH']}"})
        path.start()
        self.addCleanup(path.stop)
        for name, value in ((maintenance, "BACKUP_HEADROOM"), (maintenance.time, "sleep")):
            patch = mock.patch.object(name, value, 0 if value == "BACKUP_HEADROOM" else (lambda _: None))
            patch.start()
            self.addCleanup(patch.stop)
        self.events, self.compose_failure = [], None
        self.authority = Authority()
        self.previous_assets = self.directory("previous-assets")
        old = self.release("0.1.0", "20261111134441_MaintenanceAuthority")
        bundle = io.BytesIO()
        with tarfile.open(fileobj=bundle, mode="w:gz") as archive:
            data = json.dumps(old).encode()
            item = tarfile.TarInfo("server-release.json")
            item.size = len(data)
            archive.addfile(item, io.BytesIO(data))
        tarball = self.previous_assets / "minicloud-server-0.1.0.tar.gz"
        self.write(tarball, bundle.getvalue())
        old_sha = installer.hashlib.sha256(bundle.getvalue()).hexdigest()
        self.state.clear()
        self.state.update({
            "schemaVersion": 1, "product": "minicloud", "root": str(self.root),
            "installationId": str(uuid.uuid4()), "operationId": str(uuid.uuid4()),
            "credentialsComplete": True, "configuration": {"domain": "example.test"},
            **{key: old[key] for key in maintenance.RELEASE_FIELDS if key != "bundleSha256"},
            "bundleSha256": old_sha,
        })
        os.rename(self.material, self.recovery / self.state["installationId"])
        self.write(self.root / "installation.json", json.dumps(self.state).encode())
        self.old_manifest = old | {"bundle": {"name": tarball.name, "sha256": old_sha}}
        self.runtime = FakeInstaller(self, self.old_manifest, self.state)
        self.runtime.extract()
        self.events.clear()
        self.subject = maintenance.Maintenance(self.runtime, vars(installer))

    @staticmethod
    def release(version, migration):
        return {"releaseTag": "server-v" + version, "serverVersion": version, "sourceRevision": "c" * 40,
                "images": {"api": "ghcr.io/example/api@sha256:" + "1" * 64},
                "dependencies": {"postgres": "docker.io/library/postgres@sha256:" + "2" * 64},
                "configurationSchemaVersion": 1, "identitySchemaVersion": 1, "applicationMigration": migration}

    def run_operation(self, operation, **args):
        for key, value in {"maintenance_operation": operation, **args}.items():
            setattr(self.args, key, value)
        return maintenance.Maintenance(self.runtime, vars(installer)).execute()

    def docker_calls(self):
        if not self.docker_log.exists():
            return []
        return [json.loads(line) for line in self.docker_log.read_text().splitlines()]

    def authority_ops(self):
        return [call[0] for call in self.authority.calls if call[0] != "status"]

    def record(self):
        return json.loads((self.root / "maintenance.json").read_text())

    def test_backup_fences_waits_for_drain_captures_verifies_and_releases(self):
        self.authority.drain = ["not_drained", "drained"]
        result = self.run_operation("backup")
        self.assertEqual("backup_saved", result["state"])
        self.assertEqual(["fence", "drain-status", "drain-status", "release"], self.authority_ops())
        self.assertEqual("open", self.authority.state)
        archive = Path(result["archive"])
        self.assertEqual({"manifest.age"} | {name + ".age" for name in maintenance.COMPONENTS},
                         {path.name for path in archive.iterdir()})
        self.assertEqual([archive], list(self.backups.iterdir()))
        self.assertEqual([("compose", "pause", "openbao"), ("compose", "unpause", "openbao")], self.events)
        calls = self.docker_calls()
        self.assertEqual(2, sum("pg_dump" in call["args"] for call in calls))
        self.assertTrue(all("--network" in call["args"] and ":ro" in " ".join(call["args"])
                            for call in calls if "-cf" in call["args"]))
        self.assertFalse(any(word in call["args"] for call in calls for word in ("stop", "kill", "rm")))
        self.assertNotIn(b"synthetic-root-never-logged", self.docker_log.read_bytes())
        for path in archive.iterdir():
            self.assertNotIn(b"synthetic", path.read_bytes())
        self.assertTrue(self.record()["lastSuccessfulBackup"]["fullCoordinatedBackup"])
        verified = self.run_operation("verify-backup", archive=archive)
        self.assertEqual(("backup_verified", result["backupId"]), (verified["state"], verified["backupId"]))
        status = self.run_operation("status")
        self.assertEqual("maintenance_idle", status["state"])

    def test_backup_that_never_drains_releases_and_publishes_nothing(self):
        self.authority.drain = ["not_drained"] * 1000
        clock = iter(range(0, 100000, 10))
        with mock.patch.object(maintenance.time, "monotonic", lambda: next(clock)):
            with self.assertRaisesRegex(installer.InstallError, "did not drain within 30s.*No accepted work"):
                self.run_operation("backup", drain_timeout=30)
        self.assertEqual("open", self.authority.state)
        self.assertEqual("release", self.authority_ops()[-1])
        self.assertEqual([], list(self.backups.iterdir()))
        self.assertEqual([], self.docker_calls())
        failure = self.record()["lastFailure"]
        self.assertEqual(("backup", True), (failure["operation"], failure["fenceReleased"]))
        self.assertNotIn("heldFence", self.record())

    def test_uncertain_work_refuses_and_releases(self):
        self.authority.drain = ["uncertain"] * 1000
        clock = iter(range(0, 100000, 10))
        with mock.patch.object(maintenance.time, "monotonic", lambda: next(clock)):
            with self.assertRaisesRegex(installer.InstallError, "uncertain work.*jobs/uncertain=1"):
                self.run_operation("backup")
        self.assertEqual("open", self.authority.state)
        self.assertEqual([], list(self.backups.iterdir()))

    def test_fence_held_by_another_operation_is_left_alone(self):
        self.authority.state, self.authority.holder = "fenced", "other-operation"
        with self.assertRaisesRegex(installer.InstallError, "maintenance_fence_held"):
            self.run_operation("backup")
        self.assertEqual(("fenced", "other-operation"), (self.authority.state, self.authority.holder))
        self.assertEqual([], list(self.backups.iterdir()))

    def test_failed_capture_removes_partial_output_and_releases(self):
        self.docker_fail.write_text("volume minicloud_artifacts")
        with self.assertRaisesRegex(installer.InstallError, "volume-artifacts.tar.age failed"):
            self.run_operation("backup")
        self.assertEqual("open", self.authority.state)
        self.assertEqual([], list(self.backups.iterdir()))
        self.assertIn(("compose", "unpause", "openbao"), self.events)

    def upgrade_target(self, version="0.2.0"):
        target = self.release(version, "20261201000000_Next")
        self.runtime.manifest = target | {"bundle": {"name": f"minicloud-server-{version}.tar.gz", "sha256": "d" * 64}}
        self.runtime.installed = self.runtime.manifest
        return {"previous_assets_dir": self.previous_assets}

    def test_upgrade_backs_up_migrates_verifies_and_releases(self):
        result = self.run_operation("upgrade", **self.upgrade_target())
        self.assertEqual(("upgraded", "server-v0.1.0", "server-v0.2.0"),
                         (result["state"], result["fromTag"], result["toTag"]))
        self.assertEqual(["fence", "drain-status", "release"], self.authority_ops())
        self.assertEqual("open", self.authority.state)
        expected = [("pull", "server-v0.2.0"), ("compose", "pause", "openbao"),
                         ("compose", "stop", "worker", "api", "dashboard", "kratos"), ("extract", "server-v0.2.0"),
                         ("unseal",), ("compose", "run", "--rm", "-T", "api-migrate"),
                         ("compose", "up", "-d", "--wait", "--wait-timeout", "180", "api", "dashboard", "worker"),
                         ("service_readiness",)]
        events = iter(self.events)
        self.assertTrue(all(any(event == item for event in events) for item in expected),
                        "Upgrade must pull, back up, stop, switch, migrate, start and verify in order.")
        self.assertEqual("server-v0.2.0", json.loads((self.root / "installation.json").read_text())["releaseTag"])
        self.assertEqual("d" * 64, self.state["bundleSha256"])
        self.assertTrue((self.root / f"bundle-previous-{result['operationId']}").exists())
        self.assertEqual([Path(result["backupArchive"])], list(self.backups.iterdir()))
        self.assertNotIn("pendingUpgrade", self.record())

    def test_upgrade_refuses_downgrade_and_pre_authority_releases_before_any_change(self):
        with self.assertRaisesRegex(installer.InstallError, "Downgrade refused"):
            self.run_operation("upgrade", **self.upgrade_target("0.0.9"))
        self.assertEqual([], self.authority.calls)
        self.assertEqual([], self.events)
        with self.assertRaisesRegex(installer.InstallError, "requires --previous-assets-dir"):
            self.run_operation("upgrade", previous_assets_dir=None, **{})

    def test_upgrade_failure_before_migration_reverts_previous_release_then_releases(self):
        self.runtime.unseal_failures = 1
        with self.assertRaisesRegex(installer.InstallError, "synthetic unseal failure"):
            self.run_operation("upgrade", **self.upgrade_target())
        self.assertEqual("open", self.authority.state)
        self.assertEqual("server-v0.1.0", json.loads((self.root / "installation.json").read_text())["releaseTag"])
        self.assertEqual("server-v0.1.0", (self.root / "bundle/release").read_text())
        record = self.record()
        self.assertNotIn("pendingUpgrade", record)
        self.assertTrue(record["lastFailure"]["fenceReleased"])
        self.assertIn("previous release is running", record["lastFailure"]["rollback"])
        self.assertNotIn(("compose", "run", "--rm", "-T", "api-migrate"), self.events)

    def test_migration_failure_stays_fenced_with_rollback_guidance_and_resumes(self):
        self.compose_failure = "api-migrate"
        with self.assertRaisesRegex(installer.InstallError, "synthetic compose failure"):
            self.run_operation("upgrade", **self.upgrade_target())
        self.assertEqual("fenced", self.authority.state)
        record = self.record()
        self.assertEqual("migrating", record["pendingUpgrade"]["phase"])
        self.assertFalse(record["lastFailure"]["fenceReleased"])
        self.assertIn("maintenance restore --archive", record["lastFailure"]["rollback"])
        self.assertIn("never downgrade images alone", record["lastFailure"]["rollback"])
        self.assertEqual("maintenance_incomplete", self.run_operation("status")["state"])
        operation_id = record["pendingUpgrade"]["operationId"]
        with self.assertRaisesRegex(installer.InstallError, "upgrade is incomplete"):
            self.run_operation("release", confirm_operation_id=operation_id)
        with self.assertRaisesRegex(installer.InstallError, "incomplete"):
            self.run_operation("backup")
        self.compose_failure = None
        result = self.run_operation("upgrade")
        self.assertEqual(("upgraded", operation_id), (result["state"], result["operationId"]))
        self.assertEqual("open", self.authority.state)
        self.assertEqual(1, len(list(self.backups.iterdir())))

    def test_restore_requires_confirmation_stays_fenced_until_operator_confirms(self):
        backup = self.run_operation("backup")
        archive = Path(backup["archive"])
        captured = {call["args"][-1]: call["stdout"] for call in self.docker_calls() if "pg_dump" in call["args"]}
        self.docker_log.unlink()
        self.events.clear()
        with self.assertRaisesRegex(installer.InstallError, "--confirm-replace-live " + self.state["installationId"]):
            self.run_operation("restore", archive=archive)
        self.assertEqual([], self.docker_calls())
        self.assertEqual("open", self.authority.state)
        epoch = self.authority.epoch
        result = self.run_operation("restore", archive=archive, confirm_replace_live=self.state["installationId"])
        self.assertEqual("restored_fenced", result["state"])
        self.assertIn("is NOT stopped", result["limitation"])
        self.assertEqual(("fenced", result["operationId"], epoch + 1),
                         (self.authority.state, self.authority.holder, self.authority.epoch))
        self.assertEqual(self.runtime.host_id(), self.authority.host)
        calls = self.docker_calls()
        self.assertEqual(["ps", "-q"], calls[0]["args"][:2])
        restored = [call["stdin"] for call in calls if "pg_restore" in call["args"]]
        self.assertEqual(sorted(captured.values()), sorted(restored))
        self.assertEqual(4, sum("-i" in call["args"] for call in calls))
        self.assertIn(("compose", "run", "--rm", "-T", "api-migrate"), self.events)
        self.assertEqual("restore_pending_confirmation", self.run_operation("status")["state"])
        with self.assertRaisesRegex(installer.InstallError, "restore-confirm"):
            self.run_operation("release", confirm_operation_id=result["operationId"])
        with self.assertRaisesRegex(installer.InstallError, "exact restore"):
            self.run_operation("restore-confirm", confirm_operation_id=str(uuid.uuid4()))
        self.assertEqual("fenced", self.authority.state)
        confirmed = self.run_operation("restore-confirm", confirm_operation_id=result["operationId"])
        self.assertEqual("restore_confirmed", confirmed["state"])
        self.assertEqual("open", self.authority.state)
        self.assertNotIn("pendingRestore", self.record())

    def test_tampered_backup_rejected_before_any_restore_mutation(self):
        archive = Path(self.run_operation("backup")["archive"])
        self.docker_log.unlink()
        self.events.clear()
        component = archive / "volume-artifacts.tar.age"
        data = bytearray(component.read_bytes())
        data[-5] ^= 1
        component.write_bytes(bytes(data))
        before = (self.root / "installation.json").read_bytes()
        with self.assertRaisesRegex(installer.InstallError, "ciphertext differs"):
            self.run_operation("restore", archive=archive, confirm_replace_live=self.state["installationId"])
        self.assertEqual(([], []), (self.docker_calls(), self.events))
        self.assertEqual(before, (self.root / "installation.json").read_bytes())
        self.assertEqual("open", self.authority.state)

    def test_transfer_takes_authority_restarts_services_and_states_limitation(self):
        result = self.run_operation("transfer")
        self.assertEqual(("transferred", True, 2), (result["state"], result["released"], result["epoch"]))
        self.assertIn("NOT stopped", result["limitation"])
        self.assertIn("rotate", result["limitation"])
        self.assertEqual(["fence", "drain-status", "transfer", "release"], self.authority_ops())
        self.assertEqual(self.runtime.host_id(), self.authority.calls[-2][2])
        self.assertIn(("compose", "up", "-d", "--wait", "--wait-timeout", "180", "--force-recreate", "api", "worker"),
                      self.events)
        self.assertEqual("open", self.authority.state)

    def test_schedule_backup_authors_coordinated_units_only(self):
        self.write(self.assets / "install-server.sh", b"#!/bin/sh\n")
        result = self.run_operation("schedule-backup", output_dir=self.output, at="02:30", drain_timeout=900)
        self.assertEqual(["minicloud-backup.service", "minicloud-backup.timer"], result["units"])
        service = (self.output / "minicloud-backup.service").read_text()
        self.assertIn('"maintenance" "backup"', service)
        self.assertIn('"--drain-timeout" "900"', service)
        self.assertEqual([], self.authority.calls)

    def test_transfer_from_unauthorized_host_can_stay_fenced(self):
        self.authority.authorized = False
        result = self.run_operation("transfer", keep_fenced=True)
        self.assertEqual(["transfer"], self.authority_ops())
        self.assertEqual(("fenced", result["operationId"]), (self.authority.state, self.authority.holder))
        self.assertFalse(result["released"])
        released = self.run_operation("release", confirm_operation_id=result["operationId"])
        self.assertEqual("fence_released", released["state"])
        self.assertEqual("open", self.authority.state)


if __name__ == "__main__":
    unittest.main()
