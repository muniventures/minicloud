"""Verified host maintenance. Recovery-dependency archives are NOT full backups."""

import datetime
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import tarfile
import uuid


BLOCKER = (
    "Coordinated maintenance unavailable: this release has no production installation-wide "
    "management-write admission fence, complete accepted-work drain, or durable old-worker "
    "transfer fence. No services were stopped, migrations run, or production restore attempted. "
    "Do not infer idle from bootstrap status or stop accepted provisioning. "
    "backup-recovery protects independent OpenBao recovery dependencies ONLY, not application "
    "or identity databases, OpenBao persisted state, artifacts, installation secrets or TLS."
)
RECOVERY_FILES = {"openbao-ca.key", "openbao-recovery.json", "openbao-root-token"}
MAX_ARCHIVE = 16 * 1024 * 1024
MAX_FILE = 4 * 1024 * 1024


class Maintenance:
    def __init__(self, installer, primitives):
        self.installer = installer
        self.p = primitives
        self.args = installer.args
        self.root = installer.root
        self.state = installer.state
        self.status_path = self.root / "maintenance.json"

    def need(self, value, message):
        self.p["require"](value, message)

    def separate(self, path, *, directory=False):
        self.need(path is not None and path.is_absolute() and path == path.resolve(),
                  "Use a canonical absolute protected path.")
        self.p["protected"](path, directory)
        for namespace in (self.root, self.args.recovery_dir, self.args.assets_dir):
            self.need(not path.is_relative_to(namespace) and not namespace.is_relative_to(path),
                      "Keep archive, key and rehearsal paths outside installation/assets/recovery namespaces.")
        return path

    def crypto(self):
        self.need(shutil.which("age"), "Install age independently; authenticated age encryption is required.")

    def identity(self):
        path = self.separate(self.args.identity_file)
        self.need(self.args.backup_dir is None or not path.is_relative_to(self.args.backup_dir),
                  "Decryption identity must be outside the archive destination.")
        return path

    def source(self):
        directory = self.args.recovery_dir / self.state["installationId"]
        self.p["protected"](directory, True)
        self.need({path.name for path in directory.iterdir()} == RECOVERY_FILES,
                  "Independent recovery inventory differs; reconcile without skipping unknown files.")
        result = {}
        for name in sorted(RECOVERY_FILES):
            path = directory / name
            self.p["protected"](path)
            self.need(0 < path.stat().st_size <= MAX_FILE, "Recovery dependency is empty or oversized.")
            result[name] = path.read_bytes()
        self.check_material(result)
        return result

    def check_material(self, files):
        material = json.loads(files["openbao-recovery.json"],
                              object_pairs_hook=self.p["no_duplicates"])
        self.need(isinstance(material, dict)
                  and isinstance(material.get("unseal_keys_b64"), list)
                  and len(material["unseal_keys_b64"]) >= 2
                  and all(isinstance(key, str) and key for key in material["unseal_keys_b64"])
                  and isinstance(material.get("root_token"), str) and material["root_token"]
                  and files["openbao-root-token"].decode().strip() == material["root_token"],
                  "Incomplete or inconsistent original OpenBao recovery dependencies.")
        self.need(b"PRIVATE KEY-----" in files["openbao-ca.key"],
                  "Original independent OpenBao CA private key is missing.")
        private_public = self.p["run"](["openssl", "pkey", "-pubout"],
                                       input=files["openbao-ca.key"])
        certificate = self.root / "secrets/openbao-ca.crt"
        self.p["protected"](certificate)
        certificate_public = self.p["run"](["openssl", "x509", "-pubkey", "-noout"],
                                           input=certificate.read_bytes())
        self.need(private_public == certificate_public,
                  "Recovery CA key differs from the original installation certificate.")

    def pack(self, files):
        record = {
            "schemaVersion": 1, "kind": "minicloud-openbao-recovery-dependencies",
            "fullCoordinatedBackup": False, "backupId": str(uuid.uuid4()),
            "createdAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "installationId": self.state["installationId"], "operationId": self.state["operationId"],
            "releaseTag": self.state["releaseTag"], "sourceRevision": self.state["sourceRevision"],
            "bundleSha256": self.state["bundleSha256"],
            "files": {name: {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data)}
                      for name, data in files.items()},
        }
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w") as archive:
            for name, data in {"recovery.json": json.dumps(record, sort_keys=True).encode(), **files}.items():
                member = tarfile.TarInfo(name)
                member.mode = 0o600
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
        return record, buffer.getvalue()

    def unpack(self, data):
        self.need(len(data) <= MAX_ARCHIVE, "Decrypted recovery archive exceeds the bounded format.")
        files = {}
        with tarfile.open(fileobj=io.BytesIO(data), mode="r:") as archive:
            for member in archive:
                self.need(member.name in RECOVERY_FILES | {"recovery.json"}
                          and member.name not in files and member.isfile()
                          and 0 < member.size <= MAX_FILE,
                          "Unsafe, duplicate, incomplete or oversized recovery archive member.")
                files[member.name] = archive.extractfile(member).read()
        self.need(files.keys() == RECOVERY_FILES | {"recovery.json"},
                  "Recovery dependency archive inventory is incomplete.")
        record = json.loads(files.pop("recovery.json"), object_pairs_hook=self.p["no_duplicates"])
        fields = {"schemaVersion", "kind", "fullCoordinatedBackup", "backupId", "createdAt",
                  "installationId", "operationId", "releaseTag", "sourceRevision", "bundleSha256", "files"}
        self.need(isinstance(record, dict) and record.keys() == fields
                  and type(record["schemaVersion"]) is int and record["schemaVersion"] == 1
                  and record["kind"] == "minicloud-openbao-recovery-dependencies"
                  and record["fullCoordinatedBackup"] is False,
                  "Unknown recovery archive format; never accept it as a full backup.")
        for key in ("backupId", "installationId", "operationId"):
            self.need(isinstance(record[key], str) and str(uuid.UUID(record[key])) == record[key],
                      "Invalid recovery archive identity.")
        created = datetime.datetime.fromisoformat(record["createdAt"])
        self.need(created.tzinfo is not None, "Recovery timestamp must include its timezone.")
        for key in ("installationId", "operationId", "releaseTag", "sourceRevision", "bundleSha256"):
            self.need(record[key] == self.state[key],
                      "Recovery archive differs from the original installation or exact verified release.")
        self.need(isinstance(record["files"], dict) and record["files"].keys() == RECOVERY_FILES,
                  "Recovery digest inventory is incomplete.")
        for name, content in files.items():
            self.need(record["files"][name] == {
                "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)},
                "Recovery archive content digest/length differs.")
        self.check_material(files)
        return record, files

    def decrypt(self, path):
        self.crypto()
        self.separate(path)
        self.need(0 < path.stat().st_size <= MAX_ARCHIVE, "Encrypted recovery archive is empty or oversized.")
        # age authenticates the complete stream before unpacking or writing any plaintext.
        data = self.p["run"](["age", "--decrypt", "--identity", str(self.identity()), str(path)])
        return self.unpack(data)

    def backup_recovery(self):
        self.crypto()
        destination = self.separate(self.args.backup_dir, directory=True)
        recipient = self.separate(self.args.recipients_file)
        self.need(not recipient.is_relative_to(destination),
                  "Keep recipient/key configuration separate from archived output.")
        self.identity()
        record, plaintext = self.pack(self.source())
        self.need(shutil.disk_usage(destination).free >= MAX_ARCHIVE * 2,
                  "Insufficient recovery archive destination headroom; no automatic cleanup.")
        encrypted = self.p["run"](["age", "--encrypt", "--recipients-file", str(recipient)], input=plaintext)
        self.need(len(encrypted) <= MAX_ARCHIVE, "Encrypted recovery archive exceeds format limit.")
        # Verify the recipient/identity pairing and authenticated plaintext before publishing success.
        decoded = self.p["run"](["age", "--decrypt", "--identity", str(self.identity())], input=encrypted)
        verified, _ = self.unpack(decoded)
        self.need(verified == record, "Recovery encryption verification differs.")
        target = destination / (record["backupId"] + ".recovery.age")
        self.p["exclusive"](target, encrypted)
        return {"state": "recovery_dependencies_saved", "archive": str(target),
                "backupId": record["backupId"], "fullCoordinatedBackup": False}

    def rehearse(self):
        record, files = self.decrypt(self.args.archive)
        target = self.args.output_dir
        self.need(target is not None and target.is_absolute() and not target.exists()
                  and not target.is_symlink() and target == target.resolve(),
                  "Rehearsal output must be a new canonical directory.")
        self.separate(target.parent, directory=True)
        self.need(not self.args.identity_file.is_relative_to(target)
                  and not self.args.archive.is_relative_to(target),
                  "Rehearsal must not contain its decryption key or source archive.")
        # All authentication, inventory and exact-release checks finish before creating output.
        target.mkdir(mode=0o700)
        for name, content in files.items():
            self.p["exclusive"](target / name, content)
        return {"state": "recovery_dependencies_rehearsed", "backupId": record["backupId"],
                "output": str(target), "servicesStarted": False, "fullCoordinatedBackup": False}

    def retention(self):
        directory = self.separate(self.args.backup_dir, directory=True)
        self.need(self.args.keep is not None and 1 <= self.args.keep <= 10000,
                  "Retention requires --keep between 1 and 10000.")
        records = []
        for path in directory.iterdir():
            self.need(re.fullmatch(r"[0-9a-f-]{36}\.recovery\.age", path.name),
                      "Unknown file in recovery archive destination; no pruning inferred.")
            record, _ = self.decrypt(path)
            self.need(path.name == record["backupId"] + ".recovery.age",
                      "Recovery filename differs from its authenticated backup identity.")
            records.append((record["createdAt"], str(path), record["backupId"]))
        records.sort(reverse=True)
        return {"state": "retention_plan", "keep": self.args.keep,
                "candidates": [{"archive": path, "backupId": identity}
                               for _, path, identity in records[self.args.keep:]],
                "removed": 0, "fullCoordinatedBackup": False}

    def remove(self):
        record, _ = self.decrypt(self.args.archive)
        self.need(self.args.confirm_backup_id == record["backupId"],
                  "Removal requires the exact authenticated --confirm-backup-id.")
        self.need(self.args.archive.name == record["backupId"] + ".recovery.age",
                  "Removal requires the exact archive identity filename.")
        self.args.archive.unlink()
        self.p["sync_directory"](self.args.archive.parent)
        return {"state": "recovery_archive_removed", "backupId": record["backupId"],
                "installationDataRemoved": False}

    def schedule(self):
        destination = self.separate(self.args.output_dir, directory=True)
        self.separate(self.args.backup_dir, directory=True)
        self.separate(self.args.recipients_file)
        self.identity()
        self.need(re.fullmatch(r"(?:[01][0-9]|2[0-3]):[0-5][0-9]", self.args.at or ""),
                  "Specify daily UTC --at HH:MM.")
        installer = self.args.assets_dir / "install-server.sh"
        self.p["protected"](installer)
        command = ["/bin/sh", str(installer), "--tag", self.args.tag,
                   "--assets-dir", str(self.args.assets_dir), "maintenance", "backup-recovery",
                   "--root", str(self.root), "--config", str(self.args.config),
                   "--recovery-dir", str(self.args.recovery_dir),
                   "--backup-dir", str(self.args.backup_dir),
                   "--recipients-file", str(self.args.recipients_file),
                   "--identity-file", str(self.args.identity_file)]
        for address in self.args.public_address:
            command.extend(("--public-address", address))
        self.need(all(not any(ord(char) < 32 for char in item) for item in command),
                  "Control characters are forbidden in scheduling paths.")
        quoted = " ".join(json.dumps(item).replace("%", "%%").replace("$", "$$") for item in command)
        service = (
            "[Unit]\nDescription=Minicloud independent recovery dependencies ONLY (not full backup)\n"
            "[Service]\nType=oneshot\nUser=root\nUMask=0077\n"
            "NoNewPrivileges=true\nPrivateTmp=true\nExecStart=" + quoted + "\n"
        )
        timer = ("[Unit]\nDescription=Daily Minicloud recovery-dependency archive\n"
                 "[Timer]\nOnCalendar=*-*-* " + self.args.at + ":00 UTC\nPersistent=true\n"
                 "[Install]\nWantedBy=timers.target\n")
        for suffix in ("service", "timer"):
            self.need(not (destination / ("minicloud-recovery." + suffix)).exists(),
                      "Scheduling output exists; never overwrite installed/operator units.")
        self.p["exclusive"](destination / "minicloud-recovery.service", service.encode())
        self.p["exclusive"](destination / "minicloud-recovery.timer", timer.encode())
        return {"state": "schedule_authored_not_installed", "fullCoordinatedBackup": False}

    def execute(self):
        operation = self.args.maintenance_operation
        if operation == "status":
            result = {"state": "coordinated_maintenance_blocked", "fullCoordinatedBackup": False,
                      "admissionFence": "unavailable", "acceptedWorkDrain": "unavailable",
                      "productionTransferFence": "unavailable", "reason": BLOCKER}
            if self.status_path.exists():
                self.p["protected"](self.status_path)
                result["lastOperation"] = self.p["read_json"](self.status_path)
            return result
        history = {}
        if self.status_path.exists():
            self.p["protected"](self.status_path)
            previous = self.p["read_json"](self.status_path)
            self.need(isinstance(previous, dict), "Unknown maintenance status; reconcile without overwriting.")
            history = {key: previous[key] for key in ("lastSuccessfulRecovery", "lastFailure")
                       if key in previous}
        record = history | {
            "operation": operation, "at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "state": "started", "fullCoordinatedBackup": False}
        self.p["atomic_json"](self.status_path, record)
        try:
            if operation in ("backup", "upgrade", "restore", "transfer", "stop", "remove"):
                self.need(False, BLOCKER)
            if operation == "backup-recovery":
                result = self.backup_recovery()
            elif operation == "verify-recovery":
                archive, _ = self.decrypt(self.args.archive)
                result = {"state": "recovery_dependencies_verified", "backupId": archive["backupId"],
                          "fullCoordinatedBackup": False}
            elif operation == "rehearse-recovery":
                result = self.rehearse()
            elif operation == "retention-plan":
                result = self.retention()
            elif operation == "remove-recovery":
                result = self.remove()
            elif operation == "schedule-recovery":
                result = self.schedule()
            else:
                self.need(False, "Unknown maintenance operation.")
        except (self.p["InstallError"], OSError, ValueError, KeyError, TypeError, tarfile.TarError,
                self.p["subprocess"].TimeoutExpired):
            self.p["atomic_json"](self.status_path, record | {
                "state": "failed", "error": "Operation failed closed; no full coordinated backup or restore succeeded.",
                "lastFailure": {"operation": operation, "at": record["at"]}})
            raise
        if operation == "backup-recovery":
            record["lastSuccessfulRecovery"] = {
                "at": record["at"], "archive": result["archive"], "backupId": result["backupId"],
                "fullCoordinatedBackup": False}
        self.p["atomic_json"](self.status_path, record | {"state": result["state"]})
        return result


def execute(installer, primitives):
    return Maintenance(installer, primitives).execute()
