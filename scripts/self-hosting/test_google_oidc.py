import json
import os
import unittest
from unittest import mock

import test_server_installer as fixtures


installer = fixtures.installer


class GoogleOidcTests(unittest.TestCase):
    setUp = fixtures.InstallerTests.setUp
    write = staticmethod(fixtures.InstallerTests.write)
    fixture_chain = fixtures.InstallerTests.fixture_chain
    subject = fixtures.InstallerTests.subject

    def configure(self, option):
        config = json.loads(self.config.read_text())
        config["googleOidc"] = option
        self.config.write_text(json.dumps(config))

    def enable(self):
        path = self.base / "google.json"
        credentials = {
            "clientId": "synthetic-client.apps.googleusercontent.com",
            "clientSecret": "synthetic-not-a-google-secret",
        }
        self.write(path, json.dumps(credentials).encode())
        self.configure({"enabled": True, "credentialsFile": str(path)})
        return path, credentials

    def test_password_only_default_and_explicit_false_keep_original_credentials(self):
        for option in (None, {"enabled": False}):
            with self.subTest(option=option):
                if option is not None:
                    self.configure(option)
                subject = self.subject()
                self.assertIsNone(subject.google_provider)
                subject.state = {"installationId": "synthetic"}
                self.assertEqual("false", subject.environment()["MINICLOUD_GOOGLE_OIDC_ENABLED"])
        subject.initialize_state()
        directory = subject.root / "secrets"
        directory.mkdir(mode=0o700)
        self.write(directory / "kratos-db-password", b"synthetic-database-password")
        original = (b"DSN=postgres://kratos:synthetic-database-password@postgres:5432/kratos?sslmode=disable\n"
                    b"SECRETS_COOKIE=" + b"a" * 64 + b"\nSECRETS_CIPHER=" + b"b" * 32 + b"\n")
        self.write(directory / "kratos.env", original)
        subject.validate_kratos_credentials()
        self.assertEqual(original, (directory / "kratos.env").read_bytes())
        with (directory / "kratos.env").open("ab") as output:
            output.write(b"SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS=[]\n")
        with self.assertRaisesRegex(installer.InstallError, "Kratos credentials"):
            subject.validate_kratos_credentials()

    def test_enabled_install_imports_only_fixed_google_provider_and_replays_unchanged(self):
        path, credentials = self.enable()
        subject = self.subject()
        subject.execute()
        secret = subject.root / "secrets/kratos.env"
        original = secret.read_bytes()
        values = dict(line.split("=", 1) for line in original.decode().splitlines())
        provider, = json.loads(values["SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS"])
        self.assertEqual({
            "id": "google", "provider": "google", "client_id": credentials["clientId"],
            "client_secret": credentials["clientSecret"],
            "scope": ["openid", "email", "profile"],
            "mapper_url": "file:///etc/config/google-oidc.mapper.jsonnet",
        }, provider)
        self.assertEqual(0o600, secret.stat().st_mode & 0o777)
        command, env = subject.compose_invocation("up", "-d", "api", "kratos")
        self.assertEqual("true", env["MINICLOUD_GOOGLE_OIDC_ENABLED"])
        self.assertNotIn(credentials["clientSecret"], json.dumps([command, env, subject.state]))
        self.assertEqual(str(path), subject.state["configuration"]["googleOidc"]["credentialsFile"])
        resumed = self.subject()
        resumed.execute()
        self.assertEqual(original, secret.read_bytes())
        # Upgrade's previous-manifest path uses this same persisted-state validator.
        resumed.load_state(manifest=self.manifest)
        self.assertEqual(original, secret.read_bytes())

    def test_unknown_options_and_missing_or_inactive_credentials_fail_before_mutation(self):
        for option in (
            {}, {"enabled": "true"}, {"enabled": True},
            {"enabled": False, "credentialsFile": "/operator/google.json"},
            {"enabled": True, "credentialsFile": "relative.json"},
            {"enabled": False, "qualified": True},
            {"enabled": False, "clientSecret": "synthetic-secret"},
        ):
            with self.subTest(option=option):
                self.configure(option)
                with self.assertRaises(installer.InstallError):
                    self.subject()
                self.assertFalse(self.args.root.exists())

    def test_credential_input_rejects_unsafe_shapes_permissions_symlinks_and_output_paths(self):
        path, credentials = self.enable()
        for data in (
            '{"clientId":"synthetic.apps.googleusercontent.com","clientId":"duplicate","clientSecret":"synthetic"}',
            json.dumps(credentials | {"provider": "other"}),
            json.dumps(credentials | {"clientSecret": "synthetic\nINJECTED=true"}),
            json.dumps(credentials | {"clientSecret": "synthetic${INTERPOLATED}"}),
        ):
            with self.subTest(data=data):
                path.write_text(data)
                with self.assertRaises(installer.InstallError):
                    self.subject()
        path.write_text(json.dumps(credentials))
        os.chmod(path, 0o644)
        with self.assertRaises(installer.InstallError):
            self.subject()
        os.chmod(path, 0o600)
        link = self.base / "google-link.json"
        link.symlink_to(path)
        for target in (link, self.args.root / "google.json", self.recovery / "google.json",
                       self.assets / "google.json"):
            self.configure({"enabled": True, "credentialsFile": str(target)})
            with self.assertRaises(installer.InstallError):
                self.subject()
        self.assertFalse(self.args.root.exists())

    def test_resume_and_upgrade_validation_reject_changes_without_rewriting_secrets(self):
        path, credentials = self.enable()
        subject = self.subject()
        subject.execute()
        secret = subject.root / "secrets/kratos.env"
        original = secret.read_bytes()
        path.write_text(json.dumps(credentials | {"clientSecret": "synthetic-replacement"}))
        for manifest in (None, self.manifest):
            with self.subTest(manifest=manifest):
                resumed = self.subject()
                with self.assertRaisesRegex(installer.InstallError, "Kratos credentials"):
                    resumed.load_state(manifest=manifest)
                self.assertEqual(original, secret.read_bytes())
        path.write_text(json.dumps(credentials))
        for extra in (
            b"SELFSERVICE_METHODS_OIDC_ENABLED=true\n",
            b"SELFSERVICE_METHODS_OIDC_CONFIG_PROVIDERS=[]\n",
        ):
            secret.write_bytes(original + extra)
            resumed = self.subject()
            with mock.patch.object(self.host, "tools", side_effect=AssertionError("No Docker call")):
                with self.assertRaises(installer.InstallError):
                    resumed.execute()
            self.assertEqual(original + extra, secret.read_bytes())
        secret.write_bytes(original)
        secret.unlink()
        with self.assertRaisesRegex(installer.InstallError, "Established credential missing"):
            self.subject().execute()
        self.assertFalse(secret.exists())


if __name__ == "__main__":
    unittest.main()
