# SPDX-License-Identifier: MPL-2.0
import importlib.util
import fcntl
import json
import os
import pathlib
import shutil
import subprocess
import sys
import unittest
import uuid
from unittest.mock import patch

artifacts = pathlib.Path(sys.argv.pop(1)).resolve()
spec = importlib.util.spec_from_file_location("seed_inspector", pathlib.Path(__file__).parents[1] / "inspect-host.py")
inspector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inspector)
seed = "a" * 32


class InspectorTests(unittest.TestCase):
    def setUp(self):
        self.root = artifacts / ("inspector-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        (self.root / "opt").mkdir(mode=0o700)
        self.commands = []
        self.containers = []
        self.volumes = b""

    def tearDown(self):
        shutil.rmtree(self.root)

    def write(self, name, value):
        p = self.root / name.lstrip("/")
        p.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        p.write_text(value if isinstance(value, str) else json.dumps(value))
        p.chmod(0o600)
        return p

    def bind(self):
        self.write("/opt/minicloud.seed-operation", seed + "\n")
        self.write("/var/lib/minicloud-seed/" + seed + "/operation", seed + "\n")

    def install(self, **updates):
        self.bind()
        state = dict(schemaVersion=1, product="minicloud", root="/opt/minicloud",
                     recoveryDir="/var/lib/minicloud-seed/" + seed + "/recovery",
                     installationId="11111111-1111-4111-8111-111111111111",
                     operationId="22222222-2222-4222-8222-222222222222",
                     releaseTag="server-v1.2.3", sourceRevision="c" * 40, phase="ready", credentialsComplete=True,
                     configuration=dict(consoleUrl="https://cloud.example.com", apiUrl="https://api.cloud.example.com",
                                        appDomain="apps.example.com"), publicAddresses=["203.0.113.1"],
                     setupTokenFile="DO-NOT-EXPORT", recoverySecret="DO-NOT-EXPORT")
        state.update(updates)
        self.write("/opt/minicloud/installation.json", state)
        return state

    def run_docker(self, args, **kwargs):
        self.commands.append(args[1:])
        if args[1:] == ["ps", "-aq"]:
            output = b"\n".join(("a" * 12 + format(i, "x")).encode() for i in range(len(self.containers)))
        elif args[1:] == ["volume", "ls", "-q"]:
            output = self.volumes
        elif args[1] == "inspect":
            output = json.dumps(self.containers).encode()
        else:
            raise AssertionError("Unexpected mutation: " + repr(args))
        return subprocess.CompletedProcess(args, 0, stdout=output)

    def observe(self):
        def snapshot():
            return {str(p): (p.lstat().st_mode, p.lstat().st_mtime_ns, p.read_bytes() if p.is_file() else None)
                    for p in (self.root, *self.root.rglob("*"))}
        before = snapshot()
        with patch.object(inspector.shutil, "which", return_value="docker"):
            result = inspector.inspect_host(seed, self.root, os.getuid(), self.run_docker)
        self.assertEqual(before, snapshot(), "Check modified host files, including on failure.")
        return result

    def test_empty_host_never_creates_lock_or_metadata(self):
        self.assertEqual("empty", self.observe()["state"])
        self.assertFalse((self.root / "opt/minicloud.installation.lock").exists())

    def test_missing_corrupt_future_and_unknown_installation_metadata_fail_closed(self):
        for content in (None, "{", {"schemaVersion": 2}, {"schemaVersion": True}):
            with self.subTest(content=content):
                p = self.root / "opt/minicloud"
                p.mkdir(mode=0o700, exist_ok=True)
                if content is not None:
                    self.write("/opt/minicloud/installation.json", content)
                self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_preparation_interruption_is_not_a_fresh_installation(self):
        self.bind()
        self.assertEqual("preparing", self.observe()["state"])
        self.write("/var/lib/minicloud-seed/" + seed + "/preparation.pending", "{")
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_unknown_preparation_or_interrupted_asset_upload_is_not_an_empty_host(self):
        self.write("/var/lib/minicloud-seed/" + "b" * 32 + "/operation", "b" * 32)
        self.assertEqual("reconciliation_required", self.observe()["state"])
        shutil.rmtree(self.root / "var/lib/minicloud-seed" / ("b" * 32))
        self.bind()
        self.write("/var/lib/minicloud-seed/" + seed + "/assets/checksums.txt.pending", "partial")
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_partial_installer_preserves_ids_and_never_uses_its_status_or_bootstrap(self):
        state = self.install(phase="initialized", credentialsComplete=False)
        result = self.observe()
        self.assertEqual("installed", result["state"])
        self.assertEqual(state["installationId"], result["installationId"])
        self.assertEqual(state["operationId"], result["operationId"])
        self.assertEqual("unavailable", result["health"])
        self.assertNotIn("DO-NOT-EXPORT", json.dumps(result))
        self.assertEqual([["ps", "-aq"]], self.commands)

    def test_pending_upgrade_restore_fence_or_failed_maintenance_blocks_seed(self):
        self.install()
        for field in ("pendingUpgrade", "pendingRestore", "restoreInProgress", "heldFence"):
            with self.subTest(field=field):
                self.write("/opt/minicloud/maintenance.json", {"state": "upgraded", field: {}})
                self.assertEqual("reconciliation_required", self.observe()["state"])
        self.write("/opt/minicloud/maintenance.json", {"state": "started"})
        self.assertEqual("reconciliation_required", self.observe()["state"])
        self.write("/opt/minicloud/maintenance.json", {"state": "upgraded", "lastFailure": {"fenceReleased": False}})
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_completed_signed_maintenance_is_not_mistaken_for_active_upgrade(self):
        self.install()
        self.write("/opt/minicloud/maintenance.json", {
            "operation": "upgrade", "at": "2026-11-12", "state": "upgraded", "lastSuccessfulUpgrade": {}})
        self.assertEqual("installed", self.observe()["state"])

    def test_interrupted_installer_metadata_and_busy_shared_installer_lock_fail_closed(self):
        self.install()
        pending = self.write("/opt/minicloud/installation.json.pending", "{")
        self.assertEqual("reconciliation_required", self.observe()["state"])
        pending.unlink()
        lock = self.write("/opt/minicloud.installation.lock", "")
        with lock.open("r+") as stream:
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
            self.assertEqual("reconciliation_required", self.observe()["state"])
        self.assertEqual("installed", self.observe()["state"])

    def test_duplicate_json_and_symlinked_binding_fail_without_following_or_writing(self):
        self.bind()
        self.write("/var/lib/minicloud-seed/" + seed + "/preparation.json",
                   '{"schemaVersion":1,"schemaVersion":2}')
        self.assertEqual("reconciliation_required", self.observe()["state"])
        binding = self.root / "opt/minicloud.seed-operation"
        binding.unlink()
        binding.symlink_to(self.root / "var/lib/minicloud-seed" / seed / "operation")
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_occupied_host_with_missing_metadata_is_not_empty(self):
        (self.root / "opt/customer-data").mkdir(mode=0o700)
        self.assertEqual("reconciliation_required", self.observe()["state"])

    def test_stale_ready_metadata_never_means_live_ready_or_auto_unseal_after_reboot(self):
        self.install()
        for i, name in enumerate(("postgres", "openbao", "openbao-agent", "kratos", "api", "dashboard", "worker", "caddy")):
            self.containers.append({
                "Config": {"Labels": {"com.docker.compose.project": "minicloud",
                                      "com.docker.compose.project.working_dir": "/opt/minicloud/bundle",
                                      "com.docker.compose.service": name}},
                "State": {"Running": True, "Health": {"Status": "unhealthy" if name == "openbao" else "healthy"}}})
        result = self.observe()
        self.assertEqual("ready", result["progress"])
        self.assertEqual("unavailable", result["health"])
        self.assertEqual("unavailable", result["openBao"])
        self.assertEqual(["ps", "-aq"], self.commands[0])
        self.assertEqual("inspect", self.commands[1][0])


if __name__ == "__main__":
    unittest.main()
