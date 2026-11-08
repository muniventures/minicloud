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
