#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
TEMP_DIR="$(mktemp -d /tmp/minicloud-sftp-test.XXXXXX)"
CONTAINER_NAME="minicloud-sftp-test-$RANDOM"
PORT="${MINICLOUD_TEST_SFTP_PORT:-}"

cleanup() {
  docker rm -f "${CONTAINER_NAME}" >/dev/null 2>&1 || true
  rm -rf "${TEMP_DIR}"
}
trap cleanup EXIT

if openssl genrsa -help 2>&1 | grep -q -- '-traditional'; then
  openssl genrsa -traditional -out "${TEMP_DIR}/id_rsa" 3072 >/dev/null 2>&1
else
  openssl genrsa -out "${TEMP_DIR}/id_rsa" 3072 >/dev/null 2>&1
fi
chmod 600 "${TEMP_DIR}/id_rsa"
ssh-keygen -y -f "${TEMP_DIR}/id_rsa" > "${TEMP_DIR}/id_rsa.pub"
port_binding="127.0.0.1::22"
if [ -n "${PORT}" ]; then
  port_binding="127.0.0.1:${PORT}:22"
fi
docker run -d --rm --name "${CONTAINER_NAME}" \
  -p "${port_binding}" \
  -v "${TEMP_DIR}/id_rsa.pub:/home/minicloud/.ssh/keys/id_rsa.pub:ro" \
  atmoz/sftp:alpine 'minicloud::1001::upload' >/dev/null
if [ -z "${PORT}" ]; then
  PORT="$(docker port "${CONTAINER_NAME}" 22/tcp | awk -F: 'NR == 1 { print $NF }')"
fi

ready=false
for _ in $(seq 1 60); do
  if docker exec "${CONTAINER_NAME}" test -d /home/minicloud/upload \
      && sftp -q -b /dev/null -P "${PORT}" -i "${TEMP_DIR}/id_rsa" \
        -o BatchMode=yes -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null \
        minicloud@127.0.0.1; then
    ready=true
    break
  fi
  sleep 0.2
done
if [ "${ready}" != true ]; then
  docker logs "${CONTAINER_NAME}" >&2
  echo "Disposable SFTP server did not accept the generated key." >&2
  exit 1
fi
docker exec "${CONTAINER_NAME}" mkdir -p /home/minicloud/upload/minicloud-cancel
docker exec "${CONTAINER_NAME}" chown -R 1001:100 /home/minicloud/upload

MINICLOUD_TEST_SFTP_KEY="${TEMP_DIR}/id_rsa" \
MINICLOUD_TEST_SFTP_PORT="${PORT}" \
dotnet test "${ROOT_DIR}/modules/tests/Minicloud.Worker.Tests/Minicloud.Worker.Tests.csproj" \
  --no-restore --filter 'FullyQualifiedName~RealSftp'
