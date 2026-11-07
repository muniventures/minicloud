#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 - "$SCRIPT_DIR/remote-deploy.sh" "$SCRIPT_DIR/create-cloud-init-bootstrap.sh" <<'PY'
import json
import os
from pathlib import Path
import signal
import stat
import subprocess
import sys
import tempfile
import time
import zipfile

remote_deploy_source = Path(sys.argv[1]).read_text()
cloud_init_source = Path(sys.argv[2]).read_text()

# 1. Verify cloud-init swapfile bootstrap contract
assert '/swapfile' in cloud_init_source, "Swapfile not found in create-cloud-init-bootstrap.sh"
assert '2G' in cloud_init_source or 'count=2048' in cloud_init_source, "2GB swapfile sizing not found"
assert 'swappiness=10' in cloud_init_source, "vm.swappiness=10 not found in create-cloud-init-bootstrap.sh"
assert '99-minicloud-swap.conf' in cloud_init_source, "sysctl swap config not found in create-cloud-init-bootstrap.sh"
print('PASS: cloud-init swapfile bootstrap contract')

# Build-pool cancellation must be bounded and must verify process-group exit.
stop_builds_start = remote_deploy_source.index('  stop_builds() {')
stop_builds_end = remote_deploy_source.index('\n  reap_one_build() {', stop_builds_start)
stop_builds_source = remote_deploy_source[stop_builds_start:stop_builds_end]
assert 'kill -TERM -- "-${pid}"' in stop_builds_source
assert 'kill -KILL -- "-${pid}"' in stop_builds_source
assert 'for attempt in $(seq 1 50)' in stop_builds_source
assert 'for attempt in $(seq 1 20)' in stop_builds_source
assert 'did not stop after SIGKILL' in stop_builds_source
print('PASS: build cancellation TERM/KILL/verification contract')

# Helper to create zip archives
def create_zip(path, entries):
    with zipfile.ZipFile(path, 'w') as zf:
        for fname, content, is_symlink in entries:
            zi = zipfile.ZipInfo(fname)
            if is_symlink:
                zi.create_system = 3
                zi.external_attr = (stat.S_IFLNK | 0o777) << 16
            zf.writestr(zi, content)

stubs = r'''
# This case exercises the activation fence itself. The separate worker-exit
# case covers watchdog termination; letting both fire here races the assertion.
if [ "${LEASE_RACE:-0}" = "1" ]; then
  start_worker_lease_watchdog() { :; }
  stop_worker_lease_watchdog() { :; }
fi
CALLS="${CALLS:-/tmp/minicloud-timing/calls}"
acquire_lock() { :; }
install_runtime_maintenance() { :; }
prepare_builder() { BUILD_BUILDER=default; BUILD_PLATFORM=linux/amd64; }
prepare_paths() { mkdir -p "$APP_DIR/shared"; }
validate_next_configs() { :; }
migrate_postgres_if_needed() { :; }
ensure_postgres_data_owner() { :; }
configure_openbao() { :; }
sync_postgres_password() { :; }
verify_local_health() { echo app_health >> "$CALLS"; }
activate_caddy() {
  echo activate_caddy >> "$CALLS"
  if [ "${LEASE_RACE:-0}" = "1" ]; then printf '%s\n' changed-owner > "$LEASE_PATH"; fi
}
record_deploy() { echo record_deploy >> "$CALLS"; }
deploy_observability_optional() { :; }
cleanup_deployment_artifacts() { echo cleanup_artifacts >> "$CALLS"; }
mapfile() { read -r "$2" || true; }
sudo() {
  if [ "$1" = -u ]; then [ "$2" = caddy ] || return 1; shift 2; fi
  if [ "$1" = caddy ]; then :; else "$@"; fi
}
sleep() { :; }
curl() { :; }
docker() {
  local args="$*"
  echo "docker $args" >> "$CALLS"
  if [ "$1" = login ]; then
    echo "DOCKER_LOGIN: $*" >> "$CALLS"
    return 0
  fi
  if [ "$1 $2" = "image inspect" ] && [ "${3:-}" = "--format" ]; then
    echo '{}'
    return 0
  fi
  if [ "$1 $2" = "buildx build" ]; then
    echo "DOCKER_BUILD: $*" >> "$CALLS"
    if [ "${SLOW_BUILD:-0}" = "1" ]; then
      bash -c 'printf "%s\n" "$$" > "$BUILD_PID"; trap "" TERM; while true; do sleep 1; done'
    fi
    if [ "${FAIL_BUILD:-0}" = "1" ]; then
      echo "Step 1/2 : FROM alpine"
      echo "Step 2/2 : RUN exit 1"
      echo "The command '/bin/sh -c exit 1' returned a non-zero code: 1" >&2
      return 1
    fi
    echo "Step 1/2 : FROM alpine"
    echo "Step 2/2 : RUN echo build_success"
    echo "Successfully tagged test"
    return 0
  fi
  if [ "$1" = compose ]; then
    case "$args" in
      *"pull"*)
        echo "DOCKER_PULL: $*" >> "$CALLS"
        ;;
      *"up"*)
        echo "DOCKER_UP: $*" >> "$CALLS"
        ;;
    esac
    return 0
  fi
  return 0
}
'''

footer = '\nif [ "${2:-}" = "--observability" ]; then'
assert footer in remote_deploy_source, "footer not found in remote-deploy.sh"
instrumented_script = remote_deploy_source.replace(footer, '\n' + stubs + footer)
instrumented_output = os.environ.get('MINICLOUD_RUNTIME_INSTRUMENTED_SCRIPT_OUTPUT')
if instrumented_output:
    ssh_stubs = '\nstart_worker_lease_watchdog() { :; }\nstop_worker_lease_watchdog() { :; }\n'
    connected_script = instrumented_script.replace(footer, ssh_stubs + footer)
    Path(instrumented_output).write_text(connected_script.replace('/opt/minicloud', '/tmp/minicloud-timing'))

cases = [
    ('happy_path_artifact', False, False, False, False, True, None),
    ('path_traversal_archive', True, False, False, False, False, None),
    ('symlink_archive', False, True, False, False, False, None),
    ('docker_build_failure', False, False, True, False, False, None),
    ('custom_manifest_paths', False, False, False, True, True, None),
    ('mixed_services_ghcr', False, False, False, False, True, None),
    ('retained_partial', False, False, False, False, True, None),
    ('term_cancellation', False, False, False, False, False, 'TERM'),
    ('timeout_cancellation', False, False, False, False, False, 'timeout'),
    ('disconnect_cancellation', False, False, False, False, False, 'disconnect'),
    ('worker_lease_expiry', False, False, False, False, False, 'worker-exit'),
    ('activation_lease_race', False, False, False, False, False, None),
]

for label, with_traversal, with_symlink, fail_build, with_manifest, expected_success, cancel_mode in cases:
    with tempfile.TemporaryDirectory(prefix='minicloud-build-test-') as tmpdir:
        root = Path(tmpdir)
        host_dir = root / 'opt' / 'minicloud'
        host_dir.mkdir(parents=True)
        (host_dir / 'caddy' / 'apps').mkdir(parents=True)
        (host_dir / 'locks').mkdir(parents=True)

        app_name = 'test-app'
        deployment_id = 'dep_test123'

        script_path = root / 'deploy.sh'
        script_content = instrumented_script.replace('/opt/minicloud', str(host_dir))
        script_path.write_text(script_content)
        script_path.chmod(0o755)

        # Prepare archive
        archive_path = root / 'service.zip'
        if with_traversal:
            entries = [('../evil.txt', 'evil content', False), ('Dockerfile', 'FROM alpine', False)]
        elif with_symlink:
            entries = [('link_to_root', '/etc/passwd', True), ('Dockerfile', 'FROM alpine', False)]
        elif with_manifest:
            manifest = {
                'dockerfilePath': 'sub/Dockerfile.custom',
                'buildContextPath': 'sub',
                'buildEnv': {'VITE_PUBLIC_API_URL': 'https://api.example.test'},
            }
            entries = [
                ('minicloud-artifact-manifest.json', json.dumps(manifest), False),
                ('sub/Dockerfile.custom', 'FROM alpine\nRUN echo custom', False),
                ('sub/app.txt', 'custom context', False),
            ]
        else:
            entries = [
                ('Dockerfile', 'FROM alpine\nRUN echo hi', False),
                ('index.js', 'console.log("hello");', False),
            ]
        create_zip(archive_path, entries)

        image_tag = f'minicloud/{app_name}-web:{deployment_id}'
        payload_data = {
            'app_name': app_name,
            'customer': 'test-cust',
            'app_env': 'production',
            'deployment_id': deployment_id,
            'database': 'none',
            'build_artifacts': [
                {
                    'service_name': 'web',
                    'artifact_archive': str(archive_path),
                    'image_tag': image_tag,
                }
            ],
            'services': [
                {
                    'name': 'web',
                    'image': 'ghcr.io/muniventures/legacy/web:old-placeholder',
                    'port': 8080,
                    'public': True,
                    'path': '/',
                }
            ]
        }

        if label == 'mixed_services_ghcr':
            payload_data['ghcr_token'] = 'fake-token'
            payload_data['ghcr_username'] = 'fake-user'
            payload_data['services'].append({
                'name': 'worker',
                'image': 'ghcr.io/muniventures/minicloud-worker:v1',
                'port': 9000,
                'public': False,
            })
        elif label == 'retained_partial':
            payload_data['build_artifacts'] = []
            payload_data['services'] = [
                {
                    'name': 'web',
                    'image': 'minicloud/test-app-web:dep_previous',
                    'port': 8080,
                    'host_port': 18080,
                    'public': True,
                    'path': '/',
                    'host': 'web.example.test',
                    'env': {'MODE': 'retained'},
                },
                {
                    'name': 'worker',
                    'image': 'redis:7-alpine',
                    'port': 6379,
                    'host_port': 16379,
                    'public': False,
                },
            ]

        payload_path = root / 'payload.json'
        if cancel_mode == 'worker-exit' or label == 'activation_lease_race':
            lease_path = root / 'worker.lease'
            lease_path.write_text('owner-token')
            payload_data['ownership_token'] = 'owner-token'
            payload_data['worker_lease_file'] = str(lease_path)
        payload_path.write_text(json.dumps(payload_data))

        calls_path = root / 'calls'
        bin_dir = root / 'bin'
        bin_dir.mkdir()
        for b in ['caddy', 'flock']:
            fake_bin = bin_dir / b
            fake_bin.write_text('#!/bin/sh\nexit 0\n')
            fake_bin.chmod(0o755)

        env = {
            **os.environ,
            'PATH': f"{bin_dir}:{os.environ['PATH']}",
            'CALLS': str(calls_path),
            'FAIL_BUILD': '1' if fail_build else '0',
            'SLOW_BUILD': '1' if cancel_mode else '0',
            'BUILD_PID': str(root / 'build.pid'),
            'LEASE_RACE': '1' if label == 'activation_lease_race' else '0',
            'LEASE_PATH': str(root / 'worker.lease'),
        }

        if cancel_mode:
            process = subprocess.Popen(
                ['bash', str(script_path), str(payload_path)], env=env,
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                start_new_session=True)
            build_pid_path = root / 'build.pid'
            deadline = time.monotonic() + 5
            while not build_pid_path.exists() and time.monotonic() < deadline:
                if process.poll() is not None:
                    stdout, stderr = process.communicate()
                    raise AssertionError(f'slow build exited before start: {stdout} {stderr}')
                time.sleep(0.05)
            if not build_pid_path.exists():
                process.kill()
                stdout, stderr = process.communicate()
                raise AssertionError(f'slow build did not start: {stdout} {stderr}')
            build_pid = int(build_pid_path.read_text().strip())
            # These are the same remote signal used by direct TERM, the SSH
            # command-timeout cleanup, and disconnect cleanup respectively.
            if cancel_mode != 'worker-exit':
                process.send_signal(signal.SIGTERM)
            stdout, stderr = process.communicate(timeout=10)
            result = subprocess.CompletedProcess(process.args, process.returncode, stdout, stderr)
            try:
                os.kill(build_pid, 0)
            except ProcessLookupError:
                pass
            else:
                raise AssertionError(f'build child {build_pid} survived TERM-to-KILL cleanup')
        else:
            result = subprocess.run(
                ['bash', str(script_path), str(payload_path)],
                env=env,
                capture_output=True,
                text=True,
                timeout=15,
            )

        log = calls_path.read_text() if calls_path.exists() else ''
        trace_output = os.environ.get('MINICLOUD_RUNTIME_TIMING_TRACE_OUTPUT')
        if trace_output and label == 'happy_path_artifact':
            Path(trace_output).write_text(result.stdout + result.stderr)
        assert (result.returncode == 0) == expected_success, (
            f"Case {label} unexpected returncode {result.returncode}. stdout: {result.stdout}, stderr: {result.stderr}"
        )

        if not expected_success:
            assert 'cleanup_artifacts' not in log, label
        if expected_success:
            assert log.index('app_health') < log.index('record_deploy') < log.index('cleanup_artifacts'), label

        if cancel_mode:
            assert result.returncode != 0, label
            assert f'__MINICLOUD_DEPLOY_COMPLETE__={deployment_id}' not in result.stdout, label
            print(f'PASS: {cancel_mode} cancellation reaps active build process groups')
        elif label == 'activation_lease_race':
            assert result.returncode != 0, label
            assert 'activate_caddy' in log
            assert 'record_deploy' not in log
            assert f'__MINICLOUD_DEPLOY_COMPLETE__={deployment_id}' not in result.stdout
            assert 'lease changed during activation' in result.stderr
            print('PASS: activation lease change rolls back before publish completion')
        elif expected_success:
            assert f'__MINICLOUD_DEPLOY_COMPLETE__={deployment_id}' in result.stdout, label
            if label == 'retained_partial':
                assert 'DOCKER_BUILD' not in log, label
                app_compose = json.loads((host_dir / 'apps' / app_name / 'app.compose.json').read_text())
                assert app_compose['services']['web']['image'] == 'minicloud/test-app-web:dep_previous'
                assert app_compose['services']['web']['environment']['MODE'] == 'retained'
                assert app_compose['services']['worker']['image'] == 'redis:7-alpine'
                caddy = (host_dir / 'apps' / app_name / 'shared' / f'{app_name}.caddy.next').read_text()
                assert 'web.example.test {' in caddy
                assert 'reverse_proxy 127.0.0.1:18080' in caddy
                assert 'worker' not in caddy
            else:
                assert 'phase=build_artifacts' in result.stdout, f"build_artifacts phase missing in {label}"
                assert 'DOCKER_BUILD: buildx build --builder default --platform linux/amd64 --load --progress plain' in log and f'--tag {image_tag}' in log, f"docker build call missing in {label}"
                # Check build prefix streaming
                assert '[build:web]' in result.stdout, f"build prefix [build:web] missing from stdout in {label}"
                # Check safe extraction destination
                extracted_src = host_dir / 'apps' / app_name / 'builds' / deployment_id / 'src' / 'web'
                assert extracted_src.is_dir(), f"Extracted dir does not exist: {extracted_src}"
            if with_manifest:
                assert (extracted_src / 'sub' / 'Dockerfile.custom').exists()
                assert f'--file {extracted_src}/sub/Dockerfile.custom' in log
                assert '--build-arg VITE_PUBLIC_API_URL=https://api.example.test' in log
                assert 'secret_env' not in log

            if label == 'mixed_services_ghcr':
                # Should have logged into GHCR and pulled ONLY worker
                assert 'DOCKER_LOGIN: login ghcr.io' in log, f"Expected GHCR login in {label}"
                assert 'pull worker' in log, f"Expected compose pull worker in {label}"
                assert 'pull web' not in log, f"Local image web must not be pulled in {label}"
            elif label == 'retained_partial':
                assert 'pull worker' in log, f"Expected external worker pull in {label}"
                assert 'pull web' not in log, f"Retained local image web must not be pulled in {label}"
            else:
                # No GHCR login and no pull for web
                assert 'DOCKER_LOGIN' not in log, f"Unexpected DOCKER_LOGIN in {label}"
                assert 'DOCKER_PULL' not in log, f"Unexpected DOCKER_PULL in {label}"
                assert 'pull' not in log, f"Unexpected pull in {label}"
        else:
            assert 'docker_build_failed' in result.stderr or 'docker_build_failed' in result.stdout, (
                f"Expected error code docker_build_failed in output for {label}. stderr: {result.stderr}"
            )
            # Ensure live traffic was untouched (no caddy reload or record deploy)
            assert 'activate_caddy' not in log, f"activate_caddy should not have run in failing {label}"
            assert 'record_deploy' not in log, f"record_deploy should not have run in failing {label}"

        print('PASS:', label)

print('All 12 runtime direct build test cases passed successfully!')
PY
