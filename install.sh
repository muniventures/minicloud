#!/usr/bin/env sh
set -eu

repo="${MUNICLOUD_REPO:-muniventures/minicloud}"
version="${MUNICLOUD_VERSION:-latest}"
install_dir="${MUNICLOUD_INSTALL_DIR:-/usr/local/bin}"

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

asset="municloud-${rid_os}-${rid_arch}.tar.gz"
tmp_dir="$(mktemp -d)"
trap 'rm -rf "$tmp_dir"' EXIT INT TERM

if [ "$version" = "latest" ]; then
  url="https://github.com/${repo}/releases/latest/download/${asset}"
else
  url="https://github.com/${repo}/releases/download/${version}/${asset}"
fi

echo "Downloading $url"
curl -fsSL "$url" -o "$tmp_dir/$asset"
tar -xzf "$tmp_dir/$asset" -C "$tmp_dir"

if [ ! -x "$tmp_dir/municloud" ]; then
  echo "Release asset does not contain executable: municloud" >&2
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
    echo "Set MUNICLOUD_INSTALL_DIR to a writable directory." >&2
    exit 1
  fi
fi

$mkdir_cmd "$install_dir"
$install_cmd "$tmp_dir/municloud" "$install_dir/municloud"

echo "Installed municloud to $install_dir/municloud"
if ! command -v municloud >/dev/null 2>&1; then
  echo "Add $install_dir to PATH to run municloud from any shell."
fi

