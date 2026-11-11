#!/bin/sh
# Independently verify this exact release's signed inventory before running this file.
set -eu
exec python3 -B - "$@" <<'PY'
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tarfile
import urllib.parse
import urllib.request

def require(value, message):
    if not value:
        raise ValueError(message)

def path_chain(path):
    return (path, *path.parents)

def protected(path, directory=False):
    for parent in path_chain(path):
        require(not parent.is_symlink(), "Symlink paths are forbidden.")
        info = parent.stat()
        require(info.st_uid == 0 and not info.st_mode & 0o022,
                "Asset path ancestors must be root-owned and not group/world writable.")
    info = path.stat()
    require(info.st_uid == 0, "Assets must be root-owned.")
    require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode),
            "Assets must be regular files in a protected directory.")
    require(stat.S_IMODE(info.st_mode) == 0o700 if directory
            else stat.S_IMODE(info.st_mode) in (0o400, 0o600),
            "Assets directory must be 0700 and files 0600/0400.")

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, url):
        require(urllib.parse.urlparse(url).scheme == "https", "Insecure download redirect.")
        return super().redirect_request(request, response, code, message, headers, url)

def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description="Exact signed Minicloud server installer; no latest discovery.")
    parser.add_argument("--tag", required=True)
    parser.add_argument("--assets-dir", type=Path, required=True)
    parser.add_argument("--download", action="store_true",
                        help="Download absent assets only, from this exact tag; never overwrite.")
    args, remainder = parser.parse_known_args()
    require(os.geteuid() == 0, "Run as root after independent signature verification.")
    require(re.fullmatch(r"server-v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?", args.tag),
            "Select an exact server-v semantic version.")
    assets = args.assets_dir.absolute()
    protected(assets, True)
    names = {f"minicloud-server-{args.tag[8:]}.tar.gz", "server-manifest.json",
             "install-server.sh", "verify-server-release.py"}
    for name in sorted(names | {"checksums.txt", "checksums.txt.sigstore.json"}):
        path = assets / name
        if args.download and not path.exists():
            # A partial failed download is retained separately, never mistaken for a release asset.
            partial = assets / (name + ".partial")
            fd = os.open(partial, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            url = f"https://github.com/muniventures/minicloud/releases/download/{args.tag}/{name}"
            with os.fdopen(fd, "wb") as target, urllib.request.build_opener(NoRedirect()).open(url, timeout=60) as source:
                while data := source.read(1024 * 1024):
                    target.write(data)
                target.flush()
                os.fsync(target.fileno())
            os.rename(partial, path)
        protected(path)
    identity = f"https://github.com/muniventures/minicloud/.github/workflows/server-release.yml@refs/tags/{args.tag}"
    subprocess.run(["cosign", "verify-blob", "--new-bundle-format", "--bundle",
                    str(assets / "checksums.txt.sigstore.json"), "--certificate-identity", identity,
                    "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
                    str(assets / "checksums.txt")], check=True, capture_output=True)
    inventory = {}
    for line in (assets / "checksums.txt").read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([A-Za-z0-9._-]+)", line)
        require(match is not None, "Invalid signed inventory.")
        digest, name = match.groups()
        require(name in names and name not in inventory, "Unexpected/duplicate signed asset.")
        inventory[name] = digest
    require(set(inventory) == names, "Incomplete signed inventory.")
    for name, digest in inventory.items():
        with (assets / name).open("rb") as source:
            require(hashlib.file_digest(source, "sha256").hexdigest() == digest,
                    "Signed asset checksum mismatch.")
    # Never use an inherited remote Docker context or registry login.
    environment = {key: value for key, value in os.environ.items()
                   if key in ("PATH", "HOME", "LANG", "SSL_CERT_FILE", "SSL_CERT_DIR")}
    environment["DOCKER_HOST"] = "unix:///var/run/docker.sock"
    subprocess.run(["docker", "info", "--format", "{{.OSType}}"], check=True,
                   capture_output=True, env=environment)
    # This is the first execution of downloaded Python: its independent signature/hash checks passed.
    subprocess.run([sys.executable, "-B", str(assets / "verify-server-release.py"), "verify",
                    "--directory", str(assets), "--tag", args.tag],
                   check=True, capture_output=True, env=environment)
    manifest = json.loads((assets / "server-manifest.json").read_text())
    with tarfile.open(assets / manifest["bundle"]["name"], "r:gz") as archive:
        helper = archive.extractfile("server_install.py").read()
    require(hashlib.sha256(helper).hexdigest() == manifest["files"]["server_install.py"],
            "Installer helper checksum mismatch.")
    namespace = {"__name__": "minicloud_verified_installer"}
    exec(compile(helper, "verified-release/server_install.py", "exec"), namespace)
    try:
        return namespace["main"](["--tag", args.tag, "--assets-dir", str(assets), *remainder])
    except namespace["InstallError"] as error:
        print(str(error), file=sys.stderr)
        return 1

try:
    sys.exit(main())
except (OSError, ValueError, KeyError, tarfile.TarError, subprocess.CalledProcessError):
    print("Installer failed closed: check protected assets, exact tag, trust/network and prerequisites. No credentials were printed.", file=sys.stderr)
    sys.exit(1)
PY
