"""Shared verified-bundle host primitives. No source build, seed implementation or destructive reset."""

import argparse
import base64
import contextlib
import errno
import fcntl
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import platform
import re
import secrets
import shutil
import socket
import stat
import subprocess
import tarfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class InstallError(Exception):
    pass


RELEASE_SCOPED_MAINTENANCE = ("upgrade", "restore")
MAINTENANCE_OPERATIONS = (
    "status", "backup", "verify-backup", "remove-backup", "schedule-backup", "upgrade",
    "restore", "restore-confirm", "transfer", "release", "stop", "remove",
    "backup-recovery", "verify-recovery", "rehearse-recovery",
    "retention-plan", "remove-recovery", "schedule-recovery")
SUCCESS_STATES = (
    "ready", "preflight_passed", "prepared", "initialized",
    "maintenance_idle", "backup_saved", "backup_verified", "backup_removed", "upgraded", "restore_confirmed",
    "transferred", "fence_released",
    "recovery_dependencies_saved", "recovery_dependencies_verified",
    "recovery_dependencies_rehearsed", "retention_plan", "recovery_archive_removed",
    "schedule_authored_not_installed")


def require(condition, message):
    if not condition:
        raise InstallError(message)


def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON field.")
        result[key] = value
    return result


def read_json(path):
    return json.loads(path.read_text(), object_pairs_hook=no_duplicates)


def path_chain(path):
    return (path, *path.parents)


def protected(path, directory=False):
    require(path.is_absolute(), "Use absolute protected paths.")
    for parent in path_chain(path):
        require(not parent.is_symlink(), "Symlink paths are forbidden.")
        if parent.exists():
            info = parent.stat()
            require(info.st_uid == 0 and not info.st_mode & 0o022,
                    "Path ancestors must be root-owned and not group/world writable.")
    info = path.stat()
    require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode),
            "Expected a protected directory or regular file.")
    require(stat.S_IMODE(info.st_mode) == 0o700 if directory
            else stat.S_IMODE(info.st_mode) in (0o400, 0o600),
            "Use directory 0700 and regular input/output 0600/0400.")


def make_directory(path):
    if not path.exists():
        path.mkdir(mode=0o700)
    protected(path, True)


def exclusive(path, data):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as output:
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    sync_directory(path.parent)


def sync_directory(path):
    fd = os.open(path, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def atomic_json(path, value):
    pending = path.with_name(path.name + ".pending")
    if pending.exists():
        protected(pending)
        pending.unlink()
    exclusive(pending, (json.dumps(value, sort_keys=True, indent=2) + "\n").encode())
    os.replace(pending, path)
    sync_directory(path.parent)


@contextlib.contextmanager
def installation_lock(root):
    """Persistent sibling lock: never unlink; T44 must use this same inode/path."""
    path = root.with_name(root.name + ".installation.lock")
    require(root.parent.exists(), "Installation parent must already exist.")
    for parent in path_chain(root.parent):
        info = parent.stat()
        require(not parent.is_symlink() and info.st_uid == 0 and not info.st_mode & 0o022,
                "Unsafe installation parent.")
    fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    try:
        protected(path)
        try:
            fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise InstallError("Another installation/maintenance operation holds the installation lock.") from error
        yield
    finally:
        os.close(fd)


def run(command, *, input=None, allowed=(0,), env=None):
    if env is None:
        env = {key: value for key, value in os.environ.items()
               if key in ("PATH", "HOME", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR")}
        env["DOCKER_HOST"] = "unix:///var/run/docker.sock"
    result = subprocess.run(command, input=input, capture_output=True, env=env, timeout=600)
    require(result.returncode in allowed,
            f"Command failed ({command[0]}, exit {result.returncode}); state retained. Inspect protected service diagnostics.")
    return result.stdout


def hostname(value):
    require(isinstance(value, str) and len(value) <= 253 and "." in value
            and all(re.fullmatch(r"[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?", label)
                    for label in value.split(".")), "Use lowercase fully-qualified DNS hostnames.")
    return value


def configuration(path):
    protected(path.parent, True)
    protected(path)
    require(path.stat().st_size <= 65536, "Configuration exceeds 64 KiB.")
    value = read_json(path)
    required = {"apiUrl", "consoleUrl", "apiHostname", "consoleHostname", "appDomain"}
    require(isinstance(value, dict) and required <= value.keys()
            and value.keys() <= required | {"manageDnsRecords", "sourceUrl", "recovery", "googleOidc"},
            "Unknown/missing configuration fields.")
    for key in ("apiHostname", "consoleHostname", "appDomain"):
        hostname(value[key])
    require(value["apiHostname"] == "api." + value["consoleHostname"],
            "API hostname must be api.<dashboard hostname>.")
    require(value["appDomain"] not in (value["apiHostname"], value["consoleHostname"]),
            "Application suffix must not equal a control-plane hostname.")
    for key, host in (("apiUrl", "apiHostname"), ("consoleUrl", "consoleHostname")):
        require(value[key] == "https://" + value[host], "Public URLs must be exact HTTPS origins without paths/ports.")
    require(type(value.get("manageDnsRecords", False)) is bool, "manageDnsRecords must be boolean.")
    if "googleOidc" in value:
        google = value["googleOidc"]
        require(isinstance(google, dict) and "enabled" in google
                and google.keys() <= {"enabled", "credentialsFile"}
                and type(google["enabled"]) is bool, "Invalid Google OIDC configuration.")
        require(google.keys() == ({"enabled", "credentialsFile"} if google["enabled"] else {"enabled"}),
                "Google OIDC requires credentialsFile only when explicitly enabled.")
        if google["enabled"]:
            require(isinstance(google["credentialsFile"], str) and google["credentialsFile"]
                    and Path(google["credentialsFile"]).is_absolute(),
                    "Google OIDC credentialsFile must be an absolute protected path.")
    if "sourceUrl" in value:
        require(isinstance(value["sourceUrl"], str), "sourceUrl must be an HTTPS string.")
        source = urllib.parse.urlsplit(value["sourceUrl"])
        require(source.scheme == "https" and source.hostname and not source.username
                and not source.password and not source.fragment, "Invalid HTTPS source URL.")
    if "recovery" in value:
        recovery = value["recovery"]
        require(isinstance(recovery, dict)
                and recovery.keys() <= {"openBaoUnsealMaterialPath", "backupDestination"}
                and all(isinstance(item, str) and item for item in recovery.values()),
                "Invalid recovery configuration.")
    return value


def google_provider(config, root, recovery_dir, assets_dir):
    if not config.get("googleOidc", {}).get("enabled", False):
        return None
    path = Path(config["googleOidc"]["credentialsFile"])
    require(path == path.resolve() and not any(path.is_relative_to(directory)
            for directory in (root, recovery_dir, assets_dir)),
            "Keep Google credentials at a canonical operator input path outside installation, recovery and release assets.")
    protected(path.parent, True)
    protected(path)
    require(path.stat().st_size <= 4096, "Google credential input exceeds 4 KiB.")
    try:
        value = read_json(path)
    except (ValueError, InstallError):
        raise InstallError("Invalid Google credential input; expected unique clientId/clientSecret fields.") from None
    require(isinstance(value, dict) and value.keys() == {"clientId", "clientSecret"}
            and isinstance(value["clientId"], str) and len(value["clientId"]) <= 256
            and re.fullmatch(r"[A-Za-z0-9_-]+\.apps\.googleusercontent\.com", value["clientId"])
            and isinstance(value["clientSecret"], str)
            and re.fullmatch(r"[A-Za-z0-9_-]{1,256}", value["clientSecret"]),
            "Invalid Google credential input; use a Google web client ID and a single-line URL-safe secret.")
    return json.dumps([{
        "id": "google", "provider": "google",
        "client_id": value["clientId"], "client_secret": value["clientSecret"],
        "mapper_url": "file:///etc/config/google-oidc.mapper.jsonnet",
        "scope": ["openid", "email", "profile"],
    }], separators=(",", ":"))


def version_at_least(text, minimum):
    match = re.search(r"(\d+)\.(\d+)(?:\.(\d+))?", text)
    return match is not None and tuple(int(part or 0) for part in match.groups()) >= minimum


class Host:
    def tools(self, *, require_cosign=True):
        require(os.geteuid() == 0, "Run as root.")
        os_release = dict(line.split("=", 1) for line in Path("/etc/os-release").read_text().splitlines()
                          if "=" in line)
        require(os_release.get("ID", "").strip('"') == "ubuntu"
                and os_release.get("VERSION_ID", "").strip('"') == "24.04",
                "Only dedicated Ubuntu 24.04 hosts are accepted.")
        require(platform.machine() in ("x86_64", "aarch64"), "Only amd64/arm64 hosts are accepted.")
        for tool in ("python3", "docker", "openssl", "ssh-keygen", *(("cosign",) if require_cosign else ())):
            require(shutil.which(tool), f"Install prerequisite independently: {tool}. The installer never replaces an engine.")
        # Inspect the existing daemon before every other Docker operation.
        info = json.loads(run(["docker", "info", "--format", "{{json .}}"]))
        require(info.get("OSType") == "linux" and not info.get("Swarm", {}).get("LocalNodeState") == "active",
                "A standalone Linux Docker Engine is required; Swarm hosts are not installation targets.")
        require(info.get("Architecture") in ("x86_64", "aarch64", "amd64", "arm64"),
                "Unsupported Docker architecture.")
        expected = ("x86_64", "amd64") if platform.machine() == "x86_64" else ("aarch64", "arm64")
        require(info["Architecture"] in expected, "Host and local Docker Engine architectures differ.")
        require(version_at_least(info.get("ServerVersion", ""), (24, 0, 0)), "Docker Engine 24+ is required.")
        require(version_at_least(run(["docker", "compose", "version", "--short"]).decode(), (2, 24, 0)),
                "Compose 2.24+ is required.")
        run(["docker", "buildx", "version"])
        return info

    def resources(self, root, docker_root, manifest):
        # Encoded operational floors, NOT a measured supported hardware claim.
        memory = int(re.search(r"MemTotal:\s+(\d+)", Path("/proc/meminfo").read_text())[1]) * 1024
        require(memory >= 2 * 1024**3, "Guard: at least 2 GiB physical RAM before starting seven images.")
        require((os.cpu_count() or 0) >= 2, "Guard: at least two logical CPUs before parallel dependency startup.")
        ancestor = root
        while not ancestor.exists():
            ancestor = ancestor.parent
        archive_bytes = manifest["bundleBytes"]
        for path in {ancestor, Path(docker_root)}:
            require(shutil.disk_usage(path).free >= max(10 * 1024**3, archive_bytes * 3),
                    "Guard: free installation/engine disk must exceed 10 GiB and three archive sizes. No automatic cleanup.")

    def conflicts(self, state):
        volumes = [name for name in run(["docker", "volume", "ls", "--format", "{{.Name}}"]).decode().splitlines()
                   if name.startswith("minicloud_")]
        networks = [name for name in run(["docker", "network", "ls", "--format", "{{.Name}}"]).decode().splitlines()
                    if name.startswith("minicloud_")]
        containers = run(["docker", "ps", "-a", "--format", "{{.ID}}"]).decode().splitlines()
        if state is None:
            require(not volumes and not networks and not containers,
                    "Dedicated host has existing containers/Minicloud volumes/networks; refusing adoption.")
            return set()
        owned_ports = set()
        for container in containers:
            item = json.loads(run(["docker", "inspect", container]))[0]
            labels = item.get("Config", {}).get("Labels", {})
            require(labels.get("com.docker.compose.project") == "minicloud"
                    and labels.get("com.docker.compose.project.working_dir") == state["bundlePath"],
                    "Foreign containers found; refusing dedicated-host mutation.")
            if labels.get("com.docker.compose.service") == "caddy":
                for target, bindings in item.get("NetworkSettings", {}).get("Ports", {}).items():
                    for binding in bindings or []:
                        owned_ports.add((int(binding["HostPort"]), target.split("/")[1]))
        for kind, names in (("volume", volumes), ("network", networks)):
            for name in names:
                item = json.loads(run(["docker", kind, "inspect", name]))[0]
                labels = item.get("Labels") or {}
                require(labels.get("com.docker.compose.project") == "minicloud"
                        and labels.get("com.minicloud.installation-id") == state["installationId"],
                        "Unrecognized populated volume/network; refusing adoption.")
        return owned_ports

    def ports(self, owned):
        for port, kind in ((80, socket.SOCK_STREAM), (443, socket.SOCK_STREAM), (443, socket.SOCK_DGRAM)):
            if (port, "tcp" if kind == socket.SOCK_STREAM else "udp") in owned:
                continue
            for family, address in ((socket.AF_INET, "0.0.0.0"), (socket.AF_INET6, "::")):
                try:
                    with socket.socket(family, kind) as connection:
                        if family == socket.AF_INET6:
                            connection.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
                        connection.bind((address, port))
                except OSError as error:
                    if family == socket.AF_INET6:
                        if error.errno in (errno.EAFNOSUPPORT, errno.EPROTONOSUPPORT):
                            continue
                        disabled = Path("/proc/sys/net/ipv6/conf/all/disable_ipv6")
                        if (error.errno == errno.EADDRNOTAVAIL and disabled.exists()
                                and disabled.read_text().strip() == "1"):
                            continue
                    raise InstallError(f"Port {port} is unavailable; preserve existing firewall/SSH/service policy.") from error

    def dns(self, config, addresses):
        expected = {str(ipaddress.ip_address(item)) for item in addresses}
        require(expected and all(ipaddress.ip_address(item).is_global for item in expected),
                "Supply this host's public IPv4/IPv6 addresses, never private/test ranges.")
        for host in (config["apiHostname"], config["consoleHostname"]):
            try:
                resolved = {str(ipaddress.ip_address(item[4][0])) for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)}
            except socket.gaierror:
                return False
            if not resolved or not resolved <= expected:
                return False
        return True


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        raise InstallError("Readiness redirected away from an exact configured HTTPS endpoint.")


def https_json(url, *, no_store=False):
    with urllib.request.build_opener(NoRedirect()).open(
            urllib.request.Request(url, headers={"Accept": "application/json"}), timeout=15) as response:
        require(response.status == 200 and response.headers.get_content_type() == "application/json",
                "Readiness endpoint did not return JSON success.")
        if no_store:
            require("no-store" in response.headers.get("Cache-Control", "").lower(),
                    "HTTPS setup transport must be no-store.")
        return json.loads(response.read(65537))


class Installer:
    def __init__(self, args, host=None):
        self.args = args
        self.root = args.root
        self.host = host or Host()
        self.state_path = self.root / "installation.json"
        self.state = None
        self.config = configuration(args.config)
        declared_recovery = self.config.get("recovery", {}).get("openBaoUnsealMaterialPath")
        require(declared_recovery is None or declared_recovery == str(args.recovery_dir),
                "Configured recovery path must equal the independent --recovery-dir.")
        protected(args.assets_dir, True)
        protected(args.recovery_dir, True)
        for left, right in ((self.root, args.assets_dir), (self.root, args.recovery_dir),
                            (args.assets_dir, args.recovery_dir)):
            require(not left.is_relative_to(right) and not right.is_relative_to(left),
                    "Installation, asset and independent recovery paths must not overlap.")
        require(not args.config.is_relative_to(self.root)
                and not args.config.is_relative_to(args.recovery_dir), "Keep operator input outside installation/recovery output.")
        self.google_provider = google_provider(self.config, self.root, args.recovery_dir, args.assets_dir)
        self.manifest = read_json(args.assets_dir / "server-manifest.json")
        require(self.manifest["releaseTag"] == args.tag, "Selected release differs from manifest.")
        self.manifest["bundleBytes"] = (args.assets_dir / self.manifest["bundle"]["name"]).stat().st_size
        # The release whose bundle is installed; differs from the selected release only during upgrade.
        self.installed = self.manifest

    def load_state(self, manifest=None):
        manifest = self.manifest if manifest is None else manifest
        if self.root.exists():
            protected(self.root, True)
            require(self.state_path.exists(), "Unknown installation directory; refusing adoption.")
            protected(self.state_path)
            state = read_json(self.state_path)
            require(isinstance(state, dict) and state.get("schemaVersion") == 1 and state.get("product") == "minicloud"
                    and state.get("root") == str(self.root), "Unknown installation metadata.")
            require(state["releaseTag"] == manifest["releaseTag"], "Existing installation requires an explicit upgrade, not reinstall.")
            require(state["configuration"] == self.config and state["recoveryDir"] == str(self.args.recovery_dir)
                    and state["publicAddresses"] == self.args.public_address,
                    "Resume must use the original configuration, recovery path and public addresses.")
            for key in ("serverVersion", "sourceRevision", "images", "dependencies", "configurationSchemaVersion",
                        "identitySchemaVersion", "applicationMigration"):
                require(state[key] == manifest[key], "Persisted release metadata mismatch.")
            require(state["bundleSha256"] == manifest["bundle"]["sha256"], "Persisted archive identity mismatch.")
            self.installed = manifest
            require(type(state["credentialsComplete"]) is bool
                    and state["phase"] in ("initializing", "prepared", "initialized", "awaiting_dns_tls", "awaiting_operator", "ready"),
                    "Unknown installation checkpoint; explicit reconciliation required.")
            for key in ("installationId", "operationId"):
                require(isinstance(state[key], str) and str(uuid.UUID(state[key])) == state[key],
                        "Invalid persistent operation identity.")
            self.verify_installed_bundle(
                state, allow_incomplete=state["phase"] == "initializing" and self.args.command in ("install", "prepare"))
            for path in self.root.rglob("*"):
                protected(path, path.is_dir())
            recovery = self.args.recovery_dir / state["installationId"]
            if recovery.exists():
                protected(recovery, True)
                for path in recovery.rglob("*"):
                    protected(path, path.is_dir())
            if state["credentialsComplete"]:
                required = (
                    "postgres-admin-password", "app-db-password", "kratos-db-password", "secret-encryption-key",
                    "kratos.env", "deploy-ssh-public-key", "ssh/deploy-key", "ssh/host-pins",
                    "openbao-ca.crt", "openbao-server.crt", "openbao-server.key", "bootstrap/input/request.json",
                )
                require(all((self.root / "secrets" / name).is_file() for name in required)
                        and (recovery / "openbao-ca.key").is_file(),
                        "Established credential missing; restore it, never regenerate.")
                self.validate_kratos_credentials()
                request = read_json(self.root / "secrets/bootstrap/input/request.json")
                require(request == {key: state[key] for key in ("installationId", "operationId")},
                        "Protected bootstrap input differs from the original operation.")
            self.state = state

    def verify_installed_bundle(self, state=None, *, allow_incomplete=False):
        state = self.state if state is None else state
        bundle = self.root / "bundle"
        require(state is not None and state["bundlePath"] == str(bundle) and bundle.resolve() == bundle,
                "Installed bundle path must equal the canonical installation root/bundle.")
        if not bundle.exists():
            require(allow_incomplete, "Installed bundle is missing; resume initial extraction with install/prepare.")
            return
        protected(bundle, True)
        expected = self.installed["files"]
        directories = {parent.as_posix() for name in expected for parent in Path(name).parents
                       if parent != Path(".")}
        installed = {}
        for path in bundle.rglob("*"):
            name = path.relative_to(bundle).as_posix()
            protected(path, path.is_dir())
            if path.is_dir():
                require(name in directories, "Installed bundle contains an unlisted directory; reconcile without overwriting.")
            else:
                require(name in expected, "Installed bundle contains an unlisted file; reconcile without overwriting.")
                installed[name] = path
        require(allow_incomplete or installed.keys() == expected.keys(),
                "Installed bundle inventory is incomplete; no runtime operation is allowed.")
        for name, path in installed.items():
            with path.open("rb") as source:
                require(hashlib.file_digest(source, "sha256").hexdigest() == expected[name],
                        "Installed bundle content differs from the signed release; reconcile without overwriting.")

    def preflight(self):
        self.load_state()
        info = self.host.tools()
        errors = []
        for check in (lambda: self.host.resources(self.root, info["DockerRootDir"], self.manifest),
                      lambda: self.host.ports(self.host.conflicts(self.state))):
            try:
                check()
            except InstallError as error:
                errors.append(str(error))
        dns = self.host.dns(self.config, self.args.public_address)
        require(not errors, "Preflight failed before installation mutation:\n" + "\n".join(errors))
        return {"state": "preflight_passed" if dns else "awaiting_dns_tls",
                "action": "Continue with install." if dns else "Point both hostnames to the supplied public addresses, then resume; no insecure fallback."}

    def save(self, **updates):
        self.state.update(updates)
        atomic_json(self.state_path, self.state)

    def initialize_state(self):
        self.root.mkdir(mode=0o700)
        self.state = {
            "schemaVersion": 1, "product": "minicloud", "root": str(self.root),
            "installationId": str(uuid.uuid4()), "operationId": str(uuid.uuid4()),
            "releaseTag": self.args.tag, "serverVersion": self.manifest["serverVersion"],
            "sourceRevision": self.manifest["sourceRevision"], "images": self.manifest["images"],
            "dependencies": self.manifest["dependencies"],
            "bundleSha256": self.manifest["bundle"]["sha256"],
            "configurationSchemaVersion": self.manifest["configurationSchemaVersion"],
            "identitySchemaVersion": self.manifest["identitySchemaVersion"],
            "applicationMigration": self.manifest["applicationMigration"],
            "configuration": self.config, "publicAddresses": self.args.public_address,
            "recoveryDir": str(self.args.recovery_dir), "bundlePath": str(self.root / "bundle"),
            "phase": "initializing", "credentialsComplete": False, "baoInitStarted": False,
            "baoConfigureStarted": False, "baoConfigured": False,
            "setupTokenFile": "setup-token-initial",
            "lastSuccessfulOperation": None,
        }
        if self.manifest.get("artifactTrust") == "operator-pinned-local-source":
            self.state.update(artifactTrust=self.manifest["artifactTrust"],
                              sourceArchiveSha256=self.manifest["sourceArchiveSha256"])
        atomic_json(self.state_path, self.state)
        sync_directory(self.root.parent)

    def extract(self):
        bundle = self.root / "bundle"
        make_directory(bundle)
        with tarfile.open(self.args.assets_dir / self.manifest["bundle"]["name"], "r:gz") as archive:
            for member in archive:
                require(member.isfile() and member.name in self.manifest["files"]
                        and not Path(member.name).is_absolute() and ".." not in Path(member.name).parts,
                        "Unsafe bundle member.")
                data = archive.extractfile(member).read()
                require(hashlib.sha256(data).hexdigest() == self.manifest["files"][member.name],
                        "Bundle changed after verification.")
                path = bundle / member.name
                for parent in reversed(path.parent.relative_to(bundle).parents):
                    if str(parent) != ".":
                        make_directory(bundle / parent)
                if path.parent != bundle:
                    make_directory(path.parent)
                if path.exists():
                    protected(path)
                    require(path.read_bytes() == data, "Existing bundle file changed; explicit reconciliation required.")
                else:
                    exclusive(path, data)
        self.verify_installed_bundle()

    def pull_verified_images(self):
        run(["python3", "-B", str(self.args.assets_dir / "verify-server-release.py"), "verify",
             "--directory", str(self.args.assets_dir), "--tag", self.args.tag, "--anonymous-pulls"])
        # T40 checks both platforms. Leave the host-native variant selected even on
        # classic Docker image stores which retain only the last platform pulled.
        config = self.args.assets_dir / ".local"
        make_directory(config)
        config = config / "artifacts"
        make_directory(config)
        config = config / "installer-anonymous"
        make_directory(config)
        require(not any(config.iterdir()), "Anonymous installer Docker configuration must remain empty.")
        native = "linux/amd64" if platform.machine() == "x86_64" else "linux/arm64"
        for reference in (self.manifest["images"] | self.manifest["dependencies"]).values():
            run(["docker", "--config", str(config), "pull", "--platform", native, reference])

    def ensure_secret(self, path, producer):
        if path.exists():
            protected(path)
            require(path.stat().st_size > 0, "Incomplete credential file; preserve and reconcile it.")
        else:
            require(not self.state["credentialsComplete"], "Established credential missing; restore it, never regenerate.")
            exclusive(path, producer())

    def credentials(self):
        directory = self.root / "secrets"
        for path in (directory, directory / "ssh", directory / "openbao-approle",
                     directory / "bootstrap", directory / "bootstrap/input", directory / "bootstrap/output"):
            make_directory(path)
        for name in ("postgres-admin-password", "app-db-password", "kratos-db-password"):
            self.ensure_secret(directory / name, lambda: secrets.token_urlsafe(48).encode())
            require(re.fullmatch(r"[A-Za-z0-9_-]{64}", (directory / name).read_text()),
                    "Incomplete/unrecognized database credential; restore its original value.")
        self.ensure_secret(directory / "secret-encryption-key", lambda: base64.b64encode(secrets.token_bytes(32)))
        require(re.fullmatch(rb"[A-Za-z0-9+/]{43}=", (directory / "secret-encryption-key").read_bytes()),
                "Incomplete/unrecognized encryption credential; restore its original value.")
        self.ensure_secret(directory / "kratos.env", lambda: (
            "DSN=postgres://kratos:" + urllib.parse.quote((directory / "kratos-db-password").read_text(), safe="")
            + "@postgres:5432/kratos?sslmode=disable\nSECRETS_COOKIE=" + secrets.token_urlsafe(48)
            + "\nSECRETS_CIPHER=" + secrets.token_hex(16) + "\n"
            + ("SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS=" + self.google_provider + "\n"
               if self.google_provider is not None else "")).encode())
        self.validate_kratos_credentials()
        key = directory / "ssh/deploy-key"
        if not key.exists():
            require(not self.state["credentialsComplete"], "Established SSH key missing; restore it.")
            require(not key.with_suffix(".pub").exists(), "SSH public key exists without its private key; refusing replacement.")
            run(["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", "minicloud-deployment", "-f", str(key)])
            os.chmod(key.with_suffix(".pub"), 0o600)
        protected(key)
        self.ensure_secret(directory / "deploy-ssh-public-key", lambda: run(["ssh-keygen", "-y", "-f", str(key)]))
        require((directory / "deploy-ssh-public-key").read_bytes().strip() == run(["ssh-keygen", "-y", "-f", str(key)]).strip(),
                "Deployment SSH public/private key mismatch.")
        pins = directory / "ssh/host-pins"
        if not pins.exists():
            require(not self.state["credentialsComplete"], "Established SSH pins missing; restore them.")
            exclusive(pins, b"")
        protected(pins)
        self.certificates(directory)
        request = directory / "bootstrap/input/request.json"
        data = (json.dumps({key: self.state[key] for key in ("installationId", "operationId")}) + "\n").encode()
        if request.exists():
            protected(request)
            require(request.read_bytes() == data, "Bootstrap request does not match original operation.")
        else:
            exclusive(request, data)
        self.save(credentialsComplete=True)

    def certificates(self, directory):
        recovery = self.args.recovery_dir / self.state["installationId"]
        make_directory(recovery)
        ca_key = recovery / "openbao-ca.key"
        server_key = directory / "openbao-server.key"
        for key in (ca_key, server_key):
            self.ensure_secret(key, lambda: run(["openssl", "genpkey", "-algorithm", "RSA", "-pkeyopt", "rsa_keygen_bits:3072"]))
        self.ensure_secret(directory / "openbao-ca.crt", lambda: run([
            "openssl", "req", "-new", "-x509", "-key", str(ca_key), "-days", "3650",
            "-subj", "/CN=Minicloud private OpenBao CA",
            "-addext", "basicConstraints=critical,CA:TRUE", "-addext", "keyUsage=critical,keyCertSign,cRLSign"]))
        self.ensure_secret(directory / "openbao-server.csr", lambda: run([
            "openssl", "req", "-new", "-key", str(server_key), "-subj", "/CN=openbao",
            "-addext", "subjectAltName=DNS:openbao"]))
        self.ensure_secret(directory / "openbao-server.crt", lambda: run([
            "openssl", "x509", "-req", "-in", str(directory / "openbao-server.csr"),
            "-CA", str(directory / "openbao-ca.crt"), "-CAkey", str(ca_key),
            "-set_serial", str(secrets.randbits(128)), "-days", "825", "-copy_extensions", "copy"]))
        run(["openssl", "verify", "-CAfile", str(directory / "openbao-ca.crt"),
             "-verify_hostname", "openbao", str(directory / "openbao-server.crt")])
        for key, certificate in ((ca_key, directory / "openbao-ca.crt"), (server_key, directory / "openbao-server.crt")):
            require(run(["openssl", "pkey", "-in", str(key), "-pubout"])
                    == run(["openssl", "x509", "-in", str(certificate), "-pubkey", "-noout"]),
                    "Existing OpenBao certificate/private key mismatch; preserve and reconcile.")

    def docker_environment(self):
        # Do not inherit Docker contexts, Compose overrides, registry credentials or secret env.
        env = {key: value for key, value in os.environ.items() if key in ("PATH", "HOME", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR")}
        env["DOCKER_HOST"] = "unix:///var/run/docker.sock"
        return env

    def host_id(self):
        # Host-local identity: never part of a backup, so a restored copy elsewhere gets its own.
        path = self.root / "host-id"
        if not path.exists():
            return ""
        protected(path)
        value = path.read_text().strip()
        require(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._:-]{0,127}", value), "Invalid protected host identity.")
        return value

    def environment(self):
        env = self.docker_environment()
        env.update({
            "API_URL": self.config["apiUrl"], "CONSOLE_URL": self.config["consoleUrl"],
            "API_HOSTNAME": self.config["apiHostname"], "CONSOLE_HOSTNAME": self.config["consoleHostname"],
            "APP_DOMAIN": self.config["appDomain"],
            "MANAGE_DNS_RECORDS": str(self.config.get("manageDnsRecords", False)).lower(),
            "MINICLOUD_SOURCE_URL": self.config.get("sourceUrl", "https://github.com/muniventures/minicloud"),
            "MINICLOUD_INSTALLATION_ID": self.state["installationId"],
            "MINICLOUD_SECRETS_DIR": str(self.root / "secrets"),
            "MINICLOUD_HOST_ID": self.host_id(),
            "MINICLOUD_GOOGLE_OIDC_ENABLED": str(self.google_provider is not None).lower(),
        })
        for component, reference in (self.installed["images"] | self.installed["dependencies"]).items():
            env[f"MINICLOUD_{component.upper()}_IMAGE"] = reference
        return env

    def validate_kratos_credentials(self):
        directory = self.root / "secrets"
        path = directory / "kratos.env"
        protected(path)
        require(path.stat().st_size <= 65536, "Kratos credentials exceed 64 KiB.")
        lines = path.read_text().splitlines()
        require(all("=" in line for line in lines), "Invalid Kratos credential entry.")
        kratos = no_duplicates(line.split("=", 1) for line in lines)
        keys = {"DSN", "SECRETS_COOKIE", "SECRETS_CIPHER"}
        if self.google_provider is not None:
            keys.add("SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS")
        protected(directory / "kratos-db-password")
        expected_dsn = ("postgres://kratos:" + urllib.parse.quote((directory / "kratos-db-password").read_text(), safe="")
                        + "@postgres:5432/kratos?sslmode=disable")
        require(kratos.keys() == keys
                and kratos["DSN"] == expected_dsn and len(kratos["SECRETS_COOKIE"]) >= 32
                and len(kratos["SECRETS_CIPHER"]) == 32
                and (self.google_provider is None
                     or kratos["SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS"] == self.google_provider),
                "Incomplete/inconsistent Kratos credentials; preserve and reconcile.")

    def compose_invocation(self, *arguments, root_token=None):
        self.verify_installed_bundle()
        if self.state["credentialsComplete"]:
            self.validate_kratos_credentials()
        bundle = self.root / "bundle"
        env = self.environment()
        if root_token:
            protected(root_token)
            env["OPENBAO_ROOT_TOKEN_FILE"] = str(root_token)
        if arguments[0] in ("up", "run"):
            arguments = (arguments[0], "--pull", "never", *arguments[1:])
        return ["docker", "compose", "--project-name", "minicloud", "--project-directory", str(bundle),
                "--env-file", str(bundle / "images.env"), "-f", str(bundle / "compose.yml"),
                "--profile", "maintenance", *arguments], env

    def compose(self, *arguments, input=None, allowed=(0,), root_token=None):
        command, env = self.compose_invocation(*arguments, root_token=root_token)
        return run(command, input=input, allowed=allowed, env=env)

    def bao(self, *arguments, input=None, allowed=(0,)):
        # Bao flags precede positional paths ("write sys/unseal -").
        split = next((index for index, value in enumerate(arguments) if value.startswith("-")), len(arguments))
        flags = ["-address=https://openbao:8200", "-ca-cert=/run/secrets/openbao-ca.crt"]
        return self.compose("exec", "-T", "openbao", "bao", *arguments[:split], *flags, *arguments[split:],
                            input=input, allowed=allowed)

    def bao_status(self, wait=False):
        for attempt in range(30 if wait else 1):
            output = self.bao("status", "-format=json", allowed=(0, 1, 2))
            if output:
                value = json.loads(output)
                require(type(value.get("initialized")) is bool and type(value.get("sealed")) is bool,
                        "Unexpected OpenBao status response.")
                return value
            if wait and attempt < 29:
                time.sleep(2)
        raise InstallError("OpenBao TLS status is unavailable; preserve state and inspect protected diagnostics.")

    def openbao(self):
        self.compose("up", "-d", "--no-deps", "openbao")
        # "initialized" and "sealed" are deliberately separate from container health.
        status = self.bao_status(wait=True)
        recovery = self.args.recovery_dir / self.state["installationId"]
        material = recovery / "openbao-recovery.json"
        if not status["initialized"]:
            require(not self.state["baoInitStarted"] and not material.exists(),
                    "Uncertain OpenBao initialization: preserve volume/recovery output; reconcile, never reinitialize.")
            self.save(baoInitStarted=True)
            response = self.bao("operator", "init", "-format=json", "-key-shares=3", "-key-threshold=2")
            exclusive(material, response)
        keys = self.unseal(status)
        token = recovery / "openbao-root-token"
        if token.exists():
            protected(token)
        else:
            # Reconstruct the original credential, never mint a replacement.
            exclusive(token, keys["root_token"].encode())
        require(token.read_text().strip() == keys["root_token"], "Independent root-token file differs from original recovery material.")
        outputs = [self.root / "secrets/openbao-approle" / name for name in ("role-id", "secret-id")]
        if self.state["baoConfigureStarted"]:
            require(all(path.exists() and path.stat().st_size for path in outputs),
                    "Uncertain/missing AppRole output: restore/reconcile original scoped credentials, never regenerate.")
        self.save(baoConfigureStarted=True)
        self.compose("run", "--rm", "-T", "--no-deps", "openbao-configure", root_token=token)
        for path in outputs:
            protected(path)
        self.save(baoConfigured=True)
        self.compose("up", "-d", "--wait", "--wait-timeout", "180", "openbao-agent")

    def unseal(self, status=None):
        """Unseal with the original independent recovery material; never reinitialize."""
        status = self.bao_status(wait=True) if status is None else status
        material = self.args.recovery_dir / self.state["installationId"] / "openbao-recovery.json"
        require(material.exists(), "Initialized OpenBao has no independent recovery file; restore original recovery material.")
        protected(material)
        keys = read_json(material)
        require(isinstance(keys, dict) and isinstance(keys.get("unseal_keys_b64"), list)
                and len(keys["unseal_keys_b64"]) >= 2
                and all(isinstance(item, str) and item for item in keys["unseal_keys_b64"])
                and isinstance(keys.get("root_token"), str) and keys["root_token"],
                "Incomplete OpenBao recovery output.")
        if status["sealed"]:
            for key in keys["unseal_keys_b64"][:2]:
                self.bao("write", "-format=json", "sys/unseal", "-", input=json.dumps({"key": key}).encode())
        status = self.bao_status()
        require(status["initialized"] and not status["sealed"], "OpenBao is not unsealed; preserve state.")
        return keys

    def bootstrap(self, operation="status", output=None):
        arguments = ["run", "--rm", "-T", "--no-deps", "api-bootstrap", "bootstrap", operation,
                     "--input-file", "/run/minicloud-bootstrap/input/request.json"]
        if output:
            arguments += ["--output-file", "/run/minicloud-bootstrap/output/" + output]
        return json.loads(self.compose(*arguments))

    def initialize(self):
        self.compose("up", "-d", "--wait", "--wait-timeout", "180", "postgres")
        self.compose("run", "--rm", "-T", "database-initialize")
        self.compose("run", "--rm", "-T", "api-migrate")
        self.compose("run", "--rm", "-T", "kratos-migrate")
        self.compose("up", "-d", "--wait", "--wait-timeout", "180", "kratos")
        self.openbao()
        status = self.bootstrap()
        require(status["state"] in ("required", "in_progress", "ready"), "Shared bootstrap reports blocked state; reconcile, never reset.")
        if status["state"] == "required":
            # Prepare is replay-safe; a lost output is never silently replaced or rotated.
            self.bootstrap("prepare", "setup-token-initial")
        self.save(phase="initialized")

    def activate(self):
        self.compose("up", "-d", "--wait", "--wait-timeout", "180", "api", "dashboard", "worker")
        self.compose("up", "-d", "caddy")
        self.save(phase="awaiting_dns_tls")

    def service_readiness(self):
        output = self.compose("ps", "--all", "--format", "json").decode().strip()
        values = json.loads(output) if output.startswith("[") else [json.loads(line) for line in output.splitlines()]
        require(isinstance(values, list), "Unexpected Compose readiness response.")
        services = {value["Service"]: value for value in values}
        for name in ("postgres", "openbao", "openbao-agent", "kratos", "api", "dashboard", "worker", "caddy"):
            service = services.get(name)
            require(service and service.get("State") == "running",
                    f"Required service {name} is not running; this is not ready.")
            if name not in ("worker", "caddy"):
                require(service.get("Health") == "healthy", f"Required service {name} is not healthy.")

    def readiness(self):
        status = self.bootstrap()
        require(status["state"] in ("required", "in_progress", "ready"), "Bootstrap blocked: inspect protected host state.")
        self.service_readiness()
        if not self.host.dns(self.config, self.args.public_address):
            return {"state": "awaiting_dns_tls", "action": "Correct both public DNS records, then run status."}
        try:
            with urllib.request.build_opener(NoRedirect()).open(self.config["apiUrl"] + "/health", timeout=15) as response:
                require(response.status == 200 and response.read(64) == b"Healthy",
                        "HTTPS application health check failed.")
            instance = https_json(self.config["apiUrl"] + "/api/instance")
            remote = https_json(self.config["apiUrl"] + "/api/account/setup", no_store=True)
            dashboard = https_json(self.config["consoleUrl"] + "/runtime-config.json")
            require(dashboard.get("apiBaseUrl") == self.config["apiUrl"]
                    and dashboard.get("appDomain") == self.config["appDomain"]
                    and dashboard.get("auth") == {
                        "origin": self.config["apiUrl"],
                        "capabilitiesUrl": self.config["apiUrl"] + "/api/account/capabilities"},
                    "HTTPS dashboard runtime configuration differs from this installation.")
            with urllib.request.build_opener(NoRedirect()).open(self.config["consoleUrl"] + "/setup", timeout=15) as response:
                require(response.status == 200 and response.headers.get_content_type() == "text/html",
                        "Dashboard HTTPS setup route is not ready.")
            require(instance.get("serverVersion") == self.state["serverVersion"]
                    and instance.get("sourceRevision") == self.state["sourceRevision"],
                    "HTTPS API does not identify this exact installed release.")
            require(remote.get("state") == status["state"], "HTTPS setup state differs from protected host bootstrap.")
        except urllib.error.HTTPError as error:
            raise InstallError(f"HTTPS readiness returned HTTP {error.code}; this is not successful setup.") from error
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            return {"state": "awaiting_dns_tls", "action": "Inspect DNS, inbound 80/443 and Caddy ACME diagnostics; preserve state and run status."}
        if status["state"] != "ready":
            token = self.root / "secrets/bootstrap/output" / self.state["setupTokenFile"]
            return {"state": "awaiting_operator", "setupUrl": self.config["consoleUrl"] + "/setup",
                    "tokenFile": str(token),
                    "expiresAt": status.get("expiresAt"),
                    "action": "Read the protected token locally; complete HTTPS setup. For lost/expired output use shared host rotate-token with a new output file."}
        return {"state": "ready", "dashboardUrl": self.config["consoleUrl"],
                "action": "Sign in; provider/plan configuration is explicit. Future version changes require upgrade."}

    def execute(self):
        if self.args.command == "maintenance":
            require("server_maintenance.py" in self.manifest["files"],
                    "Selected release does not provide signed maintenance tooling.")
            if self.args.maintenance_operation in RELEASE_SCOPED_MAINTENANCE:
                # Upgrade/restore run the selected (independently verified) release's tooling and
                # establish the installed state themselves; the installed bundle may differ or be absent.
                with tarfile.open(self.args.assets_dir / self.manifest["bundle"]["name"], "r:gz") as archive:
                    code = archive.extractfile("server_maintenance.py").read()
                require(hashlib.sha256(code).hexdigest() == self.manifest["files"]["server_maintenance.py"],
                        "Maintenance helper differs from the signed release.")
            else:
                self.load_state()
                require(self.state is not None and self.state["credentialsComplete"],
                        "Maintenance requires the original complete installation and verified exact installed release.")
                self.verify_installed_bundle()
                code = (self.root / "bundle/server_maintenance.py").read_bytes()
            namespace = {"__name__": "minicloud_verified_maintenance"}
            exec(compile(code, "verified-release/server_maintenance.py", "exec"), namespace)
            return namespace["execute"](self, globals())
        # Main holds the shared installation lock here; do not race seed against
        # an interrupted maintenance operation between inspection and execution.
        maintenance = self.root / "maintenance.json"
        require(not maintenance.with_name("maintenance.json.pending").exists()
                and not maintenance.with_name("maintenance.json.pending").is_symlink(),
                "Interrupted maintenance metadata requires reconciliation.")
        if maintenance.exists() or maintenance.is_symlink():
            protected(maintenance)
            record = read_json(maintenance)
            require(isinstance(record, dict)
                    and not any(key in record for key in
                                ("pendingUpgrade", "pendingRestore", "restoreInProgress", "heldFence", "error"))
                    and set(record) <= {"operation", "at", "state", "lastSuccessfulBackup", "lastSuccessfulUpgrade",
                                        "lastSuccessfulRecovery", "lastFailure"}
                    and record.get("state") in
                    ("backup_saved", "backup_verified", "backup_removed", "upgraded", "restore_confirmed",
                     "fence_released", "maintenance_idle", "recovery_dependencies_verified",
                     "recovery_dependencies_saved", "recovery_dependencies_rehearsed", "retention_plan",
                     "recovery_archive_removed", "schedule_authored_not_installed")
                    and ("lastFailure" not in record or isinstance(record["lastFailure"], dict)
                         and record["lastFailure"].get("fenceReleased") is True),
                    "Incomplete or unknown maintenance state requires explicit reconciliation, not seed/install.")
        result = self.preflight()
        if self.args.command == "preflight":
            return result
        if self.args.command in ("install", "prepare", "initialize", "activate"):
            require(self.state is None or self.state["phase"] != "ready",
                    "Recognized finished installation: use explicit upgrade, not reinstall.")
            if result["state"] == "awaiting_dns_tls" and not getattr(self.args, "defer_dns", False):
                return result
            if self.args.command in ("install", "prepare"):
                self.pull_verified_images()
                if self.state is None:
                    self.initialize_state()
                self.extract()
                self.credentials()
                if self.state["phase"] == "initializing":
                    self.save(phase="prepared")
                if self.args.command == "prepare":
                    return {"state": "prepared", "action": "Run initialize, then activate, then status with these same inputs."}
            else:
                require(self.state is not None and self.state["credentialsComplete"],
                        "Run prepare before manual initialization/activation.")
            if self.args.command in ("install", "initialize"):
                self.initialize()
                if self.args.command == "initialize":
                    return {"state": "initialized", "action": "Run activate, then status with the same inputs."}
            require(self.state["phase"] in ("initialized", "awaiting_dns_tls", "awaiting_operator"),
                    "Initialization is incomplete; resume initialize before activating.")
            self.activate()
        else:
            require(self.state is not None and self.state["credentialsComplete"], "Installation is incomplete; resume install.")
        if self.args.command == "rotate-token":
            name = self.args.token_output_name
            require(name and re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._-]{0,100}", name),
                    "Rotation requires a new simple --token-output-name.")
            path = self.root / "secrets/bootstrap/output" / name
            require(not path.exists() and not path.is_symlink(), "Token output must be new; never overwrite it.")
            require(self.bootstrap()["state"] in ("required", "in_progress"), "Finished/blocked setup cannot rotate an installer token.")
            self.bootstrap("rotate-token", name)
            protected(path)
            self.save(setupTokenFile=name)
        result = self.readiness()
        self.save(phase=result["state"],
                  lastSuccessfulOperation=self.state["operationId"] if result["state"] == "ready" else self.state["lastSuccessfulOperation"])
        return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("preflight", "install", "prepare", "initialize", "activate", "status", "rotate-token", "maintenance"))
    parser.add_argument("maintenance_operation", nargs="?", choices=MAINTENANCE_OPERATIONS)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--assets-dir", type=Path, required=True)
    parser.add_argument("--root", type=Path, default=Path("/opt/minicloud"))
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--recovery-dir", type=Path, required=True)
    parser.add_argument("--public-address", action="append", required=True)
    parser.add_argument("--token-output-name")
    parser.add_argument("--backup-dir", type=Path)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--recipients-file", type=Path)
    parser.add_argument("--identity-file", type=Path)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--keep", type=int)
    parser.add_argument("--confirm-backup-id")
    parser.add_argument("--at")
    parser.add_argument("--previous-assets-dir", type=Path)
    parser.add_argument("--drain-timeout", type=int, default=1800)
    parser.add_argument("--confirm-replace-live")
    parser.add_argument("--confirm-operation-id")
    parser.add_argument("--keep-fenced", action="store_true")
    args = parser.parse_args(argv)
    os.umask(0o077)
    require(os.geteuid() == 0, "Run as root after independent signature verification.")
    require((args.command == "maintenance") == (args.maintenance_operation is not None),
            "Specify a maintenance operation only with the maintenance command.")
    require(all(path.is_absolute() for path in (args.assets_dir, args.root, args.config, args.recovery_dir)),
            "All paths must be absolute.")
    require(all(path == path.resolve() for path in (args.assets_dir, args.root, args.config, args.recovery_dir)),
            "Use canonical paths without symlinks or '..'.")
    with installation_lock(args.root):
        installer = Installer(args)
        try:
            result = installer.execute()
        except (InstallError, OSError, ValueError, KeyError, TypeError, tarfile.TarError, subprocess.TimeoutExpired) as error:
            if installer.state:
                installer.save(lastFailure="Installation/readiness failed; preserve state and inspect protected diagnostics.")
            detail = str(error) if isinstance(error, InstallError) else "Invalid/incomplete protected input or unavailable host dependency."
            raise InstallError(f"Failed: {detail} Original operation, volumes and credentials retained; resume after reconciliation.") from error
    print(json.dumps(result, sort_keys=True))
    return 0 if result["state"] in SUCCESS_STATES else 2


if __name__ == "__main__":
    raise SystemExit("Use the independently verified install-server.sh; never invoke an unverified helper directly.")
