#!/usr/bin/env sh
# SPDX-License-Identifier: MPL-2.0
set -eu

mode="${MINICLOUD_INSTALL_MODE:-binary}"
if [ "$mode" != binary ] && [ "$mode" != source ]; then
  echo "MINICLOUD_INSTALL_MODE must be binary or source; there is no automatic fallback." >&2
  exit 1
fi
if [ "$mode" = binary ]; then
  command -v python3 >/dev/null 2>&1 || { echo "Binary installation requires Python 3 (no SDK). See docs/cli-distribution.md." >&2; exit 1; }
  exec python3 - <<'PY'
import hashlib, json, os, platform, re, shutil, stat, subprocess, sys, tarfile, uuid
from pathlib import Path
import urllib.request, urllib.parse

def require(value, message):
    if not value:
        raise ValueError(message)

def unique(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "Duplicate JSON field.")
        value[key] = item
    return value

def load(path):
    return json.loads(path.read_text(), object_pairs_hook=unique)

def sha(path):
    with path.open("rb") as source:
        result = hashlib.sha256()
        while data := source.read(1024 * 1024):
            result.update(data)
        return result.hexdigest()

class HTTPSOnly(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, url):
        require(urllib.parse.urlsplit(url).scheme == "https", "Insecure download redirect.")
        return super().redirect_request(req, fp, code, msg, headers, url)

def download(url, target, limit):
    with urllib.request.build_opener(HTTPSOnly()).open(url, timeout=60) as source, target.open("xb") as output:
        total = 0
        while data := source.read(1024 * 1024):
            total += len(data)
            require(total <= limit, "Download exceeds safety bound.")
            output.write(data)

def safe(name):
    return all(re.fullmatch(r"[a-zA-Z0-9._-]+", part) and not part.endswith(".")
               and not re.fullmatch(r"(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part)
               for part in name.split("/"))

FINALIZE = r'''
import hashlib, os, pathlib, shutil, stat, sys, uuid
source, target = map(pathlib.Path, sys.argv[1:3])
target.mkdir(parents=True, exist_ok=True)
for parent in (target, *target.parents):
    info = parent.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_mode & 0o022 or info.st_uid not in (0, os.geteuid()):
        raise ValueError("Install path must contain only trusted non-writable directories, not links.")
store = target / ".minicloud"
if not store.exists():
    store.mkdir(mode=0o755)
if store.is_symlink() or store.stat().st_mode & 0o022 or store.stat().st_uid not in (0, os.geteuid()):
    raise ValueError("Unsafe versioned notice directory.")
notices = store / (sys.argv[3] + "-" + sys.argv[4])
if not notices.exists():
    staging = store / ("pending-" + uuid.uuid4().hex)
    shutil.copytree(source, staging)
    for path in staging.rglob("*"):
        os.chmod(path, 0o755 if path.is_dir() or path.name == "minicloud" else 0o644)
    os.chmod(staging, 0o755)
    os.rename(staging, notices)
pending = target / (".minicloud-" + uuid.uuid4().hex + ".new")
try:
    with (source / "minicloud").open("rb") as old, pending.open("xb") as new:
        shutil.copyfileobj(old, new); new.flush(); os.fsync(new.fileno())
    os.chmod(pending, 0o755)
    os.replace(pending, target / "minicloud")
    descriptor = os.open(target, os.O_RDONLY)
    try: os.fsync(descriptor)
    finally: os.close(descriptor)
finally:
    if pending.exists(): pending.unlink()
'''

stage = None
try:
    os.umask(0o077)
    require(os.environ.get("MINICLOUD_REPO", "muniventures/minicloud") == "muniventures/minicloud", "Only the canonical repository is allowed.")
    tag = os.environ.get("MINICLOUD_VERSION", "")
    require(re.fullmatch(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", tag),
            "Binary mode requires MINICLOUD_VERSION=<exact published CLI vX.Y.Z[-prerelease]>. No latest, SHA, server-v selection or unsigned historical fallback. For source use MINICLOUD_INSTALL_MODE=source.")
    targets = {
        ("Linux", "x86_64"): ("linux-x64", "cosign-linux-amd64", "c3b4f5410e608af03a5eb0aaac84a4313d8da131248e08ff1759ac70c79d1644"),
        ("Linux", "aarch64"): ("linux-arm64", "cosign-linux-arm64", "426193b4c5da4d4d643e822f48fe0cc8a476ca1782a272704831f5a0cef716d7"),
        ("Darwin", "arm64"): ("osx-arm64", "cosign-darwin-arm64", "4d41cc18f0563907c0c785b51db76e1d1af10db4422b605ba876b1758e1771ab"),
    }
    require((platform.system(), platform.machine()) in targets, "Supported: Linux x64/arm64, macOS arm64; use install.ps1 for Windows x64.")
    rid, cosign_name, cosign_sha = targets[(platform.system(), platform.machine())]
    home = Path.home()
    require(home.is_dir() and not home.is_symlink(), "Use a real trusted home directory.")
    stage = home / (".minicloud-install-" + uuid.uuid4().hex)
    stage.mkdir(mode=0o700)
    cosign = stage / cosign_name
    download("https://github.com/sigstore/cosign/releases/download/v2.6.5/" + cosign_name, cosign, 256 * 1024 * 1024)
    require(sha(cosign) == cosign_sha, "Pinned official Cosign 2.6.5 checksum failed.")
    cosign.chmod(0o700)
    archive_name = f"minicloud-{tag[1:]}-{rid}.tar.gz"
    base = f"https://github.com/muniventures/minicloud/releases/download/{tag}/"
    for name in ("checksums.txt", "checksums.txt.sigstore.json", "cli-provenance.json", archive_name):
        download(base + name, stage / name, 1024 * 1024 * 1024 if name == archive_name else 8 * 1024 * 1024)
    identity = f"https://github.com/muniventures/minicloud/.github/workflows/cli-release.yml@refs/tags/{tag}"
    subprocess.run([str(cosign), "verify-blob", "--new-bundle-format", "--bundle", str(stage / "checksums.txt.sigstore.json"),
                    "--certificate-identity", identity, "--certificate-oidc-issuer", "https://token.actions.githubusercontent.com",
                    str(stage / "checksums.txt")], check=True, capture_output=True)
    expected = {f"minicloud-{tag[1:]}-{r}.{ext}" for r, ext in
                (("linux-x64","tar.gz"),("linux-arm64","tar.gz"),("osx-arm64","tar.gz"),("win-x64","zip"))} | {"cli-provenance.json"}
    inventory = {}
    for line in (stage / "checksums.txt").read_text().splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  ([a-zA-Z0-9._-]+)", line)
        require(match and match[2] in expected and match[2] not in inventory, "Invalid signed inventory.")
        inventory[match[2]] = match[1]
    require(set(inventory) == expected, "Incomplete signed inventory.")
    for name in (archive_name, "cli-provenance.json"):
        require(sha(stage / name) == inventory[name], "Authenticated asset digest mismatch.")
    provenance = load(stage / "cli-provenance.json")
    require(provenance["schemaVersion"] == 1 and provenance["tag"] == tag
            and provenance["workflow"] == identity.split("@")[0]
            and re.fullmatch(r"[0-9a-f]{40}", provenance["sourceRevision"]), "Invalid canonical source/workflow provenance.")
    artifact = provenance["artifacts"][rid]
    require(artifact["name"] == archive_name and artifact["sha256"] == inventory[archive_name], "Provenance/archive mismatch.")
    output = stage / "payload"; output.mkdir(mode=0o700)
    seen = set(); total = 0
    with tarfile.open(stage / archive_name, "r:gz") as archive:
        for member in archive:
            require(member.isfile() and safe(member.name) and member.name not in seen and member.name in artifact["files"], "Unsafe/unlisted/duplicate archive member.")
            total += member.size
            require(0 <= member.size <= 1024 * 1024 * 1024 and total <= 1024 * 1024 * 1024, "Expanded archive exceeds safety bound.")
            data = archive.extractfile(member).read()
            require(hashlib.sha256(data).hexdigest() == artifact["files"][member.name], "Archive member digest mismatch.")
            path = output / member.name; path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("xb") as target: target.write(data)
            seen.add(member.name)
    require(seen == set(artifact["files"]) and {"minicloud", "cli-release.json", "LICENSE.txt", "SOURCE.txt"} <= seen, "Incomplete CLI archive.")
    metadata = load(output / "cli-release.json")
    require(metadata == {k:v for k,v in provenance.items() if k != "artifacts"} | {"rid":rid}, "Embedded release metadata mismatch.")
    executable = output / "minicloud"; executable.chmod(0o700)
    version = subprocess.run([str(executable), "--version"], check=True, capture_output=True, text=True).stdout.splitlines()
    require(f"minicloud version {tag[1:]}" in version and f"source {provenance['sourceRevision']}" in version, "CLI version/source smoke check failed.")
    require("minicloud" in subprocess.run([str(executable), "--help"], check=True, capture_output=True, text=True).stdout.lower(), "CLI help smoke check failed.")
    install = Path(os.environ.get("MINICLOUD_INSTALL_DIR", "/usr/local/bin")).absolute()
    parent = install
    while not parent.exists(): parent = parent.parent
    command = [sys.executable, "-c", FINALIZE, str(output), str(install), tag, provenance["sourceRevision"]]
    if not os.access(parent, os.W_OK):
        require(shutil.which("sudo"), "Install path not writable; set MINICLOUD_INSTALL_DIR.")
        command.insert(0, "sudo")
    subprocess.run(command, check=True)
    print(f"Installed {install / 'minicloud'} ({tag}, source {provenance['sourceRevision']}). Existing configuration/authentication unchanged.")
except Exception:
    print("Verified binary installation failed. No source/channel fallback. Existing executable is retained unless atomic replacement already completed; inspect exact version/prerequisites and retry.", file=sys.stderr)
    sys.exit(1)
finally:
    if stage is not None: shutil.rmtree(stage)
PY
fi

repo="${MINICLOUD_REPO:-muniventures/minicloud}"
version="${MINICLOUD_VERSION:-7416e1408ecd9d11f58c638f0caf1c28e5fa6e3f}"
install_dir="${MINICLOUD_INSTALL_DIR:-/usr/local/bin}"

if [ "$repo" != "muniventures/minicloud" ]; then
  echo "MINICLOUD_REPO must be muniventures/minicloud." >&2
  exit 1
fi

for command_name in git dotnet; do
  if ! command -v "$command_name" >/dev/null 2>&1; then
    echo "$command_name is required to build the Minicloud CLI from source." >&2
    exit 1
  fi
done
if ! dotnet --list-sdks | awk '$1 ~ /^10\./ { found=1 } END { exit !found }'; then
  echo ".NET 10 SDK is required to build the Minicloud CLI. Install the SDK; this installer will not install it for you." >&2
  exit 1
fi

if [ "${#version}" -eq 40 ] && printf '%s' "$version" | grep -Eq '^[0-9a-f]{40}$'; then
  expected_sha="$version"
  cli_version="0.0.0-source.sha$(printf '%s' "$version" | cut -c1-12)"
elif printf '%s' "$version" | grep -Eq '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'; then
  expected_sha=""
  cli_version="${version#v}"
else
  echo "MINICLOUD_VERSION must be a full lowercase commit SHA or an exact CLI tag such as v3.1.0; branches, latest, and server-v tags are not supported." >&2
  exit 1
fi

os="$(uname -s | tr '[:upper:]' '[:lower:]')"
arch="$(uname -m)"
case "$os-$arch" in
  linux-x86_64|linux-amd64) rid="linux-x64" ;;
  linux-aarch64|linux-arm64) rid="linux-arm64" ;;
  darwin-arm64|darwin-aarch64) rid="osx-arm64" ;;
  *)
    echo "Unsupported platform: $os-$arch. Supported targets are Linux x64/arm64 and macOS Apple Silicon." >&2
    exit 1
    ;;
esac

tmp_dir="$HOME/.minicloud-source-install-$$"
umask 077
mkdir -m 700 "$tmp_dir"
trap 'rm -rf "$tmp_dir"' EXIT INT TERM
source_dir="$tmp_dir/source"
publish_dir="$tmp_dir/publish"
url="https://github.com/muniventures/minicloud.git"

if [ -z "$expected_sha" ]; then
  tag_ref="refs/tags/$version"
  remote_before="$(git ls-remote "$url" "$tag_ref" "$tag_ref^{}")"
  expected_sha="$(printf '%s\n' "$remote_before" | awk -v ref="$tag_ref" '$2 == ref { tag=$1 } $2 == ref "^{}" { peeled=$1 } END { if (peeled != "") print peeled; else print tag }')"
  if ! printf '%s' "$expected_sha" | grep -Eq '^[0-9a-f]{40}$'; then
    echo "CLI tag $version was not found in the canonical repository." >&2
    exit 1
  fi
fi

echo "Building Minicloud CLI from muniventures/minicloud at $expected_sha"
git clone --filter=blob:none --no-tags --single-branch --branch main "$url" "$source_dir"
case "$version" in
  v*)
    git -C "$source_dir" fetch --no-tags origin "$tag_ref:$tag_ref"
    remote_after="$(git ls-remote "$url" "$tag_ref" "$tag_ref^{}")"
    fetched_sha="$(git -C "$source_dir" rev-parse "$tag_ref^{commit}")"
    remote_after_sha="$(printf '%s\n' "$remote_after" | awk -v ref="$tag_ref" '$2 == ref { tag=$1 } $2 == ref "^{}" { peeled=$1 } END { if (peeled != "") print peeled; else print tag }')"
    if [ "$fetched_sha" != "$expected_sha" ] || [ "$remote_after_sha" != "$expected_sha" ]; then
      echo "CLI tag $version changed while its source was being acquired; retry with a reviewed commit SHA." >&2
      exit 1
    fi
    ;;
esac
if ! git -C "$source_dir" merge-base --is-ancestor "$expected_sha" refs/remotes/origin/main; then
  echo "Selected CLI source $expected_sha is not in canonical main." >&2
  exit 1
fi
git -C "$source_dir" checkout --detach "$expected_sha"
resolved_sha="$(git -C "$source_dir" rev-parse HEAD)"
if [ "$resolved_sha" != "$expected_sha" ]; then
  echo "Resolved source revision did not match the selected revision." >&2
  exit 1
fi

(
  cd "$source_dir"
  dotnet publish modules/cli/Minicloud.Cli.csproj \
    --configuration Release \
    --runtime "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:PublishTrimmed=false -p:IncludeSourceRevisionInInformationalVersion=false \
    --output "$publish_dir" \
    -p:Version="$cli_version" \
    -p:SourceRevisionId="$resolved_sha" \
    -p:InformationalVersion="$cli_version+sha.$resolved_sha"
)

if [ -x "$publish_dir/Minicloud.Cli" ]; then
  mv "$publish_dir/Minicloud.Cli" "$publish_dir/minicloud"
fi
executable="$publish_dir/minicloud"
if [ ! -x "$executable" ]; then
  echo "Source build did not produce the minicloud executable for $rid." >&2
  exit 1
fi
if ! "$executable" --version | grep -F "$cli_version" >/dev/null; then
  echo "Built CLI version verification failed; the existing installation was not changed." >&2
  exit 1
fi
if ! "$executable" --help | grep -qi minicloud; then
  echo "Built CLI help verification failed; the existing installation was not changed." >&2
  exit 1
fi

if [ -d "$install_dir" ]; then
  writable=0
  [ -w "$install_dir" ] && writable=1
else
  parent_dir="$install_dir"
  while [ ! -e "$parent_dir" ]; do
    parent_dir="$(dirname "$parent_dir")"
  done
  writable=0
  [ -w "$parent_dir" ] && writable=1
fi

if [ "$writable" -eq 1 ]; then
  mkdir -p "$install_dir"
  pending="$install_dir/.minicloud-source-$$.new"
  (set -C; cat "$executable" > "$pending")
  chmod 755 "$pending"
  mv -f "$pending" "$install_dir/minicloud"
elif command -v sudo >/dev/null 2>&1; then
  sudo mkdir -p "$install_dir"
  pending="$install_dir/.minicloud-source-$$.new"
  sudo sh -c 'set -C; cat "$1" > "$2"; chmod 755 "$2"; mv -f "$2" "$3"' sh "$executable" "$pending" "$install_dir/minicloud"
else
  echo "$install_dir is not writable and sudo is unavailable." >&2
  echo "Set MINICLOUD_INSTALL_DIR to a writable directory." >&2
  exit 1
fi

echo "Installed minicloud to $install_dir/minicloud (source $resolved_sha)"
if ! command -v minicloud >/dev/null 2>&1; then
  echo "Add $install_dir to PATH to run minicloud from any shell."
fi
