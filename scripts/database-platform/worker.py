#!/usr/bin/env python3
"""Daily encrypted logical PostgreSQL backup. No restore into a live database."""
import datetime as dt
import hashlib
import hmac
import json
import os
from pathlib import Path
import secrets
import sqlite3
import re
import fcntl
from contextlib import contextmanager
from capacity import require_capacity, require_disk_capacity, write_encrypted_stream
import subprocess
import tempfile
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, HTTPServer


def digest(path):
    with open(path, 'rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def crypt(source, target, keyfile, decrypt=False):
    subprocess.run(['openssl', 'enc', '-aes-256-cbc', '-pbkdf2', '-iter', '200000',
                    '-d' if decrypt else '-e', '-pass', f'file:{keyfile}',
                    '-in', str(source), '-out', str(target)], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=3600)


def signature(payload, key):
    return hmac.new(key.encode(), b'minicloud-db-backup-v1\0' +
                    json.dumps(payload, sort_keys=True, separators=(',', ':')).encode(),
                    hashlib.sha256).hexdigest()


def postgres_env(value):
    env = os.environ.copy()
    for name in list(env):
        if name.startswith('PG'):
            del env[name]
    if value.startswith(('postgres://', 'postgresql://')):
        from urllib.parse import urlsplit, unquote
        parsed = urlsplit(value)
        parts = dict(host=parsed.hostname, port=parsed.port or 5432,
                     database=unquote(parsed.path[1:]), username=unquote(parsed.username or ''),
                     password=unquote(parsed.password or ''))
    else:
        parts = dict(part.split('=', 1) for part in value.split(';') if '=' in part)
        parts = {k.strip().lower(): v.strip() for k, v in parts.items()}
    parts.setdefault('port', 5432)
    for source, dest in [('host', 'PGHOST'), ('port', 'PGPORT'), ('database', 'PGDATABASE'),
                         ('username', 'PGUSER'), ('password', 'PGPASSWORD')]:
        if source in parts:
            env[dest] = str(parts[source])
    if not all(env.get(k) for k in ['PGHOST', 'PGDATABASE', 'PGUSER', 'PGPASSWORD']):
        raise ValueError('Database configuration missing')
    env['PGCONNECT_TIMEOUT'] = '15'
    return env


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        return None


def upload(url, token, path):
    # A file body with explicit length streams without loading a database into RAM.
    for attempt in range(3):
        try:
            with open(path, 'rb') as stream:
                request = urllib.request.Request(url, data=stream, method='PUT', headers={
                    'Authorization': f'Bearer {token}', 'Content-Type': 'application/octet-stream',
                    'Content-Length': str(path.stat().st_size), 'X-Ciphertext-SHA256': digest(path)})
                with urllib.request.build_opener(NoRedirect).open(request, timeout=3600) as response:
                    if response.status not in (200, 201):
                        raise RuntimeError('Backup upload failed')
                    response.read()
            return
        except Exception:
            if attempt == 2:
                raise
            time.sleep(5 * (attempt + 1))


@contextmanager
def source_lock(source_id):
    if not re.fullmatch(r'(?:db|database)_[a-z0-9]{8,64}', source_id):
        raise ValueError('Invalid source identity')
    root = Path(os.environ.get('BACKUP_LOCK_ROOT', '/opt/minicloud/locks/db-backups'))
    root.mkdir(parents=True, mode=0o700, exist_ok=True)
    if root.is_symlink() or root.stat().st_uid != os.geteuid() or root.stat().st_mode & 0o077:
        raise ValueError('Unsafe backup lock directory')
    descriptor = os.open(root / source_id, os.O_CREAT | os.O_WRONLY | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield
    finally:
        os.close(descriptor)


def backup():
    with source_lock(os.environ['BACKUP_DATABASE_ID']):
        return capture()


def postgres_database_size(command, env, run=subprocess.run):
    # Reuse exactly the pg_dump transport and scoped database environment,
    # including the Unix socket when migration fences ordinary TCP clients.
    prefix=command[:command.index('pg_dump')]
    result=run(prefix+['psql','-X','-Atc','SELECT pg_database_size(current_database())'],env=env,capture_output=True,timeout=60)
    if result.returncode or len(result.stdout)>64:
        raise RuntimeError('Database size observation failed')
    try:
        size=int(result.stdout.strip())
    except ValueError:
        raise RuntimeError('Database size observation invalid') from None
    if size <= 0 or size > 2**63-1:
        raise RuntimeError('Database size observation invalid')
    return size


def capture():
    url = os.environ['BACKUP_URL'].rstrip('/')
    if not url.startswith('https://'):
        raise ValueError('HTTPS backup destination required')
    environment, app = os.environ['BACKUP_ENVIRONMENT'], os.environ['BACKUP_DATABASE_ID']
    key, token = os.environ['BACKUP_ENCRYPTION_KEY'], os.environ['BACKUP_TOKEN']
    if any(c in key + token for c in '\r\n\0'):
        raise ValueError('Single-line secrets required')
    if len(key) < 43 or len(token) < 32:
        raise ValueError('Strong backup secrets required')
    created = dt.datetime.now(dt.timezone.utc)
    backup_id = created.strftime('%Y%m%dT%H%M%SZ') + '-' + secrets.token_hex(4)
    engine = os.environ.get('BACKUP_ENGINE', 'postgres')
    if engine not in ('postgres', 'sqlite') or not re.fullmatch(r'(?:db|database)_[a-z0-9]{8,64}', app) or not re.fullmatch(r'[a-z][a-z0-9-]{0,31}', environment):
        raise ValueError('Invalid source identity or engine')
    env = postgres_env(os.environ.get('DATABASE_URL') or os.environ.get('ConnectionStrings__Minicloud', '')) if engine == 'postgres' else None
    if engine == 'sqlite':
        source = Path(os.environ['SQLITE_PATH'])
        if source.is_symlink() or not source.is_file():
            raise ValueError('Registered SQLite source unavailable')
        wal = source.with_name(source.name + '-wal')
        estimate = source.stat().st_size + (wal.stat().st_size if wal.exists() and not wal.is_symlink() else 0)
        require_capacity(estimate * 2 + 1024**2)
    with tempfile.TemporaryDirectory(prefix='db-backup-', dir='/dev/shm' if engine == 'sqlite' else None) as directory:
        root = Path(directory)
        keyfile = root / 'key'
        keyfile.write_text(key)
        keyfile.chmod(0o600)
        encrypted = root / 'database.dump.enc'
        if engine == 'sqlite':
            source = Path(os.environ['SQLITE_PATH'])
            if source.is_symlink() or not source.is_file():
                raise ValueError('Registered SQLite source unavailable')
            snapshot = root / 'snapshot.db'
            deadline = time.monotonic() + 60
            def progress(status, remaining, total):
                require_capacity(max(0, remaining) * 65536 + 1024**2)
                if time.monotonic() > deadline:
                    raise TimeoutError('SQLite snapshot busy')
            with sqlite3.connect(source.as_uri() + '?mode=ro', uri=True) as database, sqlite3.connect(snapshot) as target:
                database.backup(target, pages=256, progress=progress, sleep=0.05)
            require_capacity(snapshot.stat().st_size + 1024**2)
            database_size=snapshot.stat().st_size
            crypt(snapshot, encrypted, keyfile)
            snapshot.unlink()
        else:
            command = ['pg_dump', '--format=custom', '--no-owner', '--no-acl', '--lock-wait-timeout=30s']
            container = os.environ.get('POSTGRES_CONTAINER')
            if container:
                # Host connections may be fenced for a final migration capture.
                # The authorized worker uses the container-local socket and the
                # selected database identity, never an application TCP session.
                env['PGHOST'] = '/var/run/postgresql'
                command = ['docker', 'exec', *sum((['-e', name] for name in ('PGHOST','PGPORT','PGUSER','PGPASSWORD','PGDATABASE')), []), container, *command]
            database_size=postgres_database_size(command,env)
            require_disk_capacity(root, min(database_size + 1024**2, 8 * 1024**3))
            with open(encrypted, 'wb') as output:
                dump = subprocess.Popen(command, env=env, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
                enc = None
                try:
                    enc = subprocess.Popen(['openssl', 'enc', '-aes-256-cbc', '-pbkdf2', '-iter', '200000',
                                            '-salt', '-pass', f'file:{keyfile}'], stdin=dump.stdout,
                                           stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
                    dump.stdout.close()
                    write_encrypted_stream(enc.stdout, output, root)
                    if enc.wait(timeout=30) or dump.wait(timeout=30):
                        raise RuntimeError('Database dump/encryption failed')
                finally:
                    for process in (enc, dump):
                        if process is not None and process.poll() is None:
                            process.kill()
                            process.wait()
            database_size=max(database_size,postgres_database_size(command,env))
        payload = dict(databaseSizeBytes=database_size, version=1, backupId=backup_id, environment=environment, databaseId=app,
                       createdAt=created.isoformat(), database=env['PGDATABASE'] if env else source.name, keyId=os.environ['BACKUP_KEY_ID'], engine=engine,
                       ciphertextSha256=digest(encrypted), ciphertextBytes=encrypted.stat().st_size,
                       format='pg_dump-custom' if env else 'sqlite-snapshot', postgresMajor=16 if env else None)
        manifest = root / 'manifest.json'
        manifest.write_text(json.dumps(dict(payload=payload, hmacSha256=signature(payload, key))))
        crypt(manifest, root / 'manifest.json.enc', keyfile)
        base = f'{url}/v1/{environment}/{app}/{backup_id}'
        for name in ['database.dump.enc', 'manifest.json.enc']:
            upload(f'{base}/{name}', token, root / name)
    return dict(**payload, completedAt=dt.datetime.now(dt.timezone.utc).isoformat())


state = {'lastSuccess': None, 'lastFailure': None, 'running': False}


def schedule():
    failures = 0
    while True:
        state['running'] = True
        try:
            state['lastSuccess'] = backup()
            state['lastFailure'] = None
            failures = 0
            print(json.dumps({'event': 'backup_complete', **state['lastSuccess']}), flush=True)
            delay = int(os.environ.get('BACKUP_INTERVAL_SECONDS', '86400'))
            if not 60 <= delay <= 31536000:
                raise ValueError('Invalid backup interval')
        except Exception as error:
            # Exception messages/tracebacks may contain credentials or connection strings.
            state['lastFailure'] = dt.datetime.now(dt.timezone.utc).isoformat()
            print(json.dumps({'event': 'backup_failed', 'type': type(error).__name__}), flush=True)
            failures += 1
            delay = min(900, 60 * 2 ** min(failures - 1, 4))
        state['running'] = False
        time.sleep(delay)


class Health(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != '/health':
            self.send_error(404)
            return
        data = json.dumps(state).encode()
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


if __name__ == '__main__':
    os.umask(0o077)
    if os.environ.get('BACKUP_ONCE') == '1':
        try:
            print(json.dumps(backup()))
        except Exception as error:
            print(json.dumps({'status':'failed','errorType':type(error).__name__}))
            raise SystemExit(1)
        raise SystemExit(0)
    threading.Thread(target=schedule, daemon=True).start()
    HTTPServer(('0.0.0.0', 6211), Health).serve_forever()
