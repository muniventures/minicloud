#!/usr/bin/env python3
"""Exercise activation, rollback and service-user validation under private umask."""
from pathlib import Path
import os
import re
import subprocess
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
    env = dict(os.environ, CADDY_APP=str(root / "app.caddy"),
               CADDY_NEXT=str(root / "next"), CADDY_PREVIOUS=str(root / "previous"),
               CALLS=str(root / "calls"))
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
print("Passed: readable activation, readable rollback, unreadable configuration blocks reload")
