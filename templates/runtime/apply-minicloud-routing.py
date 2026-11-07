#!/usr/bin/python3 -I
"""Fixed-entrypoint Caddy installer. No caller-supplied paths or commands."""
import fcntl
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import tempfile
import time


def read_json(path):
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd) as stream:
        if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):
            raise ValueError('Routing input must be a regular file.')
        value = stream.read(65537)
    if len(value) > 65536:
        raise ValueError("Routing input exceeds size limit.")
    return json.loads(value)


def apply(config, request, root=Path('/'), run=subprocess.run):
    project = config['composeProject']
    if not re.fullmatch(r'minicloud-[a-z0-9][a-z0-9-]{0,62}', project):
        raise ValueError('Invalid Compose project.')
    slug = project[len('minicloud-'):]
    if request['appId'] != config['appId'] or request['composeProject'] != project:
        raise ValueError('Routing identity mismatch.')
    routes = request['routes']
    if not isinstance(routes, list) or len(routes) > 100:
        raise ValueError('Invalid route count.')
    lock_path = root / f'opt/minicloud/locks/{slug}.deploy.lock'
    with open(lock_path, 'a') as lock:
        deadline = time.monotonic() + 20
        while True:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except BlockingIOError:
                if time.monotonic() >= deadline:
                    raise ValueError('Deployment in progress; routing will retry.')
                time.sleep(0.1)
        compose_path = root / f'opt/minicloud/apps/{slug}/app.compose.json'
        # Generated Compose contains secrets; never return its contents or parser errors.
        with open(compose_path) as stream:
            services = json.load(stream)['services']
        fragments = []
        hostnames = set()
        for route in sorted(routes, key=lambda route: route['hostname']):
            host = route['hostname']
            if len(host) > 253 or not re.fullmatch(r'(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?', host):
                raise ValueError('Invalid hostname.')
            if host in hostnames or route['pathPrefix'] != '/':
                raise ValueError('Duplicate hostname or unsupported path.')
            hostnames.add(host)
            service = services.get(route['serviceName'], {})
            matches = []
            for port in service.get('ports', []):
                match = re.fullmatch(r'127\.0\.0\.1:(\d+):(\d+)', port) if isinstance(port, str) else None
                if match and int(match[2]) == route['servicePort'] and 1 <= int(match[1]) <= 65535:
                    matches.append(int(match[1]))
            if len(matches) != 1:
                raise ValueError('Public service target missing or ambiguous; existing routes preserved.')
            fragments.append(f'{host} {{\n  reverse_proxy 127.0.0.1:{matches[0]}\n}}\n')
        candidate = ''.join(fragments)
        directory = root / 'opt/minicloud/caddy/apps'
        live = directory / f'{slug}.caddy'
        previous = live.read_text() if live.exists() else None
        if previous == candidate:
            result = run(['systemctl', 'is-active', '--quiet', 'caddy'], capture_output=True, timeout=10)
            if result.returncode != 0:
                raise ValueError('Caddy is inactive.')
            return
        with tempfile.TemporaryDirectory(prefix='minicloud-routing-') as temporary:
            stage = Path(temporary)
            for other in directory.glob('*.caddy'):
                if other.name != live.name:
                    shutil.copyfile(other, stage / other.name)
            (stage / live.name).write_text(candidate)
            (stage / 'Caddyfile').write_text(f'import {stage}/*.caddy\n')
            result = run(['caddy', 'validate', '--config', str(stage / 'Caddyfile')], capture_output=True, timeout=20)
            if result.returncode != 0:
                raise ValueError('Caddy validation failed; existing routes preserved.')
        fd, temporary = tempfile.mkstemp(prefix='.routing-', dir=directory)
        try:
            with os.fdopen(fd, 'w') as stream:
                stream.write(candidate)
            os.chmod(temporary, 0o644)
            os.replace(temporary, live)
            try:
                result = run(['systemctl', 'reload', 'caddy'], capture_output=True, timeout=20)
                if result.returncode != 0:
                    raise ValueError('Caddy reload failed; previous routes restored.')
            except Exception:
                if previous is None:
                    live.unlink()
                else:
                    Path(temporary).write_text(previous)
                    os.chmod(temporary, 0o644)
                    os.replace(temporary, live)
                run(['systemctl', 'reload', 'caddy'], capture_output=True, timeout=20)
                raise
        finally:
            Path(temporary).unlink(missing_ok=True)


def main():
    try:
        config = read_json('/opt/minicloud/agent/config.json')
        request = read_json('/var/lib/minicloud-agent/routing-request.json')
        apply(config, request)
        print('Routing applied.')
    except ValueError as error:
        # JSON errors can contain pieces of secret-bearing configuration.
        print('Invalid routing configuration.' if isinstance(error, json.JSONDecodeError) else str(error))
        raise SystemExit(1)
    except Exception:
        print('Routing installation failed; inspect runtime permissions and configuration.')
        raise SystemExit(1)


if __name__ == '__main__':
    main()
