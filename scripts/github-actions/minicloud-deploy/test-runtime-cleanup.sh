#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 - "$SCRIPT_DIR/remote-deploy.sh" <<'PYTEST'
import json, os, pathlib, subprocess, sys, tempfile
source = pathlib.Path(sys.argv[1]).read_text()
def function_block(start, end):
    return source[source.index(start):source.index(end, source.index(start))]
policy = function_block('validate_cache_policy() {', '\nacquire_lock() {')
cleanup = function_block('cleanup_deployment_artifacts() {', '\nrecord_deploy() {')
assert 'docker image prune' not in cleanup and 'docker container prune' not in cleanup
assert 'network prune' not in cleanup and '--volumes' not in cleanup
assert source.index('acquire_lock\n', source.index('main() {')) < source.index('prepare_builder; fi', source.index('main() {'))
with tempfile.TemporaryDirectory() as temp:
    calls = pathlib.Path(temp) / 'calls'
    stubs = r'''
phase() { :; }
monotonic_ms() { echo 1; }
docker() {
  printf '%s\n' "$*" >> "$CALLS"
  case "$1 $2" in
    'context show') echo default ;;
    'info --format') if [ "$3" = '{{.Architecture}}' ]; then echo x86_64; else echo /var/lib/docker; fi ;;
    'buildx version') if [ "${MISSING_BUILDER:-0}" = 1 ]; then return 1; fi; echo 'github.com/docker/buildx v0.20.1 test' ;;
    'buildx inspect') echo "Driver: ${DRIVER:-docker}" ;;
    'buildx du') printf 'Reclaimable: 1GB\nTotal: 2.1GB\n' ;;
    'builder prune') if [ "${FAIL_PRUNE:-0}" = 1 ]; then return 1; fi ;;
    'image ls') printf '%s\n' 'minicloud/club-web:current' 'minicloud/club-worker:retained' 'minicloud/club-web:old-alias' 'minicloud/another-web:current' 'postgres:16-alpine' ;;
  esac
}
'''
    disk_stub = '\nbuild_disk_free() { echo "${FREE_BYTES:-5000000000} 20000000000"; }\n'
    env = dict(os.environ, CALLS=str(calls), APP_NAME='club', DEPLOYMENT_ID='dep_test',
               CACHE_MAX_BYTES='2147483648', CACHE_MAX_AGE_HOURS='168', CACHE_MIN_FREE_BYTES='2147483648',
               CACHE_MIN_FREE_PERCENT='10', BUILD_BUILDER='default',
               SERVICES_JSON=json.dumps([{'image':'minicloud/club-web:current'}, {'image':'minicloud/club-worker:retained'}]))
    def run(command, **overrides):
        calls.write_text('')
        return subprocess.run(['bash','-euo','pipefail','-c', stubs+policy+disk_stub+cleanup+'\n'+command],
                              env=dict(env, **overrides), capture_output=True, text=True)
    result = run('cleanup_deployment_artifacts')
    assert result.returncode == 0, result.stderr
    lines = calls.read_text().splitlines()
    assert 'builder prune --force --all --filter until=168h' in lines, lines
    assert 'builder prune --force --all --keep-storage 2147483648' in lines, lines
    assert [x for x in lines if x.startswith('image rm')] == ['image rm minicloud/club-web:old-alias'], lines
    assert not any(x.startswith(('container prune','image prune','system prune')) for x in lines)
    result = run('prepare_builder')
    assert result.returncode == 0, result.stderr
    assert 'platform=linux/amd64' in result.stdout
    for overrides in [dict(MISSING_BUILDER='1'), dict(DRIVER='docker-container'), dict(FREE_BYTES='1'),
                      dict(CACHE_MAX_BYTES='0'), dict(CACHE_MAX_AGE_HOURS='721'),
                      dict(CACHE_MIN_FREE_BYTES='99999999999999999999'), dict(CACHE_MIN_FREE_PERCENT='51')]:
        result=run('prepare_builder', **overrides)
        assert result.returncode != 0, (overrides,result.stdout,result.stderr)
        if overrides.get('FREE_BYTES'):
            assert 'builder prune --force --all --keep-storage 0' in calls.read_text()
            assert 'build_disk_capacity' in result.stderr
    result=run('cleanup_deployment_artifacts',FAIL_PRUNE='1')
    assert result.returncode != 0
    assert 'image rm' not in calls.read_text()
print('PASS: bounded/aged cache, disk-pressure refusal, builder preflight, policy validation, retained images and prune failures')
PYTEST
