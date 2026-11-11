# SPDX-License-Identifier: MPL-2.0
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


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


if __name__ == "__main__":
    unittest.main()
