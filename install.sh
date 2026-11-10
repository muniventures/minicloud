#!/usr/bin/env sh
# SPDX-License-Identifier: MPL-2.0
set -eu

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

tmp_dir="$(mktemp -d "${TMPDIR:-/tmp}/minicloud-install.XXXXXX")"
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
  cp -a "$publish_dir/." "$install_dir/"
elif command -v sudo >/dev/null 2>&1; then
  sudo mkdir -p "$install_dir"
  sudo cp -a "$publish_dir/." "$install_dir/"
else
  echo "$install_dir is not writable and sudo is unavailable." >&2
  echo "Set MINICLOUD_INSTALL_DIR to a writable directory." >&2
  exit 1
fi

echo "Installed minicloud to $install_dir/minicloud (source $resolved_sha)"
if ! command -v minicloud >/dev/null 2>&1; then
  echo "Add $install_dir to PATH to run minicloud from any shell."
fi
