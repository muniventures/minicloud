#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Explicit operator-pinned source seeding; never a fallback for signed releases."""

import argparse
import gzip
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import tarfile


TRUST = "operator-pinned-local-source"
MAX_ARCHIVE = 256 * 1024**2
MAX_EXPANDED = 512 * 1024**2


def require(value, message):
    if not value:
        raise ValueError(message)


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def read_json(path):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, "Duplicate JSON property.")
            result[key] = value
        return result
    return json.loads(path.read_text(), object_pairs_hook=unique)


def encoded(value):
    return (json.dumps(value, sort_keys=True, indent=2) + "\n").encode()


def source_members(archive):
    seen, expanded = set(), 0
    for member in archive:
        name = member.name.rstrip("/")
        parts = name.split("/")
        require(re.fullmatch(r"[A-Za-z0-9._/-]+", name)
                and all(part not in ("", ".", "..", ".git", ".local") for part in parts)
                and not name.startswith("/") and name not in seen
                and (member.isfile() or member.isdir()), "Unsafe/duplicate source archive member.")
        expanded += member.size
        seen.add(name)
        require(len(seen) <= 20000 and 0 <= member.size <= 64 * 1024**2
                and expanded <= MAX_EXPANDED, "Source archive exceeds safety bounds.")
        yield member


def package_source(root, output, version):
    require(re.fullmatch(r"\d+\.\d+\.\d+-local\.[A-Za-z0-9.-]+", version),
            "Use a distinct prerelease version such as 0.0.1-local.1.")
    require(output.is_relative_to(root / ".local/artifacts") and not output.exists(),
            "Choose a new output under the repository's ignored .local/artifacts/.")
    subprocess.run(["git", "check-ignore", str(output)], cwd=root, check=True, stdout=subprocess.DEVNULL)
    require(not subprocess.check_output(["git", "status", "--porcelain"], cwd=root).strip(),
            "Commit the reviewed implementation locally before packaging a fixed source revision.")
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    descriptor = {
        "schemaVersion": 1, "kind": TRUST, "sourceRevision": revision, "serverVersion": version,
        "cliCompatibility": {"protocolVersion": 1, "minimumVersion": None,
                             "maximumExclusiveVersion": None, "sourceRevisions": [revision]},
    }
    original = subprocess.check_output(["git", "archive", "--format=tar", revision], cwd=root)
    output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with output.open("xb") as target, gzip.GzipFile(fileobj=target, mode="wb", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w") as archive:
            with tarfile.open(fileobj=io.BytesIO(original)) as source:
                for member in source_members(source):
                    archive.addfile(member, source.extractfile(member) if member.isfile() else None)
            data = encoded(descriptor)
            member = tarfile.TarInfo("minicloud-local-source.json")
            member.size, member.mode = len(data), 0o600
            archive.addfile(member, io.BytesIO(data))
        target.flush()
    require(output.stat().st_size <= MAX_ARCHIVE, "Source archive exceeds safety bounds.")
    print(json.dumps({"archive": str(output), "sha256": digest(output), **descriptor}, sort_keys=True))


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def extract_source(archive, expected, destination):
    require(re.fullmatch(r"[0-9a-f]{64}", expected)
            and 0 < archive.stat().st_size <= MAX_ARCHIVE
            and digest(archive) == expected, "Source archive differs from the explicit SHA256.")
    destination.mkdir(mode=0o700, exist_ok=True)
    require(not destination.is_symlink(), "Source destination cannot be a symlink.")
    expected_paths = set()
    with tarfile.open(archive, "r:gz") as source:
        for member in source_members(source):
            path = destination / member.name
            expected_paths.add(path)
            for parent in path.parents:
                if parent == destination:
                    break
                expected_paths.add(parent)
            if member.isdir():
                path.mkdir(mode=0o700, parents=True, exist_ok=True)
                require(path.is_dir() and not path.is_symlink(), "Source directory conflict.")
                continue
            path.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            data = source.extractfile(member).read()
            require(not path.is_symlink(), "Source file cannot be a symlink.")
            if path.exists():
                require(path.is_file() and path.read_bytes() == data,
                        "Existing source differs; never overwrite an interrupted build.")
            else:
                with path.open("xb") as target:
                    target.write(data)
                path.chmod(0o700 if member.mode & 0o111 else 0o600)
    for path in destination.rglob("*"):
        if not path.is_relative_to(destination / ".local"):
            require(path in expected_paths and not path.is_symlink(), "Unlisted source file.")
    metadata = read_json(destination / "minicloud-local-source.json")
    require(set(metadata) == {"schemaVersion", "kind", "sourceRevision", "serverVersion", "cliCompatibility"}
            and metadata["schemaVersion"] == 1 and metadata["kind"] == TRUST
            and re.fullmatch(r"[0-9a-f]{40}", metadata["sourceRevision"]),
            "Invalid local source descriptor.")
    return metadata


def verify_images(images, native, revision, *, complete=True):
    require(set(images) == {"api", "dashboard", "worker"} if complete else len(images) == 1,
            "Incomplete native image inventory.")
    for reference in images.values():
        require(re.fullmatch(r"sha256:[0-9a-f]{64}", reference), "Pin native image content IDs.")
        value = json.loads(subprocess.check_output(["docker", "image", "inspect", reference]))[0]
        require(value["Id"] == reference and value["Os"] + "/" + value["Architecture"] == native
                and value["Config"]["Labels"]["org.opencontainers.image.revision"] == revision,
                "Local image content/platform/source differs; do not rebuild or adopt silently.")


def build_swap(operation_root, shared):
    memory = int(re.search(r"MemTotal:\s+(\d+)", Path("/proc/meminfo").read_text())[1]) * 1024
    if memory >= 4 * 1024**3:
        return
    path = operation_root / "build.swap"
    if not path.exists():
        require(not path.is_symlink(), "Swap file cannot be a symlink.")
        shared.exclusive(path, b"")
        shared.run(["fallocate", "-l", str(4 * 1024**3), str(path)])
        shared.run(["mkswap", str(path)])
    shared.protected(path)
    require(path.stat().st_size == 4 * 1024**3, "Partial build swap requires reconciliation.")
    name = shared.run(["systemd-escape", "--path", "--suffix=swap", str(path)]).decode().strip()
    require(re.fullmatch(r"[A-Za-z0-9._\\-]+\.swap", name), "Invalid swap unit name.")
    unit = Path("/etc/systemd/system") / name
    data = ("[Unit]\nDescription=Minicloud local-source build swap\n[Swap]\nWhat=" + str(path)
            + "\n[Install]\nWantedBy=swap.target\n").encode()
    if unit.exists():
        require(not unit.is_symlink() and unit.read_bytes() == data, "Conflicting swap unit.")
    else:
        shared.exclusive(unit, data)
        shared.run(["systemctl", "daemon-reload"])
    shared.run(["systemctl", "enable", "--now", name])


def build_images(root, output, metadata, native, release):
    checkpoint = output / "images.json"
    revision = metadata["sourceRevision"]
    if checkpoint.exists():
        images = read_json(checkpoint)
        verify_images(images, native, revision)
        return images
    images = {}
    with (output / "build.log").open("ab") as log:
        for component in ("api", "dashboard", "worker"):
            iid = output / (component + ".iid")
            # An interrupted build can be retried on this same target and source, never a new VPS.
            if iid.exists():
                reference = iid.read_text().strip()
                verify_images({component: reference}, native, revision, complete=False)
            else:
                subprocess.run([
                    "docker", "build", "--platform", native, "--file", str(root / f"modules/{component}/Dockerfile"),
                    "--label", f"org.opencontainers.image.revision={revision}",
                    "--label", f"com.minicloud.artifact-trust={TRUST}",
                    "--iidfile", str(iid), str(root),
                ], check=True, stdout=log, stderr=subprocess.STDOUT, timeout=3600)
                reference = iid.read_text().strip()
            images[component] = reference
            if component == "worker":
                release.verify_agent(reference, native.split("/")[1], output / "agent")
    verify_images(images, native, revision)
    checkpoint.write_bytes(encoded(images))
    return images


def package_native(root, output, metadata, images, archive_digest, native, release):
    value = read_json(root / "packaging/server/release.json")
    value.update(serverVersion=metadata["serverVersion"], releaseTag="server-v" + metadata["serverVersion"],
                 sourceRevision=metadata["sourceRevision"], cliCompatibility=metadata["cliCompatibility"],
                 architectures=[native], images=images, artifactTrust=TRUST, sourceArchiveSha256=archive_digest)
    release.validate_compatibility(value["cliCompatibility"])
    require(all(re.fullmatch(release.IMAGE, ref) for ref in value["dependencies"].values()),
            "Dependencies must retain immutable registry digests.")
    files = release.payload_files(root / "packaging/server/bundle")
    compose = files["compose.yml"].decode()
    for key, reference in release.image_environment(value).items():
        compose = re.sub(r"\$\{" + key + r"(?::\?[^}]*)?\}", lambda _: reference, compose)
    files["compose.yml"] = compose.encode()
    files["images.env"] = release.environment_bytes(value)
    files["server-release.json"] = encoded(value)
    name = f"minicloud-server-{value['serverVersion']}.tar.gz"
    memory = io.BytesIO()
    with gzip.GzipFile(fileobj=memory, mode="wb", mtime=0) as compressed:
        with tarfile.open(fileobj=compressed, mode="w") as archive:
            for filename, data in sorted(files.items()):
                member = tarfile.TarInfo(filename)
                member.size, member.mode = len(data), 0o600
                archive.addfile(member, io.BytesIO(data))
    bundle = memory.getvalue()
    value["bundle"] = {"name": name, "sha256": hashlib.sha256(bundle).hexdigest()}
    value["files"] = {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}
    output.mkdir(mode=0o700, exist_ok=True)
    for name, data in {name: bundle, "server-manifest.json": encoded(value)}.items():
        path = output / name
        if path.exists():
            require(not path.is_symlink() and path.read_bytes() == data,
                    "Existing local assets differ; preserve the original installation.")
        else:
            with path.open("xb") as target:
                target.write(data)
    return value


def refresh_partial(installer, manifest, expected_previous, shared):
    state = installer.state
    require(state is not None and state["phase"] == "prepared"
            and state["artifactTrust"] == TRUST and state["sourceArchiveSha256"] == expected_previous
            and not state["baoInitStarted"] and not state["baoConfigureStarted"] and not state["baoConfigured"],
            "Source refresh is limited to the original prepared local installation before OpenBao initialization.")
    for field in ("releaseTag", "serverVersion", "dependencies", "applicationMigration",
                  "configurationSchemaVersion", "identitySchemaVersion"):
        require(state[field] == manifest[field], "Partial refresh cannot change release/dependencies/schema contracts.")
    require(state["sourceRevision"] != manifest["sourceRevision"], "Source refresh requires a new reviewed source.")
    for name in ("maintenance.json", "maintenance.json.pending", "local-source-refresh.pending"):
        require(not (installer.root / name).exists() and not (installer.root / name).is_symlink(),
                "Maintenance/interrupted refresh requires reconciliation.")
    previous = installer.root / ("bundle.previous-" + expected_previous)
    require(not previous.exists() and not previous.is_symlink(), "Previous bundle checkpoint already exists.")
    pending = installer.root / "local-source-refresh.pending"
    audit = {"previousSource": state["sourceRevision"], "previousSourceSha256": expected_previous,
             "sourceRevision": manifest["sourceRevision"], "sourceSha256": manifest["sourceArchiveSha256"],
             "installationId": state["installationId"], "operationId": state["operationId"]}
    shared.exclusive(pending, encoded(audit))
    (installer.root / "bundle").rename(previous)
    shared.sync_directory(installer.root)
    state.update(sourceRevision=manifest["sourceRevision"], images=manifest["images"],
                 sourceArchiveSha256=manifest["sourceArchiveSha256"], bundleSha256=manifest["bundle"]["sha256"])
    shared.atomic_json(installer.state_path, state)
    installer.installed = manifest
    installer.manifest = manifest
    installer.extract()
    pending.rename(installer.root / ("local-source-refresh-" + manifest["sourceArchiveSha256"] + ".json"))
    shared.sync_directory(installer.root)


def install(args):
    require(os.geteuid() == 0, "Dedicated-host installation requires root.")
    require(args.archive.is_absolute() and args.archive == args.archive.resolve(), "Use a canonical source archive.")
    for path in (args.archive, *args.archive.parents):
        info = path.lstat()
        require(not path.is_symlink() and info.st_uid == 0 and not info.st_mode & 0o022,
                "Source inputs must have protected root-owned ancestors.")
    source = args.archive.parent / "source"
    metadata = extract_source(args.archive, args.sha256, source)
    require(args.tag == "server-v" + metadata["serverVersion"], "Selected version differs from local source.")
    shared = module("local_server_install", source / "packaging/server/bundle/server_install.py")
    release = module("local_release", source / "scripts/server-release/release.py")
    native = {"x86_64": "linux/amd64", "aarch64": "linux/arm64"}.get(platform.machine())
    require(native is not None, "Unsupported build host.")
    output = source / ".local/artifacts/local-seed"
    output.mkdir(mode=0o700, parents=True, exist_ok=True)
    operation_root = args.config.parent
    local_assets = args.archive.parent / "local-assets"

    class LocalHost(shared.Host):
        def tools(self):
            return super().tools(require_cosign=False)

        def resources(self, root, docker_root, manifest):
            # Nominal 2-GiB VPSes reserve some RAM for the kernel/hypervisor.
            return super().resources(root, docker_root, manifest, reserved_memory_bytes=256 * 1024**2)

    class LocalInstaller(shared.Installer):
        def load_state(self, manifest=None):
            super().load_state(manifest)
            if self.state:
                require(self.state.get("artifactTrust") == TRUST
                        and self.state.get("sourceArchiveSha256") == args.sha256,
                        "Installed artifact authority differs from this approved local source.")

        def pull_verified_images(self):
            verify_images(self.manifest["images"], native, metadata["sourceRevision"])
            for reference in self.manifest["dependencies"].values():
                # No operator registry credentials or floating product images.
                shared.run(["docker", "--config", str(output / "anonymous"), "pull", "--platform", native, reference])

    args.root = Path("/opt/minicloud")
    args.assets_dir = local_assets
    args.command, args.defer_dns = "install", True
    args.token_output_name = None
    host = LocalHost()
    with shared.installation_lock(args.root):
        info = host.tools()
        host.resources(args.root, info["DockerRootDir"], {"bundleBytes": args.archive.stat().st_size})
        previous_installer = None
        if not args.root.exists():
            require(args.previous_source_sha256 is None, "A fresh host cannot refresh source.")
            host.conflicts(None)
        else:
            require(not (args.root / "local-source-refresh.pending").exists(), "Interrupted source refresh requires reconciliation.")
            stored = read_json(args.root / "installation.json")
            stored_digest = stored.get("sourceArchiveSha256", "")
            require(stored.get("artifactTrust") == TRUST and re.fullmatch(r"[0-9a-f]{64}", stored_digest),
                    "Only an established local-source installation can continue.")
            previous_assets = operation_root / "assets" / stored_digest / "local-assets"
            if not previous_assets.exists():
                previous_assets = operation_root / "local-assets"
            require((previous_assets / "server-manifest.json").exists(), "Original local assets are missing.")
            previous_args = argparse.Namespace(**vars(args))
            previous_args.assets_dir = previous_assets
            previous_installer = shared.Installer(previous_args, host)
            previous_installer.load_state()
            host.conflicts(previous_installer.state)
            if stored_digest != args.sha256:
                require(args.previous_source_sha256 == stored_digest, "Review an explicit previous source hash before refreshing.")
            else:
                require(args.previous_source_sha256 is None, "Source refresh is already applied; replan without the refresh flag.")
        anonymous = output / "anonymous"
        anonymous.mkdir(mode=0o700, exist_ok=True)
        require(not any(anonymous.iterdir()), "Anonymous Docker configuration must remain empty.")
        build_swap(operation_root, shared)
        images = build_images(source, output, metadata, native, release)
        manifest = package_native(source, local_assets, metadata, images, args.sha256, native, release)
        if args.previous_source_sha256 is not None:
            previous_installer.args.assets_dir = local_assets
            refresh_partial(previous_installer, manifest, args.previous_source_sha256, shared)
        installer = LocalInstaller(args, host)
        result = installer.execute()
    print(json.dumps({**result, "artifactTrust": TRUST, "sourceArchiveSha256": args.sha256}, sort_keys=True))
    return 0 if result["state"] in shared.SUCCESS_STATES else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    package = commands.add_parser("package")
    package.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    package.add_argument("--output", type=Path, required=True)
    package.add_argument("--version", required=True)
    deploy = commands.add_parser("install")
    deploy.add_argument("--archive", type=Path, required=True)
    deploy.add_argument("--sha256", required=True)
    deploy.add_argument("--tag", required=True)
    deploy.add_argument("--config", type=Path, required=True)
    deploy.add_argument("--recovery-dir", type=Path, required=True)
    deploy.add_argument("--public-address", action="append", required=True)
    deploy.add_argument("--previous-source-sha256")
    args = parser.parse_args()
    os.umask(0o077)
    if args.command == "package":
        package_source(args.root.resolve(), args.output.absolute(), args.version)
        return 0
    return install(args)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        print("Local source deployment failed; retain target, source hash, protected build logs and recovery material.", file=sys.stderr)
        sys.exit(1)
