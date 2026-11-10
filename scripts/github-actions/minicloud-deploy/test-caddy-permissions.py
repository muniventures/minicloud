#!/usr/bin/env python3
"""Exercise activation, rollback and service-user validation under private umask."""
from pathlib import Path
import json
import os
import re
import subprocess
import sys
import tempfile

source = Path(__file__).with_name("remote-deploy.sh").read_text()
def function(name):
    match = re.search(r"^" + name + r"\(\) \{\n.*?^\}", source, re.M | re.S)
    assert match, name
    return match.group(0)

stubs = r'''
phase() { :; }
sudo() {
  if [ "$1" = "-u" ]; then
    [ "$2" = "caddy" ] || return 1
    shift 2
    echo service-validation >> "$CALLS"
    [ "$(stat -c %a "$CADDY_APP" 2>/dev/null || stat -f %Lp "$CADDY_APP")" = 644 ] || return 1
  fi
  if [ "$1" = "caddy" ]; then
    if [ "$2" = "validate" ]; then ! grep -q invalid "$CADDY_APP"; return; fi
    echo reload >> "$CALLS"
    return
  fi
  command "$@"
}
'''
with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    payload = root / 'payload.json'
    payload.write_text('{}')
    (root / 'shared').mkdir()
    env = dict(os.environ, CADDY_APP=str(root / "app.caddy"),
               CADDY_NEXT=str(root / "next"), CADDY_PREVIOUS=str(root / "previous"),
               CALLS=str(root / "calls"), PAYLOAD_FILE=str(payload), APP_DIR=str(root), APP_NAME='database')
    script = "set -euo pipefail\numask 077\n" + stubs + "\n" + function("reload_caddy") + "\n" + function("activate_caddy")
    next_file = Path(env["CADDY_NEXT"])
    next_file.write_text("valid\n"); next_file.chmod(0o600)
    subprocess.run(["bash", "-c", script + "\nactivate_caddy"], env=env, check=True)
    live = Path(env["CADDY_APP"])
    assert live.stat().st_mode & 0o777 == 0o644
    assert (root / "calls").read_text().splitlines() == ["service-validation", "reload"]
    next_file.write_text("invalid\n")
    live.chmod(0o600)
    result = subprocess.run(["bash", "-c", script + "\nactivate_caddy"], env=env)
    assert result.returncode == 1
    assert live.read_text() == "valid\n"
    assert live.stat().st_mode & 0o777 == 0o644
    (root / "calls").unlink()
    live.chmod(0o600)
    result = subprocess.run(["bash", "-c", script + "\nreload_caddy"], env=env)
    assert result.returncode == 1
    assert (root / "calls").read_text().splitlines() == ["service-validation"]

    payload.write_text(json.dumps({'managed_database_server': True, 'database': 'postgres', 'services': []}))
    next_file.write_text('')
    (root / 'calls').unlink()
    infra_script = ('set -euo pipefail\n' + function('validate_next_configs') + '\n' + function('activate_caddy') + r'''
phase() { :; }
compose() { [ "$*" = config ]; echo compose-config >> "$CALLS"; }
sudo() { echo unexpected-caddy-mutation >> "$CALLS"; return 99; }
validate_next_configs
activate_caddy
''')
    subprocess.run(['bash', '-c', infra_script], env=env, check=True)
    assert live.read_text() == 'valid\n'
    assert (root / 'calls').read_text().splitlines() == ['compose-config']

    if '--real-caddy' in sys.argv:
        imports = root / 'imports'
        imports.mkdir()
        empty = imports / 'database.caddy'
        empty.write_text('')
        config = root / 'Caddyfile'
        config.write_text('import /fixture/imports/*.caddy\n')
        def validate():
            return subprocess.run(['docker', 'run', '--rm', '--network', 'none', '-v',
                                   str(root) + ':/fixture:ro', 'caddy:2.6.2-alpine',
                                   'caddy', 'validate', '--config', '/fixture/Caddyfile'],
                                  capture_output=True, text=True, timeout=60)
        rejected = validate()
        assert rejected.returncode != 0 and 'EOF' in rejected.stderr, rejected.stderr
        empty.unlink()
        (imports / 'web.caddy').write_text('http://example.test {\n reverse_proxy 127.0.0.1:18080\n}\n')
        accepted = validate()
        assert accepted.returncode == 0, accepted.stderr
        assert live.read_text() == 'valid\n'
        print('Passed: real Caddy rejects empty imported snippet; existing HTTP route remains valid')
print("Passed: readable activation, readable rollback, unreadable configuration blocks reload")
print('Passed: infrastructure-only PostgreSQL validates Compose without changing Caddy routes')
