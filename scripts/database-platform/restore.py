#!/usr/bin/env python3
"""Authenticated restore into an explicitly empty new destination."""
import importlib.util
import os
from pathlib import Path
import sqlite3
import subprocess
import tempfile

from operation import identifier, require_database_space
from worker import crypt
from capacity import require_capacity, require_disk_capacity, write_encrypted_stream

_spec = importlib.util.spec_from_file_location('restore_check', Path(__file__).with_name('restore-check.py'))
_checker = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_checker)


def restore(directory, key, expected_database_id, target, *, container=None, admin_user='minicloud', target_role=None, expected_backup_id=None, run=subprocess.run):
    encrypted_path = Path(directory) / 'database.dump.enc'
    if encrypted_path.is_symlink() or not encrypted_path.is_file():
        raise ValueError('Encrypted backup unavailable')
    require_capacity(encrypted_path.stat().st_size * 2 + 2 * 1024**2)
    with tempfile.TemporaryDirectory(prefix='minicloud-recovery-', dir='/dev/shm') as work:
        root = Path(work)
        payload, ciphertext, keyfile = _checker.verify_backup(directory, key, root)
        if payload['databaseId'] != expected_database_id or (expected_backup_id is not None and payload['backupId'] != expected_backup_id):
            raise ValueError('Backup source identity mismatch')
        database_size=payload.get('databaseSizeBytes')
        if type(database_size) is not int or not 0 < database_size <= 2**63-1:
            raise ValueError('Backup database size is missing or invalid')
        if payload['format']=='pg_dump-custom':
            # Reserve three times the signed physical source size for rebuilt
            # data/indexes and WAL, then preserve the shared-server disk floor.
            require_database_space(container,database_size*3+256*1024**2,run)
        dump = root / 'database.dump'
        require_capacity(ciphertext.stat().st_size + 1024**2)
        crypt(ciphertext, dump, keyfile, decrypt=True)
        if payload['format'] == 'sqlite-snapshot':
            destination = Path(target)
            descriptor = os.open(destination, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
            os.close(descriptor)
            try:
                with sqlite3.connect(dump.as_uri() + '?mode=ro', uri=True) as source, sqlite3.connect(destination) as database:
                    if source.execute('PRAGMA integrity_check').fetchone()[0] != 'ok':
                        raise ValueError('SQLite backup integrity failed')
                    source.backup(database)
            except Exception:
                destination.unlink()
                raise
        else:
            identifier(target)
            identifier(target_role)
            if not isinstance(admin_user, str) or not __import__('re').fullmatch(r'[a-z][a-z0-9_]{0,62}', admin_user):
                raise ValueError('Invalid administrator identity')
            if not container or target == payload['database']:
                raise ValueError('A distinct PostgreSQL target is required')
            # Parent creates and binds an empty target before calling this. Verify
            # emptiness independently before running a non-cleaning pg_restore.
            check = run(['docker','exec',container,'psql','-X','-U',admin_user,'-d',target,'-Atc',
                         "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND c.relkind IN ('r','v','S','m','f');"], capture_output=True, timeout=60)
            if check.returncode or check.stdout.strip() != b'0':
                raise ValueError('Restore destination is not empty')
            with dump.open('rb') as source:
                result = run(['docker','exec','-i',container,'pg_restore','--username='+admin_user,'--role='+target_role,'--dbname='+target,
                              '--no-owner','--no-acl','--exit-on-error','--single-transaction'], stdin=source, capture_output=True, timeout=3600)
            if result.returncode:
                raise RuntimeError('Database restore failed')
        return {'status':'restored','backupId':payload['backupId'],'databaseId':expected_database_id}


def download_restore(request):
    import re
    import urllib.request
    from worker import NoRedirect
    url = request['repositoryUrl']
    namespace, database_id, backup_id = request['namespace'], request['sourceDatabaseId'], request['backupId']
    if (not url.startswith('https://') or not re.fullmatch(r'[a-z][a-z0-9-]{0,31}', namespace) or
            not re.fullmatch(r'(?:db|database)_[a-z0-9]{8,64}', database_id) or
            not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{7,95}', backup_id)):
        raise ValueError('Invalid repository or source binding')
    opener = urllib.request.build_opener(NoRedirect)
    with tempfile.TemporaryDirectory(prefix='minicloud-download-') as directory:
        root = Path(directory)
        for name, maximum in [('manifest.json.enc',1024**2),('database.dump.enc',8*1024**3)]:
            endpoint = url.rstrip('/') + f'/v1/{namespace}/{database_id}/{backup_id}/{name}'
            fetch = urllib.request.Request(endpoint, headers={'Authorization':'Bearer '+request['repositoryToken']})
            with opener.open(fetch,timeout=3600) as response, (root/name).open('xb') as target:
                require_disk_capacity(root, 1024**2)
                write_encrypted_stream(response, target, root, maximum)
        result = restore(root,request['encryptionKey'],database_id,request['target'],
                       container=request.get('container'),admin_user=request.get('adminUser','minicloud'),
                       target_role=request.get('username'),expected_backup_id=backup_id)
        if result['backupId'] != backup_id:
            raise ValueError('Backup identity mismatch')
        return result
