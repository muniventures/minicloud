from pathlib import Path
import json
import os
import subprocess
import tempfile


source = (Path(__file__).parent / 'remote-deploy.sh').read_text()
bootstrap = source.split('\npayload_string() {', 1)[0]
probe = r'''
python3 - "$@" <<'PY'
import json, os, sys
from pathlib import Path
with Path(os.environ['CALLS']).open('a') as log:
    log.write(json.dumps({'event': 'probe', 'args': sys.argv[1:]}) + '\n')
PY
docker info
'''

stub = r'''#!/usr/bin/env python3
import json, os, subprocess, sys
from pathlib import Path
name = Path(sys.argv[0]).name
args = sys.argv[1:]
env = dict(os.environ)
def record(event):
    with Path(env['CALLS']).open('a') as log:
        log.write(json.dumps({'event': event}) + '\n')
if name == 'timeout':
    raise SystemExit(subprocess.run(args[1:], env=env).returncode)
if name == 'sudo':
    assert args[0] == '-n'
    raise SystemExit(subprocess.run(args[1:], env=env).returncode)
if name == 'cloud-init':
    assert args == ['status', '--wait']
    record('cloud-init')
    raise SystemExit(int(env.get('CLOUD_INIT_STATUS', '0')))
if name == 'id':
    if args == ['-un']:
        print('minicloud')
    elif args == ['-nG']:
        print(env['CURRENT_GROUPS'])
    else:
        assert args == ['-nG', 'minicloud']
        print(env['ACCOUNT_GROUPS'])
    raise SystemExit(0)
if name == 'sg':
    assert len(args) == 3 and args[:2] == ['docker', '-c']
    record('sg')
    if env.get('REFRESH_WORKS', '1') == '1':
        env['CURRENT_GROUPS'] += ' docker'
    raise SystemExit(subprocess.run(['/bin/sh', '-c', args[2]], env=env).returncode)
if name == 'docker':
    record('docker')
    raise SystemExit(0 if 'docker' in env['CURRENT_GROUPS'].split() else 13)
if name == 'jq':
    raise SystemExit('Payload must not be parsed by the bootstrap test')
raise SystemExit('Unexpected command ' + name)
'''


def run_case(current, account, refresh=True, cloud_status=0, observability=False):
    with tempfile.TemporaryDirectory(prefix='minicloud-bootstrap-test-') as temp:
        root = Path(temp)
        commands = root / 'bin'
        commands.mkdir()
        for name in ('timeout', 'sudo', 'cloud-init', 'id', 'sg', 'docker', 'jq'):
            command = commands / name
            command.write_text(stub)
            command.chmod(0o755)
        # Neither command-string evaluation nor sg's /bin/sh may reinterpret argv.
        script = root / "deploy script ' ; $(touch INJECTED)\n.sh"
        script.write_text(bootstrap + probe)
        payload = root / "payload ' ; $(touch INJECTED)\n.json"
        payload.write_text('{}')
        args = [str(payload)] + (['--observability'] if observability else [])
        calls = root / 'calls'
        env = dict(os.environ, PATH=str(commands) + os.pathsep + os.environ['PATH'],
                   CALLS=str(calls), CURRENT_GROUPS=current, ACCOUNT_GROUPS=account,
                   REFRESH_WORKS='1' if refresh else '0', CLOUD_INIT_STATUS=str(cloud_status))
        env.pop('MINICLOUD_DOCKER_GROUP_REFRESHED', None)
        result = subprocess.run(['bash', str(script), *args], env=env, cwd=root,
                                text=True, capture_output=True, timeout=10)
        events = [json.loads(line) for line in calls.read_text().splitlines()]
        assert not (root / 'INJECTED').exists()
        for event in events:
            if event['event'] == 'probe':
                assert event['args'] == args, event
        return result, [event['event'] for event in events]


for observability in (False, True):
    result, events = run_case('minicloud', 'minicloud docker', observability=observability)
    assert result.returncode == 0, result.stderr
    assert events == ['cloud-init', 'sg', 'cloud-init', 'probe', 'docker'], events

result, events = run_case('minicloud docker', 'minicloud docker')
assert result.returncode == 0 and events == ['cloud-init', 'probe', 'docker']

result, events = run_case('minicloud', 'minicloud')
assert result.returncode != 0 and events == ['cloud-init', 'probe', 'docker']

result, events = run_case('minicloud', 'minicloud docker', refresh=False)
assert result.returncode != 0 and events == ['cloud-init', 'sg', 'cloud-init']
assert '__MINICLOUD_DEPLOY_FAILURE__=bootstrap_failed' in result.stdout

result, events = run_case('minicloud', 'minicloud docker', cloud_status=1)
assert result.returncode != 0 and events == ['cloud-init']
assert '__MINICLOUD_DEPLOY_FAILURE__=bootstrap_failed' in result.stdout

result, events = run_case('minicloud', 'minicloud docker', cloud_status=2)
assert result.returncode == 0 and events == ['cloud-init', 'sg', 'cloud-init', 'probe', 'docker']

print('Passed: first-SSH Docker group refresh, bootstrap ordering, argv preservation, authorized membership, and bounded failures.')


def runtime_function(name):
    body = source.split('\n' + name + '() {', 1)[1].split('\n}\n', 1)[0]
    return name + '() {' + body + '\n}\n'


def ghcr_case(token='', pull_status=0, login_status=0,
              image='ghcr.io/muniventures/minicloud-backup-repository:release-test'):
    functions = ''.join(runtime_function(name) for name in ('has_external_ghcr_images', 'preflight', 'start_infra'))
    stubs = r'''
phase() { :; }
sudo() { :; }
caddy() { :; }
flock() { :; }
docker() {
  printf 'docker %s\n' "$*" >> "$CALLS"
  if [ "$1" = login ]; then
    read -r supplied_token
    [ "$supplied_token" = "$GHCR_TOKEN" ] || return 20
    return "$LOGIN_STATUS"
  fi
  return 21
}
compose() { printf 'compose %s\n' "$*" >> "$CALLS"; return "$PULL_STATUS"; }
configure_openbao() { echo configured >> "$CALLS"; }
'''
    with tempfile.TemporaryDirectory(prefix='minicloud-ghcr-test-') as temp:
        root = Path(temp)
        payload = root / 'payload.json'
        payload.write_text('{}')
        calls = root / 'calls'
        env = dict(os.environ, CALLS=str(calls), PAYLOAD_FILE=str(payload), GHCR_TOKEN=token,
                   GHCR_USERNAME='synthetic-user', APP_NAME='repository', CUSTOMER='customer', APP_ENV='staging',
                   DATABASE='none', SERVICES_JSON=json.dumps([
                       {'name': 'repository', 'image': image, 'port': 8080, 'public': True}]),
                   IMAGE_REF='', MAX_PARALLEL_NETWORK_OPERATIONS='1', MINICLOUD_OPENBAO_ENABLED='false',
                   BUILD_ARTIFACTS_JSON='', PULL_STATUS=str(pull_status), LOGIN_STATUS=str(login_status))
        result = subprocess.run(['bash', '-c', 'set -euo pipefail\n' + functions + stubs + '\npreflight\nstart_infra'],
                                env=env, text=True, capture_output=True, timeout=10)
        return result, calls.read_text().splitlines() if calls.exists() else []


result, events = ghcr_case()
assert result.returncode == 0, (result.stdout, result.stderr)
assert events == ['compose pull', 'configured'], events
result, events = ghcr_case('synthetic-token', image='ghcr.io/synthetic/private:release-test')
assert result.returncode == 0, (result.stdout, result.stderr)
assert events == ['docker login ghcr.io -u synthetic-user --password-stdin', 'compose pull', 'configured'], events
result, events = ghcr_case(pull_status=17, image='ghcr.io/synthetic/private:release-test')
assert result.returncode == 17 and events == ['compose pull'], (result.returncode, events)
result, events = ghcr_case('synthetic-token', login_status=18)
assert result.returncode == 18 and len(events) == 1 and events[0].startswith('docker login '), events
print('Passed: anonymous public GHCR pull, explicit credential login, and failed pull/login block activation.')
