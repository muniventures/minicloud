#!/usr/bin/env python3
"""Authenticate a logical backup and restore only into a disposable local container."""
import argparse
import datetime as dt
import hmac
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import tempfile
import time

from worker import crypt, digest, signature


class RestoreCheckError(Exception):
    pass


def private_file(path, contents=b''):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'wb') as stream:
        stream.write(contents)


def verify_backup(directory, key, temporary):
    """Authenticate the manifest and ciphertext before decrypting any dump bytes."""
    if not isinstance(key, str) or len(key) < 43 or any(c in key for c in '\r\n\0'):
        raise RestoreCheckError('A strong single-line backup key is required.')
    root = Path(directory)
    manifest = root / 'manifest.json.enc'
    ciphertext = root / 'database.dump.enc'
    for path in (manifest, ciphertext):
        if path.is_symlink() or not path.is_file():
            raise RestoreCheckError('The encrypted backup pair is missing or is not regular files.')
    if not 0 < manifest.stat().st_size <= 1024 * 1024 or not 0 < ciphertext.stat().st_size <= 8 * 1024**3:
        raise RestoreCheckError('The encrypted backup pair exceeds its bounds.')
    keyfile = temporary / 'key'
    plaintext_manifest = temporary / 'manifest.json'
    private_file(keyfile, key.encode())
    private_file(plaintext_manifest)
    crypt(manifest, plaintext_manifest, keyfile, decrypt=True)
    envelope = json.loads(plaintext_manifest.read_text())
    if not isinstance(envelope, dict) or set(envelope) != {'payload', 'hmacSha256'} or not isinstance(envelope['payload'], dict):
        raise RestoreCheckError('Backup manifest is invalid.')
    payload, supplied = envelope['payload'], envelope['hmacSha256']
    if not isinstance(supplied, str) or not re.fullmatch(r'[a-f0-9]{64}', supplied) or not hmac.compare_digest(signature(payload, key), supplied):
        raise RestoreCheckError('Backup manifest authentication failed.')
    if type(payload.get('version')) is not int or payload['version'] != 1 or payload.get('format') not in ('pg_dump-custom', 'sqlite-snapshot'):
        raise RestoreCheckError('Unsupported backup format or PostgreSQL version.')
    if payload['format'] == 'pg_dump-custom' and payload.get('postgresMajor') != 16:
        raise RestoreCheckError('Unsupported PostgreSQL version.')
    if (not isinstance(payload.get('backupId'), str) or not re.fullmatch(r'[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}', payload['backupId']) or
            not isinstance(payload.get('environment'), str) or
            not isinstance(payload.get('databaseId'), str) or not re.fullmatch(r'[A-Za-z0-9_-]{1,100}', payload['databaseId']) or
            not isinstance(payload.get('database'), str) or not 0 < len(payload['database']) <= 128):
        raise RestoreCheckError('Backup manifest identity is invalid.')
    created = dt.datetime.fromisoformat(payload.get('createdAt', '').replace('Z', '+00:00'))
    if created.tzinfo is None:
        raise RestoreCheckError('Backup manifest timestamp has no timezone.')
    # Verify a private immutable working copy, avoiding a source-file change
    # between authentication and dump decryption.
    verified_ciphertext = temporary / 'database.dump.enc'
    private_file(verified_ciphertext)
    with ciphertext.open('rb') as source, verified_ciphertext.open('wb') as target:
        shutil.copyfileobj(source, target, length=1024 * 1024)
    expected_digest = payload.get('ciphertextSha256')
    if (type(payload.get('ciphertextBytes')) is not int or payload['ciphertextBytes'] != verified_ciphertext.stat().st_size or
            not isinstance(expected_digest, str) or not re.fullmatch(r'[a-f0-9]{64}', expected_digest) or
            not hmac.compare_digest(expected_digest, digest(verified_ciphertext))):
        raise RestoreCheckError('Backup ciphertext integrity check failed.')
    return payload, verified_ciphertext, keyfile


def command(arguments, *, stdin=None, env=None, timeout=60, optional=False):
    result = subprocess.run(arguments, stdin=stdin, env=env, capture_output=True, timeout=timeout)
    if result.returncode:
        if optional:
            return None
        # Docker/restore diagnostics may contain data or configuration. Keep them private.
        raise RestoreCheckError('An isolated restore command failed.')
    if len(result.stdout) > 1024 * 1024:
        raise RestoreCheckError('An isolated restore observation exceeded its bound.')
    return result.stdout.decode().strip()


def restore_check(directory, key, run=command, sleep=time.sleep):
    name = 'minicloud-restore-' + secrets.token_hex(12)
    ownership = secrets.token_hex(16)
    started = False
    with tempfile.TemporaryDirectory(prefix='minicloud-restore-') as temporary:
        root = Path(temporary)
        payload, encrypted, keyfile = verify_backup(directory, key, root)
        dump = root / 'database.dump'
        private_file(dump)
        crypt(encrypted, dump, keyfile, decrypt=True)
        try:
            docker_env = os.environ.copy()
            docker_env.pop('BACKUP_ENCRYPTION_KEY', None)
            docker_env['POSTGRES_PASSWORD'] = secrets.token_hex(24)
            run(['docker', 'run', '--detach', '--pull=never', '--name', name, '--network', 'none',
                 '--label', 'minicloud.restore-check=' + ownership, '--user', 'postgres',
                 '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true',
                 '--memory', '768m', '--cpus', '1', '--pids-limit', '128',
                 '--env', 'POSTGRES_PASSWORD', '--env', 'POSTGRES_DB=restorecheck', 'postgres:16-alpine'], env=docker_env)
            started = True
            for _ in range(60):
                # Avoid the temporary bootstrap server's short-lived socket.
                process = run(['docker', 'exec', name, 'cat', '/proc/1/comm'], timeout=5, optional=True)
                if process == 'postgres' and run(['docker', 'exec', name, 'pg_isready', '-U', 'postgres', '-d', 'restorecheck'], timeout=5, optional=True) is not None:
                    break
                sleep(0.5)
            else:
                raise RestoreCheckError('The disposable PostgreSQL container did not become ready.')
            with dump.open('rb') as stream:
                run(['docker', 'exec', '-i', name, 'pg_restore', '--host=/var/run/postgresql', '--port=5432',
                     '--username=postgres', '--dbname=restorecheck', '--no-owner', '--no-acl', '--exit-on-error'], stdin=stream, timeout=3600)

            def sql(value):
                return run(['docker', 'exec', name, 'psql', '-X', '--host=/var/run/postgresql', '--port=5432',
                            '--username=postgres', '--dbname=restorecheck', '--tuples-only', '--no-align',
                            '--set=ON_ERROR_STOP=1', '--command', value])

            table_count = int(sql("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE';"))
            return dict(status='restored', backupId=payload['backupId'], environment=payload['environment'],
                        databaseId=payload['databaseId'], createdAt=payload['createdAt'], tableCount=table_count)

        finally:
            label = run(['docker', 'inspect', '--format', '{{index .Config.Labels "minicloud.restore-check"}}', name], optional=True)
            if label == ownership:
                run(['docker', 'rm', '--force', '--volumes', name])
            elif started:
                raise RestoreCheckError('Disposable container cleanup ownership could not be verified.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('encrypted_backup_directory', type=Path)
    args = parser.parse_args()
    os.umask(0o077)
    try:
        result = restore_check(args.encrypted_backup_directory, os.environ.get('BACKUP_ENCRYPTION_KEY', ''))
        print(json.dumps(result))
    except Exception as error:
        print(json.dumps(dict(status='failed', errorType=type(error).__name__)))
        raise SystemExit(1)


if __name__ == '__main__':
    main()
