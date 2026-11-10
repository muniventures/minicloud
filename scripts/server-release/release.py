#!/usr/bin/env python3
# SPDX-License-Identifier: MPL-2.0
"""Build and verify the distinct Minicloud server release channel."""

import argparse
import base64
import gzip
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tarfile
import tempfile


REPOSITORY = "muniventures/minicloud"
SOURCE_URI = f"git+https://github.com/{REPOSITORY}"
WORKFLOW = ".github/workflows/server-release.yml"
ISSUER = "https://token.actions.githubusercontent.com"
ARCHITECTURES = ["linux/amd64", "linux/arm64"]
COMPONENTS = ("api", "dashboard", "worker")
DEPENDENCIES = {"postgres", "openbao", "kratos", "caddy"}
REQUIRED_PAYLOAD = {
    "compose.yml", "configuration.schema.json", "configuration.example.json",
    "install-server.sh", "maintenance.sh", "release-notes.md", "upgrade-prerequisites.md",
}
INPUT_FIELDS = {
    "schemaVersion", "serverVersion", "architectures", "dependencies",
    "applicationMigration", "identitySchemaVersion", "configurationSchemaVersion",
    "cliCompatibility",
}
VERSION = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?"
SHA = r"[0-9a-f]{40}"
DIGEST = r"sha256:[0-9a-f]{64}"
IMAGE = rf"[a-z0-9][a-z0-9./:_-]*@{DIGEST}"


class ReleaseError(ValueError):
    pass


def require(condition, message):
    if not condition:
        raise ReleaseError(message)


def matches(pattern, value):
    return isinstance(value, str) and re.fullmatch(pattern, value) is not None


def version_key(value):
    require(matches(VERSION, value), f"Invalid semantic version: {value!r}")
    core, separator, prerelease = value.partition("-")
    identifiers = prerelease.split(".") if separator else []
    require(all(not (part.isdigit() and len(part) > 1 and part.startswith("0"))
                for part in identifiers), f"Invalid numeric prerelease: {value}")
    return (tuple(map(int, core.split("."))), not separator,
            tuple((0, int(part)) if part.isdigit() else (1, part) for part in identifiers))


def version_from_tag(tag):
    require(isinstance(tag, str) and tag.startswith("server-v"), "Select an exact server-v<version> tag.")
    version = tag[len("server-v"):]
    version_key(version)
    return version


def identity(tag):
    version_from_tag(tag)
    return f"https://github.com/{REPOSITORY}/{WORKFLOW}@refs/tags/{tag}"


def json_bytes(value):
    return (json.dumps(value, indent=2, sort_keys=True) + "\n").encode()


def read_json(path):
    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            require(key not in result, f"Duplicate JSON key: {key}")
            result[key] = value
        return result
    return json.loads(Path(path).read_text(), object_pairs_hook=unique_keys)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def run(command, **kwargs):
    return subprocess.run(command, check=True, text=True, stdout=subprocess.PIPE, **kwargs).stdout


def validate_compatibility(value):
    require(isinstance(value, dict) and set(value) == {
        "protocolVersion", "minimumVersion", "maximumExclusiveVersion", "sourceRevisions",
    }, "Invalid CLI compatibility shape.")
    require(type(value["protocolVersion"]) is int and value["protocolVersion"] == 1,
            "CLI protocolVersion must be 1.")
    revisions = value["sourceRevisions"]
    require(isinstance(revisions, list) and all(matches(SHA, sha) for sha in revisions)
            and len(set(revisions)) == len(revisions), "CLI sourceRevisions must be unique full lowercase SHAs.")
    minimum, maximum = value["minimumVersion"], value["maximumExclusiveVersion"]
    require((minimum is None) == (maximum is None), "CLI version interval needs both bounds.")
    if minimum is not None:
        require(version_key(minimum) < version_key(maximum), "CLI version interval is empty or reversed.")
    require(minimum is not None or revisions, "Declare tested CLI source SHAs or a tested version interval.")


def validate_core(value, tag, source=None):
    require(isinstance(value, dict), "Release metadata must be an object.")
    require(type(value.get("schemaVersion")) is int and value["schemaVersion"] == 1,
            "Unsupported manifest schemaVersion.")
    require(value.get("serverVersion") == version_from_tag(tag) and value.get("releaseTag") == tag,
            "Mixed release: version/tag does not match the selected release.")
    require(matches(SHA, value.get("sourceRevision")), "sourceRevision must be a full lowercase SHA.")
    if source is not None:
        require(value["sourceRevision"] == source, "Source revision does not match the build.")
    require(value.get("architectures") == ARCHITECTURES, "Exactly linux/amd64 and linux/arm64 are required.")
    for field in ("identitySchemaVersion", "configurationSchemaVersion"):
        require(type(value.get(field)) is int and value[field] > 0, f"{field} must be a positive integer.")
    require(matches(r"[0-9]{14}_[A-Za-z0-9_]+", value.get("applicationMigration")),
            "applicationMigration must name an exact EF migration.")
    dependencies = value.get("dependencies")
    require(isinstance(dependencies, dict) and set(dependencies) == DEPENDENCIES,
            "Pin postgres, openbao, kratos and caddy dependencies.")
    require(all(matches(IMAGE, ref) for ref in dependencies.values()), "Dependencies must use immutable sha256 digests.")
    validate_compatibility(value.get("cliCompatibility"))


def validate_images(images):
    require(isinstance(images, dict) and set(images) == set(COMPONENTS), "Pin exactly api, dashboard and worker images.")
    for component, reference in images.items():
        require(matches(rf"ghcr.io/{REPOSITORY}-{component}@{DIGEST}", reference),
                f"Invalid canonical {component} image digest.")


def validate_manifest(value, tag):
    require(isinstance(value, dict) and set(value) == INPUT_FIELDS | {
        "releaseTag", "sourceRevision", "images", "bundle", "files",
    }, "Invalid manifest field set.")
    validate_core(value, tag)
    validate_images(value["images"])
    bundle = value["bundle"]
    require(isinstance(bundle, dict) and set(bundle) == {"name", "sha256"}
            and bundle["name"] == f"minicloud-server-{value['serverVersion']}.tar.gz"
            and matches(r"[0-9a-f]{64}", bundle["sha256"]), "Invalid bundle binding.")
    files = value["files"]
    require(isinstance(files, dict) and REQUIRED_PAYLOAD | {"images.env", "server-release.json"} <= set(files),
            "Incomplete bundle file inventory.")
    for name, digest in files.items():
        safe_name(name)
        require(matches(r"[0-9a-f]{64}", digest), f"Invalid file checksum: {name}")


def safe_name(name):
    require(matches(r"[A-Za-z0-9_.][A-Za-z0-9._/-]*", name)
            and not PurePosixPath(name).is_absolute()
            and all(part not in ("", ".", "..") for part in name.split("/")),
            f"Unsafe bundle path: {name!r}")


def load_input(config, tag, source):
    value = read_json(config)
    require(isinstance(value, dict) and set(value) == INPUT_FIELDS, "Invalid release input field set.")
    value.update(releaseTag=tag, sourceRevision=source)
    validate_core(value, tag, source)
    return value


def payload_files(payload):
    require(payload.is_dir(), f"Missing T41/T43/T44 bundle directory: {payload}")
    files = {}
    for path in sorted(payload.rglob("*")):
        require(not path.is_symlink() and (path.is_dir() or path.is_file()),
                f"Bundle cannot contain links or special files: {path}")
        if path.is_file():
            name = path.relative_to(payload).as_posix()
            safe_name(name)
            files[name] = path.read_bytes()
    require(REQUIRED_PAYLOAD <= files.keys(), f"Missing bundle assets: {sorted(REQUIRED_PAYLOAD - files.keys())}")
    require(not {"images.env", "server-release.json"} & files.keys(), "Generated release files must not be supplied by the payload.")
    for name in REQUIRED_PAYLOAD:
        require(files[name].strip(), f"Empty bundle asset: {name}")
    return files


def preflight(root, config, payload, tag, source):
    value = load_input(config, tag, source)
    payload_files(payload)
    migration = root / "modules/common/Migrations" / f"{value['applicationMigration']}.cs"
    require(migration.is_file(), f"Application migration is not in this source revision: {migration.name}")
    migrations = sorted(path.stem for path in migration.parent.glob("*.cs")
                        if matches(r"[0-9]{14}_[A-Za-z0-9_]+", path.stem))
    require(migrations and migrations[-1] == value["applicationMigration"],
            "applicationMigration must be the latest migration in the release source.")
    worker = (root / "modules/worker/Dockerfile").read_text()
    require(re.search(r"^ARG MINICLOUD_AGENT_RID(?:=linux-x64)?\s*$", worker, re.M)
            and re.search(r"-r\s+[\"']?\$(?:MINICLOUD_AGENT_RID|\{MINICLOUD_AGENT_RID\})", worker),
            "T41 handoff missing: worker must publish using ARG MINICLOUD_AGENT_RID.")
    dashboard = (root / "modules/dashboard/Dockerfile").read_text()
    require(not re.search(r"^\s*ARG\s+VITE_", dashboard, re.M),
            "T41 handoff missing: dashboard must use public startup configuration, not VITE build arguments.")
    return value


def image_environment(value):
    refs = {**value["images"], **value["dependencies"]}
    return {f"MINICLOUD_{name.upper()}_IMAGE": ref for name, ref in refs.items()}


def environment_bytes(value):
    return "".join(f"{key}={ref}\n" for key, ref in sorted(image_environment(value).items())).encode()


def package(root, config, payload, tag, source, images, output):
    value = preflight(root, config, payload, tag, source)
    value["images"] = read_json(images)
    validate_images(value["images"])
    files = payload_files(payload)
    # Other installation settings stay uninterpolated; no operator secrets are needed to package.
    compose = json.loads(run(["docker", "compose", "--project-directory", str(payload),
                              "-f", str(payload / "compose.yml"), "config", "--format", "json",
                              "--no-interpolate", "--no-env-resolution"]))
    services = compose.get("services", {})
    require(services and all("build" not in service for service in services.values()),
            "Release Compose must not build from source.")
    environment = image_environment(value)
    template = files["compose.yml"].decode()
    referenced = set()
    for service in services.values():
        reference = service.get("image")
        if isinstance(reference, str):
            for key, pinned in environment.items():
                pattern = rf"\$\{{{key}(?::\?[^}}]*)?\}}"
                if re.fullmatch(pattern, reference):
                    reference = pinned
                template = re.sub(pattern, lambda _: pinned, template)
        referenced.add(reference)
    require(referenced == set(value["images"].values()) | set(value["dependencies"].values()),
            "Compose images must match all and only the manifest's pinned images/dependencies.")
    files["compose.yml"] = template.encode()
    files["images.env"] = environment_bytes(value)
    files["server-release.json"] = json_bytes(value)
    output = output.resolve()
    require(output.is_relative_to((root / ".local/artifacts").resolve()),
            "Build outputs must be under the repository's ignored .local/artifacts/.")
    require(not output.exists() or not any(output.iterdir()), "Release output directory must be empty.")
    output.mkdir(parents=True, exist_ok=True)
    name = f"minicloud-server-{value['serverVersion']}.tar.gz"
    with (output / name).open("wb") as target:
        with gzip.GzipFile(filename="", mode="wb", fileobj=target, mtime=0) as compressed:
            with tarfile.open(fileobj=compressed, mode="w") as archive:
                for filename, data in sorted(files.items()):
                    member = tarfile.TarInfo(filename)
                    member.size = len(data)
                    member.mode = 0o755 if filename.endswith(".sh") else 0o644
                    archive.addfile(member, io.BytesIO(data))
    value["files"] = {filename: sha256(data) for filename, data in sorted(files.items())}
    value["bundle"] = {"name": name, "sha256": sha256((output / name).read_bytes())}
    validate_manifest(value, tag)
    (output / "server-manifest.json").write_bytes(json_bytes(value))
    (output / "install-server.sh").write_bytes(files["install-server.sh"])
    (output / "verify-server-release.py").write_bytes(Path(__file__).read_bytes())
    checksums = {filename: sha256((output / filename).read_bytes()) for filename in (
        name, "server-manifest.json", "install-server.sh", "verify-server-release.py",
    )}
    (output / "checksums.txt").write_text("".join(f"{digest}  {filename}\n" for filename, digest in sorted(checksums.items())))


def verify_assets(directory, tag):
    version = version_from_tag(tag)
    # Cosign's default production trust root and transparency verification are mandatory.
    run(["cosign", "verify-blob", "--new-bundle-format", "--bundle", str(directory / "checksums.txt.sigstore.json"),
         "--certificate-identity", identity(tag), "--certificate-oidc-issuer", ISSUER,
         str(directory / "checksums.txt")])
    expected = {f"minicloud-server-{version}.tar.gz", "server-manifest.json",
                "install-server.sh", "verify-server-release.py"}
    checksums = {}
    for line in (directory / "checksums.txt").read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9._-]+)", line)
        require(match is not None, "Invalid signed checksum inventory.")
        digest, name = match.groups()
        require(name in expected and name not in checksums, f"Unexpected or duplicate signed asset: {name}")
        checksums[name] = digest
    require(set(checksums) == expected, "Signed assets are incomplete.")
    for name, digest in checksums.items():
        require(sha256((directory / name).read_bytes()) == digest, f"Asset checksum mismatch: {name}")
    manifest = read_json(directory / "server-manifest.json")
    validate_manifest(manifest, tag)
    bundle = manifest["bundle"]
    require(checksums[bundle["name"]] == bundle["sha256"], "Bundle and manifest belong to different releases.")
    seen = {}
    with tarfile.open(directory / bundle["name"], "r:gz") as archive:
        for member in archive:
            safe_name(member.name)
            require(member.isfile() and member.name not in seen, f"Unsafe or duplicate tar member: {member.name}")
            require(member.name in manifest["files"], f"Unexpected tar member: {member.name}")
            data = archive.extractfile(member).read()
            require(sha256(data) == manifest["files"][member.name], f"Bundle checksum mismatch: {member.name}")
            seen[member.name] = data
    require(set(seen) == set(manifest["files"]), "Bundle inventory is incomplete.")
    core = {key: value for key, value in manifest.items() if key not in ("files", "bundle")}
    require(seen["server-release.json"] == json_bytes(core), "Runtime metadata differs from the signed manifest.")
    require(seen["images.env"] == environment_bytes(core), "Image environment differs from the signed manifest.")
    require(seen["install-server.sh"] == (directory / "install-server.sh").read_bytes(),
            "Detached installer differs from the versioned bundle.")
    return manifest


def json_documents(text):
    decoder = json.JSONDecoder()
    while text.strip():
        value, end = decoder.raw_decode(text.lstrip())
        yield from value if isinstance(value, list) else [value]
        text = text.lstrip()[end:]


def verify_provenance(text, reference, manifest, tag):
    for envelope in json_documents(text):
        statement = json.loads(base64.b64decode(envelope["payload"], validate=True))
        predicate = statement.get("predicate", {})
        source = predicate.get("invocation", {}).get("configSource", {})
        if (statement.get("predicateType") == "https://slsa.dev/provenance/v0.2"
                and predicate.get("builder", {}).get("id") == identity(tag)
                and source == {"uri": SOURCE_URI, "digest": {"sha1": manifest["sourceRevision"]}, "entryPoint": WORKFLOW}
                and {"uri": SOURCE_URI, "digest": {"sha1": manifest["sourceRevision"]}} in predicate.get("materials", [])
                and any(subject.get("digest", {}).get("sha256") == reference.split("@sha256:")[1]
                        for subject in statement.get("subject", []))):
            return
    raise ReleaseError(f"No verified provenance binds {reference} to this source/workflow.")


def verify_index(reference, product):
    index = json.loads(run(["docker", "buildx", "imagetools", "inspect", "--raw", reference]))
    platforms = []
    for descriptor in index.get("manifests", []):
        platform = descriptor.get("platform", {})
        if descriptor.get("annotations", {}).get("vnd.docker.reference.type") == "attestation-manifest":
            require(platform.get("os") == "unknown" and platform.get("architecture") == "unknown",
                    f"Attestation descriptor has a runnable platform in {reference}.")
            continue
        platforms.append(f"{platform.get('os')}/{platform.get('architecture')}")
    require(set(ARCHITECTURES) <= set(platforms), f"Missing supported architecture in {reference}.")
    if product:
        require(sorted(platforms) == ARCHITECTURES, f"Unexpected or duplicate product architecture in {reference}.")


def verify_images(manifest, tag, anonymous, scratch):
    certificate = ["--certificate-identity", identity(tag), "--certificate-oidc-issuer", ISSUER]
    for reference in manifest["images"].values():
        verify_index(reference, product=True)
        run(["cosign", "verify", *certificate, reference])
        text = run(["cosign", "verify-attestation", "--type", "slsaprovenance", *certificate, reference])
        verify_provenance(text, reference, manifest, tag)
    for reference in manifest["dependencies"].values():
        verify_index(reference, product=False)
    if anonymous:
        scratch.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="anonymous-pull-", dir=scratch) as config:
            environment = {**os.environ, "DOCKER_CONFIG": config}
            environment.pop("DOCKER_AUTH_CONFIG", None)
            for reference in [*manifest["images"].values(), *manifest["dependencies"].values()]:
                for platform in ARCHITECTURES:
                    run(["docker", "pull", "--platform", platform, reference], env=environment)


def build_images(root, tag, source, output):
    version = version_from_tag(tag)
    require(matches(SHA, source), "Build source must be a full lowercase SHA.")
    require(output.resolve().is_relative_to((root / ".local/artifacts").resolve()),
            "Build outputs must be under the repository's ignored .local/artifacts/.")
    output.mkdir(parents=True, exist_ok=True)
    images = {}
    for component in COMPONENTS:
        repository = f"ghcr.io/{REPOSITORY}-{component}"
        references = []
        for architecture, rid in (("amd64", "linux-x64"), ("arm64", "linux-arm64")):
            metadata = output / f"{component}-{architecture}.json"
            command = [
                "docker", "buildx", "build", "--platform", f"linux/{architecture}",
                "--file", str(root / f"modules/{component}/Dockerfile"),
                "--tag", f"{repository}:{version}-{architecture}",
                "--label", f"org.opencontainers.image.source=https://github.com/{REPOSITORY}",
                "--label", f"org.opencontainers.image.revision={source}",
                "--label", f"org.opencontainers.image.version={version}",
                "--metadata-file", str(metadata), "--push",
            ]
            if component == "worker":
                command.extend(["--build-arg", f"MINICLOUD_AGENT_RID={rid}"])
            run([*command, str(root)])
            digest = read_json(metadata).get("containerimage.digest")
            require(matches(DIGEST, digest), f"Missing build digest for {component}/{architecture}.")
            references.append(f"{repository}@{digest}")
            if component == "worker":
                verify_agent(references[-1], architecture, output / f"agent-{architecture}")
        run(["docker", "buildx", "imagetools", "create", "--tag", f"{repository}:{version}", *references])
        descriptor = json.loads(run(["docker", "buildx", "imagetools", "inspect",
                                     f"{repository}:{version}", "--format", "{{json .Manifest}}"]))
        digest = descriptor.get("digest")
        require(matches(DIGEST, digest), f"Missing multi-architecture digest for {component}.")
        images[component] = f"{repository}@{digest}"
        verify_index(images[component], product=True)
    (output / "images.json").write_bytes(json_bytes(images))


def verify_agent(reference, architecture, output):
    container = run(["docker", "create", "--platform", f"linux/{architecture}", reference]).strip()
    require(matches(r"[0-9a-f]{64}", container), "Docker did not return a container ID for agent inspection.")
    try:
        run(["docker", "cp", f"{container}:/app/runtime-agent/minicloud-agent", str(output)])
        data = output.read_bytes()
        machine = {"amd64": 62, "arm64": 183}[architecture]
        require(len(data) >= 20 and data[:6] == b"\x7fELF\x02\x01"
                and int.from_bytes(data[18:20], "little") == machine,
                f"Embedded worker agent is not a linux/{architecture} ELF executable.")
    finally:
        run(["docker", "rm", container])


def provenance(manifest):
    return {
        "builder": {"id": identity(manifest["releaseTag"])},
        "buildType": f"https://github.com/{REPOSITORY}/{WORKFLOW}",
        "invocation": {"configSource": {
            "uri": SOURCE_URI, "digest": {"sha1": manifest["sourceRevision"]}, "entryPoint": WORKFLOW,
        }},
        "materials": [{"uri": SOURCE_URI, "digest": {"sha1": manifest["sourceRevision"]}}],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("preflight", "package"):
        command = commands.add_parser(name)
        command.add_argument("--root", type=Path, default=Path.cwd())
        command.add_argument("--config", type=Path, required=True)
        command.add_argument("--payload", type=Path, required=True)
        command.add_argument("--tag", required=True)
        command.add_argument("--source", required=True)
        if name == "package":
            command.add_argument("--images", type=Path, required=True)
            command.add_argument("--output", type=Path, required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("--directory", type=Path, required=True)
    verify.add_argument("--tag", required=True)
    verify.add_argument("--anonymous-pulls", action="store_true")
    build = commands.add_parser("build-images")
    build.add_argument("--root", type=Path, default=Path.cwd())
    build.add_argument("--tag", required=True)
    build.add_argument("--source", required=True)
    build.add_argument("--output", type=Path, required=True)
    predicate = commands.add_parser("provenance")
    predicate.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "preflight":
        preflight(args.root, args.config, args.payload, args.tag, args.source)
    elif args.command == "package":
        package(args.root, args.config, args.payload, args.tag, args.source, args.images, args.output)
    elif args.command == "build-images":
        build_images(args.root, args.tag, args.source, args.output)
    elif args.command == "provenance":
        manifest = read_json(args.manifest)
        validate_manifest(manifest, manifest["releaseTag"])
        print(json_bytes(provenance(manifest)).decode(), end="")
        return
    else:
        manifest = verify_assets(args.directory, args.tag)
        verify_images(manifest, args.tag, args.anonymous_pulls, args.directory / ".local/artifacts")
    print(f"Server release {args.command} passed for {args.tag}.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, subprocess.CalledProcessError, tarfile.TarError) as error:
        print(f"Server release failed: {error}", file=sys.stderr)
        if isinstance(error, subprocess.CalledProcessError) and error.stdout:
            print(error.stdout, file=sys.stderr)
        sys.exit(1)
