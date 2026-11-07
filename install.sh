#!/usr/bin/env sh
# SPDX-License-Identifier: MPL-2.0
set -eu

repo="${MINICLOUD_REPO:-muniventures/minicloud}"
version="${MINICLOUD_VERSION:-latest}"
install_dir="${MINICLOUD_INSTALL_DIR:-/usr/local/bin}"

os="$(uname -s | tr '[:upper:]' '[:lower:]')"
arch="$(uname -m)"

case "$os" in
  linux) rid_os="linux" ;;
  darwin) rid_os="osx" ;;
  *)
    echo "Unsupported OS: $os" >&2
    exit 1
    ;;
esac

case "$arch" in
  x86_64|amd64) rid_arch="x64" ;;
  arm64|aarch64) rid_arch="arm64" ;;
  *)
    echo "Unsupported architecture: $arch" >&2
    exit 1
    ;;
esac

asset="minicloud-${rid_os}-${rid_arch}.tar.gz"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT INT TERM

if [ "$version" = "latest" ]; then
  release="https://github.com/${repo}/releases/latest/download"
else
  release="https://github.com/${repo}/releases/download/${version}"
fi

echo "Downloading $release/$asset"
curl -fsSL "$release/$asset" -o "$tmp_dir/$asset"
curl -fsSL "$release/checksums.txt" -o "$tmp_dir/checksums.txt"
awk -v asset="$asset" '$2 == asset && $1 ~ /^[a-fA-F0-9]+$/ && length($1) == 64 { print $1 "  " asset; found++ } END { exit(found != 1) }' \
  "$tmp_dir/checksums.txt" > "$tmp_dir/checksum"
if command -v sha256sum >/dev/null 2>&1; then
  (cd "$tmp_dir" && sha256sum -c checksum)
else
  (cd "$tmp_dir" && shasum -a 256 -c checksum)
fi
tar -xzf "$tmp_dir/$asset" -C "$tmp_dir"

if [ ! -x "$tmp_dir/minicloud" ]; then
  echo "Release asset does not contain executable: minicloud" >&2
  exit 1
fi

mkdir_cmd="mkdir -p"
install_cmd="install -m 0755"
if [ ! -w "$install_dir" ]; then
  if command -v sudo >/dev/null 2>&1; then
    mkdir_cmd="sudo mkdir -p"
    install_cmd="sudo install -m 0755"
  else
    echo "$install_dir is not writable and sudo is unavailable." >&2
    echo "Set MINICLOUD_INSTALL_DIR to a writable directory." >&2
    exit 1
  fi
fi

$mkdir_cmd "$install_dir"
$install_cmd "$tmp_dir/minicloud" "$install_dir/minicloud"

echo "Installed minicloud to $install_dir/minicloud"
if ! command -v minicloud >/dev/null 2>&1; then
  echo "Add $install_dir to PATH to run minicloud from any shell."
fi

