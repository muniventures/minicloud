import json
import os
import pathlib
import stat
import subprocess
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[2]
BUNDLE = ROOT / "packaging" / "server" / "bundle"


class OpenBaoConfigurationTests(unittest.TestCase):
    def test_server_uses_persistent_tls_storage_without_dev_mode(self):
        config = (BUNDLE / "openbao.hcl").read_text()
        self.assertIn('storage "file"', config)
        self.assertIn('path = "/openbao/data"', config)
        self.assertIn('tls_cert_file', config)
        self.assertIn('tls_key_file', config)
        self.assertIn('tls_min_version = "tls13"', config)
        self.assertNotIn("dev", config.lower())
        handoff = " ".join((BUNDLE / "openbao-handoff.md").read_text().split())
        self.assertIn("do not publish either listener port", handoff)
        self.assertIn("separately backed up", handoff)

    def test_agent_uses_approle_and_renewable_shared_file_sink(self):
        config = (BUNDLE / "openbao-agent.hcl").read_text()
        self.assertIn('method "approle"', config)
        self.assertIn("secret_id_file_path", config)
        self.assertIn("remove_secret_id_file_after_reading = false", config)
        self.assertIn('path = "/run/minicloud-openbao/token"', config)
        self.assertIn("mode = 0640", config)
        self.assertIn("tls_skip_verify = false", config)

    def test_initializer_is_idempotent_and_keeps_bootstrap_secrets_off_cli(self):
        script = (ROOT / "scripts" / "self-hosting" / "openbao-init.sh").read_text()
        policy = (BUNDLE / "openbao-policy.hcl").read_text()
        self.assertNotIn("|| true", script)
        self.assertIn("OPENBAO_ROOT_TOKEN_FILE", script)
        self.assertNotIn("bao login", script)
        self.assertIn("bao write -field=secret_id", script)
        self.assertIn("secret_id=$(bao write", script)
        self.assertIn("token_period=24h", script)
        self.assertIn("minicloud-apps/data/env/+/orgs/+/apps/+/services/+/+", policy)
        self.assertIn("minicloud-apps/data/env/+/orgs/+/providers/+/credentials/+", policy)
        self.assertNotIn('path "minicloud-apps/"', policy)

    def test_initializer_is_idempotent_with_synthetic_openbao_cli(self):
        artifact_dir = ROOT / ".local" / "artifacts"
        artifact_dir.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=artifact_dir) as directory:
            fixture = pathlib.Path(directory)
            fake_bin = fixture / "bin"
            fake_bin.mkdir()
            fake_cli = fake_bin / "bao"
            fake_cli.write_text(
                """#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
state = pathlib.Path(os.environ["FIXTURE_STATE"])
with open(os.environ["FIXTURE_LOG"], "a") as log:
    log.write(json.dumps(args) + "\\n")
if args == ["status"]:
    raise SystemExit(0)
if args[:3] == ["secrets", "list", "-format=json"]:
    print(json.dumps({"minicloud-apps/": {}} if (state / "kv").exists() else {}))
elif args[:2] == ["secrets", "enable"]:
    (state / "kv").touch()
elif args[:2] == ["auth", "list"]:
    print(json.dumps({"approle/": {}} if (state / "approle").exists() else {}))
elif args[:2] == ["auth", "enable"]:
    (state / "approle").touch()
elif args[:2] == ["read", "-format=json"] and "sys/auth/approle" in args:
    print(json.dumps({"data": {"type": "approle"}}))
elif args[:2] == ["read", "-format=json"]:
    print(json.dumps({"data": {"type": "kv", "options": {"version": "2"}}}))
elif args[:2] == ["read", "-field=role_id"]:
    print("synthetic-role-id")
elif args[:2] == ["write", "-field=secret_id"]:
    print("synthetic-secret-id")
elif args[:2] == ["write", "auth/approle/role/minicloud"]:
    pass
elif args[:2] == ["policy", "write"]:
    pass
else:
    print("unexpected synthetic bao invocation", file=sys.stderr)
    raise SystemExit(2)
"""
            )
            fake_cli.chmod(0o755)
            state = fixture / "state"
            state.mkdir()
            root_token = fixture / "root-token"
            root_token.write_text("synthetic-bootstrap-token\n")
            role_file = fixture / "role-id"
            secret_file = fixture / "secret-id"
            log_file = fixture / "argv.jsonl"
            env = os.environ | {
                "PATH": str(fake_bin) + os.pathsep + os.environ["PATH"],
                "FIXTURE_STATE": str(state),
                "FIXTURE_LOG": str(log_file),
                "OPENBAO_ROOT_TOKEN_FILE": str(root_token),
                "OPENBAO_ROLE_ID_FILE": str(role_file),
                "OPENBAO_SECRET_ID_FILE": str(secret_file),
                "OPENBAO_POLICY_FILE": str(BUNDLE / "openbao-policy.hcl"),
                "BAO_ADDR": "https://bao.synthetic",
            }
            initializer = ROOT / "scripts" / "self-hosting" / "openbao-init.sh"

            for _ in range(2):
                result = subprocess.run(
                    [str(initializer)], env=env, text=True, capture_output=True, check=False
                )
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertNotIn("synthetic-bootstrap-token", result.stdout + result.stderr)
                self.assertNotIn("synthetic-secret-id", result.stdout + result.stderr)

            self.assertEqual("synthetic-role-id\n", role_file.read_text())
            self.assertEqual("synthetic-secret-id\n", secret_file.read_text())
            self.assertEqual(0o600, stat.S_IMODE(role_file.stat().st_mode))
            self.assertEqual(0o600, stat.S_IMODE(secret_file.stat().st_mode))
            invocations = [json.loads(line) for line in log_file.read_text().splitlines()]
            self.assertEqual(1, sum(args[:2] == ["write", "-field=secret_id"] for args in invocations))
            self.assertNotIn("synthetic-bootstrap-token", log_file.read_text())


if __name__ == "__main__":
    unittest.main()
