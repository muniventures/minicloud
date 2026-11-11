# SPDX-License-Identifier: MPL-2.0
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor


ROOT = Path(__file__).resolve().parents[2]
INITIALIZER = ROOT / "packaging/server/bundle/init-databases.sh"


class DatabaseInitializerTests(unittest.TestCase):
    def setUp(self):
        artifacts = ROOT / ".local/artifacts"
        artifacts.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="database-initializer-", dir=artifacts)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.secrets = self.root / "secrets"
        self.secrets.mkdir()
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.sql = self.root / "sql.txt"
        self.log = self.root / "psql.log"
        psql = self.bin / "psql"
        psql.write_text(
            "#!/bin/sh\n"
            "cat > \"$CAPTURE_SQL\"\n"
            "printf '%s\\n' \"PGPASSWORD=$PGPASSWORD\" > \"$CAPTURE_LOG\"\n"
        )
        psql.chmod(0o700)

    def run_initializer(self, values):
        environment = dict(os.environ)
        environment.update({
            "PATH": f"{self.bin}:{environment.get('PATH', '')}",
            "CAPTURE_SQL": str(self.sql),
            "CAPTURE_LOG": str(self.log),
            "MINICLOUD_POSTGRES_ADMIN_PASSWORD_FILE": str(self.secret("admin", "synthetic-admin")),
            "MINICLOUD_APP_DB_PASSWORD_FILE": str(self.secret("app", values.get("app", "synthetic-app"))),
            "MINICLOUD_KRATOS_DB_PASSWORD_FILE": str(self.secret("kratos", values.get("kratos", "synthetic-kratos"))),
        })
        return subprocess.run(
            ["/bin/sh", str(INITIALIZER)], env=environment, text=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
        )

    def secret(self, name, value):
        path = self.secrets / name
        path.write_text(value)
        path.chmod(0o600)
        return path

    def test_creates_separate_roles_and_databases_idempotently_without_printing_secrets(self):
        result = self.run_initializer({})
        self.assertEqual(0, result.returncode, result.stderr)
        sql = self.sql.read_text()
        self.assertIn("CREATE ROLE minicloud LOGIN PASSWORD", sql)
        self.assertIn("CREATE ROLE kratos LOGIN PASSWORD", sql)
        self.assertIn("CREATE DATABASE minicloud OWNER minicloud", sql)
        self.assertIn("CREATE DATABASE kratos OWNER kratos", sql)
        self.assertIn("NOT EXISTS", sql)
        self.assertIn("refusing to alter it", sql)
        self.assertNotIn("synthetic-admin", result.stdout + result.stderr)
        self.assertNotIn("synthetic-app", result.stdout + result.stderr)
        self.assertNotIn("synthetic-kratos", result.stdout + result.stderr)
        self.assertEqual("PGPASSWORD=synthetic-admin\n", self.log.read_text())
        self.assertNotIn("synthetic-app", self.log.read_text())
        self.assertNotIn("synthetic-kratos", self.log.read_text())

    def test_missing_or_empty_protected_credential_fails_closed(self):
        missing = dict(os.environ)
        missing["PATH"] = f"{self.bin}:{missing.get('PATH', '')}"
        missing["MINICLOUD_POSTGRES_ADMIN_PASSWORD_FILE"] = str(self.secrets / "missing")
        missing["MINICLOUD_APP_DB_PASSWORD_FILE"] = str(self.secret("app", "app"))
        missing["MINICLOUD_KRATOS_DB_PASSWORD_FILE"] = str(self.secret("kratos", "kratos"))
        result = subprocess.run(
            ["/bin/sh", str(INITIALIZER)], env=missing, text=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("app", result.stderr)
        self.assertNotIn("kratos", result.stderr)
        self.assertFalse(self.sql.exists())

        self.secret("admin", "\n")
        result = self.run_initializer({"app": "", "kratos": "kratos"})
        self.assertNotEqual(0, result.returncode)
        self.assertFalse(self.sql.exists())

    def test_role_passwords_are_supplied_as_quoted_psql_variables(self):
        app = "contains ' quote ; and $dollar"
        kratos = "another ' pass"
        result = self.run_initializer({"app": app, "kratos": kratos})
        self.assertEqual(0, result.returncode, result.stderr)
        sql = self.sql.read_text()
        self.assertIn("\\getenv app_password MINICLOUD_APP_DB_PASSWORD", sql)
        self.assertIn("\\getenv kratos_password MINICLOUD_KRATOS_DB_PASSWORD", sql)
        self.assertIn(":'app_password'", sql)
        self.assertIn(":'kratos_password'", sql)
        self.assertNotIn(app, sql)
        self.assertNotIn(kratos, sql)
        self.assertNotIn(app, result.stdout + result.stderr)
        self.assertNotIn(kratos, result.stdout + result.stderr)

    def test_initialization_locks_and_validates_existing_state_before_creation(self):
        result = self.run_initializer({})
        self.assertEqual(0, result.returncode, result.stderr)
        sql = self.sql.read_text()
        lock = sql.index("SELECT pg_advisory_lock(")
        validation = sql.index("DO $$")
        creation = sql.index("SELECT format('CREATE ROLE")
        self.assertLess(lock, validation)
        self.assertLess(validation, creation)
        self.assertIn("pg_auth_members", sql)
        self.assertIn("rolsuper", sql)
        self.assertIn("rolbypassrls", sql)
        self.assertNotIn("ALTER ROLE", sql)
        self.assertNotIn("ALTER DATABASE", sql)


@unittest.skipUnless(
    os.environ.get("MINICLOUD_TEST_CACHED_POSTGRES") == "1",
    "Set MINICLOUD_TEST_CACHED_POSTGRES=1 for the isolated cached-image Docker check.",
)
class DatabaseInitializerPostgresTests(unittest.TestCase):
    def test_concurrent_replay_interruption_and_existing_volume_preserve_state(self):
        image = "postgres:16-alpine"
        subprocess.run(["docker", "image", "inspect", image], check=True, capture_output=True)
        name = f"minicloud-t41-db-{uuid.uuid4().hex[:12]}"
        volume = f"{name}-data"
        artifacts = ROOT / ".local/artifacts"
        artifacts.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix="database-postgres-", dir=artifacts) as directory:
            fixture = Path(directory)
            for role in ("admin", "app", "kratos"):
                credential = fixture / role
                credential.write_text(f"synthetic-{role}-password")
                credential.chmod(0o600)

            def docker(*args, check=True):
                result = subprocess.run(
                    ["docker", *args], capture_output=True, text=True, timeout=60, check=False,
                )
                if check:
                    self.assertEqual(0, result.returncode, result.stderr)
                return result

            def start():
                docker(
                    "run", "--detach", "--pull=never", "--name", name,
                    "--network", "none", "--memory", "256m", "--cpus", "0.5",
                    "--pids-limit", "128",
                    "--mount", f"type=volume,source={volume},target=/var/lib/postgresql/data",
                    "--mount", f"type=bind,source={fixture},target=/fixture,readonly",
                    "--mount", f"type=bind,source={INITIALIZER},target=/init-databases.sh,readonly",
                    "--env", "POSTGRES_PASSWORD_FILE=/fixture/admin",
                    image, "-c", "shared_buffers=16MB", "-c", "max_connections=20",
                )
                for _ in range(60):
                    if docker("exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", check=False).returncode == 0:
                        return
                    time.sleep(0.5)
                self.fail("Isolated PostgreSQL did not become ready.")

            def sql(statement, database="postgres"):
                return docker(
                    "exec", name, "psql", "-U", "postgres", "-d", database,
                    "-v", "ON_ERROR_STOP=1", "-Atc", statement,
                ).stdout.strip()

            def initialize():
                return docker(
                    "exec",
                    "--env", "PGUSER=postgres", "--env", "PGDATABASE=postgres",
                    "--env", "MINICLOUD_POSTGRES_ADMIN_PASSWORD_FILE=/fixture/admin",
                    "--env", "MINICLOUD_APP_DB_PASSWORD_FILE=/fixture/app",
                    "--env", "MINICLOUD_KRATOS_DB_PASSWORD_FILE=/fixture/kratos",
                    name, "/bin/sh", "/init-databases.sh", check=False,
                )

            docker("volume", "create", volume)
            try:
                start()
                # Resume a process interrupted after creating only the first role.
                docker(
                    "exec", name, "/bin/sh", "-ec",
                    "APP_PASSWORD=$(cat /fixture/app); export APP_PASSWORD; "
                    "psql -U postgres -v ON_ERROR_STOP=1 <<'SQL'\n"
                    "\\getenv app_password APP_PASSWORD\n"
                    "SELECT format('CREATE ROLE minicloud LOGIN PASSWORD %L', :'app_password') \\gexec\n"
                    "SQL",
                )
                with ThreadPoolExecutor(max_workers=3) as executor:
                    results = list(executor.map(lambda _: initialize(), range(3)))
                for result in results:
                    self.assertEqual(0, result.returncode, result.stderr)
                for user, credential in (("minicloud", "app"), ("kratos", "kratos")):
                    authenticated = docker(
                        "exec", name, "/bin/sh", "-ec",
                        'PGPASSWORD=$(cat "$1"); export PGPASSWORD; '
                        'exec psql -h 127.0.0.1 -U "$2" -d "$2" -Atc "SELECT current_user"',
                        "psql", f"/fixture/{credential}", user,
                    )
                    self.assertEqual(user, authenticated.stdout.strip())

                sql("CREATE TABLE t41_marker (value text); INSERT INTO t41_marker VALUES ('retained')", "minicloud")
                self.assertEqual("minicloud|minicloud\nkratos|kratos", sql(
                    "SELECT datname, pg_get_userbyid(datdba) FROM pg_database "
                    "WHERE datname IN ('minicloud', 'kratos') ORDER BY datname DESC"
                ))
                docker("rm", "--force", name)
                start()
                result = initialize()
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual("retained", sql("SELECT value FROM t41_marker", "minicloud"))

                sql("ALTER DATABASE kratos OWNER TO postgres")
                result = initialize()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("unexpected owner", result.stderr)
                self.assertEqual("postgres", sql("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname='kratos'"))
                sql("ALTER DATABASE kratos OWNER TO kratos; ALTER ROLE minicloud SUPERUSER")
                result = initialize()
                self.assertNotEqual(0, result.returncode)
                self.assertIn("unexpected privileges", result.stderr)
                self.assertEqual("retained", sql("SELECT value FROM t41_marker", "minicloud"))
            finally:
                docker("rm", "--force", name, check=False)
                docker("volume", "rm", volume, check=False)


if __name__ == "__main__":
    unittest.main()
