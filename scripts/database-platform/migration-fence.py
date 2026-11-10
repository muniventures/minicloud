#!/usr/bin/env python3
"""Fence only one database's network clients while retaining local backup access."""
import json
import os
import re
import subprocess
import sys


def execute(request, run=subprocess.run):
    container, database, operation = request['container'], request['databaseName'], request['operationId']
    if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}', container) or not re.fullmatch(r'[a-z][a-z0-9_]{0,62}', database) or not re.fullmatch(r'dbop_[a-zA-Z0-9_-]{1,96}', operation):
        raise ValueError('Invalid migration binding')
    command = ['docker', 'exec', container, 'psql', '-X', '-U', 'minicloud', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1']
    def sql(statement):
        return run(command + ['-Atc', statement], check=True, capture_output=True, timeout=30)
    path = sql('SHOW hba_file').stdout.decode().strip()
    if not path.startswith('/var/lib/postgresql/') or '..' in path.split('/') or '\n' in path:
        raise ValueError('Unexpected PostgreSQL configuration path')
    saved = path + '.migration-' + operation
    if request['action'] == 'fence':
        prefix = f'host {database} all 0.0.0.0/0 reject\nhost {database} all ::/0 reject\n'
        run(['docker', 'exec', '-i', '-u', 'postgres', container, 'sh', '-c', 'umask 077; test -e "$2" || cp "$1" "$2"; cat > "$1.next"; cat "$2" >> "$1.next"; mv "$1.next" "$1"', 'hba', path, saved], input=prefix.encode(), check=True, capture_output=True, timeout=30)
        if sql('SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL').stdout.strip() != b'0':
            run(['docker', 'exec', '-u', 'postgres', container, 'cp', saved, path], check=True, capture_output=True, timeout=30)
            raise ValueError('Invalid migration fencing rules')
        sql('SELECT pg_reload_conf()')
        sql(f"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='{database}' AND backend_type='client backend' AND pid<>pg_backend_pid()")
        return {'status': 'fenced'}
    if request['action'] == 'unfence':
        run(['docker', 'exec', '-u', 'postgres', container, 'cp', saved, path], check=True, capture_output=True, timeout=30)
        sql('SELECT pg_reload_conf()')
        return {'status': 'unfenced'}
    raise ValueError('Unsupported migration fencing action')

if __name__ == '__main__':
    os.umask(0o077)
    try:
        with open(sys.argv[1]) as source:
            request = json.load(source)
        result = execute(request)
        print('__MINICLOUD_DATABASE_RESULT__=' + json.dumps(result))
        print('__MINICLOUD_DEPLOY_COMPLETE__=' + request['operationId'])
    except Exception as error:
        print('__MINICLOUD_DATABASE_RESULT__=' + json.dumps({'status':'failed','errorType':type(error).__name__}))
        raise SystemExit(1)
