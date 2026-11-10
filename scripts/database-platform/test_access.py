import unittest
from access import rules

class AccessTests(unittest.TestCase):
    def test_public_neighbor_does_not_admit_internal_database(self):
        text, public = rules([
            dict(databaseName='private_db', username='private_user', mode='internal', internalCidrs=['172.22.0.0/24']),
            dict(databaseName='public_db', username='public_user', mode='public', allowedCidrs=['192.0.2.0/24'])])
        self.assertTrue(public)
        self.assertIn('host private_db all 0.0.0.0/0 reject', text)
        self.assertNotIn('hostssl private_db', text)
        self.assertIn('hostssl public_db public_user 192.0.2.0/24', text)
        self.assertLess(text.index('host private_db private_user'), text.index('host private_db all'))
    def test_internal_only_has_no_public_listener_requirement(self):
        text, public = rules([dict(databaseName='private_db', username='private_user', mode='internal')])
        self.assertFalse(public)
        self.assertNotIn('hostssl', text)
    def test_rejects_sql_and_network_injection(self):
        with self.assertRaises(ValueError):
            rules([dict(databaseName='db\nhost all all all trust', username='user', mode='internal')])
        with self.assertRaises(ValueError):
            rules([dict(databaseName='db', username='user', mode='public',allowedCidrs=['0/0\nhost all all all trust'])])

class AccessApplyTests(unittest.TestCase):
    def test_invalid_hba_restores_previous_rules_before_reload(self):
        from types import SimpleNamespace
        from access import apply
        calls=[]
        def run(args,**kw):
            calls.append(args)
            if args[-1]=='SHOW hba_file':
                return SimpleNamespace(stdout=b'/var/lib/postgresql/data/pg_hba.conf\n')
            if args[-1].startswith('SELECT count(*)'):
                return SimpleNamespace(stdout=b'1\n')
            return SimpleNamespace(stdout=b'')
        with self.assertRaises(ValueError):
            apply(dict(container='db-1',databases=[dict(databaseName='private_db',username='private_user',mode='internal')]),run)
        self.assertTrue(any(args[-3:]==['cp','/var/lib/postgresql/data/pg_hba.conf.previous','/var/lib/postgresql/data/pg_hba.conf'] for args in calls))
        self.assertFalse(any('SELECT pg_reload_conf()' in args for args in calls))


class PrivateAccessTests(unittest.TestCase):
    def test_private_listener_and_tls_hba(self):
        from access import listener_addresses
        databases=[dict(databaseName='private_db',username='private_user',mode='private',privateIp='10.42.0.5',privateSubnet='10.42.0.0/24')]
        text,public=rules(databases)
        self.assertFalse(public)
        self.assertIn('hostssl private_db private_user 10.42.0.0/24 scram-sha-256',text)
        self.assertEqual(listener_addresses(databases+databases),['10.42.0.5'])
        databases.append(dict(databaseName='public_db',username='public_user',mode='public'))
        self.assertEqual(listener_addresses(databases),['0.0.0.0'])

    def test_private_listener_must_belong_to_subnet(self):
        with self.assertRaises(ValueError):
            rules([dict(databaseName='private_db',username='private_user',mode='private',privateIp='10.43.0.5',privateSubnet='10.42.0.0/24')])

    def test_private_sessions_are_not_revoked_when_subnet_is_retained(self):
        from access import apply
        from types import SimpleNamespace
        calls=[]
        def run(args,**kw):
            calls.append(args)
            if args[-1]=='SHOW hba_file':
                return SimpleNamespace(stdout=b'/var/lib/postgresql/data/pg_hba.conf')
            return SimpleNamespace(stdout=b'0')
        apply(dict(container='db-1',databases=[dict(databaseName='private_db',username='private_user',mode='private',privateIp='10.42.0.5',privateSubnet='10.42.0.0/24')]),run)
        terminate=next(args[-1] for args in calls if 'pg_terminate_backend' in args[-1])
        self.assertIn('10.42.0.0/24',terminate)


class TlsIdentityTests(unittest.TestCase):
    def test_root_stays_stable_and_leaf_covers_changed_sans(self):
        import subprocess,tempfile
        from pathlib import Path
        from access import ensure_tls_identity
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)/'tls'
            key,leaf,ca=ensure_tls_identity(root,['192.0.2.4','10.42.0.5'])
            original_ca=ca.read_bytes()
            original_leaf=leaf.read_bytes()
            original_root_key=(root/'ca.key').read_bytes()
            key,leaf,ca=ensure_tls_identity(root,['192.0.2.4','10.42.0.6'])
            self.assertEqual(ca.read_bytes(),original_ca)
            self.assertEqual((root/'ca.key').read_bytes(),original_root_key)
            self.assertNotEqual(leaf.read_bytes(),original_leaf)
            subprocess.run(['openssl','verify','-CAfile',str(ca),str(leaf)],check=True,capture_output=True)
            certificate=subprocess.run(['openssl','x509','-in',str(leaf),'-text','-noout'],check=True,capture_output=True).stdout.decode()
            for address in ('192.0.2.4','10.42.0.6'):
                self.assertIn('IP Address:'+address,certificate)
            self.assertNotIn('IP Address:10.42.0.5',certificate)
            for name in ('ca.key','ca.crt','server.key','server.crt'):
                self.assertEqual((root/name).stat().st_mode & 0o777,0o600)
            retained=leaf.read_bytes()
            ensure_tls_identity(root,['192.0.2.4','10.42.0.6'])
            self.assertEqual(leaf.read_bytes(),retained)


class AccessRollbackTests(unittest.TestCase):
    def test_listener_failure_restores_previous_hba_auto_config_and_override(self):
        import json,subprocess,tempfile
        from pathlib import Path
        from types import SimpleNamespace
        from unittest.mock import patch
        import access
        with tempfile.TemporaryDirectory() as directory:
            base=Path(directory)
            app=base/'fixture'
            app.mkdir()
            override=app/'compose.database-access.json'
            previous=json.dumps({'services':{'db':{'ports':['127.0.0.1:5432:5432']}}}).encode()
            override.write_bytes(previous)
            calls=[]
            attempts=0
            readiness_attempts=0
            clock=[0]
            original_wait=access.wait_for_postgres
            def sleep(seconds):
                clock[0]+=seconds
            def wait(container, runner):
                return original_wait(container, runner, monotonic=lambda: clock[0], sleep=sleep)
            def run(args,**kw):
                nonlocal attempts, readiness_attempts
                calls.append((args,kw))
                output=b''
                if args[:2]==['docker','inspect']:
                    output=json.dumps([{'NetworkSettings':{'Networks':{}}}]).encode()
                elif args[-1]=='SHOW hba_file': output=b'/var/lib/postgresql/data/pg_hba.conf'
                elif args[-2:]==['cat','/var/lib/postgresql/data/pg_hba.conf']: output=b'previous hba'
                elif args[-2:]==['cat','/var/lib/postgresql/data/postgresql.auto.conf']: output=b'previous auto config'
                elif args[-1].startswith('SELECT count(*)'): output=b'0'
                if args[:2]==['docker','compose']:
                    attempts+=1
                    if attempts==1: raise subprocess.CalledProcessError(1,args)
                if 'pg_isready' in args:
                    readiness_attempts+=1
                    if readiness_attempts==1: raise subprocess.CalledProcessError(1,args)
                return SimpleNamespace(stdout=output,returncode=0)
            def mapped_path(value):
                return base if value=='/opt/minicloud/apps' else Path(value)
            with patch.object(access,'Path',side_effect=mapped_path), patch.object(access,'wait_for_postgres',side_effect=wait):
                with self.assertRaisesRegex(RuntimeError,'prior access configuration restored') as failure:
                    access.expose(dict(appSlug='fixture',databases=[dict(databaseName='fixture_db',username='fixture_user',mode='internal')]),run)
            self.assertEqual(failure.exception.stage, 'access-listener')
            self.assertEqual(failure.exception.cause_type, 'CalledProcessError')
            from dispatch import failure_result
            self.assertEqual(failure_result(failure.exception), dict(status='failed', errorType='AccessChangeRolledBackError', errorStage='access-listener', causeType='CalledProcessError'))
            self.assertEqual(attempts,2)
            self.assertEqual(readiness_attempts,2)
            rollback_start=max(index for index,(args,_) in enumerate(calls) if args[:2]==['docker','compose'])
            rollback_reload=next(index for index,(args,_) in enumerate(calls)
                                 if index>rollback_start and args[-1]=='SELECT pg_reload_conf()')
            last_readiness=max(index for index,(args,_) in enumerate(calls) if 'pg_isready' in args)
            self.assertLess(last_readiness, rollback_reload)
            self.assertEqual(override.read_bytes(),previous)
            restored=[kwargs.get('input') for args,kwargs in calls if 'rollback' in args]
            self.assertEqual(restored,[b'previous hba',b'previous auto config'])


class RetainedMigrationFenceTests(unittest.TestCase):
    def test_fenced_source_stays_deny_only_during_neighbor_reconciliation(self):
        databases=[dict(databaseName='retained_source',username='source_user',mode='public',fenced=True,
                        internalCidrs=['127.0.0.1/32','172.20.0.0/16'],allowedCidrs=['0.0.0.0/0']),
                   dict(databaseName='live_neighbor',username='neighbor_user',mode='internal',internalCidrs=['172.20.0.0/16'])]
        text,public=rules(databases)
        self.assertFalse(public)
        source_lines=[line for line in text.splitlines() if 'retained_source' in line]
        self.assertEqual(source_lines,['host retained_source all 0.0.0.0/0 reject','host retained_source all ::/0 reject'])
        self.assertIn('host live_neighbor neighbor_user 172.20.0.0/16 scram-sha-256',text)
        from access import listener_addresses
        self.assertEqual(listener_addresses(databases),[])

    def test_fenced_private_source_does_not_require_retired_network_metadata(self):
        from access import listener_addresses
        databases=[dict(databaseName='retained_source',username='source_user',mode='private',fenced=True)]
        text,public=rules(databases)
        self.assertFalse(public)
        self.assertNotIn('scram-sha-256',text)
        self.assertEqual(listener_addresses(databases),[])

    def test_fenced_source_revokes_all_tcp_sessions(self):
        from access import apply
        from types import SimpleNamespace
        calls=[]
        def run(args,**kwargs):
            calls.append(args)
            return SimpleNamespace(stdout=b'/var/lib/postgresql/data/pg_hba.conf' if args[-1]=='SHOW hba_file' else b'0')
        apply(dict(container='db-1',databases=[dict(databaseName='retained_source',username='source_user',mode='public',fenced=True,
                    internalCidrs=['127.0.0.1/32'],allowedCidrs=['0.0.0.0/0'])]),run)
        statement=next(args[-1] for args in calls if 'pg_terminate_backend' in args[-1])
        self.assertIn('NOT (FALSE)',statement)


class FailureDiagnosticsTests(unittest.TestCase):
    def test_failure_summary_never_serializes_secret_subprocess_details(self):
        import json, subprocess
        from access import AccessChangeRolledBackError
        from dispatch import failure_result
        secret = 'SENTINEL_PASSWORD_TOKEN_PROVIDER_SECRET'
        cause = subprocess.CalledProcessError(1, ['psql', secret], output=secret.encode(), stderr=secret.encode())
        error = AccessChangeRolledBackError(secret, 'access-listener', cause)
        result = failure_result(error)
        self.assertEqual(result, dict(status='failed', errorType='AccessChangeRolledBackError', errorStage='access-listener', causeType='CalledProcessError'))
        self.assertNotIn(secret, json.dumps(result))
        error.stage = secret
        error.cause_type = secret
        self.assertNotIn(secret, json.dumps(failure_result(error)))


class PostgresReadinessTests(unittest.TestCase):
    def test_delayed_start_is_observed_without_repeating_mutations(self):
        import subprocess
        from access import wait_for_postgres
        clock = [0]
        calls = []
        def run(args, **kwargs):
            calls.append(args)
            if len(calls) < 3:
                raise subprocess.CalledProcessError(1, args)
        def sleep(seconds):
            clock[0] += seconds
        wait_for_postgres('db-1', run, timeout=4, monotonic=lambda: clock[0], sleep=sleep)
        self.assertEqual(len(calls), 3)
        self.assertTrue(all(args[3] == 'pg_isready' for args in calls))
        self.assertEqual(clock[0], 2)

    def test_permanent_failure_has_bounded_deadline_and_no_command_details(self):
        import subprocess
        from access import wait_for_postgres
        clock = [0]
        calls = []
        def run(args, **kwargs):
            calls.append(args)
            raise subprocess.CalledProcessError(1, args, stderr=b'SENTINEL_SECRET')
        def sleep(seconds):
            clock[0] += seconds
        with self.assertRaisesRegex(RuntimeError, '^Database readiness deadline exceeded$'):
            wait_for_postgres('db-1', run, timeout=3, monotonic=lambda: clock[0], sleep=sleep)
        self.assertEqual(clock[0], 3)
        self.assertEqual(len(calls), 3)
