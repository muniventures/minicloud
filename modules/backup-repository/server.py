#!/usr/bin/env python3
"""Append-only ciphertext transport. This is not a database backup engine."""
from contextlib import contextmanager
import fcntl
import hashlib
import hmac
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import stat
import threading
import time


ARTIFACTS = {"database.dump.enc", "manifest.json.enc"}
ROUTE = re.compile(r"/v1/([a-z][a-z0-9-]{0,31})/((?:db|database)_[a-z0-9]{8,64})/([A-Za-z0-9][A-Za-z0-9_-]{7,95})/(database\.dump\.enc|manifest\.json\.enc)\Z")
STATUS_ROUTE = re.compile(r"/v1/([a-z][a-z0-9-]{0,31})/((?:db|database)_[a-z0-9]{8,64})/status\Z")
SHA256 = re.compile(r"[0-9a-f]{64}\Z")
CHUNK = 1024 * 1024


class Rejected(Exception):
    def __init__(self, status, code):
        self.status, self.code = status, code


class Settings:
    def __init__(self, root, bindings, repository_id, *, max_bytes=8 * 1024**3,
                 reserve_bytes=256 * 1024**2, quota_bytes=64 * 1024**3,
                 trusted_proxies=(), workers=4, request_timeout=30, upload_seconds=3600):
        self.root = Path(root)
        self.bindings = {}
        self.policies = {}
        tokens = set()
        for binding in bindings:
            environment, app, token = binding["environment"], binding["databaseId"], binding["token"]
            if not ROUTE.fullmatch(f"/v1/{environment}/{app}/fixture01/database.dump.enc"):
                raise ValueError("Invalid repository binding identity")
            if not isinstance(token, str) or len(token) < 32 or len(token) > 512 or any(c.isspace() for c in token):
                raise ValueError("Repository tokens must be distinct strong opaque values")
            if token in tokens or (environment, app) in self.bindings:
                raise ValueError("Duplicate repository binding or token")
            tokens.add(token)
            self.bindings[(environment, app)] = token
            retention = binding.get("retentionDays", 30)
            minimum = binding.get("minimumComplete", 2)
            if (type(retention) is not int or not 1 <= retention <= 3650 or type(minimum) is not int or not 1 <= minimum <= 100 or
                    type(binding.get("policyRevision",0)) is not int or binding.get("policyRevision",0)<0 or
                    type(binding.get("enabled", True)) is not bool or type(binding.get("keepLastCopies", False)) is not bool):
                raise ValueError("Invalid retention policy")
            self.policies[(environment, app)] = dict(retentionDays=retention, minimumComplete=minimum,
                enabled=binding.get("enabled", True), keepLastCopies=binding.get("keepLastCopies", False), policyRevision=binding.get("policyRevision",0))
        if not re.fullmatch(r"[A-Za-z0-9_-]{8,96}", repository_id):
            raise ValueError("Repository bindings and identity are required")
        if not (1 <= max_bytes <= 8 * 1024**3 and 0 <= reserve_bytes and max_bytes <= quota_bytes and 1 <= workers <= 16):
            raise ValueError("Invalid repository capacity bounds")
        self.repository_id = repository_id
        self.max_bytes, self.reserve_bytes, self.quota_bytes = max_bytes, reserve_bytes, quota_bytes
        self.trusted_proxies = tuple(ipaddress.ip_network(value, strict=False) for value in trusted_proxies)
        if not self.trusted_proxies:
            raise ValueError("Explicit HTTPS ingress proxy networks are required")
        self.workers, self.request_timeout, self.upload_seconds = workers, request_timeout, upload_seconds

    @classmethod
    def from_env(cls):
        return cls(os.environ.get("BACKUP_ROOT", "/data/backups"),
                   json.loads(os.environ.get("BACKUP_BINDINGS_JSON", "[]")),
                   os.environ.get("BACKUP_REPOSITORY_ID", ""),
                   max_bytes=int(os.environ.get("BACKUP_MAX_ARTIFACT_BYTES", 8 * 1024**3)),
                   reserve_bytes=int(os.environ.get("BACKUP_RESERVE_BYTES", 256 * 1024**2)),
                   quota_bytes=int(os.environ.get("BACKUP_QUOTA_BYTES", 64 * 1024**3)),
                   trusted_proxies=os.environ.get("BACKUP_TRUSTED_PROXIES", "").split(",") if os.environ.get("BACKUP_TRUSTED_PROXIES") else (),
                   workers=int(os.environ.get("BACKUP_MAX_WORKERS", 4)))


def regular_file(fd):
    value = os.fstat(fd)
    if not stat.S_ISREG(value.st_mode) or value.st_uid != os.geteuid():
        raise Rejected(503, "repository_unavailable")
    return value


class Repository:
    def __init__(self, settings, *, mount_check=os.path.ismount, disk_usage=shutil.disk_usage):
        self.settings, self.mount_check, self.disk_usage = settings, mount_check, disk_usage
        self.write_lock = threading.Lock()

    @contextmanager
    def root(self):
        root = self.settings.root
        if root == Path("/") or not self.mount_check(root):
            raise Rejected(503, "repository_unavailable")
        info = root.lstat()
        if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) & 0o022:
            raise Rejected(503, "repository_unavailable")
        fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            actual = os.fstat(fd)
            if (actual.st_dev, actual.st_ino) != (info.st_dev, info.st_ino):
                raise Rejected(503, "repository_unavailable")
            yield fd
        finally:
            os.close(fd)

    def verify_identity(self, root_fd):
        expected = self.settings.repository_id.encode("ascii")
        temporary = ".identity-" + secrets.token_hex(16)
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=root_fd)
        try:
            with os.fdopen(fd, "wb") as stream:
                stream.write(expected)
                stream.flush()
                os.fsync(stream.fileno())
            try:
                os.link(temporary, ".repository-id", src_dir_fd=root_fd, dst_dir_fd=root_fd, follow_symlinks=False)
                os.fsync(root_fd)
            except FileExistsError:
                pass
        finally:
            os.unlink(temporary, dir_fd=root_fd)
        fd = os.open(".repository-id", os.O_RDONLY | os.O_NOFOLLOW, dir_fd=root_fd)
        with os.fdopen(fd, "rb") as stream:
            regular_file(stream.fileno())
            if not hmac.compare_digest(stream.read(128), expected):
                raise Rejected(503, "repository_identity_mismatch")

    def refresh_bindings(self):
        path = Path(os.environ.get("BACKUP_BINDINGS_FILE", "/data/backups/.bindings.json"))
        if not path.exists():
            return
        if path.is_symlink() or not path.is_file() or path.stat().st_mode & 0o077 or path.stat().st_uid != os.geteuid() or path.stat().st_size > 1024*1024:
            raise Rejected(503, "repository_bindings_unavailable")
        values = json.loads(path.read_text())
        validated = Settings(self.settings.root, values, self.settings.repository_id,
            trusted_proxies=[str(network) for network in self.settings.trusted_proxies])
        self.settings.bindings = validated.bindings
        self.settings.policies = validated.policies

    def ready(self):
        self.refresh_bindings()
        with self.root() as fd:
            self.verify_identity(fd)

    @contextmanager
    def artifact_dir(self, identifiers, *, create=True):
        with self.root() as root_fd:
            self.verify_identity(root_fd)
            descriptors = []
            parent = root_fd
            try:
                for component in identifiers:
                    if create:
                        try:
                            os.mkdir(component, 0o700, dir_fd=parent)
                            os.fsync(parent)
                        except FileExistsError:
                            pass
                    try:
                        fd = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
                    except FileNotFoundError:
                        raise Rejected(404, "artifact_not_found") from None
                    info = os.fstat(fd)
                    if info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) & 0o022:
                        os.close(fd)
                        raise Rejected(503, "repository_unavailable")
                    descriptors.append(fd)
                    parent = fd
                yield root_fd, parent
            finally:
                for fd in reversed(descriptors):
                    os.close(fd)

    def create_metadata(self, directory, identifiers):
        metadata = {"repositoryId": self.settings.repository_id, "environment": identifiers[0],
                    "databaseId": identifiers[1], "backupId": identifiers[2], "createdAt": time.time()}
        temporary = ".metadata-" + secrets.token_hex(16)
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
        try:
            with os.fdopen(fd, "w") as stream:
                json.dump(metadata, stream)
                stream.flush()
                os.fsync(stream.fileno())
            try:
                os.link(temporary, ".backup-meta.json", src_dir_fd=directory, dst_dir_fd=directory, follow_symlinks=False)
                os.fsync(directory)
            except FileExistsError:
                pass
        finally:
            os.unlink(temporary, dir_fd=directory)

    def read_metadata(self, directory, identifiers):
        fd = os.open(".backup-meta.json", os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
        with os.fdopen(fd, "r") as stream:
            if regular_file(stream.fileno()).st_size > 4096:
                raise Rejected(503, "repository_unavailable")
            value = json.loads(stream.read())
        expected = (self.settings.repository_id, *identifiers)
        if tuple(value.get(key) for key in ("repositoryId", "environment", "databaseId", "backupId")) != expected:
            raise Rejected(503, "repository_unavailable")
        if not isinstance(value.get("createdAt"), (int, float)) or value["createdAt"] <= 0:
            raise Rejected(503, "repository_unavailable")
        return value

    def complete_metadata(self, directory, identifiers):
        metadata = self.read_metadata(directory, identifiers)
        artifacts = {}
        for name in sorted(ARTIFACTS):
            try:
                fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
            except FileNotFoundError:
                return
            try:
                size, digest = self.digest_file(fd)
                artifacts[name] = {"sizeBytes": size, "sha256": digest}
            finally:
                os.close(fd)
        if metadata.get("completedAt") and metadata.get("artifacts") == artifacts:
            return
        metadata["completedAt"] = time.time()
        metadata["expiresAt"] = metadata["completedAt"] + self.settings.policies[identifiers[:2]]["retentionDays"] * 86400
        metadata["artifacts"] = artifacts
        temporary = ".metadata-" + secrets.token_hex(16)
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
        try:
            with os.fdopen(fd, "w") as stream:
                json.dump(metadata, stream)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, ".backup-meta.json", src_dir_fd=directory, dst_dir_fd=directory)
            os.fsync(directory)
        finally:
            try:
                os.unlink(temporary, dir_fd=directory)
            except FileNotFoundError:
                pass

    def source_records(self, environment, app):
        records = []
        try:
            with self.artifact_dir((environment, app), create=False) as (_, parent):
                for name in os.listdir(parent):
                    if not ROUTE.fullmatch(f"/v1/{environment}/{app}/{name}/database.dump.enc"):
                        continue
                    try:
                        fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=parent)
                        try:
                            metadata = self.read_metadata(fd, (environment, app, name))
                            complete = bool(metadata.get("completedAt"))
                            for artifact in ARTIFACTS:
                                try:
                                    artifact_fd = os.open(artifact, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=fd)
                                    try:
                                        actual = regular_file(artifact_fd)
                                        expected = metadata.get("artifacts", {}).get(artifact, {})
                                        complete = complete and actual.st_size == expected.get("sizeBytes") and bool(
                                            SHA256.fullmatch(expected.get("sha256", "")))
                                    finally:
                                        os.close(artifact_fd)
                                except (OSError, Rejected):
                                    complete = False
                            metadata["complete"] = complete
                            records.append(metadata)
                        finally:
                            os.close(fd)
                    except (OSError, ValueError, Rejected):
                        continue
        except Rejected as error:
            if error.status != 404:
                raise
        return records

    def status(self, environment, app):
        complete = [record for record in self.source_records(environment, app) if record["complete"]]
        complete.sort(key=lambda record: (record["completedAt"], record["backupId"]), reverse=True)
        latest = complete[0] if complete else None
        if latest:
            latest = {key: latest[key] for key in ("backupId", "createdAt", "completedAt", "artifacts")}
        return {"latest": latest, "completeCount": len(complete), "backups": complete, **self.settings.policies[(environment, app)]}

    def expire(self, now=None):
        """Backend-only expiry. Never remove unknown directories or newest two complete sets."""
        self.refresh_bindings()
        now = time.time() if now is None else now
        removed = 0
        with self.root() as root_fd, self.serialize_upload(root_fd):
            self.verify_identity(root_fd)
            for environment, app in self.settings.bindings:
                records = self.source_records(environment, app)
                complete = sorted((record for record in records if record["complete"]),
                                  key=lambda record: (record["completedAt"], record["backupId"]), reverse=True)
                policy = self.settings.policies[(environment, app)]
                count = policy["minimumComplete"] if policy["enabled"] or policy["keepLastCopies"] else 0
                protected = {record["backupId"] for record in complete[:count]}
                for record in records:
                    expired = (record["complete"] and record["backupId"] not in protected and
                               now > record.get("expiresAt", record["completedAt"] + 30 * 86400))
                    orphan = not record["complete"] and now - record["createdAt"] > 86400
                    if not (expired or orphan):
                        continue
                    identifiers = (environment, app, record["backupId"])
                    with self.artifact_dir(identifiers, create=False) as (_, directory):
                        names = os.listdir(directory)
                        if any(name not in ARTIFACTS and name != ".backup-meta.json" and
                               not re.fullmatch(r"\.(upload|metadata)-[0-9a-f]{32}", name) for name in names):
                            continue
                        if any(not stat.S_ISREG(os.stat(name, dir_fd=directory, follow_symlinks=False).st_mode) for name in names):
                            continue
                        for name in names:
                            os.unlink(name, dir_fd=directory)
                        os.fsync(directory)
                    with self.artifact_dir((environment, app), create=False) as (_, parent):
                        os.rmdir(record["backupId"], dir_fd=parent)
                        os.fsync(parent)
                    removed += 1
        return removed

    def purge_source(self, environment, database_id, expected_revision, expected_repository_id):
        """Privileged local administration only. No HTTP route exposes this operation."""
        if not ROUTE.fullmatch(f"/v1/{environment}/{database_id}/fixture01/database.dump.enc") or type(expected_revision) is not int or expected_revision < 1:
            raise Rejected(400,"invalid_purge_identity")
        with self.root() as root_fd, self.serialize_upload(root_fd):
            self.refresh_bindings()
            self.verify_identity(root_fd)
            policy=self.settings.policies.get((environment,database_id))
            if expected_repository_id != self.settings.repository_id or policy is None or policy['policyRevision'] != expected_revision:
                raise Rejected(409,"purge_identity_changed")
            if policy['enabled'] or policy['keepLastCopies']:
                raise Rejected(409,"purge_source_protected")
            try:
                with self.artifact_dir((environment,database_id),create=False) as (_,parent):
                    names=sorted(os.listdir(parent))
            except Rejected as error:
                if error.status==404:
                    return {'status':'purged','purgedBackupIds':[],'purgedCount':0,'remainingCount':0}
                raise
            # Validate the entire source before deleting any batch. Unknown or
            # foreign metadata, symlinks and unexpected files fail closed.
            for backup_id in names:
                if not ROUTE.fullmatch(f"/v1/{environment}/{database_id}/{backup_id}/database.dump.enc"):
                    raise Rejected(409,"purge_unrecognized_content")
                with self.artifact_dir((environment,database_id,backup_id),create=False) as (_,directory):
                    files=os.listdir(directory)
                    # A prior purge may have removed metadata immediately before
                    # interruption at rmdir. Empty owned directories hold no data.
                    if files:
                        self.read_metadata(directory,(environment,database_id,backup_id))
                    for name in files:
                        if name not in ARTIFACTS and name!='.backup-meta.json' and not re.fullmatch(r"\.(upload|metadata)-[0-9a-f]{32}",name):
                            raise Rejected(409,"purge_unrecognized_content")
                        fd=os.open(name,os.O_RDONLY|os.O_NOFOLLOW,dir_fd=directory)
                        try: regular_file(fd)
                        finally: os.close(fd)
            selected=names[:100]
            for backup_id in selected:
                with self.artifact_dir((environment,database_id,backup_id),create=False) as (_,directory):
                    files=os.listdir(directory)
                    if files:
                        self.read_metadata(directory,(environment,database_id,backup_id))
                    # Ownership metadata is removed last to preserve retry proof
                    # if interruption happens while removing artifact bytes.
                    for name in sorted(files,key=lambda value:value=='.backup-meta.json'):
                        os.unlink(name,dir_fd=directory)
                    os.fsync(directory)
                with self.artifact_dir((environment,database_id),create=False) as (_,parent):
                    os.rmdir(backup_id,dir_fd=parent)
                    os.fsync(parent)
            return {'status':'purged','purgedBackupIds':selected,'purgedCount':len(selected),'remainingCount':len(names)-len(selected)}

    def used_bytes(self):
        total = 0
        for directory, dirs, files in os.walk(self.settings.root, followlinks=False):
            for name in dirs + files:
                try:
                    info = (Path(directory) / name).lstat()
                except FileNotFoundError:
                    continue
                if stat.S_ISLNK(info.st_mode):
                    raise Rejected(503, "repository_unavailable")
                if stat.S_ISREG(info.st_mode):
                    total += info.st_size
        return total

    @contextmanager
    def serialize_upload(self, root_fd):
        with self.write_lock:
            fd = os.open(".upload-lock", os.O_WRONLY | os.O_CREAT | os.O_NOFOLLOW, 0o600, dir_fd=root_fd)
            try:
                regular_file(fd)
                fcntl.flock(fd, fcntl.LOCK_EX)
                yield
            finally:
                os.close(fd)

    @staticmethod
    def digest_file(fd):
        metadata = regular_file(fd)
        digest = hashlib.sha256()
        os.lseek(fd, 0, os.SEEK_SET)
        while chunk := os.read(fd, CHUNK):
            digest.update(chunk)
        os.lseek(fd, 0, os.SEEK_SET)
        return metadata.st_size, digest.hexdigest()

    def put(self, identifiers, artifact, source, length, expected_hash):
        with self.root() as root_fd, self.serialize_upload(root_fd):
            self.refresh_bindings()
            policy=self.settings.policies.get(identifiers[:2])
            if policy is None or not policy['enabled']:
                raise Rejected(403,"backup_source_disabled")
            with self.artifact_dir(identifiers) as (_, directory):
                self.create_metadata(directory, identifiers)
                existing = False
                try:
                    existing_fd = os.open(artifact, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
                except FileNotFoundError:
                    pass
                else:
                    try:
                        if regular_file(existing_fd).st_size > self.settings.max_bytes:
                            raise Rejected(503, "repository_unavailable")
                        existing = True
                    finally:
                        os.close(existing_fd)
                if self.disk_usage(self.settings.root).free - length < self.settings.reserve_bytes:
                    raise Rejected(507, "repository_capacity")
                if self.used_bytes() + (0 if existing else length) > self.settings.quota_bytes:
                    raise Rejected(507, "repository_capacity")
                temporary = ".upload-" + secrets.token_hex(16)
                fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory)
                try:
                    digest, received, deadline = hashlib.sha256(), 0, time.monotonic() + self.settings.upload_seconds
                    with os.fdopen(fd, "wb") as stream:
                        while received < length:
                            if time.monotonic() > deadline:
                                raise Rejected(408, "upload_timeout")
                            chunk = source.read(min(CHUNK, length - received))
                            if not chunk:
                                raise Rejected(400, "incomplete_upload")
                            received += len(chunk)
                            digest.update(chunk)
                            stream.write(chunk)
                        if not hmac.compare_digest(digest.hexdigest(), expected_hash):
                            raise Rejected(400, "ciphertext_hash_mismatch")
                        stream.flush()
                        os.fsync(stream.fileno())
                    try:
                        os.link(temporary, artifact, src_dir_fd=directory, dst_dir_fd=directory, follow_symlinks=False)
                        os.fsync(directory)
                        created = True
                    except FileExistsError:
                        existing_fd = os.open(artifact, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
                        try:
                            size, actual_hash = self.digest_file(existing_fd)
                        finally:
                            os.close(existing_fd)
                        if size != length or not hmac.compare_digest(actual_hash, expected_hash):
                            raise Rejected(409, "artifact_conflict")
                        created = False
                    self.complete_metadata(directory, identifiers)
                    return {"sha256": expected_hash, "sizeBytes": length, "created": created}
                finally:
                    try:
                        os.unlink(temporary, dir_fd=directory)
                        os.fsync(directory)
                    except FileNotFoundError:
                        pass

    @contextmanager
    def get(self, identifiers, artifact):
        with self.artifact_dir(identifiers, create=False) as (_, directory):
            try:
                fd = os.open(artifact, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
            except FileNotFoundError:
                raise Rejected(404, "artifact_not_found") from None
            try:
                if regular_file(fd).st_size > self.settings.max_bytes:
                    raise Rejected(503, "repository_unavailable")
                size, digest = self.digest_file(fd)
                if size > self.settings.max_bytes:
                    raise Rejected(503, "repository_unavailable")
                yield fd, size, digest
            finally:
                os.close(fd)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "CiphertextRepository"

    def setup(self):
        super().setup()
        self.connection.settimeout(self.server.repository.settings.request_timeout)

    def log_message(self, *_):
        pass

    def json_response(self, status, value):
        body = json.dumps(value, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        if self.command != "HEAD":
            self.wfile.write(body)

    def admission(self):
        match = ROUTE.fullmatch(self.path) or STATUS_ROUTE.fullmatch(self.path)
        if not match:
            raise Rejected(404, "not_found")
        self.server.repository.refresh_bindings()
        settings = self.server.repository.settings
        peer = ipaddress.ip_address(self.client_address[0])
        if not any(peer in network for network in settings.trusted_proxies) or self.headers.get_all("X-Forwarded-Proto") != ["https"]:
            raise Rejected(403, "https_ingress_required")
        environment, app = match.groups()[:2]
        expected = settings.bindings.get((environment, app))
        values = self.headers.get_all("Authorization") or []
        supplied = values[0][7:] if len(values) == 1 and values[0].startswith("Bearer ") else ""
        if not expected or not hmac.compare_digest(supplied, expected):
            raise Rejected(401, "unauthorized")
        if len(match.groups()) == 2:
            return (environment, app), None
        return match.groups()[:3], match.groups()[3]

    def serve(self):
        if self.path == "/health/live" and self.command in {"GET", "HEAD"}:
            return self.json_response(200, {"status": "live"})
        if self.path == "/health/ready" and self.command in {"GET", "HEAD"}:
            self.server.repository.ready()
            return self.json_response(200, {"status": "ready"})
        if self.command not in {"PUT", "GET", "HEAD"}:
            raise Rejected(405, "method_not_allowed")
        identifiers, artifact = self.admission()
        if artifact is None:
            if self.command not in {"GET", "HEAD"}:
                raise Rejected(405, "method_not_allowed")
            return self.json_response(200, self.server.repository.status(*identifiers))
        if self.command == "PUT":
            if not self.server.repository.settings.policies[identifiers[:2]]["enabled"]:
                raise Rejected(403, "backup_source_disabled")
            lengths = self.headers.get_all("Content-Length") or []
            if self.headers.get("Transfer-Encoding") or len(lengths) != 1 or not re.fullmatch(r"[0-9]{1,12}", lengths[0]):
                raise Rejected(400, "finite_content_length_required")
            length = int(lengths[0])
            if length < 1 or length > self.server.repository.settings.max_bytes:
                raise Rejected(413, "artifact_size_limit")
            hashes = self.headers.get_all("X-Ciphertext-SHA256") or []
            if len(hashes) != 1 or not SHA256.fullmatch(hashes[0]):
                raise Rejected(400, "ciphertext_hash_required")
            result = self.server.repository.put(identifiers, artifact, self.rfile, length, hashes[0])
            return self.json_response(201 if result["created"] else 200, result)
        with self.server.repository.get(identifiers, artifact) as (fd, length, digest):
            self.send_response(200)
            self.send_header("Content-Length", str(length))
            self.send_header("Content-Type", "application/octet-stream")
            self.send_header("X-Ciphertext-SHA256", digest)
            self.send_header("Cache-Control", "no-store")
            self.send_header("Connection", "close")
            self.end_headers()
            self.close_connection = True
            if self.command == "GET":
                while chunk := os.read(fd, CHUNK):
                    self.wfile.write(chunk)

    def dispatch(self):
        try:
            self.serve()
        except Rejected as error:
            self.json_response(error.status, {"error": error.code})
        except (OSError, ValueError):
            self.json_response(503, {"error": "repository_unavailable"})

    do_GET = do_HEAD = do_PUT = do_POST = do_DELETE = do_PATCH = dispatch


class Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address, repository):
        self.repository = repository
        self.slots = threading.BoundedSemaphore(repository.settings.workers)
        super().__init__(address, Handler)

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            try:
                request.sendall(b"HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            finally:
                self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def handle_error(self, *_):
        # Never emit request headers, tokens, ciphertext or exception payloads.
        pass


def retention_loop(repository):
    while True:
        try:
            repository.expire()
        except Exception:
            # No request, token or filesystem data enters logs.
            pass
        time.sleep(3600)


if __name__ == "__main__":
    try:
        settings = Settings.from_env()
        repository = Repository(settings)
        threading.Thread(target=retention_loop, args=(repository,), daemon=True).start()
        Server(("0.0.0.0", 8080), repository).serve_forever()
    except Exception:
        raise SystemExit("Repository configuration or startup failed; sensitive details suppressed.")
