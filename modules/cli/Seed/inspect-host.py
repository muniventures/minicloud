# SPDX-License-Identifier: MPL-2.0
"""Observational seed inspector. Never import or execute installed host code."""
import fcntl
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import uuid


def inspect_host(seed, base=pathlib.Path("/"), owner=0, run=subprocess.run, acquire_lock=True):
    def path(name):
        return base / name.lstrip("/")

    def protected(p, directory=False):
        for parent in (p, *p.parents):
            info = parent.lstat()
            assert not stat.S_ISLNK(info.st_mode) and info.st_uid in (0, owner)
            assert not info.st_mode & 0o022
            if parent == p and not directory:
                assert info.st_uid == owner
                assert stat.S_ISREG(info.st_mode) and stat.S_IMODE(info.st_mode) in (0o400, 0o600)
                assert info.st_size < 1048576
            else:
                assert stat.S_ISDIR(info.st_mode)

    def read(p):
        protected(p)
        return p.read_text()

    def exists(p):
        return p.exists() or p.is_symlink()

    def unique(pairs):
        result = {}
        for key, value in pairs:
            assert key not in result
            result[key] = value
        return result

    def document(p):
        value = json.loads(read(p), object_pairs_hook=unique)
        assert isinstance(value, dict)
        return value

    lock = path("/opt/minicloud.installation.lock")
    descriptor = None
    try:
        if acquire_lock and exists(lock):
            protected(lock)
            descriptor = os.open(lock, os.O_RDONLY | os.O_NOFOLLOW)
            fcntl.flock(descriptor, fcntl.LOCK_SH | fcntl.LOCK_NB)
        root = path("/opt/minicloud")
        binding = path("/opt/minicloud.seed-operation")
        assert not exists(binding.with_name(binding.name + ".pending"))
        preparation = path("/var/lib/minicloud-seed") / seed
        if exists(preparation.parent):
            protected(preparation.parent, True)
            assert not any(item.name != seed for item in preparation.parent.iterdir())
        bound = exists(binding)
        if bound:
            assert read(binding).strip() == seed
        prepared = False
        identity = None
        if exists(preparation):
            protected(preparation, True)
            assert stat.S_IMODE(preparation.lstat().st_mode) == 0o700
            assert not any(item.name.endswith((".pending", ".partial")) for item in preparation.rglob("*"))
            assert read(preparation / "operation").strip() == seed
            assert bound
            checkpoint = preparation / "preparation.json"
            if exists(checkpoint):
                record = document(checkpoint)
                assert type(record.get("schemaVersion")) is int and record["schemaVersion"] == 1 and record.get("seedId") == seed
                assert record.get("phase") == "prepared"
                identity = record["identity"]
                assert isinstance(identity, dict) and set(identity) == {
                    "provider", "providerAccount", "serverId", "region", "size", "image", "architecture"}
                assert all(isinstance(value, str) and 0 < len(value) <= 200 for value in identity.values())
                prepared = True
        elif bound:
            raise ValueError("orphaned binding")
        state = None
        if exists(root):
            protected(root, True)
            assert stat.S_IMODE(root.lstat().st_mode) == 0o700
            assert not any(exists(root / name) for name in ("installation.json.pending", "maintenance.json.pending"))
            state = document(root / "installation.json")
            assert bound and exists(preparation)
            assert type(state.get("schemaVersion")) is int and state["schemaVersion"] == 1 and state.get("product") == "minicloud"
            assert state.get("root") == "/opt/minicloud"
            assert state.get("recoveryDir") == "/var/lib/minicloud-seed/" + seed + "/recovery"
            for key in ("installationId", "operationId"):
                assert str(uuid.UUID(state[key])) == state[key]
            assert re.fullmatch(r"server-v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?", state["releaseTag"])
            assert re.fullmatch(r"[a-f0-9]{40}", state["sourceRevision"])
            assert state["phase"] in ("initializing", "prepared", "initialized", "awaiting_dns_tls", "awaiting_operator", "ready")
            assert type(state["credentialsComplete"]) is bool
            maintenance = root / "maintenance.json"
            if exists(maintenance):
                record = document(maintenance)
                assert not any(key in record for key in ("pendingUpgrade", "pendingRestore", "restoreInProgress", "heldFence", "error"))
                # Unknown records cannot establish that maintenance is idle.
                assert set(record) <= {"operation", "at", "state", "lastSuccessfulBackup", "lastSuccessfulUpgrade",
                                       "lastSuccessfulRecovery", "lastFailure"}
                assert record.get("state") in ("backup_saved", "backup_verified", "backup_removed", "upgraded",
                                               "restore_confirmed", "fence_released", "maintenance_idle",
                                               "recovery_dependencies_verified", "recovery_dependencies_saved",
                                               "recovery_dependencies_rehearsed", "retention_plan", "recovery_archive_removed",
                                               "schedule_authored_not_installed")
                if "lastFailure" in record:
                    assert isinstance(record["lastFailure"], dict) and record["lastFailure"].get("fenceReleased") is True
        else:
            protected(path("/opt"), True)
            assert not any(p.name not in ("minicloud.seed-operation", "minicloud.installation.lock")
                           for p in path("/opt").iterdir())

        services = {}
        docker = shutil.which("docker")
        if docker:
            listing = run([docker, "ps", "-aq"], capture_output=True, timeout=20, check=False)
            assert listing.returncode == 0 or state is not None
            ids = listing.stdout.decode().split() if listing.returncode == 0 else []
            assert all(re.fullmatch(r"[a-f0-9]{12,64}", item) for item in ids)
            if not state:
                assert not ids
                volumes = run([docker, "volume", "ls", "-q"], capture_output=True, timeout=20, check=False)
                assert volumes.returncode == 0 and not volumes.stdout.strip()
            elif ids:
                containers = run([docker, "inspect", *ids], capture_output=True, timeout=20, check=False)
                assert containers.returncode == 0
                for container in json.loads(containers.stdout):
                    labels = container.get("Config", {}).get("Labels") or {}
                    if labels.get("com.docker.compose.project") != "minicloud":
                        continue
                    assert labels.get("com.docker.compose.project.working_dir") == "/opt/minicloud/bundle"
                    name = labels.get("com.docker.compose.service")
                    status = container.get("State", {})
                    assert name not in services
                    services[name] = status.get("Running") is True and (name in ("worker", "caddy")
                        or status.get("Health", {}).get("Status") == "healthy")
        if state is None:
            return {"state": "preparing" if bound else "empty", "progress": "prepared" if prepared else "pending",
                    "prepared": prepared, "identity": identity}
        required = ("postgres", "openbao", "openbao-agent", "kratos", "api", "dashboard", "worker", "caddy")
        healthy = all(services.get(name) for name in required)
        return {"state": "installed", "progress": state["phase"], "installationId": state["installationId"],
                "operationId": state["operationId"], "release": state["releaseTag"], "source": state["sourceRevision"],
                "health": "pending" if healthy else "unavailable",
                "openBao": "ready" if services.get("openbao") else "unavailable",
                "browser": "unknown", "prepared": True,
                "configuration": {key: state["configuration"][key] for key in ("consoleUrl", "apiUrl", "appDomain")},
                "publicAddresses": state["publicAddresses"], "identity": identity}
    except (AssertionError, KeyError, ValueError, OSError, subprocess.SubprocessError, TypeError):
        return {"state": "reconciliation_required", "progress": "unknown"}
    finally:
        if descriptor is not None:
            os.close(descriptor)


if __name__ == "__main__":
    assert len(sys.argv) == 2 and re.fullmatch("[a-f0-9]{32}", sys.argv[1])
    print(json.dumps(inspect_host(sys.argv[1])))
