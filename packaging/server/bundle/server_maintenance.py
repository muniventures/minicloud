"""Verified host maintenance.

Coordinated backup, upgrade, restore and transfer use the durable maintenance authority
(`dotnet Minicloud.Api.dll maintenance ...`) run inside the API image. Independent
recovery-dependency archives (`*-recovery`) remain available and are NOT full backups.
"""

import datetime
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import time
import uuid


RECOVERY_FILES = {"openbao-ca.key", "openbao-recovery.json", "openbao-root-token"}
MAX_ARCHIVE = 16 * 1024 * 1024
MAX_FILE = 4 * 1024 * 1024
CHUNK = 1024 * 1024
FULL_KIND = "minicloud-coordinated-backup"
DATABASES = ("minicloud", "kratos")
VOLUMES = ("openbao", "artifacts", "caddy-data", "caddy-config")
COMPONENTS = (tuple(f"postgres-{name}.dump" for name in DATABASES)
              + tuple(f"volume-{name}.tar" for name in VOLUMES) + ("installation.tar", "recovery.tar"))
RELEASE_FIELDS = ("releaseTag", "serverVersion", "sourceRevision", "images", "dependencies", "bundleSha256",
                  "configurationSchemaVersion", "identitySchemaVersion", "applicationMigration")
MAINTENANCE_MIGRATION = "20261111134441_MaintenanceAuthority"
POLL_SECONDS = 10
UNCERTAIN_GRACE_SECONDS = 60
BACKUP_HEADROOM = 2 * 1024**3
APPLICATION_SERVICES = ("worker", "api", "dashboard", "kratos")
UPGRADE_PHASES = ("started", "fenced", "drained", "backed_up", "stopped", "switched", "dependencies",
                  "migrating", "migrated", "started_services", "verified")
TRANSFER_LIMITATION = (
    "Transfer revokes old processes only when they next reach THIS database. An old host running "
    "on its own restored or copied database, or an unreachable old host that still holds live "
    "provider, DNS or SSH credentials, is NOT stopped. Shut the old host down or rotate those "
    "credentials before relying on this installation.")


class Maintenance:
    def __init__(self, installer, primitives):
        self.i = installer
        self.p = primitives
        self.args = installer.args
        self.root = installer.root
        self.status_path = self.root / "maintenance.json"
        self.record = {}

    @property
    def state(self):
        return self.i.state

    def need(self, value, message):
        self.p["require"](value, message)

    # ----------------------------------------------------------------- shared path/crypto guards

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
        self.need(self.args.archive is None or not path.is_relative_to(self.args.archive),
                  "Decryption identity must be outside the archive.")
        return path

    def capture_targets(self):
        self.crypto()
        destination = self.separate(self.args.backup_dir, directory=True)
        recipient = self.separate(self.args.recipients_file)
        self.need(not recipient.is_relative_to(destination),
                  "Keep recipient/key configuration separate from archived output.")
        return destination, recipient, self.identity()

    @staticmethod
    def tool_environment():
        return {key: value for key, value in os.environ.items() if key in ("PATH", "HOME", "LANG")}

    @staticmethod
    def file_sha256(path):
        with path.open("rb") as source:
            return hashlib.file_digest(source, "sha256").hexdigest()

    @staticmethod
    def stop(*processes):
        for process in processes:
            if process is None:
                continue
            if process.poll() is None:
                process.kill()
                process.wait()
            for pipe in (process.stdin, process.stdout):
                if pipe is not None and not pipe.closed:
                    try:
                        pipe.close()
                    except BrokenPipeError:
                        pass

    def encrypt_stream(self, target, recipient, source):
        """Encrypt bytes or a producer command's stdout into a new file; return plaintext digests."""
        digest = hashlib.sha256()
        size = 0
        producer = None
        fd = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, "wb") as output:
            age = subprocess.Popen(["age", "--encrypt", "--recipients-file", str(recipient)],
                                   stdin=subprocess.PIPE, stdout=output, stderr=subprocess.DEVNULL,
                                   env=self.tool_environment())
            try:
                if isinstance(source, bytes):
                    chunks = (source[index:index + CHUNK] for index in range(0, len(source), CHUNK))
                else:
                    command, env = source
                    producer = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                                stderr=subprocess.DEVNULL, env=env)
                    chunks = iter(lambda: producer.stdout.read(CHUNK), b"")
                for chunk in chunks:
                    digest.update(chunk)
                    size += len(chunk)
                    age.stdin.write(chunk)
                age.stdin.close()
                produced = 0 if producer is None else producer.wait()
                encrypted = age.wait()
            except BrokenPipeError:
                produced = encrypted = 1
            finally:
                self.stop(producer, age)
            output.flush()
            os.fsync(output.fileno())
        self.need(produced == 0 and encrypted == 0 and size > 0,
                  f"Capture of {target.name} failed or was empty; the backup is incomplete and was not published.")
        return {"sha256": digest.hexdigest(), "bytes": size, "ciphertextSha256": self.file_sha256(target)}

    def decrypt_stream(self, path, identity, sink=None, limit=None):
        """Authenticated decryption; plaintext is hashed and optionally piped to a sink command or collected."""
        digest = hashlib.sha256()
        size = 0
        collected = [] if limit else None
        age = subprocess.Popen(["age", "--decrypt", "--identity", str(identity), str(path)],
                               stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                               env=self.tool_environment())
        consumer = None
        try:
            if sink is not None:
                command, env = sink
                consumer = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL,
                                            stderr=subprocess.DEVNULL, env=env)
            for chunk in iter(lambda: age.stdout.read(CHUNK), b""):
                digest.update(chunk)
                size += len(chunk)
                if collected is not None:
                    self.need(size <= limit, "Decrypted component exceeds its bounded format.")
                    collected.append(chunk)
                if consumer is not None:
                    consumer.stdin.write(chunk)
            decrypted = age.wait()
            consumed = 0
            if consumer is not None:
                consumer.stdin.close()
                consumed = consumer.wait()
        except BrokenPipeError:
            # The sink exited early; stop age instead of waiting on its full pipe.
            decrypted, consumed = 0, 1
        finally:
            self.stop(consumer, age)
        self.need(decrypted == 0, f"Authenticated decryption of {path.name} failed: tampered, truncated or wrong identity.")
        self.need(consumed == 0, f"Restoring {path.name} failed; the installation stays fenced.")
        return digest.hexdigest(), size, (b"".join(collected) if collected is not None else None)

    # ------------------------------------------------------------- recovery-dependency material

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

    def check_material(self, files, certificate=None):
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
        if certificate is None:
            path = self.root / "secrets/openbao-ca.crt"
            self.p["protected"](path)
            certificate = path.read_bytes()
        certificate_public = self.p["run"](["openssl", "x509", "-pubkey", "-noout"], input=certificate)
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

    def unpack(self, data, state=None, certificate=None):
        state = self.state if state is None else state
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
            self.need(record[key] == state[key],
                      "Recovery archive differs from the original installation or exact verified release.")
        self.need(isinstance(record["files"], dict) and record["files"].keys() == RECOVERY_FILES,
                  "Recovery digest inventory is incomplete.")
        for name, content in files.items():
            self.need(record["files"][name] == {
                "sha256": hashlib.sha256(content).hexdigest(), "bytes": len(content)},
                "Recovery archive content digest/length differs.")
        self.check_material(files, certificate)
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
        identity = self.identity()
        recovery, full, incomplete = [], [], []
        for path in directory.iterdir():
            if re.fullmatch(r"[0-9a-f-]{36}\.recovery\.age", path.name):
                record, _ = self.decrypt(path)
                self.need(path.name == record["backupId"] + ".recovery.age",
                          "Recovery filename differs from its authenticated backup identity.")
                recovery.append((record["createdAt"], str(path), record["backupId"]))
            elif re.fullmatch(r"[0-9a-f-]{36}\.backup", path.name):
                record = self.read_backup(path, identity)
                full.append((record["createdAt"], str(path), record["backupId"]))
            elif re.fullmatch(r"[0-9a-f-]{36}\.partial", path.name):
                incomplete.append(str(path))
            else:
                self.need(False, "Unknown file in archive destination; no pruning inferred.")
        recovery.sort(reverse=True)
        full.sort(reverse=True)
        return {"state": "retention_plan", "keep": self.args.keep,
                "candidates": [{"archive": path, "backupId": identity}
                               for _, path, identity in recovery[self.args.keep:]],
                "fullBackupCandidates": [{"archive": path, "backupId": identity}
                                         for _, path, identity in full[self.args.keep:]],
                "incompleteCaptures": sorted(incomplete),
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

    def schedule(self, operation):
        destination = self.separate(self.args.output_dir, directory=True)
        self.separate(self.args.backup_dir, directory=True)
        self.separate(self.args.recipients_file)
        self.identity()
        self.need(re.fullmatch(r"(?:[01][0-9]|2[0-3]):[0-5][0-9]", self.args.at or ""),
                  "Specify daily UTC --at HH:MM.")
        installer = self.args.assets_dir / "install-server.sh"
        self.p["protected"](installer)
        command = ["/bin/sh", str(installer), "--tag", self.args.tag,
                   "--assets-dir", str(self.args.assets_dir), "maintenance", operation,
                   "--root", str(self.root), "--config", str(self.args.config),
                   "--recovery-dir", str(self.args.recovery_dir),
                   "--backup-dir", str(self.args.backup_dir),
                   "--recipients-file", str(self.args.recipients_file),
                   "--identity-file", str(self.args.identity_file)]
        if operation == "backup":
            command.extend(("--drain-timeout", str(self.args.drain_timeout)))
        for address in self.args.public_address:
            command.extend(("--public-address", address))
        self.need(all(not any(ord(char) < 32 for char in item) for item in command),
                  "Control characters are forbidden in scheduling paths.")
        quoted = " ".join(json.dumps(item).replace("%", "%%").replace("$", "$$") for item in command)
        if operation == "backup":
            unit, description = "minicloud-backup", "Minicloud coordinated full backup (fence, drain, capture, release)"
        else:
            unit, description = "minicloud-recovery", "Minicloud independent recovery dependencies ONLY (not full backup)"
        service = (
            f"[Unit]\nDescription={description}\n"
            "[Service]\nType=oneshot\nUser=root\nUMask=0077\n"
            "NoNewPrivileges=true\nPrivateTmp=true\nExecStart=" + quoted + "\n"
        )
        timer = (f"[Unit]\nDescription=Daily {description}\n"
                 "[Timer]\nOnCalendar=*-*-* " + self.args.at + ":00 UTC\nPersistent=true\n"
                 "[Install]\nWantedBy=timers.target\n")
        for suffix in ("service", "timer"):
            self.need(not (destination / (unit + "." + suffix)).exists(),
                      "Scheduling output exists; never overwrite installed/operator units.")
        self.p["exclusive"](destination / (unit + ".service"), service.encode())
        self.p["exclusive"](destination / (unit + ".timer"), timer.encode())
        return {"state": "schedule_authored_not_installed", "units": [unit + ".service", unit + ".timer"],
                "fullCoordinatedBackup": operation == "backup"}

    # ------------------------------------------------------------------- maintenance authority

    def authority(self, operation, operation_id=None, host_id=None):
        """Run the host command inside the API image; protected input travels on stdin only."""
        payload = {"installationId": self.state["installationId"]}
        if operation_id is not None:
            payload["operationId"] = operation_id
        if host_id is not None:
            payload["hostId"] = host_id
        output = self.i.compose("run", "--rm", "-T", "--no-deps", "api-bootstrap", "maintenance", operation,
                                "--stdin", input=json.dumps(payload).encode(), allowed=(0, 2, 3))
        lines = [line for line in output.decode().splitlines() if line.strip()]
        self.need(lines, "Maintenance authority returned no result.")
        value = json.loads(lines[-1], object_pairs_hook=self.p["no_duplicates"])
        self.need(isinstance(value, dict) and value.get("operation") == operation
                  and value.get("result") in ("ok", "drained", "not_drained", "refused"),
                  "Unexpected maintenance authority response.")
        return value

    def authority_ok(self, operation, operation_id=None, host_id=None):
        value = self.authority(operation, operation_id, host_id)
        self.need(value["result"] != "refused",
                  f"Maintenance authority refused {operation} ({value.get('code')}): {value.get('message')}")
        return value

    def fence(self, operation_id, operation):
        value = self.authority_ok("fence", operation_id)
        self.record["heldFence"] = {"operationId": operation_id, "operation": operation,
                                    "at": self.now(), "epoch": value["authority"]["epoch"]}
        self.persist()
        return value["authority"]

    def release(self, operation_id):
        value = self.authority_ok("release", operation_id)
        if self.record.get("heldFence", {}).get("operationId") == operation_id:
            self.record.pop("heldFence")
            self.persist()
        return value["authority"]

    def try_release(self, operation_id):
        try:
            self.release(operation_id)
            return True
        except (self.p["InstallError"], OSError, ValueError, KeyError, TypeError,
                subprocess.TimeoutExpired):
            return False

    def drain(self, operation_id):
        """Wait (bounded) for accepted work to finish. Never stops or cancels accepted work."""
        timeout = self.args.drain_timeout
        self.need(type(timeout) is int and 1 <= timeout <= 86400, "--drain-timeout must be 1-86400 seconds.")
        deadline = time.monotonic() + timeout
        uncertain_since = None
        while True:
            value = self.authority("drain-status", operation_id)
            self.need(value["result"] != "refused",
                      f"Drain status refused ({value.get('code')}): {value.get('message')}")
            status = value.get("status")
            self.need(isinstance(status, dict) and isinstance(status.get("authority"), dict),
                      "Unexpected drain status response.")
            if value["result"] == "drained":
                return status
            authority = status["authority"]
            self.need(authority.get("state") == "fenced" and authority.get("holderOperationId") == operation_id
                      and authority.get("localHostAuthorized") is True,
                      "This operation no longer holds an authorized fence; nothing further was changed.")
            blocking = [item for item in status.get("work") or [] if item.get("blocksDrain")]
            summary = ", ".join(f"{item.get('category')}/{item.get('state')}={item.get('count')}"
                                for item in blocking) or "unknown"
            now = time.monotonic()
            uncertain = any(item.get("state") == "uncertain" for item in blocking)
            uncertain_since = (uncertain_since or now) if uncertain else None
            self.need(uncertain_since is None or now - uncertain_since < UNCERTAIN_GRACE_SECONDS,
                      f"Drain reports uncertain work ({summary}); reconcile it explicitly before maintenance. "
                      "No accepted work was stopped.")
            self.need(now < deadline,
                      f"Accepted work did not drain within {timeout}s ({summary}). "
                      "No accepted work was stopped; retry later or raise --drain-timeout.")
            time.sleep(max(0, min(POLL_SECONDS, deadline - now)))

    def ensure_host_id(self):
        path = self.root / "host-id"
        if not path.exists():
            self.p["exclusive"](path, ("host-" + str(uuid.uuid4()) + "\n").encode())
        return self.i.host_id()

    # --------------------------------------------------------------- local maintenance record

    @staticmethod
    def now():
        return datetime.datetime.now(datetime.timezone.utc).isoformat()

    def load_record(self):
        if self.status_path.exists():
            self.p["protected"](self.status_path)
            previous = self.p["read_json"](self.status_path)
            self.need(isinstance(previous, dict), "Unknown maintenance status; reconcile without overwriting.")
            self.record = previous

    def persist(self):
        if self.root.exists():
            self.p["atomic_json"](self.status_path, self.record)

    def pending(self):
        return {key: self.record[key] for key in ("pendingUpgrade", "pendingRestore", "restoreInProgress")
                if key in self.record}

    # ------------------------------------------------------------------- coordinated backup

    def volume_capture(self, volume):
        image = self.state["dependencies"]["postgres"]
        return (["docker", "run", "--rm", "--pull", "never", "--network", "none",
                 "--volume", f"minicloud_{volume}:/source:ro", "--entrypoint", "tar", image,
                 "-C", "/source", "-cf", "-", "."], self.i.docker_environment())

    def volume_restore(self, volume):
        image = self.state["dependencies"]["postgres"]
        return (["docker", "run", "--rm", "-i", "--pull", "never", "--network", "none",
                 "--volume", f"minicloud_{volume}:/target", "--entrypoint", "sh", image,
                 "-c", "find /target -mindepth 1 -delete && tar -C /target -xpf -"], self.i.docker_environment())

    def installation_bytes(self):
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w") as archive:
            paths = [self.i.state_path] + sorted(path for path in (self.root / "secrets").rglob("*") if path.is_file())
            for path in paths:
                self.p["protected"](path)
                data = path.read_bytes()
                member = tarfile.TarInfo(path.relative_to(self.root).as_posix())
                member.mode = 0o600
                member.size = len(data)
                archive.addfile(member, io.BytesIO(data))
        data = buffer.getvalue()
        self.need(len(data) <= MAX_ARCHIVE, "Installation credentials exceed the bounded format.")
        return data

    def capture(self, operation_id, epoch):
        """Full encrypted capture. Caller holds a drained fence."""
        destination, recipient, identity = self.capture_targets()
        self.need(shutil.disk_usage(destination).free >= BACKUP_HEADROOM,
                  "Less than 2 GiB free at the backup destination; no automatic cleanup.")
        backup_id = str(uuid.uuid4())
        staging = destination / (backup_id + ".partial")
        staging.mkdir(mode=0o700)
        try:
            components = {}
            for name in DATABASES:
                components[f"postgres-{name}.dump"] = self.encrypt_stream(
                    staging / f"postgres-{name}.dump.age", recipient, self.i.compose_invocation(
                        "exec", "-T", "postgres", "pg_dump", "--username", "postgres",
                        "--format", "custom", "--dbname", name))
            # Pausing (not stopping) keeps OpenBao unsealed while its file storage is copied.
            self.i.compose("pause", "openbao")
            try:
                components["volume-openbao.tar"] = self.encrypt_stream(
                    staging / "volume-openbao.tar.age", recipient, self.volume_capture("openbao"))
            finally:
                self.i.compose("unpause", "openbao")
            for name in VOLUMES[1:]:
                components[f"volume-{name}.tar"] = self.encrypt_stream(
                    staging / f"volume-{name}.tar.age", recipient, self.volume_capture(name))
            components["installation.tar"] = self.encrypt_stream(
                staging / "installation.tar.age", recipient, self.installation_bytes())
            components["recovery.tar"] = self.encrypt_stream(
                staging / "recovery.tar.age", recipient, self.pack(self.source())[1])
            record = {
                "schemaVersion": 1, "kind": FULL_KIND, "fullCoordinatedBackup": True,
                "backupId": backup_id, "createdAt": self.now(),
                "installationId": self.state["installationId"], "maintenanceOperationId": operation_id,
                "authorityEpoch": epoch, **{key: self.state[key] for key in RELEASE_FIELDS},
                "components": components,
            }
            self.encrypt_stream(staging / "manifest.age", recipient,
                                json.dumps(record, sort_keys=True).encode())
            # Authenticate every component with the escrowed identity before publishing success.
            verified = self.verify_backup(staging, identity)[0]
            self.need(verified == record, "Backup verification differs from the capture.")
            target = destination / (backup_id + ".backup")
            os.rename(staging, target)
            self.p["sync_directory"](destination)
        except BaseException:
            shutil.rmtree(staging, ignore_errors=True)
            raise
        return {"backupId": backup_id, "archive": str(target), "createdAt": record["createdAt"]}

    def read_backup(self, directory, identity):
        self.need(re.fullmatch(r"[0-9a-f-]{36}\.(?:backup|partial)", directory.name) is not None,
                  "Backup directory name must be <backupId>.backup.")
        self.p["protected"](directory, True)
        expected = {"manifest.age"} | {name + ".age" for name in COMPONENTS}
        self.need({path.name for path in directory.iterdir()} == expected,
                  "Backup inventory is incomplete or contains unknown files.")
        for name in expected:
            self.p["protected"](directory / name)
        _, _, data = self.decrypt_stream(directory / "manifest.age", identity, limit=MAX_FILE)
        record = json.loads(data, object_pairs_hook=self.p["no_duplicates"])
        fields = {"schemaVersion", "kind", "fullCoordinatedBackup", "backupId", "createdAt", "installationId",
                  "maintenanceOperationId", "authorityEpoch", "components", *RELEASE_FIELDS}
        self.need(isinstance(record, dict) and record.keys() == fields
                  and type(record["schemaVersion"]) is int and record["schemaVersion"] == 1
                  and record["kind"] == FULL_KIND and record["fullCoordinatedBackup"] is True
                  and type(record["authorityEpoch"]) is int,
                  "Unknown backup format.")
        for key in ("backupId", "installationId", "maintenanceOperationId"):
            self.need(isinstance(record[key], str) and str(uuid.UUID(record[key])) == record[key],
                      "Invalid backup identity.")
        self.need(directory.name.split(".")[0] == record["backupId"],
                  "Backup directory differs from its authenticated identity.")
        self.need(datetime.datetime.fromisoformat(record["createdAt"]).tzinfo is not None,
                  "Backup timestamp must include its timezone.")
        components = record["components"]
        self.need(isinstance(components, dict) and components.keys() == set(COMPONENTS)
                  and all(isinstance(item, dict) and item.keys() == {"sha256", "bytes", "ciphertextSha256"}
                          and type(item["bytes"]) is int and item["bytes"] > 0
                          for item in components.values()),
                  "Backup component inventory is incomplete.")
        return record

    def verify_backup(self, directory, identity):
        """Authenticate every component before any mutation; returns record and small components."""
        record = self.read_backup(directory, identity)
        small = {}
        for name in COMPONENTS:
            path = directory / (name + ".age")
            expected = record["components"][name]
            self.need(self.file_sha256(path) == expected["ciphertextSha256"],
                      f"Backup component {name} ciphertext differs from the manifest.")
            limit = MAX_ARCHIVE if name in ("installation.tar", "recovery.tar") else None
            digest, size, data = self.decrypt_stream(path, identity, limit=limit)
            self.need(digest == expected["sha256"] and size == expected["bytes"],
                      f"Backup component {name} content differs from the manifest.")
            if data is not None:
                small[name] = data
        installation = {}
        with tarfile.open(fileobj=io.BytesIO(small["installation.tar"]), mode="r:") as archive:
            for member in archive:
                name = member.name
                self.need(member.isfile() and name not in installation and 0 < member.size <= MAX_FILE
                          and (name == "installation.json" or name.startswith("secrets/"))
                          and not Path(name).is_absolute() and ".." not in Path(name).parts,
                          "Unsafe installation component member.")
                installation[name] = archive.extractfile(member).read()
        self.need("installation.json" in installation, "Backup lacks installation metadata.")
        state = json.loads(installation["installation.json"], object_pairs_hook=self.p["no_duplicates"])
        self.need(isinstance(state, dict) and state.get("product") == "minicloud"
                  and state.get("installationId") == record["installationId"]
                  and all(state.get(key) == record[key] for key in RELEASE_FIELDS)
                  and state.get("credentialsComplete") is True,
                  "Backup installation metadata differs from its manifest.")
        self.need("secrets/openbao-ca.crt" in installation, "Backup lacks the OpenBao CA certificate.")
        _, recovery = self.unpack(small["recovery.tar"], state, installation["secrets/openbao-ca.crt"])
        return record, state, installation, recovery

    def backup(self):
        self.need(not self.pending(), "Another maintenance operation is incomplete; resolve it first.")
        self.capture_targets()
        operation_id = str(uuid.uuid4())
        authority = self.fence(operation_id, "backup")
        try:
            self.drain(operation_id)
            result = self.capture(operation_id, authority["epoch"])
        except BaseException:
            released = self.try_release(operation_id)
            self.record["lastFailure"] = {"operation": "backup", "at": self.record["at"], "fenceReleased": released,
                                          "operationId": operation_id}
            raise
        self.release(operation_id)
        self.record["lastSuccessfulBackup"] = result | {"fullCoordinatedBackup": True}
        return {"state": "backup_saved", "fullCoordinatedBackup": True, "operationId": operation_id, **result,
                "action": "Copy the .backup directory off-host and escrow the age identity separately."}

    def verify_backup_command(self):
        self.crypto()
        directory = self.separate(self.args.archive, directory=True)
        record, *_ = self.verify_backup(directory, self.identity())
        self.need(record["installationId"] == self.state["installationId"],
                  "Backup belongs to a different installation.")
        return {"state": "backup_verified", "backupId": record["backupId"], "releaseTag": record["releaseTag"],
                "createdAt": record["createdAt"], "fullCoordinatedBackup": True}

    def remove_backup(self):
        self.crypto()
        directory = self.separate(self.args.archive, directory=True)
        record = self.read_backup(directory, self.identity())
        self.need(directory.name == record["backupId"] + ".backup"
                  and self.args.confirm_backup_id == record["backupId"],
                  "Removal requires the exact authenticated --confirm-backup-id.")
        shutil.rmtree(directory)
        self.p["sync_directory"](directory.parent)
        return {"state": "backup_removed", "backupId": record["backupId"], "installationDataRemoved": False}

    # ------------------------------------------------------------------------------ upgrade

    def previous_manifest(self, state):
        directory = self.args.previous_assets_dir
        self.need(directory is not None and directory.is_absolute() and directory == directory.resolve()
                  and directory != self.args.assets_dir,
                  "Upgrade requires --previous-assets-dir: the retained verified assets of the installed release.")
        self.p["protected"](directory, True)
        name = f"minicloud-server-{state['serverVersion']}.tar.gz"
        path = directory / name
        self.p["protected"](path)
        digest = self.file_sha256(path)
        self.need(digest == state["bundleSha256"],
                  "Previous release assets differ from the installed release's verified bundle.")
        files, core = {}, None
        with tarfile.open(path, "r:gz") as archive:
            for member in archive:
                self.need(member.isfile() and member.name not in files, "Unsafe previous bundle member.")
                data = archive.extractfile(member).read()
                files[member.name] = hashlib.sha256(data).hexdigest()
                if member.name == "server-release.json":
                    core = json.loads(data, object_pairs_hook=self.p["no_duplicates"])
        self.need(isinstance(core, dict) and core.get("releaseTag") == state["releaseTag"],
                  "Previous bundle lacks matching release metadata.")
        return core | {"files": files, "bundle": {"name": name, "sha256": digest}}

    @staticmethod
    def version(value):
        match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)(?:-.*)?", value)
        return tuple(int(part) for part in match.groups()) if match else None

    def compatible(self, previous, target):
        self.need(previous["applicationMigration"] >= MAINTENANCE_MIGRATION,
                  "The installed release predates the durable maintenance authority; coordinated upgrade is unavailable.")
        self.need(target["releaseTag"] != previous["releaseTag"], "The selected release is already installed.")
        before, after = self.version(previous["serverVersion"]), self.version(target["serverVersion"])
        self.need(before is not None and after is not None and after >= before
                  and target["applicationMigration"] >= previous["applicationMigration"]
                  and target["configurationSchemaVersion"] >= previous["configurationSchemaVersion"]
                  and target["identitySchemaVersion"] >= previous["identitySchemaVersion"],
                  "Downgrade refused: restore a backup taken by the older release instead.")

    def switch_bundle(self, operation_id, target):
        bundle = self.root / "bundle"
        aside = self.root / f"bundle-previous-{operation_id}"
        if not aside.exists():
            os.rename(bundle, aside)
            self.p["sync_directory"](self.root)
        self.i.installed = target
        self.i.extract()
        self.i.save(**{key: (target["bundle"]["sha256"] if key == "bundleSha256" else target[key])
                       for key in RELEASE_FIELDS})

    def revert_bundle(self, operation_id, previous, pending):
        aside = self.root / f"bundle-previous-{operation_id}"
        if aside.exists():
            bundle = self.root / "bundle"
            if bundle.exists():
                os.rename(bundle, self.root / f"bundle-failed-{operation_id}")
            os.rename(aside, bundle)
            self.p["sync_directory"](self.root)
        self.i.installed = previous
        self.i.save(**pending["previousRelease"])

    def start_services(self):
        self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "kratos")
        self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "api", "dashboard", "worker")
        self.i.compose("up", "-d", "caddy")

    def upgrade(self):
        self.need(self.root.exists() and self.i.state_path.exists(), "No installation to upgrade.")
        self.p["protected"](self.i.state_path)
        raw = self.p["read_json"](self.i.state_path)
        self.need(isinstance(raw, dict) and isinstance(raw.get("releaseTag"), str), "Unknown installation metadata.")
        self.need("pendingRestore" not in self.record and "restoreInProgress" not in self.record,
                  "A restore is pending confirmation; finish it before upgrading.")
        pending = self.record.get("pendingUpgrade")
        target = self.i.manifest
        previous = None
        if pending is not None:
            self.need(pending["toTag"] == target["releaseTag"],
                      f"An upgrade to {pending['toTag']} is incomplete; resume it with that release or restore "
                      f"pre-upgrade backup {pending.get('backupId')}.")
        if raw["releaseTag"] == target["releaseTag"]:
            self.need(pending is not None and UPGRADE_PHASES.index(pending["phase"]) >= UPGRADE_PHASES.index("switched"),
                      "The selected release is already installed.")
            self.i.load_state()
        else:
            previous = self.previous_manifest(raw)
            self.i.load_state(manifest=previous)
            self.compatible(previous, target)
        self.need(self.state is not None and self.state["credentialsComplete"],
                  "Upgrade requires the original complete installation.")
        self.capture_targets()
        if pending is None:
            self.i.pull_verified_images()
            pending = {"operationId": str(uuid.uuid4()), "fromTag": previous["releaseTag"],
                       "toTag": target["releaseTag"], "phase": "started", "backupId": None,
                       "previousRelease": {key: self.state[key] for key in RELEASE_FIELDS}, "at": self.now()}
            self.record["pendingUpgrade"] = pending
            self.persist()
        operation_id = pending["operationId"]
        self.ensure_host_id()

        def reached(phase):
            return UPGRADE_PHASES.index(pending["phase"]) >= UPGRADE_PHASES.index(phase)

        def advance(phase, **updates):
            pending.update(updates, phase=phase)
            self.persist()

        try:
            if not reached("fenced"):
                authority = self.fence(operation_id, "upgrade")
                advance("fenced", epoch=authority["epoch"])
            if not reached("drained"):
                self.drain(operation_id)
                advance("drained")
            if not reached("backed_up"):
                result = self.capture(operation_id, pending["epoch"])
                self.record["lastSuccessfulBackup"] = result | {"fullCoordinatedBackup": True}
                advance("backed_up", backupId=result["backupId"], backupArchive=result["archive"])
            if not reached("stopped"):
                advance("stopped")
                self.i.compose("stop", *APPLICATION_SERVICES)
            if not reached("switched"):
                self.switch_bundle(operation_id, target)
                advance("switched")
            if not reached("dependencies"):
                self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "postgres")
                self.i.compose("up", "-d", "--no-deps", "openbao")
                self.i.unseal()
                self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "--no-deps", "openbao-agent")
                advance("dependencies")
        except BaseException:
            self.upgrade_failed_before_migration(pending, previous)
            raise
        try:
            if not reached("migrated"):
                advance("migrating")
                self.i.compose("run", "--rm", "-T", "api-migrate")
                self.i.compose("run", "--rm", "-T", "kratos-migrate")
                advance("migrated")
            if not reached("started_services"):
                self.start_services()
                advance("started_services")
            if not reached("verified"):
                self.i.service_readiness()
                readiness = self.i.readiness()
                self.need(readiness["state"] in ("ready", "awaiting_operator"),
                          f"Upgraded release is not ready ({readiness['state']}).")
                advance("verified")
            self.release(operation_id)
        except BaseException:
            self.record["lastFailure"] = {
                "operation": "upgrade", "at": self.record["at"], "operationId": operation_id, "phase": pending["phase"],
                "fenceReleased": False,
                "rollback": (f"Writes remain fenced by {operation_id}; the schema may already be migrated, so never "
                             "downgrade images alone. Fix the cause and re-run this upgrade with the same target "
                             f"release to resume, or roll back by running the PREVIOUS release's verified installer "
                             f"`maintenance restore --archive {pending.get('backupArchive')}` with that release's "
                             "assets.")}
            self.persist()
            raise
        self.record.pop("pendingUpgrade")
        self.record["lastSuccessfulUpgrade"] = {"operationId": operation_id, "fromTag": pending["fromTag"],
                                                "toTag": pending["toTag"], "backupId": pending["backupId"],
                                                "at": self.now()}
        return {"state": "upgraded", "fromTag": pending["fromTag"], "toTag": pending["toTag"],
                "operationId": operation_id, "backupId": pending["backupId"],
                "backupArchive": pending.get("backupArchive")}

    def upgrade_failed_before_migration(self, pending, previous):
        """No schema change yet: put the previous release back and only then reopen writes."""
        operation_id = pending["operationId"]
        failure = {"operation": "upgrade", "at": self.record["at"], "operationId": operation_id,
                   "phase": pending["phase"], "fenceReleased": False}
        try:
            if pending["phase"] == "started":
                self.record.pop("pendingUpgrade")
                self.persist()
                failure["rollback"] = "No fence was taken and nothing changed."
                return
            if UPGRADE_PHASES.index(pending["phase"]) >= UPGRADE_PHASES.index("stopped"):
                self.need(previous is not None, "Previous release metadata is unavailable for automatic rollback.")
                self.revert_bundle(operation_id, previous, pending)
                self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "postgres")
                self.i.compose("up", "-d", "--no-deps", "openbao")
                self.i.unseal()
                self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "--no-deps", "openbao-agent")
                self.start_services()
                self.i.service_readiness()
            self.release(operation_id)
            failure["fenceReleased"] = True
            failure["rollback"] = "Failed before migration; the previous release is running and writes reopened."
            self.record.pop("pendingUpgrade")
        except (self.p["InstallError"], OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
            failure["rollback"] = (f"Automatic rollback did not complete; writes remain fenced by {operation_id}. "
                                   "No migration ran. Re-run this upgrade to resume, or restore the previous release "
                                   "bundle and start it, then `maintenance release --confirm-operation-id "
                                   f"{operation_id}`.")
        finally:
            self.record["lastFailure"] = failure
            self.persist()

    # ------------------------------------------------------------------------------ restore

    def stop_project(self):
        env = self.i.docker_environment()
        containers = self.p["run"](["docker", "ps", "-q", "--filter", "label=com.docker.compose.project=minicloud"],
                                   env=env).decode().split()
        if containers:
            self.p["run"](["docker", "stop", *containers], env=env)

    def write_installation(self, state, installation, operation_id):
        secrets = self.root / "secrets"
        wanted = {name: data for name, data in installation.items() if name.startswith("secrets/")}
        if secrets.exists():
            current = {path.relative_to(self.root).as_posix(): path.read_bytes()
                       for path in secrets.rglob("*") if path.is_file()}
            if current != wanted:
                os.rename(secrets, self.root / f"secrets-superseded-{operation_id}")
        if not secrets.exists():
            for name in ("secrets", "secrets/ssh", "secrets/openbao-approle", "secrets/bootstrap",
                         "secrets/bootstrap/input", "secrets/bootstrap/output"):
                self.p["make_directory"](self.root / name)
            for name, data in sorted(wanted.items()):
                path = self.root / name
                for parent in reversed(path.relative_to(self.root).parents):
                    if str(parent) != ".":
                        self.p["make_directory"](self.root / parent)
                self.p["exclusive"](path, data)
        bundle = self.root / "bundle"
        if bundle.exists():
            try:
                self.i.installed = self.i.manifest
                self.i.state = state
                self.i.verify_installed_bundle()
            except self.p["InstallError"]:
                os.rename(bundle, self.root / f"bundle-superseded-{operation_id}")
        self.i.state = state
        self.i.installed = self.i.manifest
        self.p["atomic_json"](self.i.state_path, state)
        self.i.extract()

    def write_recovery(self, installation_id, recovery):
        directory = self.args.recovery_dir / installation_id
        if directory.exists():
            self.p["protected"](directory, True)
            current = {path.name: path.read_bytes() for path in directory.iterdir()}
            self.need(current == recovery, "Existing independent recovery files differ from the backup; reconcile.")
            return
        self.p["make_directory"](directory)
        for name, data in recovery.items():
            self.p["exclusive"](directory / name, data)

    def restore(self):
        self.crypto()
        archive = self.separate(self.args.archive, directory=True)
        identity = self.identity()
        record, backup_state, installation, recovery = self.verify_backup(archive, identity)
        target = self.i.manifest
        self.need(record["releaseTag"] == target["releaseTag"]
                  and record["bundleSha256"] == target["bundle"]["sha256"]
                  and all(record[key] == target[key] for key in RELEASE_FIELDS if key not in ("releaseTag", "bundleSha256")),
                  f"Restore with the exact release that created this backup ({record['releaseTag']}); "
                  "upgrade afterwards if needed.")
        self.need(backup_state.get("root") == str(self.root),
                  "Restore into the original installation root path.")
        self.need(backup_state.get("configuration") == self.i.config,
                  "Restore requires the original configuration file content.")
        in_place = self.root.exists()
        authority = "absent"
        if in_place:
            self.p["protected"](self.root, True)
            self.p["protected"](self.i.state_path)
            existing = self.p["read_json"](self.i.state_path)
            self.need(isinstance(existing, dict) and existing.get("installationId") == record["installationId"],
                      "A different installation exists at this root; refusing to replace it.")
            if self.pending() or "heldFence" in self.record:
                authority = "fenced"
            else:
                try:
                    self.i.load_state()
                    authority = self.authority_ok("status")["authority"]["state"]
                except (self.p["InstallError"], OSError, ValueError, KeyError, TypeError,
                        subprocess.TimeoutExpired):
                    authority = "unknown"
            self.need(authority == "fenced" or self.args.confirm_replace_live == record["installationId"],
                      f"A live installation ({authority} maintenance authority) may be serving writes. Re-run with "
                      f"--confirm-replace-live {record['installationId']} to fence, drain and replace it.")
        operation_id = str(uuid.uuid4())
        if authority == "open":
            self.fence(operation_id, "restore")
            try:
                self.drain(operation_id)
            except BaseException:
                self.try_release(operation_id)
                raise
        state = backup_state | {"publicAddresses": self.args.public_address,
                                "recoveryDir": str(self.args.recovery_dir)}
        if not in_place:
            self.i.host.tools()
            self.root.mkdir(mode=0o700)
            self.p["sync_directory"](self.root.parent)
        self.record.pop("heldFence", None)
        self.record["restoreInProgress"] = {"operationId": operation_id, "backupId": record["backupId"],
                                            "at": self.now()}
        self.persist()
        try:
            if in_place:
                self.stop_project()
            else:
                self.i.state = state
                self.i.pull_verified_images()
            self.write_installation(state, installation, operation_id)
            self.write_recovery(record["installationId"], recovery)
            host_id = self.ensure_host_id()
            self.i.compose("up", "--no-start", "--no-deps", "postgres", "openbao", "openbao-agent", "api", "caddy")
            for name in VOLUMES:
                self.decrypt_stream(archive / f"volume-{name}.tar.age", identity, sink=self.volume_restore(name))
            self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "postgres")
            self.i.compose("run", "--rm", "-T", "database-initialize")
            for name in DATABASES:
                self.decrypt_stream(archive / f"postgres-{name}.dump.age", identity, sink=self.i.compose_invocation(
                    "exec", "-T", "postgres", "pg_restore", "--username", "postgres", "--dbname", "postgres",
                    "--clean", "--if-exists", "--create", "--exit-on-error"))
            self.i.compose("up", "-d", "--no-deps", "openbao")
            self.i.unseal()
            self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "--no-deps", "openbao-agent")
            self.i.compose("run", "--rm", "-T", "api-migrate")
            self.i.compose("run", "--rm", "-T", "kratos-migrate")
            # The restored copy is fenced by the backup; transfer takes it over under a new epoch.
            transferred = self.authority_ok("transfer", operation_id, host_id)["authority"]
            self.start_services()
            self.i.service_readiness()
        except BaseException:
            self.record["lastFailure"] = {
                "operation": "restore", "at": self.record["at"], "operationId": operation_id, "fenceReleased": False,
                "rollback": "Restore is repeatable: fix the cause and re-run it with the same archive. "
                            "Services stay stopped/fenced; no data outside this installation was touched."}
            self.persist()
            raise
        self.record.pop("restoreInProgress")
        self.record["pendingRestore"] = {"operationId": operation_id, "backupId": record["backupId"],
                                         "epoch": transferred["epoch"], "hostId": host_id, "at": self.now()}
        return {"state": "restored_fenced", "operationId": operation_id, "backupId": record["backupId"],
                "epoch": transferred["epoch"], "hostId": host_id, "limitation": TRANSFER_LIMITATION,
                "action": ("Writes stay fenced. Verify the restored installation, then run "
                           f"`maintenance restore-confirm --confirm-operation-id {operation_id}` to reopen writes.")}

    def restore_confirm(self):
        pending = self.record.get("pendingRestore")
        self.need(pending is not None, "No restore is awaiting confirmation.")
        self.need(self.args.confirm_operation_id == pending["operationId"],
                  "Confirm with the exact restore --confirm-operation-id.")
        self.i.service_readiness()
        self.release(pending["operationId"])
        self.record.pop("pendingRestore")
        return {"state": "restore_confirmed", "operationId": pending["operationId"], "backupId": pending["backupId"]}

    # ----------------------------------------------------------------------------- transfer

    def transfer(self):
        self.need(not self.pending(), "Another maintenance operation is incomplete; resolve it first.")
        host_id = self.ensure_host_id()
        current = self.authority_ok("status")["authority"]
        fence_id = None
        # A host that is not yet authorized cannot fence; transfer itself fences under the new epoch.
        if current["state"] == "open" and current.get("localHostAuthorized") is True:
            fence_id = str(uuid.uuid4())
            self.fence(fence_id, "transfer")
            try:
                self.drain(fence_id)
            except BaseException:
                self.try_release(fence_id)
                raise
        operation_id = str(uuid.uuid4())
        try:
            authority = self.authority_ok("transfer", operation_id, host_id)["authority"]
        except BaseException:
            if fence_id:
                self.try_release(fence_id)
            raise
        self.record["heldFence"] = {"operationId": operation_id, "operation": "transfer", "at": self.now(),
                                    "epoch": authority["epoch"]}
        self.persist()
        try:
            # Local processes registered at the superseded epoch are revoked; restart them under the new one.
            self.i.compose("up", "-d", "--wait", "--wait-timeout", "180", "--force-recreate", "api", "worker")
            self.i.service_readiness()
        except BaseException:
            self.record["lastFailure"] = {
                "operation": "transfer", "at": self.record["at"], "operationId": operation_id, "fenceReleased": False,
                "rollback": f"Authority moved to this host (epoch {authority['epoch']}) but its services are not "
                            "ready; writes remain fenced. Fix them, then `maintenance release "
                            f"--confirm-operation-id {operation_id}`."}
            self.persist()
            raise
        released = False
        if not self.args.keep_fenced:
            self.release(operation_id)
            released = True
        return {"state": "transferred", "operationId": operation_id, "epoch": authority["epoch"],
                "hostId": host_id, "released": released, "limitation": TRANSFER_LIMITATION}

    def release_command(self):
        operation_id = self.args.confirm_operation_id
        self.need(operation_id, "Specify the fence holder with --confirm-operation-id.")
        self.need(self.record.get("pendingRestore", {}).get("operationId") != operation_id,
                  "Use restore-confirm for a pending restore.")
        self.need(self.record.get("pendingUpgrade", {}).get("operationId") != operation_id,
                  "An upgrade is incomplete; re-run it to resume or restore its pre-upgrade backup.")
        self.i.service_readiness()
        self.release(operation_id)
        return {"state": "fence_released", "operationId": operation_id}

    # -------------------------------------------------------------------------------- status

    def status(self):
        result = {"lastOperation": self.record, "fullCoordinatedBackup": "lastSuccessfulBackup" in self.record}
        try:
            result["authority"] = self.authority_ok("status")["authority"]
        except (self.p["InstallError"], OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
            result["authority"] = None
        if "pendingRestore" in self.record:
            state = "restore_pending_confirmation"
        elif self.pending():
            state = "maintenance_incomplete"
        elif result["authority"] is None:
            state = "authority_unavailable"
        elif result["authority"]["state"] != "open":
            state = "fenced"
        else:
            state = "maintenance_idle"
        return {"state": state, **result}

    # ------------------------------------------------------------------------------- dispatch

    def execute(self):
        operation = self.args.maintenance_operation
        self.load_record()
        if operation == "status":
            return self.status()
        self.record.pop("state", None)
        self.record.update(operation=operation, at=self.now(), state="started")
        self.persist()
        try:
            if operation in ("stop", "remove"):
                self.need(False, "Not provided: stopping/removing containers is not a maintenance operation. "
                                 "Persistent volumes and customer servers are never removed by this tooling.")
            handlers = {
                "backup": self.backup, "verify-backup": self.verify_backup_command,
                "remove-backup": self.remove_backup, "schedule-backup": lambda: self.schedule("backup"),
                "upgrade": self.upgrade, "restore": self.restore, "restore-confirm": self.restore_confirm,
                "transfer": self.transfer, "release": self.release_command,
                "backup-recovery": self.backup_recovery,
                "verify-recovery": lambda: self.verify_recovery(),
                "rehearse-recovery": self.rehearse, "retention-plan": self.retention,
                "remove-recovery": self.remove, "schedule-recovery": lambda: self.schedule("backup-recovery"),
            }
            self.need(operation in handlers, "Unknown maintenance operation.")
            result = handlers[operation]()
        except (self.p["InstallError"], OSError, ValueError, KeyError, TypeError, tarfile.TarError,
                subprocess.TimeoutExpired):
            self.record["state"] = "failed"
            self.record.setdefault("lastFailure", {})
            if self.record["lastFailure"].get("at") != self.record["at"]:
                self.record["lastFailure"] = {"operation": operation, "at": self.record["at"]}
            self.record["error"] = "Operation failed; see lastFailure for fence and rollback state."
            self.persist()
            raise
        self.record.pop("error", None)
        if operation == "backup-recovery":
            self.record["lastSuccessfulRecovery"] = {
                "at": self.record["at"], "archive": result["archive"], "backupId": result["backupId"],
                "fullCoordinatedBackup": False}
        self.record["state"] = result["state"]
        self.persist()
        return result

    def verify_recovery(self):
        archive, _ = self.decrypt(self.args.archive)
        return {"state": "recovery_dependencies_verified", "backupId": archive["backupId"],
                "fullCoordinatedBackup": False}


def execute(installer, primitives):
    return Maintenance(installer, primitives).execute()
