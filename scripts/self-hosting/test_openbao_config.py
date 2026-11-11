import json
import os
import pathlib
import stat
import subprocess
import tempfile
import time
import unittest
import uuid


ROOT = pathlib.Path(__file__).resolve().parents[2]
BUNDLE = ROOT / "packaging" / "server" / "bundle"


class OpenBaoConfigurationTests(unittest.TestCase):
    @unittest.skipUnless(
        os.environ.get("MINICLOUD_TEST_COMPOSE_BINARY"),
        "Set MINICLOUD_TEST_COMPOSE_BINARY to an installed Compose executable.",
    )
    def test_compose_entry_points_keep_administration_private_and_gate_startup(self):
        artifacts = ROOT / ".local/artifacts"
        artifacts.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="compose-model-", dir=artifacts) as directory:
            fixture = pathlib.Path(directory)
            (fixture / "kratos.env").write_text("DSN=postgres://kratos:synthetic@postgres/kratos\n")
            env = os.environ | {
                "API_URL": "https://api.alpha.example",
                "CONSOLE_URL": "https://console.alpha.example",
                "API_HOSTNAME": "api.alpha.example",
                "CONSOLE_HOSTNAME": "console.alpha.example",
                "APP_DOMAIN": "apps.alpha.example",
                "MINICLOUD_SECRETS_DIR": str(fixture),
            }
            for component in ("API", "DASHBOARD", "WORKER", "POSTGRES", "OPENBAO", "KRATOS", "CADDY"):
                env[f"MINICLOUD_{component}_IMAGE"] = f"example.test/{component.lower()}@sha256:{'a' * 64}"
            models = []
            for compose in (ROOT / "compose.self-host.yml", BUNDLE / "compose.yml"):
                result = subprocess.run(
                    [env["MINICLOUD_TEST_COMPOSE_BINARY"], "-f", str(compose), "config", "--format", "json"],
                    env=env, text=True, capture_output=True, check=False,
                )
                self.assertEqual(0, result.returncode, result.stderr)
                services = json.loads(result.stdout)["services"]
                models.append(services)
                self.assertEqual({"caddy"}, {name for name, service in services.items() if service.get("ports")})
                self.assertEqual({80, 443}, {port["target"] for port in services["caddy"]["ports"]})
                self.assertEqual(
                    {"MINICLOUD_API_URL", "MINICLOUD_APP_DOMAIN", "MINICLOUD_SOURCE_URL"},
                    services["dashboard"]["environment"].keys(),
                )
                self.assertEqual("service_healthy", services["worker"]["depends_on"]["api"]["condition"])
                self.assertEqual("service_completed_successfully", services["api"]["depends_on"]["api-migrate"]["condition"])
                self.assertEqual("service_completed_successfully", services["kratos"]["depends_on"]["kratos-migrate"]["condition"])
                self.assertEqual("service_healthy", services["api"]["depends_on"]["openbao-agent"]["condition"])
                for migrator in ("api-migrate", "kratos-migrate"):
                    self.assertEqual("service_completed_successfully", services[migrator]["depends_on"]["database-initialize"]["condition"])
                self.assertIn("/opt/minicloud/openbao-ready.sh", services["openbao-agent"]["healthcheck"]["test"])
                for consumer in ("api", "worker"):
                    token_volume = next(mount for mount in services[consumer]["volumes"] if mount["target"] == "/run/minicloud-openbao")
                    self.assertTrue(token_volume["read_only"])
                    self.assertNotIn("BAO_TOKEN", services[consumer]["environment"])
                    self.assertNotIn("OPENBAO_ROOT_TOKEN_FILE", services[consumer]["environment"])
            self.assertEqual(models[0], models[1])

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
        script = (BUNDLE / "openbao-init.sh").read_text()
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
            initializer = BUNDLE / "openbao-init.sh"

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

    def test_readiness_verifies_the_sink_token_without_disclosing_credentials(self):
        artifact_dir = ROOT / ".local" / "artifacts"
        artifact_dir.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=artifact_dir) as directory:
            fixture = pathlib.Path(directory)
            cli = fixture / "bao"
            cli.write_text(
                "#!/usr/bin/env python3\n"
                "import json, os, pathlib, sys\n"
                "assert os.environ['BAO_TOKEN'] == 'synthetic-service-token'\n"
                "assert os.environ['BAO_ADDR'] == 'https://openbao:8200'\n"
                "assert os.environ['BAO_CACERT'] == '/run/secrets/openbao-ca.crt'\n"
                "pathlib.Path(os.environ['FIXTURE_LOG']).write_text(json.dumps(sys.argv[1:]))\n"
                "print('synthetic-service-token')\n"
                "print('synthetic-service-token', file=sys.stderr)\n"
                "sys.exit(int(os.environ['FIXTURE_EXIT']))\n"
            )
            cli.chmod(0o700)
            token = fixture / "token"
            log = fixture / "argv.json"
            env = os.environ | {
                "PATH": str(fixture) + os.pathsep + os.environ["PATH"],
                "OPENBAO_TOKEN_FILE": str(token),
                "FIXTURE_LOG": str(log),
                "BAO_ADDR": "https://openbao:8200",
                "BAO_CACERT": "/run/secrets/openbao-ca.crt",
                "BAO_TOKEN": "unrelated-inherited-root-token",
            }
            for content, server_exit, expected in [
                (None, 0, False),
                ("", 0, False),
                ("\n", 0, False),
                ("synthetic-service-token\n", 0, True),
                ("synthetic-service-token\n", 2, False),
                ("synthetic-service-token\n", 1, False),
            ]:
                with self.subTest(content=content, server_exit=server_exit):
                    if log.exists():
                        log.unlink()
                    if content is not None:
                        token.write_text(content)
                        token.chmod(0o600)
                    result = subprocess.run(
                        ["/bin/sh", str(BUNDLE / "openbao-ready.sh")],
                        env=env | {"FIXTURE_EXIT": str(server_exit)},
                        capture_output=True, text=True, check=False,
                    )
                    self.assertEqual(expected, result.returncode == 0)
                    self.assertEqual("", result.stdout + result.stderr)
                    if content and content.strip():
                        self.assertEqual(["token", "lookup", "-format=json"], json.loads(log.read_text()))
                    else:
                        self.assertFalse(log.exists())


@unittest.skipUnless(
    os.environ.get("MINICLOUD_TEST_CACHED_OPENBAO") == "1",
    "Set MINICLOUD_TEST_CACHED_OPENBAO=1 for the isolated cached-image TLS lifecycle check.",
)
class OpenBaoLifecycleTests(unittest.TestCase):
    def test_scoped_agent_readiness_seal_unseal_and_persistent_restart(self):
        image = "openbao/openbao:2.2.1"
        subprocess.run(["docker", "image", "inspect", image], check=True, capture_output=True)
        name = f"minicloud-t41-bao-{uuid.uuid4().hex[:12]}"
        volume = f"{name}-data"
        artifacts = ROOT / ".local/artifacts"
        artifacts.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="openbao-lifecycle-", dir=artifacts) as directory:
            fixture = pathlib.Path(directory)
            subprocess.run(
                ["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                 "-keyout", str(fixture / "openbao-server.key"),
                 "-out", str(fixture / "openbao-server.crt"), "-days", "1",
                 "-subj", "/CN=openbao",
                 "-addext", "subjectAltName=DNS:openbao,IP:127.0.0.1"],
                check=True, capture_output=True,
            )
            (fixture / "openbao-ca.crt").write_bytes((fixture / "openbao-server.crt").read_bytes())
            approle = fixture / "approle"
            approle.mkdir(mode=0o700)
            agent = (BUNDLE / "openbao-agent.hcl").read_text()
            agent = agent.replace("https://openbao:8200", "https://127.0.0.1:8200")
            agent = agent.replace("/run/secrets/openbao-role-id", "/fixture/approle/role-id")
            agent = agent.replace("/run/secrets/openbao-secret-id", "/fixture/approle/secret-id")
            (fixture / "agent.hcl").write_text(agent)

            def docker(*args, check=True):
                result = subprocess.run(
                    ["docker", *args], text=True, capture_output=True, timeout=60, check=False,
                )
                if check:
                    self.assertEqual(0, result.returncode, result.stderr)
                return result

            def execute(*args, check=True):
                return docker(
                    "exec", "--env", "BAO_ADDR=https://127.0.0.1:8200",
                    "--env", "BAO_CACERT=/run/secrets/openbao-ca.crt",
                    name, *args, check=check,
                )

            def root(*args):
                return execute(
                    "/bin/sh", "-ec",
                    'BAO_TOKEN=$(cat /fixture/root-token); export BAO_TOKEN; exec bao "$@"',
                    "bao", *args,
                )

            def service(*args, check=True):
                return execute(
                    "/bin/sh", "-ec",
                    'BAO_TOKEN=$(cat /run/minicloud-openbao/token); export BAO_TOKEN; exec bao "$@"',
                    "bao", *args, check=check,
                )

            def ready():
                return execute("/bin/sh", "/bundle/openbao-ready.sh", check=False)

            def await_agent():
                for _ in range(60):
                    if ready().returncode == 0:
                        return
                    time.sleep(0.5)
                self.fail("Scoped OpenBao Agent did not become ready.")

            def start_agent():
                docker("exec", "--detach", name, "bao", "agent", "-config=/fixture/agent.hcl")
                await_agent()

            def unseal():
                execute("bao", "write", "sys/unseal", "key=@/fixture/unseal-key")

            docker("volume", "create", volume)
            try:
                docker(
                    "run", "--detach", "--pull=never", "--name", name,
                    "--network", "none", "--memory", "256m", "--cpus", "0.5",
                    "--pids-limit", "128",
                    "--env", "BAO_SKIP_DROP_ROOT=true", "--env", "SKIP_CHOWN=true",
                    "--mount", f"type=volume,source={volume},target=/openbao/data",
                    "--mount", f"type=bind,source={fixture},target=/fixture",
                    "--mount", f"type=bind,source={fixture},target=/run/secrets,readonly",
                    "--mount", f"type=bind,source={BUNDLE},target=/bundle,readonly",
                    "--tmpfs", "/run/minicloud-openbao:rw,nosuid,size=1m,mode=0750",
                    image, "server", "-config=/bundle/openbao.hcl",
                )
                for _ in range(60):
                    status = execute("bao", "status", "-format=json", check=False)
                    if status.returncode == 2:
                        break
                    time.sleep(0.5)
                else:
                    self.fail("Isolated OpenBao did not reach sealed state.")
                initialized = json.loads(execute(
                    "bao", "operator", "init", "-key-shares=1", "-key-threshold=1", "-format=json",
                ).stdout)
                for filename, value in [
                    ("unseal-key", initialized["unseal_keys_b64"][0]),
                    ("root-token", initialized["root_token"]),
                ]:
                    (fixture / filename).write_text(value)
                    (fixture / filename).chmod(0o600)
                unseal()
                for _ in range(2):
                    execute(
                        "/bin/sh", "-ec",
                        "export OPENBAO_ROOT_TOKEN_FILE=/fixture/root-token "
                        "OPENBAO_ROLE_ID_FILE=/fixture/approle/role-id "
                        "OPENBAO_SECRET_ID_FILE=/fixture/approle/secret-id "
                        "OPENBAO_POLICY_FILE=/bundle/openbao-policy.hcl; "
                        "exec /bin/sh /bundle/openbao-init.sh",
                    )
                start_agent()
                lookup = service("token", "lookup", "-format=json")
                data = json.loads(lookup.stdout)["data"]
                self.assertTrue(data["renewable"])
                self.assertEqual(86400, data["period"])
                self.assertIn("minicloud", data["policies"])
                self.assertNotIn("root", data["policies"])
                denied = service("read", "sys/auth", check=False)
                self.assertNotEqual(0, denied.returncode)
                service("kv", "put", "minicloud-apps/env/production/orgs/test/apps/test/services/test/key", "value=retained")
                root("operator", "seal")
                self.assertEqual(0, execute(
                    "/bin/sh", "-ec", "test -s /run/minicloud-openbao/token",
                ).returncode)
                sealed = ready()
                self.assertNotEqual(0, sealed.returncode)
                self.assertEqual("", sealed.stdout + sealed.stderr)
                unseal()
                await_agent()
                root("write", "auth/token/revoke", "token=@/run/minicloud-openbao/token")
                self.assertNotEqual(0, ready().returncode)
                docker("restart", name)
                for _ in range(60):
                    if execute("bao", "status", check=False).returncode == 2:
                        break
                    time.sleep(0.5)
                else:
                    self.fail("Restarted OpenBao did not reach sealed state.")
                self.assertNotEqual(0, ready().returncode)
                unseal()
                start_agent()
                self.assertEqual("retained", service(
                    "kv", "get", "-field=value",
                    "minicloud-apps/env/production/orgs/test/apps/test/services/test/key",
                ).stdout.strip())
            finally:
                docker("rm", "--force", name, check=False)
                docker("volume", "rm", volume, check=False)


if __name__ == "__main__":
    unittest.main()
