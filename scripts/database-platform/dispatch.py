#!/usr/bin/env python3
"""Private worker request dispatcher; only bounded operation results reach stdout."""
import json
import os
from pathlib import Path
import sys
import re
import sqlite3
import subprocess
import signal


def dispatch(request):
    action = request['action']
    if action == 'restore':
        from restore import download_restore
        return download_restore(request)
    if action == 'access':
        from access import expose
        return expose(request)
    if action == 'inspect':
        if request['engine'] == 'sqlite':
            path = Path(request['sqlitePath'])
            if path.is_symlink() or not path.is_file():
                raise ValueError('Registered SQLite file is unavailable')
            with sqlite3.connect(path.as_uri() + '?mode=ro', uri=True) as source:
                if source.execute('PRAGMA quick_check').fetchone()[0] != 'ok':
                    raise ValueError('SQLite integrity check failed')
        elif request['engine'] == 'postgres':
            container = request['container']
            if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}', container):
                raise ValueError('Invalid container identity')
            subprocess.run(['docker','exec',container,'pg_isready','-U','minicloud','-d',request['databaseName']],check=True,capture_output=True,timeout=30)
            version = subprocess.run(['docker','exec',container,'psql','-X','-U','minicloud','-d',request['databaseName'],'-Atc','SHOW server_version_num'],check=True,capture_output=True,timeout=30)
            if not 160000 <= int(version.stdout.strip()) < 170000:
                raise ValueError('Unsupported PostgreSQL version')
        else:
            raise ValueError('Unsupported engine')
        return {'status':'ready','engine':request['engine']}
    if action in ('create', 'rotate', 'delete'):
        from operation import execute
        return execute(request)
    if action == 'backup':
        environment = request['environment']
        permitted = {'BACKUP_URL','BACKUP_ENVIRONMENT','BACKUP_DATABASE_ID','BACKUP_TOKEN',
                     'BACKUP_ENCRYPTION_KEY','BACKUP_KEY_ID','BACKUP_ENGINE','DATABASE_URL',
                     'SQLITE_PATH','POSTGRES_CONTAINER'}
        if not isinstance(environment, dict) or set(environment) - permitted:
            raise ValueError('Unexpected backup configuration')
        for key in permitted:
            os.environ.pop(key, None)
        os.environ.update({key: str(value) for key, value in environment.items()})
        from worker import backup
        return backup()
    raise ValueError('Unsupported operation')


if __name__ == '__main__':
    os.umask(0o077)
    def stop_operation(signum, frame):
        raise SystemExit(128 + signum)
    signal.signal(signal.SIGTERM, stop_operation)
    try:
        with open(sys.argv[1]) as source:
            request = json.load(source)
        result = dispatch(request)
        print('__MINICLOUD_DATABASE_RESULT__=' + json.dumps(result))
        print('__MINICLOUD_DEPLOY_COMPLETE__=' + request['operationId'])
    except Exception as error:
        print('__MINICLOUD_DATABASE_RESULT__=' + json.dumps({'status':'failed','errorType':type(error).__name__}))
        raise SystemExit(1)
