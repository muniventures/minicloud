#!/usr/bin/env python3
"""Apply database-specific connection rules; retain local application access."""
import ipaddress
import json
import re
import os
import secrets
import tempfile
import subprocess

class AccessRollbackUnverifiedError(RuntimeError):
    pass

class AccessChangeRolledBackError(RuntimeError):
    pass
from pathlib import Path


def rules(databases):
    lines = ['local all all trust']
    public = False
    for item in databases:
        name, role = item['databaseName'], item['username']
        if not re.fullmatch(r'[a-z][a-z0-9_]{0,62}', name) or not re.fullmatch(r'[a-z][a-z0-9_]{0,62}', role):
            raise ValueError('Invalid database identity')
        if type(item.get('fenced',False)) is not bool:
            raise ValueError('Invalid database fence state')
        if item.get('fenced',False):
            lines += [f'host {name} all 0.0.0.0/0 reject',f'host {name} all ::/0 reject']
            continue
        for cidr in item.get('internalCidrs', []):
            network = ipaddress.ip_network(cidr, strict=False)
            lines.append(f'host {name} {role} {network} scram-sha-256')
        if item['mode'] not in ('internal','private','public'):
            raise ValueError('Unsupported database access mode')
        if item['mode'] == 'private':
            address = ipaddress.ip_address(item['privateIp'])
            network = ipaddress.ip_network(item['privateSubnet'], strict=False)
            if address not in network:
                raise ValueError('Private listener is outside its private network')
            lines.append(f'hostssl {name} {role} {network} scram-sha-256')
        if item['mode'] == 'public':
            public = True
            for cidr in item.get('allowedCidrs', []):
                network = ipaddress.ip_network(cidr, strict=False)
                lines.append(f'hostssl {name} {role} {network} scram-sha-256')
        lines += [f'host {name} all 0.0.0.0/0 reject', f'host {name} all ::/0 reject']
    lines += ['host all all 0.0.0.0/0 reject', 'host all all ::/0 reject']
    return '\n'.join(lines) + '\n', public


def apply(request, run=subprocess.run):
    container = request['container']
    if not re.fullmatch(r'[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}', container):
        raise ValueError('Invalid container identity')
    hba, public = rules(request['databases'])
    admin = request.get('adminUser', 'minicloud')
    if not re.fullmatch(r'[a-z][a-z0-9_]{0,62}', admin):
        raise ValueError('Invalid administrator')
    # Resolve PGDATA through the running container; never accept a source path
    # from an HTTP caller. This changes authentication, not data directories.
    probe = run(['docker','exec',container,'psql','-X','-U',admin,'-d','postgres','-Atc','SHOW hba_file'],capture_output=True,check=True,timeout=30)
    path = probe.stdout.decode().strip()
    if not path.startswith('/var/lib/postgresql/') or '..' in path.split('/') or '\n' in path:
        raise ValueError('Unexpected PostgreSQL configuration path')
    run(['docker','exec','-i','-u','postgres',container,'sh','-c','umask 077; cat > "$1.next" && cp "$1" "$1.previous" && mv "$1.next" "$1"','hba',path],input=hba.encode(),capture_output=True,check=True,timeout=30)
    verify = run(['docker','exec',container,'psql','-X','-U',admin,'-d','postgres','-Atc',"SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL"],capture_output=True,check=True,timeout=30)
    if verify.stdout.strip() != b'0':
        run(['docker','exec',container,'cp',path+'.previous',path],capture_output=True,check=True,timeout=30)
        raise ValueError('Authentication rules failed validation')
    run(['docker','exec',container,'psql','-X','-U',admin,'-d','postgres','-v','ON_ERROR_STOP=1','-c','SELECT pg_reload_conf()'],capture_output=True,check=True,timeout=30)
    for item in request['databases']:
        allowed = [] if item.get('fenced',False) else list(item.get('internalCidrs', []))
        if item['mode'] == 'private' and not item.get('fenced',False):
            allowed.append(item['privateSubnet'])
        if item['mode'] == 'public' and not item.get('fenced',False):
            allowed += item.get('allowedCidrs', [])
        networks = ','.join("'"+str(ipaddress.ip_network(value,strict=False))+"'" for value in allowed)
        permit = 'client_addr <<= ANY(ARRAY['+networks+']::cidr[])' if allowed else 'FALSE'
        # Reload affects future authentications only. Revoke already-open
        # sessions whose source no longer belongs to an admitted path.
        statement = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='"+item['databaseName']+"' AND client_addr IS NOT NULL AND pid<>pg_backend_pid() AND NOT ("+permit+")"
        run(['docker','exec',container,'psql','-X','-U',admin,'-d','postgres','-v','ON_ERROR_STOP=1','-c',statement],capture_output=True,check=True,timeout=30)
    return {'publicListenerRequired':public,'status':'rules-applied'}


def listener_addresses(databases):
    private = sorted({str(ipaddress.ip_address(item['privateIp'])) for item in databases if item['mode'] == 'private' and not item.get('fenced',False)})
    if any(item['mode'] == 'public' and not item.get('fenced',False) for item in databases):
        return ['0.0.0.0']
    return private


def _secure_file(path):
    if path.is_symlink() or not path.is_file() or path.stat().st_uid != os.geteuid() or path.stat().st_mode & 0o077:
        raise ValueError('Invalid protected TLS material')


def ensure_tls_identity(directory, addresses, run=subprocess.run):
    addresses = sorted({str(ipaddress.ip_address(value)) for value in addresses})
    if not addresses:
        raise ValueError('TLS listener identities are required')
    directory = Path(directory)
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    if directory.is_symlink() or directory.stat().st_uid != os.geteuid() or directory.stat().st_mode & 0o077:
        raise ValueError('Invalid TLS directory')
    ca_key, ca_cert = directory/'ca.key', directory/'ca.crt'
    if ca_key.exists() != ca_cert.exists():
        raise ValueError('Incomplete root CA identity')
    if not ca_key.exists():
        with tempfile.TemporaryDirectory(prefix='.ca-',dir=directory) as temporary:
            root=Path(temporary)
            run(['openssl','req','-x509','-newkey','rsa:3072','-nodes','-keyout',str(root/'key'),'-out',str(root/'cert'),
                 '-days','3650','-subj','/CN=Minicloud database root '+secrets.token_hex(8),
                 '-addext','basicConstraints=critical,CA:TRUE,pathlen:0','-addext','keyUsage=critical,keyCertSign,cRLSign'],check=True,capture_output=True,timeout=60)
            for source,target in ((root/'key',ca_key),(root/'cert',ca_cert)):
                source.chmod(0o600)
                os.replace(source,target)
    _secure_file(ca_key)
    _secure_file(ca_cert)
    run(['openssl','x509','-in',str(ca_cert),'-checkend','604800','-noout'],check=True,capture_output=True,timeout=30)
    key,cert=directory/'server.key',directory/'server.crt'
    metadata=directory/'server-addresses.json'
    reusable = False
    if key.exists() and cert.exists() and metadata.exists():
        for path in (key,cert,metadata): _secure_file(path)
        try:
            reusable=(json.loads(metadata.read_text()) == addresses and
                run(['openssl','x509','-in',str(cert),'-checkend','604800','-noout'],capture_output=True,timeout=30).returncode==0 and
                run(['openssl','verify','-CAfile',str(ca_cert),str(cert)],capture_output=True,timeout=30).returncode==0)
        except (ValueError,OSError):
            reusable=False
    if not reusable:
        with tempfile.TemporaryDirectory(prefix='.leaf-',dir=directory) as temporary:
            root=Path(temporary)
            extension=root/'extensions'
            extension.write_text('basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName='+','.join('IP:'+value for value in addresses)+'\n')
            run(['openssl','req','-new','-newkey','rsa:3072','-nodes','-keyout',str(root/'key'),'-out',str(root/'request'),'-subj','/CN=Minicloud database'],check=True,capture_output=True,timeout=60)
            run(['openssl','x509','-req','-in',str(root/'request'),'-CA',str(ca_cert),'-CAkey',str(ca_key),'-set_serial',str(secrets.randbits(128) or 1),
                 '-out',str(root/'cert'),'-days','365','-extfile',str(extension)],check=True,capture_output=True,timeout=60)
            run(['openssl','verify','-CAfile',str(ca_cert),str(root/'cert')],check=True,capture_output=True,timeout=30)
            (root/'addresses').write_text(json.dumps(addresses))
            for source,target in ((root/'key',key),(root/'cert',cert),(root/'addresses',metadata)):
                source.chmod(0o600)
                os.replace(source,target)
    return key,cert,ca_cert


def expose(request, run=subprocess.run):
    slug=request['appSlug']
    if not re.fullmatch(r'[a-z0-9][a-z0-9-]{0,62}',slug):
        raise ValueError('Invalid source app identity')
    app=Path('/opt/minicloud/apps')/slug
    if app.is_symlink() or not app.is_dir():
        raise ValueError('Source application unavailable')
    container='minicloud-'+slug+'-db-1'
    info=run(['docker','inspect',container],check=True,capture_output=True,timeout=30)
    networks=json.loads(info.stdout)[0]['NetworkSettings']['Networks']
    for item in request['databases']:
        item['internalCidrs']=['127.0.0.1/32','::1/128']
        for name in networks:
            detail=run(['docker','network','inspect',name],check=True,capture_output=True,timeout=30)
            item['internalCidrs'] += [entry['Subnet'] for entry in json.loads(detail.stdout)[0]['IPAM']['Config'] if entry.get('Subnet')]
    hba,public=rules(request['databases'])
    addresses=listener_addresses(request['databases'])
    identities=[str(ipaddress.ip_address(item['privateIp'])) for item in request['databases'] if item['mode']=='private' and not item.get('fenced',False)]
    if public or (identities and request.get('serverIp')):
        identities.append(str(ipaddress.ip_address(request['serverIp'])))
    override=app/'compose.database-access.json'
    if override.is_symlink(): raise ValueError('Invalid access configuration')
    previous_override=override.read_bytes() if override.exists() else None
    certdir=app/'shared'/'database-tls'
    previous_tls={}
    for name in ('server.key','server.crt','server-addresses.json'):
        source=certdir/name
        if source.exists():
            _secure_file(source)
            previous_tls[name]=source.read_bytes()
    compose=['docker','compose','-p','minicloud-'+slug,'-f',str(app/'infra.compose.json'),'-f',str(app/'app.compose.json'),'-f',str(override),'up','-d','db']
    path=run(['docker','exec',container,'psql','-X','-U','minicloud','-d','postgres','-Atc','SHOW hba_file'],check=True,capture_output=True,timeout=30).stdout.decode().strip()
    if not path.startswith('/var/lib/postgresql/') or '..' in path.split('/') or '\n' in path:
        raise ValueError('Unexpected PostgreSQL configuration path')
    auto=path.rsplit('/',1)[0]+'/postgresql.auto.conf'
    previous_hba=run(['docker','exec',container,'cat',path],check=True,capture_output=True,timeout=30).stdout
    previous_auto=run(['docker','exec',container,'cat',auto],check=True,capture_output=True,timeout=30).stdout
    try:
        ca_cert=None
        if identities:
            key,cert,ca_cert=ensure_tls_identity(app/'shared'/'database-tls',identities,run)
            for source,name in ((key,'server.key'),(cert,'server.crt')):
                run(['docker','cp',str(source),container+':/var/lib/postgresql/data/'+name],check=True,capture_output=True,timeout=30)
            run(['docker','exec','-u','root',container,'chown','70:70','/var/lib/postgresql/data/server.key','/var/lib/postgresql/data/server.crt'],check=True,capture_output=True,timeout=30)
            run(['docker','exec','-u','root',container,'chmod','600','/var/lib/postgresql/data/server.key'],check=True,capture_output=True,timeout=30)
            sql="ALTER SYSTEM SET ssl = 'on';\nALTER SYSTEM SET ssl_cert_file = '/var/lib/postgresql/data/server.crt';\nALTER SYSTEM SET ssl_key_file = '/var/lib/postgresql/data/server.key';\nSELECT pg_reload_conf();\n"
            run(['docker','exec','-i',container,'psql','-X','-U','minicloud','-d','postgres','-v','ON_ERROR_STOP=1'],input=sql.encode(),check=True,capture_output=True,timeout=30)
        result=apply(dict(container=container,databases=request['databases']),run)
        ports=['127.0.0.1:15432:5432']+[('['+address+']' if ':' in address else address)+':5432:5432' for address in addresses]
        override.write_text(json.dumps({'services':{'db':{'ports':ports}}}))
        override.chmod(0o600)
        run(compose,check=True,capture_output=True,timeout=300)
        run(['docker','exec',container,'pg_isready','-U','minicloud','-d','postgres'],check=True,capture_output=True,timeout=30)
        if identities:
            enabled=run(['docker','exec',container,'psql','-X','-U','minicloud','-d','postgres','-Atc','SHOW ssl'],check=True,capture_output=True,timeout=30)
            if enabled.stdout.strip()!=b'on': raise ValueError('Database TLS was not activated')
        return dict(result,public=public,listenAddresses=addresses,caCertificatePem=ca_cert.read_text() if ca_cert else None)
    except Exception:
        try:
            for name,contents in previous_tls.items():
                source=certdir/name
                source.write_bytes(contents)
                source.chmod(0o600)
                if name in ('server.key','server.crt'):
                    run(['docker','cp',str(source),container+':/var/lib/postgresql/data/'+name],check=True,capture_output=True,timeout=30)
            if previous_tls:
                run(['docker','exec','-u','root',container,'chown','70:70','/var/lib/postgresql/data/server.key','/var/lib/postgresql/data/server.crt'],check=True,capture_output=True,timeout=30)
                run(['docker','exec','-u','root',container,'chmod','600','/var/lib/postgresql/data/server.key'],check=True,capture_output=True,timeout=30)
            for target,contents in ((path,previous_hba),(auto,previous_auto)):
                run(['docker','exec','-i','-u','root',container,'sh','-c','cat > "$1"','rollback',target],input=contents,check=True,capture_output=True,timeout=30)
            if previous_override is None:
                override.write_text(json.dumps({'services':{'db':{'ports':[]}}}))
            else:
                override.write_bytes(previous_override)
            override.chmod(0o600)
            run(compose,check=True,capture_output=True,timeout=300)
            run(['docker','exec',container,'psql','-X','-U','minicloud','-d','postgres','-v','ON_ERROR_STOP=1','-c','SELECT pg_reload_conf()'],check=True,capture_output=True,timeout=30)
            run(['docker','exec',container,'pg_isready','-U','minicloud','-d','postgres'],check=True,capture_output=True,timeout=30)
            if previous_override is None: override.unlink()
        except Exception:
            raise AccessRollbackUnverifiedError('Database access failed and rollback could not be verified') from None
        raise AccessChangeRolledBackError('Database access failed; prior access configuration restored') from None
