#!/usr/bin/env python3
"""Restricted PostgreSQL lifecycle operations. Input is a private JSON file."""
import json
import os
import re
import subprocess
import sys


def identifier(value):
    if not isinstance(value, str) or not re.fullmatch(r'[a-z][a-z0-9_]{0,62}', value) or value.startswith('pg_') or value in ('postgres', 'template0', 'template1'):
        raise ValueError('Invalid managed database identity')
    return '"' + value + '"'


def require_database_space(container, required, run=subprocess.run):
    if not isinstance(container,str) or not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}',container) or type(required) is not int or required < 0:
        raise ValueError('Invalid database capacity request')
    capacity=run(['docker','exec',container,'df','-Pk','/var/lib/postgresql/data'],capture_output=True,timeout=30)
    if capacity.returncode or len(capacity.stdout)>8192:
        raise RuntimeError('Database capacity could not be inspected')
    try:
        fields=capacity.stdout.decode().strip().splitlines()[-1].split()
        total,available=int(fields[-5])*1024,int(fields[-3])*1024
        if not 0 <= available <= total or total <= 0: raise ValueError()
    except (ValueError,IndexError,UnicodeError):
        raise RuntimeError('Database capacity observation is invalid') from None
    if available-required < max(256*1024**2,total*15//100):
        from capacity import BackupCapacityError
        raise BackupCapacityError('Database storage reserve is insufficient')


def execute(request, run=subprocess.run):
    action = request['action']
    database = identifier(request['databaseName'])
    username = identifier(request['username'])
    container = request['container']
    if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}', container):
        raise ValueError('Invalid container binding')
    admin = request.get('adminUser', 'minicloud')
    if not re.fullmatch(r'[a-z][a-z0-9_]{0,62}', admin):
        raise ValueError('Invalid administrator identity')
    password = request.get('password', '')
    if action in ('create', 'rotate') and (not isinstance(password, str) or len(password) < 32 or any(c in password for c in '\0\r\n')):
        raise ValueError('Strong credentials required')
    literal = "'" + password.replace("'", "''") + "'"
    role_literal = "'" + request['username'] + "'"
    db_literal = "'" + request['databaseName'] + "'"
    marker = "'minicloud.database:" + request['databaseName'] + "'"
    # A retained role may only be reused when its platform marker matches. Role
    # creation+marker is atomic; a database created before interruption is safe
    # to recover only when owned by that marked role.
    guard = f"SELECT 1 / CASE WHEN NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname={role_literal}) OR EXISTS(SELECT 1 FROM pg_roles WHERE rolname={role_literal} AND shobj_description(oid,'pg_authid')={marker}) THEN 1 ELSE 0 END;\n"
    create = guard + f"BEGIN;\nSELECT format('CREATE ROLE %I LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS', {role_literal}, {literal}) WHERE NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname={role_literal})\n\\gexec\nCOMMENT ON ROLE {username} IS {marker};\nCOMMIT;\n"
    create += f"SELECT 1 / CASE WHEN NOT EXISTS(SELECT 1 FROM pg_database WHERE datname={db_literal}) OR EXISTS(SELECT 1 FROM pg_database WHERE datname={db_literal} AND pg_get_userbyid(datdba)={role_literal}) THEN 1 ELSE 0 END;\n"
    create += f"SELECT format('CREATE DATABASE %I OWNER %I', {db_literal}, {role_literal}) WHERE NOT EXISTS(SELECT 1 FROM pg_database WHERE datname={db_literal})\n\\gexec\nREVOKE ALL ON DATABASE {database} FROM PUBLIC;\n"
    database_guard = f"SELECT 1 / CASE WHEN NOT EXISTS(SELECT 1 FROM pg_database WHERE datname={db_literal}) OR EXISTS(SELECT 1 FROM pg_database WHERE datname={db_literal} AND pg_get_userbyid(datdba)={role_literal}) THEN 1 ELSE 0 END;\n"
    statements = {
        'create': create,
        'rotate': guard + f'ALTER ROLE {username} PASSWORD {literal};\n',
        'delete': guard + database_guard + f'DROP DATABASE IF EXISTS {database};\nDROP ROLE IF EXISTS {username};\n'
    }
    if action not in statements:
        raise ValueError('Unsupported operation')
    if action == 'create':
        require_database_space(container,32*1024**2,run)
    result = run(['docker', 'exec', '-i', container, 'psql', '-X', '-U', admin, '-d', 'postgres', '-v', 'ON_ERROR_STOP=1'],
                 input=statements[action].encode(), capture_output=True, timeout=120)
    if result.returncode:
        raise RuntimeError('Database lifecycle command failed')
    return {'status': 'completed', 'kind': action}


if __name__ == '__main__':
    os.umask(0o077)
    try:
        with open(sys.argv[1]) as source:
            request = json.load(source)
        result = execute(request)
        print('__MINICLOUD_DATABASE_RESULT__=' + json.dumps(result))
        print('__MINICLOUD_DEPLOY_COMPLETE__=' + request['operationId'])
    except Exception as error:
        print('__MINICLOUD_DATABASE_RESULT__=' + json.dumps({'status': 'failed', 'errorType': type(error).__name__}))
        raise SystemExit(1)
