#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 - "${SCRIPT_DIR}/remote-deploy.sh" <<'PY'
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time

source = Path(sys.argv[1]).read_text()
footer = '\nif [ "${2:-}" = "--observability" ]; then'
assert footer in source

stubs = r'''
verify_image_asset_requirements() { :; }
preflight() {
  phase preflight
  [[ "$MAX_PARALLEL_NETWORK_OPERATIONS" =~ ^[1-8]$ ]]
}
acquire_lock() { :; }
install_runtime_maintenance() { :; }
prepare_paths() { mkdir -p "$APP_DIR/shared"; }
verify_retained_images() { :; }
build_artifacts() { :; }
wait_for_rollout_gate() { :; }
render_next_configs() { normalize_services; }
validate_next_configs() { :; }
migrate_postgres_if_needed() { :; }
ensure_postgres_data_owner() { :; }
start_infra() { :; }
sync_postgres_password() { :; }
start_app_services() { :; }
activate_caddy() { echo activate_caddy >> "$CALLS"; }
record_deploy() { :; }
deploy_observability_optional() { :; }
cleanup_deployment_artifacts() { :; }
compose_diagnostics() { echo diagnostics >> "$CALLS"; }
sleep() {
  if [ "$1" = "2" ]; then
    return
  fi
  command sleep "$@"
}
'''

instrumented = source.replace(footer, '\n' + stubs + footer)
# Observe the real terminal probe failure before sibling cancellation starts.
terminal = '    return 1\n  }\n\n  stop_health_checks() {'
assert terminal in instrumented
instrumented = instrumented.replace(terminal, '    : > "$STATE/terminal_failure"\n' + terminal, 1)

fake_curl = r'''#!/usr/bin/env python3
import fcntl, os, signal, sys, time
from pathlib import Path
state = Path(os.environ['STATE'])
port = sys.argv[-1].split(':', 2)[-1].split('/', 1)[0]
lock = (state / 'lock').open('w')
finished = False
def update(delta):
    global finished
    fcntl.flock(lock, fcntl.LOCK_EX)
    active = int((state / 'active').read_text()) + delta
    (state / 'active').write_text(f'{active}\n')
    if delta > 0:
        maximum = int((state / 'max').read_text())
        (state / 'max').write_text(f'{max(active, maximum)}\n')
        with (state / 'events').open('a') as events: events.write(f'start:{port}\n')
    fcntl.flock(lock, fcntl.LOCK_UN)
    if delta < 0: finished = True
def terminate(signum, frame):
    with (state / 'terminated').open('a') as output: output.write(f'{port}\n')
    if not finished: update(-1)
    raise SystemExit(143)
signal.signal(signal.SIGTERM, terminate)
signal.signal(signal.SIGINT, terminate)
update(1)
if port == os.environ.get('FAIL_PORT'):
    update(-1)
    print(f'failed {port}', file=sys.stderr)
    raise SystemExit(22)
time.sleep(3)
with (state / 'events').open('a') as events: events.write(f'end:{port}\n')
update(-1)
'''

def run_case(limit, fail_port=None, cancel=False):
    with tempfile.TemporaryDirectory(prefix='minicloud-health-test-') as tmp:
        root = Path(tmp)
        host = root / 'opt' / 'minicloud'
        (host / 'apps' / 'health-app' / 'shared').mkdir(parents=True)
        script = root / 'deploy.sh'
        script.write_text(instrumented.replace('/opt/minicloud', str(host)))
        script.chmod(0o755)
        payload = root / 'payload.json'
        payload.write_text(json.dumps({
            'app_name': 'health-app', 'customer': 'customer', 'app_env': 'production',
            'deployment_id': 'dep-health', 'database': 'none',
            'max_parallel_network_operations': limit,
            'services': [
                {'name': f'service-{port}', 'image': f'test:{port}', 'port': port,
                 'host_port': port, 'public': True, 'health_path': '/health'}
                for port in (18080, 18081, 18082)
            ],
        }))
        state = root / 'state'
        state.mkdir()
        (state / 'active').write_text('0\n')
        (state / 'max').write_text('0\n')
        (state / 'events').write_text('')
        (state / 'terminated').write_text('')
        calls = root / 'calls'
        bindir = root / 'bin'
        bindir.mkdir()
        curl = bindir / 'curl'
        curl.write_text(fake_curl)
        curl.chmod(0o755)
        env = {**os.environ, 'PATH': f'{bindir}:{os.environ["PATH"]}',
               'STATE': str(state), 'CALLS': str(calls), 'FAIL_PORT': str(fail_port or '')}
        started = time.monotonic()
        process = subprocess.Popen(['bash', str(script), str(payload)], env=env,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        if cancel or fail_port:
            deadline = time.monotonic() + 10
            ready = lambda: (state / 'events').read_text().strip() if cancel else (state / 'terminal_failure').exists()
            while not ready():
                assert time.monotonic() < deadline, 'probe cancellation boundary was not reached'
                if process.poll() is not None:
                    break
                time.sleep(.005)
            assert ready(), 'deployment exited before the expected probe boundary'
        cancellation_started = time.monotonic()
        if cancel:
            process.terminate()
        stdout, stderr = process.communicate(timeout=15)
        result = subprocess.CompletedProcess(process.args, process.returncode, stdout, stderr)
        completed = time.monotonic()
        print(f'TIMING health_fixture setup_retry_ms={(cancellation_started - started) * 1000:.1f} completion_ms={(completed - cancellation_started) * 1000:.1f}')
        # The unchanged 2s bound measures actual cancellation/reaping, not allowed health retries.
        return result, int((state / 'max').read_text()), completed - cancellation_started, \
            calls.read_text() if calls.exists() else '', (state / 'terminated').read_text()

serial, serial_max, _, serial_calls, _ = run_case(1)
assert serial.returncode == 0, (serial.stdout, serial.stderr)
assert serial_max == 1, serial_max
assert 'activate_caddy' in serial_calls
print('PASS: local health limit one remains serial')

parallel, parallel_max, _, parallel_calls, _ = run_case(2)
assert parallel.returncode == 0, (parallel.stdout, parallel.stderr)
assert parallel_max == 2, parallel_max
assert 'activate_caddy' in parallel_calls
print('PASS: local health checks use bounded rolling concurrency')

failed, _, elapsed, failed_calls, terminated = run_case(2, 18080)
assert failed.returncode != 0
assert elapsed < 2, elapsed
assert 'activate_caddy' not in failed_calls
assert terminated.strip(), (failed.stdout, failed.stderr)
assert 'Health check failed for service-18080' in failed.stdout
print('PASS: failed probe stops and reaps siblings before activation')

cancelled, _, elapsed, cancelled_calls, terminated = run_case(2, cancel=True)
assert cancelled.returncode != 0
assert elapsed < 2, elapsed
assert 'activate_caddy' not in cancelled_calls
assert len(terminated.splitlines()) == 2, terminated
print('PASS: cancellation stops and reaps active probes before activation')

print('All 4 runtime local health test cases passed successfully!')
PY
