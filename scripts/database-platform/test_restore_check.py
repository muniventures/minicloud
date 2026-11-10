import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent))
import worker

spec = importlib.util.spec_from_file_location('restore_check', Path(__file__).with_name('restore-check.py'))
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


class Fixture:
    key = '0123456789abcdef' * 4
    plaintext = b'PGDMP-synthetic-encrypted-backup-fixture'

    def __init__(self, directory):
        self.directory = Path(directory)
        self.keyfile = self.directory / 'fixture-key'
        checker.private_file(self.keyfile, self.key.encode())
        plain = self.directory / 'fixture.dump'
        checker.private_file(plain, self.plaintext)
        self.cipher = self.directory / 'database.dump.enc'
        worker.crypt(plain, self.cipher, self.keyfile)
        self.payload = dict(version=1, backupId='20261108T120000Z-aabbccdd', environment='staging', databaseId='db_fixture',
                            createdAt='2026-11-08T12:00:00+00:00', database='fixture',
                            ciphertextSha256=worker.digest(self.cipher), ciphertextBytes=self.cipher.stat().st_size,
                            format='pg_dump-custom', postgresMajor=16)
        self.write_manifest()
        plain.unlink()

    def write_manifest(self, signature_key=None):
        manifest = self.directory / 'fixture-manifest.json'
        manifest.write_text(json.dumps(dict(payload=self.payload, hmacSha256=worker.signature(self.payload, signature_key or self.key))))
        worker.crypt(manifest, self.directory / 'manifest.json.enc', self.keyfile)
        manifest.unlink()


class FakeDocker:
    def __init__(self, fail_restore=False):
        self.calls = []
        self.ownership = None
        self.name = None
        self.dump_path = None
        self.fail_restore = fail_restore

    def __call__(self, args, **kwargs):
        self.calls.append(args)
        if args[:2] == ['docker', 'run']:
            self.name = args[args.index('--name') + 1]
            self.ownership = args[args.index('--label') + 1].split('=', 1)[1]
            assert 'BACKUP_ENCRYPTION_KEY' not in kwargs['env']
            return 'a' * 64
        if args[:2] == ['docker', 'inspect']:
            return self.ownership
        if args[:2] == ['docker', 'rm']:
            return self.name
        if '/proc/1/comm' in args:
            return 'postgres'
        if 'pg_isready' in args:
            return 'accepting connections'
        if 'pg_restore' in args:
            self.dump_path = kwargs['stdin'].name
            assert kwargs['stdin'].read() == Fixture.plaintext
            if self.fail_restore:
                raise checker.RestoreCheckError('Synthetic restore failure')
            return ''
        if 'psql' in args:
            sql = args[-1]
            if 'information_schema.tables' in sql:
                return '3'
            if 'to_regclass' in sql:
                return 'present'
            if 'json_agg' in sql:
                return '["00000000-0000-0000-0000-000000000001"]'
            if '__EFMigrationsHistory' in sql:
                return '4'
            if 'count(*)' in sql:
                return '1'
        raise AssertionError('Unexpected command ' + repr(args))


class CryptoAndRestoreTests(unittest.TestCase):
    def test_worker_crypto_roundtrip_and_signed_manifest(self):
        with tempfile.TemporaryDirectory() as directory, tempfile.TemporaryDirectory() as work:
            fixture = Fixture(directory)
            payload, cipher, keyfile = checker.verify_backup(directory, fixture.key, Path(work))
            self.assertEqual(payload['backupId'], fixture.payload['backupId'])
            self.assertNotEqual(cipher, fixture.cipher)
            target = Path(work) / 'plain'
            checker.private_file(target)
            worker.crypt(cipher, target, keyfile, decrypt=True)
            self.assertEqual(target.read_bytes(), fixture.plaintext)
            self.assertEqual(keyfile.stat().st_mode & 0o777, 0o600)
            self.assertEqual(target.stat().st_mode & 0o777, 0o600)

    def test_wrong_key_or_signed_manifest_tampering_never_decrypts_dump_or_calls_docker(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(directory)
            for key, change in ((fixture.key, True), ('other-secret-key-' * 4, False)):
                if change:
                    fixture.write_manifest(signature_key='not-the-authentication-key')
                docker = FakeDocker()
                decrypted = []
                original = checker.crypt

                def observe(source, *args, **kwargs):
                    decrypted.append(Path(source).name)
                    return original(source, *args, **kwargs)

                with patch.object(checker, 'crypt', side_effect=observe):
                    with self.assertRaises(Exception):
                        checker.restore_check(directory, key, docker)
                self.assertEqual(decrypted, ['manifest.json.enc'])
                self.assertEqual(docker.calls, [])

    def test_ciphertext_corruption_fails_before_dump_decryption(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(directory)
            damaged = bytearray(fixture.cipher.read_bytes())
            damaged[-1] ^= 1
            fixture.cipher.write_bytes(damaged)
            docker = FakeDocker()
            decrypted = []
            original = checker.crypt

            def observe(source, *args, **kwargs):
                decrypted.append(Path(source).name)
                return original(source, *args, **kwargs)

            with patch.object(checker, 'crypt', side_effect=observe):
                with self.assertRaises(checker.RestoreCheckError):
                    checker.restore_check(directory, fixture.key, docker)
            self.assertEqual(decrypted, ['manifest.json.enc'])
            self.assertEqual(docker.calls, [])

    def test_restore_is_new_network_isolated_and_cleans_plaintext_and_anonymous_volumes(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(directory)
            docker = FakeDocker()
            result = checker.restore_check(directory, fixture.key, docker)
            self.assertEqual(result['tableCount'], 3)
            create = docker.calls[0]
            self.assertEqual(create[create.index('--network') + 1], 'none')
            self.assertNotIn('--publish', create)
            self.assertNotIn('-p', create)
            self.assertNotIn('--volume', create)
            self.assertEqual(create[-1], 'postgres:16-alpine')
            self.assertIn(['docker', 'rm', '--force', '--volumes', docker.name], docker.calls)
            self.assertFalse(Path(docker.dump_path).exists())
            self.assertTrue(fixture.cipher.exists())

    def test_failed_restore_still_cleans_only_its_owned_container_and_plaintext(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Fixture(directory)
            docker = FakeDocker(fail_restore=True)
            with self.assertRaises(checker.RestoreCheckError):
                checker.restore_check(directory, fixture.key, docker)
            self.assertIn(['docker', 'rm', '--force', '--volumes', docker.name], docker.calls)
            self.assertFalse(Path(docker.dump_path).exists())

    def test_symlink_backup_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory, tempfile.TemporaryDirectory() as work:
            fixture = Fixture(directory)
            target = fixture.cipher.with_name('ciphertext-real')
            fixture.cipher.rename(target)
            fixture.cipher.symlink_to(target)
            with self.assertRaises(checker.RestoreCheckError):
                checker.verify_backup(directory, fixture.key, Path(work))


class ConnectionParsingTests(unittest.TestCase):
    def test_url_decodes_credentials_database_and_sets_default_port(self):
        with patch.dict(os.environ, {}, clear=True):
            env = worker.postgres_env('postgresql://user%40tenant:pa%3Dss%3Bword@db.example:5433/site%20db')
            self.assertEqual((env['PGHOST'], env['PGPORT'], env['PGUSER'], env['PGPASSWORD'], env['PGDATABASE']),
                             ('db.example', '5433', 'user@tenant', 'pa=ss;word', 'site db'))
            self.assertEqual(worker.postgres_env('postgres://user:password@db/app')['PGPORT'], '5432')

    def test_minicloud_connection_string_preserves_equals_inside_password(self):
        with patch.dict(os.environ, {}, clear=True):
            env = worker.postgres_env('Host=db;Port=5432;Database=minicloud;Username=minicloud;Password=secret=with=equals')
            self.assertEqual(env['PGPASSWORD'], 'secret=with=equals')
            self.assertEqual(env['PGCONNECT_TIMEOUT'], '15')

    def test_invalid_configuration_cannot_inherit_a_different_ambient_database(self):
        with patch.dict(os.environ, {'PGHOST': 'other-production', 'PGPORT': '7777', 'PGDATABASE': 'other-db',
                                     'PGUSER': 'other-user', 'PGPASSWORD': 'other-secret'}, clear=True):
            for value in ('', 'Host=db;', 'Host=db;Database=app;Username=user;Password='):
                with self.subTest(value=value), self.assertRaises(ValueError):
                    worker.postgres_env(value)
            env = worker.postgres_env('Host=db;Database=app;Username=user;Password=password')
            self.assertEqual(env['PGPORT'], '5432')


if __name__ == '__main__':
    unittest.main()
