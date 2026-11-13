#!/bin/bash
# SPDX-License-Identifier: MPL-2.0
set -euo pipefail
umask 077
operation="$1"
architecture="$2"
[[ "$operation" =~ ^[0-9a-f]{32}$ ]]
test "$(id -u)" = 0
. /etc/os-release
test "$ID:$VERSION_ID" = ubuntu:24.04
case "$(uname -m):$architecture" in
  x86_64:linux/amd64) debarch=amd64; archive=https://archive.ubuntu.com/ubuntu; cosign_arch=amd64
    cosign_sha=c3b4f5410e608af03a5eb0aaac84a4313d8da131248e08ff1759ac70c79d1644 ;;
  aarch64:linux/arm64) debarch=arm64; archive=https://ports.ubuntu.com/ubuntu-ports; cosign_arch=arm64
    cosign_sha=426193b4c5da4d4d643e822f48fe0cc8a476ca1782a272704831f5a0cef716d7 ;;
  *) exit 1 ;;
esac
check_dir() {
  test -d "$1" && test ! -L "$1"
  test "$(stat -c %u "$1")" = 0
  local permissions
  permissions="$(stat -c %a "$1")"
  test "$(( 8#$permissions & 8#022 ))" = 0
}
for parent in / /var /var/lib /opt; do check_dir "$parent"; done
lock=/opt/minicloud.installation.lock
if test -e "$lock" || test -L "$lock"; then
  test -f "$lock" && test ! -L "$lock"
  test "$(stat -c %u:%a "$lock")" = 0:600
fi
# Use the installer's persistent inode, including while preparing a partial host.
exec 9<>"$lock"
flock -xn 9
test ! -e /opt/minicloud && test ! -L /opt/minicloud
test -z "$(find /opt -mindepth 1 -maxdepth 1 ! -name minicloud.installation.lock ! -name minicloud.seed-operation -print -quit)"
test ! -e /opt/minicloud.seed-operation.pending && test ! -L /opt/minicloud.seed-operation.pending
base=/var/lib/minicloud-seed
if test ! -e "$base"; then mkdir -m 700 "$base"; fi
check_dir "$base"
test -z "$(find "$base" -mindepth 1 -maxdepth 1 ! -name "$operation" -print -quit)"
root="$base/$operation"
if test ! -e "$root"; then
  # Never prepare an existing installation on the strength of a name.
  test ! -e /opt/minicloud && test ! -L /opt/minicloud
  test ! -e /opt/minicloud.seed-operation
  if command -v docker >/dev/null; then
    test -z "$(docker ps -aq)"
    test -z "$(docker volume ls -q)"
  fi
  mkdir -m 700 "$root"
  (set -C; printf '%s\n' "$operation" > "$root/operation.pending")
  sync -f "$root/operation.pending"
  mv -T "$root/operation.pending" "$root/operation"
  sync -f "$root"
fi
check_dir "$root"
test "$(stat -c %a "$root")" = 700
test -f "$root/operation" && test ! -L "$root/operation"
test "$(stat -c %u:%a "$root/operation")" = 0:600
test "$(cat "$root/operation")" = "$operation"
test -z "$(find "$root" -name '*.pending' -o -name '*.partial')"
if test -e /opt/minicloud || test -e /opt/minicloud.seed-operation; then
  test -f /opt/minicloud.seed-operation && test ! -L /opt/minicloud.seed-operation
  test "$(cat /opt/minicloud.seed-operation)" = "$operation"
else
  # Durable host/operation binding before any installer input can be transmitted.
  test ! -L /opt/minicloud.seed-operation
  (set -C; printf '%s\n' "$operation" > /opt/minicloud.seed-operation.pending)
  sync -f /opt/minicloud.seed-operation.pending
  mv -T /opt/minicloud.seed-operation.pending /opt/minicloud.seed-operation
  sync -f /opt
fi
test "$(stat -c %u:%a /opt/minicloud.seed-operation)" = 0:600
# Use only Ubuntu's authenticated archive, never image-supplied third-party sources.
sources="$root/ubuntu.sources"
if test -e "$sources" || test -L "$sources"; then
  test -f "$sources" && test ! -L "$sources"
  test "$(stat -c %u "$sources")" = 0
  test "$(stat -c %a "$sources")" = 600
fi
printf 'Types: deb\nURIs: %s\nSuites: noble noble-updates noble-security\nComponents: main universe\nArchitectures: %s\nSigned-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg\n' "$archive" "$debarch" > "$sources"
apt_options=(-o "Dir::Etc::sourcelist=$sources" -o Dir::Etc::sourceparts=- -o APT::Get::AllowUnauthenticated=false -o Acquire::AllowInsecureRepositories=false)
export DEBIAN_FRONTEND=noninteractive
apt-get "${apt_options[@]}" update
apt-get "${apt_options[@]}" install -y --no-install-recommends \
  ca-certificates curl python3 openssl openssh-client openssh-server docker.io docker-compose-v2 docker-buildx
check_dir /etc
check_dir /etc/ssh
check_dir /etc/ssh/sshd_config.d
ssh_policy=/etc/ssh/sshd_config.d/00-minicloud-seed.conf
policy=$'PasswordAuthentication no\nKbdInteractiveAuthentication no\nPermitRootLogin prohibit-password\nPubkeyAuthentication yes\nAllowAgentForwarding no\nAllowTcpForwarding no\nPermitTunnel no\nX11Forwarding no\n'
if test -e "$ssh_policy" || test -L "$ssh_policy"; then
  test -f "$ssh_policy" && test ! -L "$ssh_policy"
  test "$(stat -c %u "$ssh_policy")" = 0
  test "$(stat -c %a "$ssh_policy")" = 600
  test "$(cat "$ssh_policy")" = "${policy%$'\n'}"
else
  (set -C; printf '%s' "$policy" > "$ssh_policy")
fi
sshd -t
sshd -T | grep -qx 'passwordauthentication no'
sshd -T | grep -qx 'kbdinteractiveauthentication no'
sshd -T | grep -Eq '^permitrootlogin (prohibit-password|without-password)$'
systemctl reload ssh
systemctl enable --now docker
cosign="$root/cosign-linux-$cosign_arch"
if test ! -e "$cosign"; then
  test ! -e "$cosign.partial"
  curl --proto '=https' --proto-redir '=https' --tlsv1.2 -fsSL \
    "https://github.com/sigstore/cosign/releases/download/v2.6.5/cosign-linux-$cosign_arch" -o "$cosign.partial"
  printf '%s  %s\n' "$cosign_sha" "$cosign.partial" | sha256sum --check --strict -
  mv -n "$cosign.partial" "$cosign"
fi
test ! -L "$cosign"
printf '%s  %s\n' "$cosign_sha" "$cosign" | sha256sum --check --strict -
# A versioned, root-only binary, not a mutable PATH shim.
check_dir /usr/local
check_dir /usr/local/bin
test ! -L /usr/local/bin/cosign
install -m 755 "$cosign" /usr/local/bin/cosign
cosign version --json
docker version --format '{{.Server.Version}}'
docker compose version
docker buildx version
for directory in assets recovery; do
  if test ! -e "$root/$directory"; then mkdir -m 700 "$root/$directory"; fi
  check_dir "$root/$directory"
  test "$(stat -c %a "$root/$directory")" = 700
done
python3 -B -c '
import base64, json, os, pathlib, stat, sys
p=pathlib.Path(sys.argv[1]) / "preparation.json"
identity=json.loads(base64.b64decode(sys.argv[3],validate=True))
data=(json.dumps({"schemaVersion":1,"seedId":sys.argv[2],"phase":"prepared","identity":identity},sort_keys=True)+"\n").encode()
if p.exists() or p.is_symlink():
 s=p.lstat()
 assert stat.S_ISREG(s.st_mode) and s.st_uid == 0 and stat.S_IMODE(s.st_mode) == 0o600
 assert p.read_bytes() == data
else:
 pending=p.with_name("preparation.pending")
 fd=os.open(pending,os.O_WRONLY|os.O_CREAT|os.O_EXCL|os.O_NOFOLLOW,0o600)
 with os.fdopen(fd,"wb") as target:
  target.write(data); target.flush(); os.fsync(target.fileno())
 os.replace(pending,p)
 fd=os.open(p.parent,os.O_RDONLY|os.O_DIRECTORY)
 try: os.fsync(fd)
 finally: os.close(fd)
' "$root" "$operation" "$3"
