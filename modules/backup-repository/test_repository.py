from concurrent.futures import ThreadPoolExecutor
import hashlib
import http.client
import io
import json
from pathlib import Path
import socket
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest

from server import Rejected, Repository, Server, Settings


TOKEN_STAGE = "fixture-stage-" + "a" * 48
TOKEN_PROD = "fixture-prod-" + "b" * 48
APP_STAGE = "db_stagefixture01"
APP_PROD = "db_prodfixture01"
PATH_STAGE = f"/v1/staging/{APP_STAGE}/backup20261108/database.dump.enc"
PATH_PROD = f"/v1/production/{APP_PROD}/backup20261108/database.dump.enc"


class RepositoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.settings = Settings(self.directory.name,
            [{"environment": "staging", "databaseId": APP_STAGE, "token": TOKEN_STAGE},
             {"environment": "production", "databaseId": APP_PROD, "token": TOKEN_PROD}],
            "fixture-repository", max_bytes=8192, reserve_bytes=1024, quota_bytes=65536,
            trusted_proxies=["127.0.0.1/32"], workers=4, request_timeout=1)
        # Injected mount predicate is synthetic test state, not provider qualification.
        self.mounted = True
        self.repository = Repository(self.settings, mount_check=lambda _: self.mounted,
                                     disk_usage=lambda _: SimpleNamespace(free=1024**3))
        self.server = Server(("127.0.0.1", 0), self.repository)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def test_empty_repository_starts_ready_but_admits_no_source(self):
        empty = Settings(self.directory.name, [], "fixture-repository", trusted_proxies=["127.0.0.1/32"])
        self.settings.bindings = empty.bindings
        self.settings.policies = empty.policies
        self.repository.ready()
        self.assertEqual(self.request("PUT", PATH_STAGE, b"ciphertext")[0], 401)

    def test_disabled_source_rejects_new_upload_but_allows_existing_download(self):
        self.assertEqual(self.request("PUT", PATH_STAGE, b"ciphertext")[0], 201)
        self.settings.policies[("staging", APP_STAGE)]["enabled"] = False
        self.assertEqual(self.request("GET", PATH_STAGE)[0], 200)
        self.assertEqual(self.request("PUT", PATH_STAGE.replace("backup20261108", "otherbackup001"), b"ciphertext")[0], 403)

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()
        self.directory.cleanup()

    def request(self, method, path=PATH_STAGE, body=None, *, token=TOKEN_STAGE, headers=None):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=4)
        supplied = {"Authorization": "Bearer " + token, "X-Forwarded-Proto": "https"}
        if body is not None:
            supplied["Content-Length"] = str(len(body))
            supplied["X-Ciphertext-SHA256"] = hashlib.sha256(body).hexdigest()
        supplied.update(headers or {})
        connection.request(method, path, body=body, headers=supplied)
        response = connection.getresponse()
        result = response.status, dict(response.getheaders()), response.read()
        connection.close()
        return result

    def test_append_read_head_and_exact_retry(self):
        ciphertext = b"ciphertext-not-plaintext-contents"
        status, _, body = self.request("PUT", body=ciphertext)
        self.assertEqual(status, 201)
        self.assertTrue(json.loads(body)["created"])
        self.assertEqual(self.request("GET")[2], ciphertext)
        status, headers, body = self.request("HEAD")
        self.assertEqual((status, body), (200, b""))
        self.assertEqual(headers["Content-Length"], str(len(ciphertext)))
        self.assertEqual(headers["X-Ciphertext-SHA256"], hashlib.sha256(ciphertext).hexdigest())
        status, _, body = self.request("PUT", body=ciphertext)
        self.assertEqual(status, 200)
        self.assertFalse(json.loads(body)["created"])

    def test_different_bytes_conflict_and_no_delete_overwrite(self):
        self.assertEqual(self.request("PUT", body=b"original")[0], 201)
        self.assertEqual(self.request("PUT", body=b"replacement")[0], 409)
        for method in ("DELETE", "PATCH", "POST"):
            self.assertEqual(self.request(method)[0], 405)
        self.assertEqual(self.request("GET")[2], b"original")

    def test_wrong_token_cross_environment_app_and_no_token_query(self):
        self.assertEqual(self.request("PUT", body=b"stage", token=TOKEN_PROD)[0], 401)
        self.assertEqual(self.request("PUT", PATH_PROD, b"prod", token=TOKEN_STAGE)[0], 401)
        self.assertEqual(self.request("PUT", PATH_STAGE.replace(APP_STAGE, APP_PROD), b"wrong")[0], 401)
        self.assertEqual(self.request("PUT", PATH_STAGE + "?token=" + TOKEN_STAGE, b"wrong")[0], 404)
        self.assertEqual(self.request("PUT", PATH_PROD, b"prod", token=TOKEN_PROD)[0], 201)
        self.assertEqual(self.request("GET", PATH_PROD, token=TOKEN_STAGE)[0], 401)

    def test_traversal_encoding_unknown_artifacts_and_indexes_rejected(self):
        for path in ("/", "/v1/staging", PATH_STAGE.replace("backup20261108", ".."),
                     PATH_STAGE.replace("backup20261108", "%2e%2e"),
                     PATH_STAGE.replace("database.dump.enc", "other.enc"), PATH_STAGE + "/tail"):
            with self.subTest(path=path):
                self.assertEqual(self.request("PUT", path, b"denied")[0], 404)

    def test_bad_hash_missing_hash_bad_size_and_chunking_rejected(self):
        for headers, expected in (({"X-Ciphertext-SHA256": "0" * 64}, 400),
                                  ({"X-Ciphertext-SHA256": ""}, 400),
                                  ({"Content-Length": "-1"}, 400),
                                  ({"Content-Length": "9000"}, 413),
                                  ({"Transfer-Encoding": "chunked"}, 400)):
            with self.subTest(headers=headers):
                self.assertEqual(self.request("PUT", body=b"invalid", headers=headers)[0], expected)
        self.assertEqual(self.request("GET")[0], 404)

    def test_duplicate_content_length_is_rejected(self):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=4)
        connection.putrequest("PUT", PATH_STAGE)
        for key, value in (("Authorization", "Bearer " + TOKEN_STAGE), ("X-Forwarded-Proto", "https"),
                           ("Content-Length", "1"), ("Content-Length", "2"),
                           ("X-Ciphertext-SHA256", hashlib.sha256(b"x").hexdigest())):
            connection.putheader(key, value)
        connection.endheaders(b"x")
        response = connection.getresponse()
        self.assertEqual(response.status, 400)
        response.read()
        connection.close()

    def test_partial_upload_is_not_committed_and_temp_is_removed(self):
        with self.assertRaises(Rejected) as context:
            self.repository.put(("staging", APP_STAGE, "backup20261108"), "database.dump.enc",
                                io.BytesIO(b"partial"), 20, "0" * 64)
        self.assertEqual(context.exception.code, "incomplete_upload")
        self.assertEqual(self.request("GET")[0], 404)
        self.assertFalse([path for path in Path(self.directory.name).rglob(".upload-*") if path.name != ".upload-lock"])

    def test_missing_mount_fails_closed_while_live_remains_healthy(self):
        self.mounted = False
        self.assertEqual(self.request("GET", "/health/live", token="")[0], 200)
        self.assertEqual(self.request("GET", "/health/ready", token="")[0], 503)
        self.assertEqual(self.request("PUT", body=b"ciphertext")[0], 503)
        self.assertEqual(list(Path(self.directory.name).iterdir()), [])

    def test_symlink_directory_and_artifact_are_rejected(self):
        outside = tempfile.TemporaryDirectory()
        self.addCleanup(outside.cleanup)
        (Path(self.directory.name) / "staging").symlink_to(outside.name, target_is_directory=True)
        self.assertEqual(self.request("PUT", body=b"ciphertext")[0], 503)
        self.assertEqual(list(Path(outside.name).iterdir()), [])
        (Path(self.directory.name) / "staging").unlink()
        self.assertEqual(self.request("PUT", body=b"original")[0], 201)
        artifact = Path(self.directory.name) / "staging" / APP_STAGE / "backup20261108" / "database.dump.enc"
        artifact.unlink()
        outside_file = Path(outside.name) / "private"
        outside_file.write_bytes(b"outside")
        artifact.symlink_to(outside_file)
        self.assertEqual(self.request("GET")[0], 503)
        self.assertEqual(self.request("PUT", body=b"replacement")[0], 503)
        self.assertEqual(outside_file.read_bytes(), b"outside")

    def test_concurrent_collisions_never_overwrite_and_same_hash_is_idempotent(self):
        def upload(body):
            try:
                self.repository.put(("staging", APP_STAGE, "backup20261108"), "database.dump.enc",
                                    io.BytesIO(body), len(body), hashlib.sha256(body).hexdigest())
                return 200
            except Rejected as error:
                return error.status
        with ThreadPoolExecutor(max_workers=8) as executor:
            results = list(executor.map(upload, [b"one", b"two"] * 4))
        winner = self.request("GET")[2]
        self.assertIn(winner, (b"one", b"two"))
        self.assertEqual(results.count(409), 4)
        with ThreadPoolExecutor(max_workers=8) as executor:
            self.assertEqual(list(executor.map(upload, [winner] * 8)), [200] * 8)

    def test_capacity_reserve_and_logical_quota_reject_before_commit(self):
        self.repository.disk_usage = lambda _: SimpleNamespace(free=1025)
        self.assertEqual(self.request("PUT", body=b"reserve-too-small")[0], 507)
        self.repository.disk_usage = lambda _: SimpleNamespace(free=1024**3)
        self.settings.quota_bytes = 5
        self.assertEqual(self.request("PUT", body=b"overquota")[0], 507)
        self.assertEqual(self.request("GET")[0], 404)

    def test_https_edge_and_repository_identity_are_required(self):
        self.assertEqual(self.request("PUT", body=b"data", headers={"X-Forwarded-Proto": "http"})[0], 403)
        self.settings.trusted_proxies = ()
        self.assertEqual(self.request("PUT", body=b"data")[0], 403)
        self.settings.trusted_proxies = (ip_network("127.0.0.1/32"),)
        self.repository.ready()
        (Path(self.directory.name) / ".repository-id").write_text("another-repository")
        self.assertEqual(self.request("GET", "/health/ready")[0], 503)

    def test_missing_get_does_not_allocate_arbitrary_directories(self):
        self.assertEqual(self.request("GET")[0], 404)
        self.assertFalse((Path(self.directory.name) / "staging").exists())

    def test_status_only_reports_complete_authenticated_backup_pairs(self):
        path = f"/v1/staging/{APP_STAGE}/status"
        self.assertEqual(json.loads(self.request("GET", path)[2])["latest"], None)
        self.assertEqual(self.request("PUT", body=b"encrypted-database")[0], 201)
        self.assertEqual(json.loads(self.request("GET", path)[2])["latest"], None)
        manifest = PATH_STAGE.replace("database.dump.enc", "manifest.json.enc")
        self.assertEqual(self.request("PUT", manifest, b"encrypted-manifest")[0], 201)
        status = json.loads(self.request("GET", path)[2])
        self.assertEqual(status["latest"]["backupId"], "backup20261108")
        self.assertEqual(set(status["latest"]["artifacts"]), {"database.dump.enc", "manifest.json.enc"})
        self.assertEqual(status["completeCount"], 1)
        self.assertNotIn(TOKEN_STAGE, json.dumps(status))
        self.assertEqual(self.request("GET", path, token=TOKEN_PROD)[0], 401)
        self.assertEqual(self.request("DELETE", path)[0], 405)

    def test_status_does_not_count_a_symlink_as_a_complete_artifact(self):
        self.assertEqual(self.request("PUT", body=b"encrypted-database")[0], 201)
        self.assertEqual(self.request("PUT", PATH_STAGE.replace("database.dump.enc", "manifest.json.enc"), b"encrypted-manifest")[0], 201)
        artifact = Path(self.directory.name) / "staging" / APP_STAGE / "backup20261108" / "database.dump.enc"
        target = Path(self.directory.name) / "unrelated"
        target.write_bytes(b"outside")
        artifact.unlink()
        artifact.symlink_to(target)
        status = self.repository.status("staging", APP_STAGE)
        self.assertIsNone(status["latest"])
        self.assertEqual(status["completeCount"], 0)

    def test_retention_keeps_two_latest_complete_and_expires_only_owned_old_sets(self):
        now = time.time()
        for number, age in enumerate((50, 40, 35, 31)):
            backup = f"retention{number:08}"
            base = PATH_STAGE.replace("backup20261108", backup)
            self.assertEqual(self.request("PUT", base, b"database")[0], 201)
            self.assertEqual(self.request("PUT", base.replace("database.dump.enc", "manifest.json.enc"), b"manifest")[0], 201)
            metadata_path = Path(self.directory.name) / "staging" / APP_STAGE / backup / ".backup-meta.json"
            metadata = json.loads(metadata_path.read_text())
            metadata.update(createdAt=now-age*86400, completedAt=now-age*86400, expiresAt=now+(30-age)*86400)
            metadata_path.write_text(json.dumps(metadata))
        orphan_path = PATH_STAGE.replace("backup20261108", "orphan00000001")
        self.assertEqual(self.request("PUT", orphan_path, b"orphan")[0], 201)
        orphan = Path(self.directory.name) / "staging" / APP_STAGE / "orphan00000001" / ".backup-meta.json"
        metadata = json.loads(orphan.read_text())
        metadata["createdAt"] = now-25*3600
        orphan.write_text(json.dumps(metadata))
        unknown = orphan.parent.parent / "unknown00000001"
        unknown.mkdir()
        (unknown / "database.dump.enc").write_bytes(b"not-created-by-server")
        self.assertEqual(self.repository.expire(now), 3)
        self.assertTrue(unknown.exists())
        status = self.repository.status("staging", APP_STAGE)
        self.assertEqual(status["completeCount"], 2)
        self.assertEqual(status["latest"]["backupId"], "retention00000003")
        for number in (2, 3):
            self.assertEqual(self.request("GET", PATH_STAGE.replace("backup20261108", f"retention{number:08}"))[0], 200)
        self.assertEqual(self.repository.expire(now), 0)


from ipaddress import ip_network


if __name__ == "__main__":
    unittest.main()
