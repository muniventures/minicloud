import importlib.util
import json
import os
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location('private_network', Path(__file__).with_name('private-network.py'))
network = importlib.util.module_from_spec(spec)
spec.loader.exec_module(network)


class PrivateNetworkTests(unittest.TestCase):
    def test_authorization_pins_loopback_endpoint_not_container_address(self):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory)
            binding='dbbinding_fixture'
            (base/binding).mkdir(mode=0o700)
            calls=[]
            def run(args,**kwargs):
                calls.append((args,kwargs))
                if args[:2]==['docker','inspect']:
                    return json.dumps([{'NetworkSettings':{'Ports':{'5432/tcp':[{'HostIp':'127.0.0.1','HostPort':'15432'}]},'Networks':{'db':{'IPAddress':'172.23.0.99'}}}}])
                if args[:2]==['id','-G']: return '1001'
                if args[-1]=='/etc/ssh/ssh_host_ed25519_key.pub': return 'ssh-ed25519 '+'A'*64
                return ''
            def mapped_path(value): return base if value=='/opt/minicloud/database-network' else Path(value)
            account=SimpleNamespace(pw_uid=1001,pw_gid=1001,pw_name=network.tunnel_user(binding),pw_dir='/nonexistent',pw_shell='/bin/sh')
            with patch.object(network,'Path',side_effect=mapped_path), patch.object(network,'run',side_effect=run), patch.object(network.pwd,'getpwnam',return_value=account),patch.object(network,'reload_sshd'):
                result=network.execute(dict(bindingId=binding,action='authorize-client',container='fixture-db',publicKey='ssh-ed25519 '+'A'*64))
            self.assertEqual(result['target'],'127.0.0.1')
            authorization=next(kwargs['input'].decode() for args,kwargs in calls if args[:3]==['sudo','-n','tee'] and args[-1].endswith('authorized_keys'))
            self.assertIn('permitopen="127.0.0.1:15432"',authorization)
            self.assertNotIn('172.23.0.99',authorization)

    def test_client_systemd_uses_stable_remote_port_and_authenticated_probe(self):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory)
            binding='dbbinding_fixture'
            (base/binding).mkdir(mode=0o700)
            calls=[]
            def run(args,**kwargs):
                calls.append((args,kwargs))
                return '1' if args[:2]==['docker','run'] else ''
            def mapped_path(value): return base if value=='/opt/minicloud/database-network' else Path(value)
            connection=MagicMock()
            connection.__enter__.return_value.recv.return_value=b'S'
            with patch.object(network,'Path',side_effect=mapped_path),patch.object(network,'run',side_effect=run),patch.object(network.socket,'create_connection',return_value=connection):
                result=network.execute(dict(bindingId=binding,action='connect-client',gateway='172.20.0.1',serverIp='192.0.2.5',target='127.0.0.1',port=20001,hostKey='ssh-ed25519 '+'A'*64,sshUser=network.tunnel_user(binding),credentials=dict(username='fixture_user',databaseName='fixture_db',password='private-fixture')))
            service=next(kwargs['input'].decode() for args,kwargs in calls if args[:3]==['sudo','-n','tee'])
            self.assertIn('-L 172.20.0.1:20001:127.0.0.1:15432',service)
            self.assertIn('StrictHostKeyChecking=yes',service)
            self.assertEqual(result['status'],'connected')
            probe=next((args,kwargs) for args,kwargs in calls if args[:2]==['docker','run'])
            self.assertNotIn('private-fixture',' '.join(probe[0]))
            self.assertEqual(probe[1]['env']['PGPASSWORD'],'private-fixture')

    def test_failed_authenticated_probe_stops_forwarding(self):
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory)
            binding='dbbinding_fixture'
            (base/binding).mkdir(mode=0o700)
            calls=[]
            def run(args,**kwargs):
                calls.append(args)
                return '0' if args[:2]==['docker','run'] else ''
            def mapped_path(value): return base if value=='/opt/minicloud/database-network' else Path(value)
            connection=MagicMock()
            connection.__enter__.return_value.recv.return_value=b'S'
            with patch.object(network,'Path',side_effect=mapped_path),patch.object(network,'run',side_effect=run),patch.object(network.socket,'create_connection',return_value=connection):
                with self.assertRaisesRegex(RuntimeError,'forwarding stopped'):
                    network.execute(dict(bindingId=binding,action='connect-client',gateway='172.20.0.1',serverIp='192.0.2.5',target='127.0.0.1',port=20001,hostKey='ssh-ed25519 '+'A'*64,sshUser=network.tunnel_user(binding),credentials=dict(username='fixture_user',databaseName='fixture_db',password='private-fixture')))
            self.assertEqual(calls[-1],['sudo','-n','systemctl','stop','minicloud-db-'+binding+'.service'])
