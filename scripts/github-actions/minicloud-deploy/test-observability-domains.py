#!/usr/bin/env python3
"""Render observability routes with operator endpoints; reject missing/unsafe hosts."""
import json
import os
from pathlib import Path
import subprocess
import tempfile

source = Path(__file__).with_name("remote-deploy.sh").read_text()
start = source.index("write_observability_caddy_config() {")
end = source.index("\nwait_for_observability_service()", start)
function = source[start:end]
with tempfile.TemporaryDirectory() as directory:
    target = Path(directory) / "observability.caddy"
    script = '''set -euo pipefail
sudo() {
  case "$1" in
    tee) cat > "$OUTPUT" ;;
    mkdir|chmod|caddy) : ;;
    *) return 1 ;;
  esac
}
reload_caddy() { :; }
''' + function + "\nwrite_observability_caddy_config\n"
    for ingestion, access, valid in [
        ("https://ingest.operator.test/otlp/v1/logs", "https://logs.operator.test", True),
        ("", "", False),
        ("https://safe.test/otlp/v1/logs", "https://bad.test { injected }", False),
        ("http://ingest.operator.test/otlp/v1/logs", "https://logs.operator.test", False),
    ]:
        target.unlink(missing_ok=True)
        env = dict(os.environ, APP_NAME="minicloud", OUTPUT=str(target),
                   SERVICES_JSON=json.dumps([{"env": {"Telemetry__IngestionUrl": ingestion,
                                                      "Telemetry__AccessOrigin": access}}]))
        result = subprocess.run(["bash", "-c", script], env=env, capture_output=True, text=True)
        assert (result.returncode == 0) == valid, result.stderr
        if valid:
            assert target.read_text() == "ingest.operator.test {\n  reverse_proxy 127.0.0.1:8088\n}\n\nlogs.operator.test {\n  reverse_proxy 127.0.0.1:8088\n}\n"
        else:
            assert not target.exists()
print("Passed: operator telemetry routes rendered; missing/unsafe origins rejected")
