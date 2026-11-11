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


class MaintenanceTests(unittest.TestCase):
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
            output_dir=None, at=None, keep=None, confirm_backup_id=None,
        )
        self.runtime = types.SimpleNamespace(args=self.args, root=self.root, state=self.state)
        self.subject = maintenance.Maintenance(self.runtime, vars(installer))

    def directory(self, name):
        path = self.base / name
        path.mkdir(mode=0o700)
        return path

    @staticmethod
    def write(path, data):
        installer.exclusive(path, data)

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
        self.args.maintenance_operation = "upgrade"
        with self.assertRaises(installer.InstallError):
            self.subject.execute()
        self.args.maintenance_operation = "status"
        status = self.subject.execute()["lastOperation"]
        self.assertEqual("failed", status["state"])
        self.assertEqual("upgrade", status["lastFailure"]["operation"])
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

    def test_blocked_operations_never_stop_work_migrate_activate_restore_or_claim_fence(self):
        with mock.patch.object(installer, "run", side_effect=AssertionError("No subprocess allowed")):
            for operation in ("backup", "upgrade", "restore", "transfer", "stop", "remove"):
                with self.subTest(operation=operation):
                    self.args.maintenance_operation = operation
                    with self.assertRaisesRegex(installer.InstallError, "admission fence"):
                        self.subject.execute()
            self.args.maintenance_operation = "status"
            status = self.subject.execute()
            self.assertEqual("unavailable", status["admissionFence"])
            self.assertFalse(status["fullCoordinatedBackup"])
            self.assertEqual("failed", status["lastOperation"]["state"])
        self.assertEqual([], list(self.backups.iterdir()))

    def test_persistent_installation_lock_serializes_with_installer_and_keeps_inode(self):
        with installer.installation_lock(self.root):
            inode = self.root.with_name("installation.installation.lock").stat().st_ino
            with self.assertRaisesRegex(installer.InstallError, "holds the installation lock"):
                with installer.installation_lock(self.root):
                    self.fail("A concurrent installer/maintenance invocation must not proceed.")
        with installer.installation_lock(self.root):
            self.assertEqual(inode, self.root.with_name("installation.installation.lock").stat().st_ino)


if __name__ == "__main__":
    unittest.main()
