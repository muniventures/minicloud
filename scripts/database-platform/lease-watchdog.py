#!/usr/bin/env python3
"""Stop this operation's process group if its SSH ownership heartbeat expires."""
import json
import os
from pathlib import Path
import signal
import sys
import time

request=Path(sys.argv[1])
leader=int(sys.argv[2])
with request.open() as stream:
    token=json.load(stream).get('ownership_token')
if not token:
    raise SystemExit('Missing operation lease')
lease=request.with_name('worker.lease')
while True:
    try:
        valid=not lease.is_symlink() and lease.read_text()==token and time.time()-lease.stat().st_mtime<=5
    except OSError:
        valid=False
    if not valid:
        group=os.getpgid(leader)
        if group==leader:
            os.killpg(group,signal.SIGTERM)
        else:
            os.kill(leader,signal.SIGTERM)
        raise SystemExit(1)
    if '--once' in sys.argv[3:]:
        raise SystemExit(0)
    time.sleep(1)
