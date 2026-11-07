#!/usr/bin/env python3
"""Render the production Caddy block with multiple, disabled and private bindings."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

source = Path(__file__).with_name("remote-deploy.sh").read_text()
start = source.index("  {\n    printf '%s' \"${SERVICES_JSON}\" | jq")
end = source.index('  } > "${CADDY_NEXT}"', start) + len('  } > "${CADDY_NEXT}"')
block = source[start:end]
cases = [
    ({"name": "portal", "public": True, "hosts": ["legacy.example.test", "nexus.example.test"], "host_port": 18082},
     "legacy.example.test {\n  reverse_proxy 127.0.0.1:18082\n}\nnexus.example.test {\n  reverse_proxy 127.0.0.1:18082\n}\n"),
    ({"name": "disabled", "public": True, "hosts": [], "host_port": 18083}, ""),
    ({"name": "private", "public": False, "hosts": ["private.example.test"], "host_port": 18084}, ""),
    ({"name": "workflow", "public": True, "host": "workflow.example.test", "host_port": 18085},
     "workflow.example.test {\n  reverse_proxy 127.0.0.1:18085\n}\n"),
]
with tempfile.TemporaryDirectory() as directory:
    target = Path(directory) / "app.caddy.next"
    for service, expected in cases:
        env = dict(os.environ, SERVICES_JSON=json.dumps([service]), CUSTOMER="org", APP_NAME="app",
                   DOMAIN="fallback.example.test", CADDY_NEXT=str(target))
        subprocess.run(["bash", "-c", "set -euo pipefail\n" + block], env=env, check=True)
        assert target.read_text() == expected, (service, target.read_text())
print("Passed: both aliases share the service port; disabled/private bindings omitted; workflow host preserved")
