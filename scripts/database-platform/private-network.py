#!/usr/bin/env python3
"""Restricted per-binding SSH database forwarding using existing OpenSSH/systemd."""
import hashlib
import pwd
import ipaddress
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import socket
import time


def run(args, **kw):
    return subprocess.run(args, check=True, capture_output=True, timeout=60, **kw).stdout.decode().strip()


def identity(value):
    if not re.fullmatch(r'[a-z][a-z0-9_]{7,95}',value):
        raise ValueError('Invalid binding identity')
    return value


def tunnel_user(binding):
    return 'mcdb_' + hashlib.sha256(binding.encode()).hexdigest()[:20]


def validate_account(account):
    if account.pw_uid == 0 or account.pw_dir != '/nonexistent' or account.pw_shell != '/bin/sh':
        raise ValueError('Unexpected private network account')
    groups = run(['id','-G',account.pw_name]).split()
    if set(groups) != {str(account.pw_gid)}:
        raise ValueError('Private network account has unexpected group privileges')


def install_sshd_policy():
    directory = '/etc/minicloud/database-network'
    run(['sudo','-n','install','-d','-o','root','-g','root','-m','755',directory])
    policy = directory + '/sshd.conf'
    content = ('Match User mcdb_*\n'
               '    AllowTcpForwarding local\n'
               '    AllowStreamLocalForwarding no\n'
               '    PermitTunnel no\n'
               '    PermitListen none\n'
               '    AuthorizedKeysFile /etc/minicloud/database-network/%u/authorized_keys\n'
               '    ForceCommand /bin/false\n'
               '    PasswordAuthentication no\n'
               '    KbdInteractiveAuthentication no\n'
               '    PermitTTY no\n'
               '    X11Forwarding no\n'
               '    AllowAgentForwarding no\n'
               '    PermitUserRC no\n'
               '    GatewayPorts no\n'
               'Match all\n')
    main = '/etc/ssh/sshd_config'
    previous = run(['sudo','-n','cat',main]) + '\n'
    include = 'Include ' + policy
    updated = previous if include in previous.splitlines() else previous + '\n# Minicloud database forwarding\nMatch all\n' + include + '\n'
    try:
        old_policy = run(['sudo','-n','cat',policy]) + '\n'
    except subprocess.CalledProcessError:
        old_policy = None
    try:
        run(['sudo','-n','tee',policy],input=content.encode())
        run(['sudo','-n','chmod','644',policy])
        run(['sudo','-n','tee',main],input=updated.encode())
        # Validate the complete effective configuration before touching the daemon.
        run(['sudo','-n','/usr/sbin/sshd','-t'])
        reload_sshd()
    except Exception:
        run(['sudo','-n','tee',main],input=previous.encode())
        if old_policy is None:
            run(['sudo','-n','rm','-f','--',policy])
        else:
            run(['sudo','-n','tee',policy],input=old_policy.encode())
        run(['sudo','-n','/usr/sbin/sshd','-t'])
        reload_sshd()
        raise


def reload_sshd():
    first = subprocess.run(['sudo','-n','systemctl','reload','ssh'],capture_output=True,timeout=30)
    if first.returncode != 0:
        run(['sudo','-n','systemctl','reload','sshd'])


def execute(request):
    binding = identity(request['bindingId'])
    root = Path('/opt/minicloud/database-network') / binding
    if root.is_symlink():
        raise ValueError('Invalid private network root')
    run(['sudo','-n','install','-d','-m','700','-o','minicloud','-g','minicloud',str(root)])
    if root.is_symlink() or root.stat().st_uid != os.geteuid():
        raise ValueError('Invalid private network root')
    root.chmod(0o700)
    action = request['action']
    if action == 'stop-client':
        service = f'minicloud-db-{binding}.service'
        run(['sudo','-n','systemctl','disable','--now',service])
        return {'status':'stopped'}
    if action == 'revoke-client':
        user = tunnel_user(binding)
        authorization = '/etc/minicloud/database-network/' + user + '/authorized_keys'
        run(['sudo','-n','rm','-f','--',authorization])
        try:
            account = pwd.getpwnam(user)
        except KeyError:
            return {'status':'revoked'}
        validate_account(account)
        run(['sudo','-n','usermod','--expiredate','1',user])
        stopped = subprocess.run(['sudo','-n','pkill','-KILL','-u',str(account.pw_uid)],capture_output=True,timeout=15)
        if stopped.returncode not in (0,1):
            raise RuntimeError('Private network session termination failed')
        remaining = subprocess.run(['sudo','-n','pgrep','-u',str(account.pw_uid)],capture_output=True,timeout=15)
        if remaining.returncode != 1:
            raise RuntimeError('Private network sessions are still active')
        return {'status':'revoked'}
    if action == 'prepare-client':
        key = root/'identity'
        if not key.exists():
            run(['ssh-keygen','-q','-t','ed25519','-N','','-f',str(key)])
        if key.is_symlink() or key.stat().st_uid != os.geteuid() or key.stat().st_mode & 0o077:
            raise ValueError('Insecure private network key')
        network = request['dockerNetwork']
        if not re.fullmatch(r'[a-zA-Z0-9_.-]{1,120}',network):
            raise ValueError('Invalid application network')
        data = json.loads(run(['docker','network','inspect',network]))[0]
        gateway = str(ipaddress.ip_address(data['IPAM']['Config'][0]['Gateway']))
        return {'publicKey':run(['ssh-keygen','-y','-f',str(key)]),'gateway':gateway}
    if action == 'authorize-client':
        container = request['container']
        if not re.fullmatch(r'[a-zA-Z0-9_.-]{1,120}',container):
            raise ValueError('Invalid database container')
        data = json.loads(run(['docker','inspect',container]))[0]
        published = data['NetworkSettings']['Ports'].get('5432/tcp') or []
        if not any(item['HostIp']=='127.0.0.1' and item['HostPort']=='15432' for item in published):
            raise ValueError('Stable private database endpoint is unavailable')
        target = '127.0.0.1'
        public = request['publicKey']
        if not re.fullmatch(r'ssh-ed25519 [A-Za-z0-9+/=]{40,120}',public):
            raise ValueError('Invalid client public key')
        user = tunnel_user(binding)
        try:
            account = pwd.getpwnam(user)
        except KeyError:
            run(['sudo','-n','useradd','--system','--user-group','--no-create-home','--home-dir','/nonexistent','--shell','/bin/sh','--password','*',user])
            account = pwd.getpwnam(user)
        validate_account(account)
        run(['sudo','-n','usermod','--expiredate','',user])
        install_sshd_policy()
        directory = '/etc/minicloud/database-network/' + user
        run(['sudo','-n','install','-d','-o','root','-g','root','-m','755',directory])
        keyline = f'restrict,port-forwarding,command="/bin/false",permitopen="{target}:15432" {public} minicloud-database-{binding}\n'
        authorization = directory + '/authorized_keys'
        run(['sudo','-n','tee',authorization],input=keyline.encode())
        run(['sudo','-n','chmod','644',authorization])
        run(['sudo','-n','chown','root:root',authorization])
        hostkey = run(['sudo','-n','cat','/etc/ssh/ssh_host_ed25519_key.pub']).split()[:2]
        return {'target':target,'hostKey':' '.join(hostkey),'sshUser':user}
    if action == 'connect-client':
        gateway = str(ipaddress.ip_address(request['gateway']))
        host = str(ipaddress.ip_address(request['serverIp']))
        target = str(ipaddress.ip_address(request['target']))
        port = request['port']
        if type(port) is not int or not 20000 <= port <= 60000:
            raise ValueError('Invalid private network port')
        hostkey = request['hostKey']
        if not re.fullmatch(r'ssh-ed25519 [A-Za-z0-9+/=]{40,120}',hostkey):
            raise ValueError('Invalid server host key')
        user = request['sshUser']
        if user != tunnel_user(binding):
            raise ValueError('Invalid private network account')
        known = root/'known_hosts'
        known.write_text(host+' '+hostkey+'\n')
        known.chmod(0o600)
        service = f'minicloud-db-{binding}.service'
        config = '[Unit]\nAfter=network-online.target docker.service\nWants=network-online.target\n[Service]\nUser=minicloud\nExecStart=/usr/bin/ssh -N -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -o StrictHostKeyChecking=yes -o UserKnownHostsFile='+str(known)+' -i '+str(root/'identity')+' -L '+gateway+':'+str(port)+':'+target+':15432 '+user+'@'+host+'\nRestart=always\nRestartSec=5\n[Install]\nWantedBy=multi-user.target\n'
        run(['sudo','-n','tee','/etc/systemd/system/'+service],input=config.encode())
        run(['sudo','-n','systemctl','daemon-reload'])
        run(['sudo','-n','systemctl','enable','--now',service])
        run(['sudo','-n','systemctl','restart',service])
        run(['sudo','-n','systemctl','is-active','--quiet',service])
        deadline = time.monotonic() + 20
        while True:
            try:
                with socket.create_connection((gateway,port),timeout=2) as connection:
                    connection.sendall(bytes.fromhex('0000000804d2162f'))
                    if connection.recv(1) not in (b'S',b'N'):
                        raise OSError('Database forwarding did not respond')
                break
            except OSError:
                if time.monotonic() >= deadline:
                    run(['sudo','-n','systemctl','stop',service])
                    raise RuntimeError('Private database path failed verification')
                time.sleep(1)
        credential = request['credentials']
        if not re.fullmatch(r'[a-z][a-z0-9_]{0,62}',credential['username']) or not re.fullmatch(r'[a-z][a-z0-9_]{0,62}',credential['databaseName']):
            raise ValueError('Invalid database login binding')
        environment = dict(os.environ, PGPASSWORD=credential['password'])
        try:
            answer = run(['docker','run','--rm','--network','host','-e','PGPASSWORD','postgres:16-alpine','psql','-X','-h',gateway,'-p',str(port),'-U',credential['username'],'-d',credential['databaseName'],'-Atc','SELECT 1'],env=environment)
            if answer.strip() != '1':
                raise RuntimeError('Authenticated database path failed verification')
        except Exception:
            run(['sudo','-n','systemctl','stop',service])
            raise RuntimeError('Authenticated database path failed verification; forwarding stopped') from None
        return {'host':'host.docker.internal','port':port,'status':'connected'}
    raise ValueError('Unsupported private network operation')


if __name__ == '__main__':
    os.umask(0o077)
    try:
        with open(sys.argv[1]) as stream:
            request=json.load(stream)
        result=execute(request)
        print('__MINICLOUD_DATABASE_RESULT__='+json.dumps(result))
        print('__MINICLOUD_DEPLOY_COMPLETE__='+request['operationId'])
    except Exception as error:
        print('__MINICLOUD_DATABASE_RESULT__='+json.dumps({'status':'failed','errorType':type(error).__name__}))
        raise SystemExit(1)
