#!/usr/bin/env python3
"""Update source admission on an already mounted, independently bound repository."""
import json
import os
import re
import subprocess
import sys

PROGRAM = '''import json,os,pathlib,tempfile
root=pathlib.Path('/data/backups')
if not os.path.ismount(root): raise RuntimeError('Repository volume missing')
path=root/'.bindings.json'
if path.is_symlink(): raise RuntimeError('Invalid bindings file')
bindings=json.loads(path.read_text()) if path.exists() else []
request=json.load(__import__('sys').stdin)
new=request['binding']
existing=next((v for v in bindings if (v['environment'],v['databaseId'])==(new['environment'],new['databaseId'])),None)
if request.get('action')=='purge-source':
 if (existing is None or existing.get('policyRevision',0)!=new.get('policyRevision',0) or
     existing.get('enabled',True) is not False or existing.get('keepLastCopies',False) is not False or
     not __import__('hmac').compare_digest(existing['token'],new['token'])):
  raise RuntimeError('Repository purge policy changed')
 from server import Repository,Settings
 repository=Repository(Settings.from_env())
 print(json.dumps(repository.purge_source(new['environment'],new['databaseId'],new['policyRevision'],request['expectedRepositoryId'])))
 raise SystemExit(0)
if existing is not None and existing.get('policyRevision',0)>new.get('policyRevision',0): raise RuntimeError('Repository policy revision changed')
bindings=[v for v in bindings if (v['environment'],v['databaseId'])!=(new['environment'],new['databaseId'])]+[new]
fd,name=tempfile.mkstemp(prefix='.bindings-',dir=root)
try:
 os.fchmod(fd,0o600)
 with os.fdopen(fd,'w') as target:
  json.dump(bindings,target)
  target.flush();os.fsync(target.fileno())
 os.replace(name,path)
finally:
 if os.path.exists(name):os.unlink(name)
from server import Repository,Settings
repository=Repository(Settings.from_env())
repository.refresh_bindings()
records=repository.source_records(new['environment'],new['databaseId'])
complete={record['backupId'] for record in records if record['complete']}
print(json.dumps({'status':'configured','presentBackupIds':[value for value in request['backupIds'] if value in complete]}))
'''


def configure(request, run=subprocess.run):
    container = request['container']
    binding = request['binding']
    if (not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.-]{0,127}',container) or
        not re.fullmatch(r'[a-z][a-z0-9-]{0,31}',binding['environment']) or
        not re.fullmatch(r'(?:db|database)_[a-z0-9]{8,64}',binding['databaseId']) or
        not isinstance(binding['token'],str) or not 32<=len(binding['token'])<=512 or
        type(binding['retentionDays']) is not int or not 1<=binding['retentionDays']<=3650 or
        type(binding['minimumComplete']) is not int or not 1<=binding['minimumComplete']<=100 or
        type(binding.get('policyRevision',0)) is not int or binding.get('policyRevision',0)<0 or
        type(binding['enabled']) is not bool or type(binding.get('keepLastCopies',False)) is not bool):
        raise ValueError('Invalid repository binding')
    action=request.get('action','configure')
    if action not in ('configure','purge-source'):
        raise ValueError('Invalid repository action')
    if action=='purge-source' and (binding['enabled'] or binding.get('keepLastCopies',False) or binding.get('policyRevision',0)<1 or not re.fullmatch(r'[A-Za-z0-9_-]{8,96}',request.get('expectedRepositoryId',''))):
        raise ValueError('Explicit disabled source purge identity required')
    backup_ids=request.get('backupIds',[])
    if not isinstance(backup_ids,list) or len(backup_ids)>100 or any(not isinstance(v,str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{7,95}',v) for v in backup_ids):
        raise ValueError('Invalid bounded backup observation')
    result=run(['docker','exec','-i',container,'python','-c',PROGRAM],input=json.dumps({'binding':binding,'backupIds':backup_ids,'action':action,'expectedRepositoryId':request.get('expectedRepositoryId')}).encode(),capture_output=True,timeout=60)
    if result.returncode: raise RuntimeError('Repository binding update failed')
    if len(result.stdout)>16384: raise RuntimeError('Repository observation exceeded bound')
    observed=json.loads(result.stdout)
    if not isinstance(observed,dict): raise RuntimeError('Repository observation invalid')
    if action=='purge-source':
        values=observed.get('purgedBackupIds')
        if observed.get('status')!='purged' or not isinstance(values,list) or len(values)>100 or observed.get('purgedCount')!=len(values) or type(observed.get('remainingCount')) is not int or observed['remainingCount']<0 or any(not isinstance(v,str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_-]{7,95}',v) for v in values):
            raise RuntimeError('Repository purge observation invalid')
    elif observed.get('status')!='configured' or not isinstance(observed.get('presentBackupIds'),list):
        raise RuntimeError('Repository observation invalid')
    return observed


if __name__=='__main__':
    os.umask(0o077)
    try:
        with open(sys.argv[1]) as source: request=json.load(source)
        print('__MINICLOUD_DATABASE_RESULT__='+json.dumps(configure(request)))
        print('__MINICLOUD_DEPLOY_COMPLETE__='+request['operationId'])
    except Exception as error:
        print('__MINICLOUD_DATABASE_RESULT__='+json.dumps({'status':'failed','errorType':type(error).__name__}))
        raise SystemExit(1)
