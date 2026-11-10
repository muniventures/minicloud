#!/usr/bin/env python3
"""Disposable Docker proof of shared database lifecycle and access isolation."""
import json
import secrets
import subprocess
import time
import operation
import access
import importlib.util
from pathlib import Path

name='minicloud-db-test-'+secrets.token_hex(6)
password='synthetic-integration-'+secrets.token_hex(24)
clients=[]

def run(args,**kw):
    return subprocess.run(args,check=True,capture_output=True,timeout=120,**kw)

def sql(database,user,statement):
    return run(['docker','exec','-i',name,'psql','-X','-U',user,'-d',database,'-v','ON_ERROR_STOP=1'],input=statement.encode())

try:
    run(['docker','run','-d','--name',name,'--network','none','-e','POSTGRES_USER=minicloud','-e','POSTGRES_PASSWORD='+password,'postgres:16-alpine'])
    for attempt in range(60):
        probe=subprocess.run(['docker','exec','-e','PGPASSWORD='+password,name,'psql','-h','127.0.0.1','-U','minicloud','-d','postgres','-c','SELECT 1'],capture_output=True)
        if probe.returncode==0:
            break
        time.sleep(1)
    else:
        raise RuntimeError('Disposable PostgreSQL did not start')
    first=dict(action='create',databaseName='fixture_one',username='fixture_user_one',password=password,container=name)
    second=dict(action='create',databaseName='fixture_two',username='fixture_user_two',password=password,container=name)
    operation.execute(first)
    operation.execute(second)
    sql('fixture_one','fixture_user_one',"CREATE TABLE retained(value text); INSERT INTO retained VALUES ('preserved');")
    operation.execute(first)
    output=sql('fixture_one','fixture_user_one','SELECT value FROM retained;').stdout
    assert b'preserved' in output
    denied=subprocess.run(['docker','exec',name,'psql','-X','-U','fixture_user_one','-d','fixture_two','-c','SELECT 1'],capture_output=True)
    assert denied.returncode != 0, 'Neighbor database admitted unrelated credential'
    escalation=subprocess.run(['docker','exec',name,'psql','-X','-U','fixture_user_one','-d','fixture_one','-v','ON_ERROR_STOP=1','-c','CREATE ROLE forbidden SUPERUSER'],capture_output=True)
    assert escalation.returncode != 0, 'Application role gained administrative authority'
    data=json.loads(run(['docker','inspect',name]).stdout)[0]
    assert not data['HostConfig']['PortBindings'], 'Private database unexpectedly published a host port'
    spec=importlib.util.spec_from_file_location('migration_fence',Path(__file__).with_name('migration-fence.py'))
    fence=importlib.util.module_from_spec(spec)
    spec.loader.exec_module(fence)
    fence_request=dict(action='fence',container=name,databaseName='fixture_one',operationId='dbop_fixtureintegration')
    fence.execute(fence_request)
    denied=subprocess.run(['docker','exec','-e','PGPASSWORD='+password,name,'psql','-h','127.0.0.1','-U','fixture_user_one','-d','fixture_one','-c','SELECT 1'],capture_output=True)
    assert denied.returncode != 0, 'Migration fence admitted a TCP writer'
    dump=run(['docker','exec','-e','PGHOST=/var/run/postgresql',name,'pg_dump','-U','fixture_user_one','-d','fixture_one','--format=custom','--no-owner','--no-acl']).stdout
    assert dump.startswith(b'PGDMP'), 'Final migration snapshot did not use the protected local path'
    fence.execute(dict(fence_request,action='unfence'))
    for attempt in range(50):
        probe=subprocess.run(['docker','exec','-e','PGPASSWORD='+password,name,'psql','-h','127.0.0.1','-U','fixture_user_one','-d','fixture_one','-c','SELECT 1'],capture_output=True)
        if probe.returncode == 0:
            break
        time.sleep(0.1)
    else:
        raise RuntimeError('Unfenced database did not admit its existing credential')
    for database,user,label in [('fixture_one','fixture_user_one','revoked-client'),('fixture_two','fixture_user_two','retained-client')]:
        clients.append(subprocess.Popen(['docker','exec','-e','PGPASSWORD='+password,'-e','PGAPPNAME='+label,name,'psql','-h','127.0.0.1','-U',user,'-d',database,'-c','SELECT pg_sleep(60)'],stdout=subprocess.PIPE,stderr=subprocess.PIPE))
    for attempt in range(50):
        count=run(['docker','exec',name,'psql','-X','-U','minicloud','-d','postgres','-Atc',"SELECT count(*) FROM pg_stat_activity WHERE application_name IN ('revoked-client','retained-client')"]).stdout.strip()
        if count==b'2':
            break
        time.sleep(0.1)
    else:
        raise RuntimeError('Disposable clients failed to connect')
    access.apply(dict(container=name,databases=[dict(databaseName='fixture_one',username='fixture_user_one',mode='internal',internalCidrs=[]),dict(databaseName='fixture_two',username='fixture_user_two',mode='public',internalCidrs=['127.0.0.1/32'],allowedCidrs=[])]))
    assert clients[0].wait(timeout=10) != 0, 'Revoked existing connection remained active'
    assert clients[1].poll() is None, 'Allowed database neighbor was disconnected'
    sql('postgres','minicloud',"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name='retained-client'")
    clients[1].wait(timeout=10)
    operation.execute(dict(first,action='rotate',password='rotated-fixture-'+secrets.token_hex(24)))
    operation.execute(dict(second,action='delete'))
    assert b'preserved' in sql('fixture_one','fixture_user_one','SELECT value FROM retained;').stdout
    print('Passed: real shared database creation, retry preservation, neighbor/admin denial, private exposure, rotation, scoped deletion and writer-fenced local capture and live session revocation.')
finally:
    subprocess.run(['docker','rm','-f','-v',name],capture_output=True)
    for client in clients:
        client.communicate(timeout=10)
