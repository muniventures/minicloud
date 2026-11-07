#!/usr/bin/env bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 - "$SCRIPT_DIR/remote-deploy.sh" <<'TEST'
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import zipfile

source = Path(sys.argv[1]).read_text()
start = source.index('    if ! python3 - "${archive_path}" "${extract_dir}" <<\'PY\'\n')
script = source[start:].split("\n", 1)[1].split("\nPY\n", 1)[0]
with tempfile.TemporaryDirectory(prefix="minicloud-artifact-mode-test-") as tmp:
    root = Path(tmp)
    archive = root / "source.zip"
    with zipfile.ZipFile(archive, "w") as z:
        for name, mode in [("nested/appsettings.json", 0o100600), ("entrypoint.sh", 0o104755)]:
            entry = zipfile.ZipInfo(name)
            entry.external_attr = mode << 16
            z.writestr(entry, "source")
    parent = root / "private"
    parent.mkdir(mode=0o700)
    target = parent / "source"
    result = subprocess.run([sys.executable, "-c", script, str(archive), str(target)],
                            preexec_fn=lambda: os.umask(0o077), capture_output=True, text=True)
    assert result.returncode == 0, result.stderr
    assert stat.S_IMODE((target / "nested/appsettings.json").stat().st_mode) == 0o644
    assert stat.S_IMODE((target / "entrypoint.sh").stat().st_mode) == 0o755
    assert stat.S_IMODE((target / "nested").stat().st_mode) == 0o755
    assert stat.S_IMODE(parent.stat().st_mode) == 0o700
    for name, mode in [("../escaped", 0o100644), ("link", 0o120777)]:
        unsafe = root / "unsafe.zip"
        with zipfile.ZipFile(unsafe, "w") as z:
            entry = zipfile.ZipInfo(name)
            entry.external_attr = mode << 16
            z.writestr(entry, "target")
        result = subprocess.run([sys.executable, "-c", script, str(unsafe), str(parent / "rejected")],
                                capture_output=True, text=True)
        assert result.returncode != 0
    assert not (parent / "escaped").exists()
print("PASS: non-root source readability, execute bits, private parent, traversal and symlink rejection")
TEST
