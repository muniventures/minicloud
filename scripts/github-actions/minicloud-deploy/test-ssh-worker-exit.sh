#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fixture="$(mktemp -d "${TMPDIR:-/tmp}/minicloud-ssh-exit.XXXXXX")"
sshd_pid=""
cleanup() {
  [ -z "${sshd_pid}" ] || kill "${sshd_pid}" 2>/dev/null || true
  rm -rf "${fixture}"
}
trap cleanup EXIT

ssh-keygen -q -t ed25519 -N '' -f "${fixture}/host"
ssh-keygen -q -t ed25519 -N '' -f "${fixture}/client"
cp "${fixture}/client.pub" "${fixture}/authorized_keys"
chmod 600 "${fixture}/authorized_keys"
port="$((22000 + RANDOM % 1000))"
cat >"${fixture}/sshd_config" <<EOF
Port ${port}
ListenAddress 127.0.0.1
HostKey ${fixture}/host
AuthorizedKeysFile ${fixture}/authorized_keys
PasswordAuthentication no
ChallengeResponseAuthentication no
UsePAM no
StrictModes no
PidFile ${fixture}/sshd.pid
LogLevel ERROR
EOF
/usr/sbin/sshd -D -e -f "${fixture}/sshd_config" &
sshd_pid=$!

ssh_args=(-q -p "${port}" -i "${fixture}/client" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o BatchMode=yes)
for _ in $(seq 1 50); do
  ssh "${ssh_args[@]}" "${USER}@127.0.0.1" true 2>/dev/null && break
  sleep 0.1
done

python3 - "${script_dir}/remote-deploy.sh" "${script_dir}/test-runtime-direct-build.sh" "${fixture}" <<'PY'
import json, os, re, stat, sys, zipfile
from pathlib import Path
remote = Path(sys.argv[1]).read_text()
test = Path(sys.argv[2]).read_text()
root = Path(sys.argv[3])
stubs = re.search(r"stubs = r'''\n(.*?)\n'''", test, re.S).group(1)
footer = '\nif [ "${2:-}" = "--observability" ]; then'
host = root / 'opt' / 'minicloud'
(host / 'caddy' / 'apps').mkdir(parents=True)
(host / 'locks').mkdir(parents=True)
deploy = root / 'deploy.sh'
deploy.write_text(remote.replace(footer, '\n' + stubs + footer).replace('/opt/minicloud', str(host)))
deploy.chmod(0o700)
archive = root / 'web.zip'
with zipfile.ZipFile(archive, 'w') as z: z.writestr('Dockerfile', 'FROM scratch')
lease = root / 'worker.lease'; lease.write_text('owner-token')
payload = {
 'app_name':'ssh-exit','customer':'test','app_env':'production','deployment_id':'dep-ssh-exit',
 'database':'none','ownership_token':'owner-token','worker_lease_file':str(lease),
 'build_artifacts':[{'service_name':'web','artifact_archive':str(archive),'image_tag':'minicloud/ssh-exit-web:test'}],
 'services':[{'name':'web','image':'minicloud/ssh-exit-web:test','port':8080,'public':True,'path':'/'}]
}
(root / 'payload.json').write_text(json.dumps(payload))
for name in ('caddy','flock'):
 p=root/name
 p.write_text('#!/bin/sh\nif [ "$1" = "-x" ] && [ "${3:-}" = "-c" ]; then exec sh -c "$4"; fi\nexit 0\n' if name == 'flock' else '#!/bin/sh\nexit 0\n')
 p.chmod(0o700)
PY

target="${USER}@127.0.0.1"
lease="${fixture}/worker.lease"
(
  while ssh "${ssh_args[@]}" "${target}" \
      "PATH='${fixture}':\$PATH flock -x '${lease}.lock' -c 'test \"\$(cat \"${lease}\")\" = owner-token && touch \"${lease}\"'"; do
    sleep 1
  done
) &
heartbeat_pid=$!

remote_cmd="env PATH='${fixture}':\$PATH CALLS='${fixture}/calls' SLOW_BUILD=1 BUILD_PID='${fixture}/build.pid' bash '${fixture}/deploy.sh' '${fixture}/payload.json'"
ssh "${ssh_args[@]}" "${target}" "${remote_cmd}" >"${fixture}/stdout" 2>"${fixture}/stderr" &
deploy_ssh_pid=$!
for _ in $(seq 1 80); do [ -s "${fixture}/build.pid" ] && break; sleep 0.1; done
[ -s "${fixture}/build.pid" ]
build_pid="$(cat "${fixture}/build.pid")"

kill -KILL "${heartbeat_pid}"
wait "${heartbeat_pid}" 2>/dev/null || true
for _ in $(seq 1 150); do
  if ! kill -0 "${build_pid}" 2>/dev/null; then break; fi
  sleep 0.1
done
kill "${deploy_ssh_pid}" 2>/dev/null || true
wait "${deploy_ssh_pid}" 2>/dev/null || true

if kill -0 "${build_pid}" 2>/dev/null; then
  echo "FAIL: TERM-resistant remote build survived worker lease expiry" >&2
  exit 1
fi
! grep -q '^activate_caddy$' "${fixture}/calls"
! grep -q '__MINICLOUD_DEPLOY_COMPLETE__=' "${fixture}/stdout"
grep -q 'worker_lease_expired' "${fixture}/stderr"
echo "PASS: OpenSSH worker SIGKILL stopped heartbeats; lease expiry reaped remote build before activation"
