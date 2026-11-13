import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
from unittest import mock


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("local_source", Path(__file__).with_name("local_source.py"))
local = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(local)


class LocalSourceTests(unittest.TestCase):
    def setUp(self):
        artifacts = ROOT / ".local/artifacts/local-source-tests"
        artifacts.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=artifacts)
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)

    def archive(self, extra=None):
        path = self.base / "source.tar.gz"
        descriptor = {"schemaVersion": 1, "kind": local.TRUST, "sourceRevision": "a" * 40,
                      "serverVersion": "0.0.1-local.1", "cliCompatibility": {}}
        with tarfile.open(path, "w:gz") as archive:
            data = json.dumps(descriptor).encode()
            member = tarfile.TarInfo("minicloud-local-source.json")
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
            if extra:
                archive.addfile(extra, io.BytesIO(b"x") if extra.isfile() else None)
        return path

    def test_pinned_source_is_replayable_but_changed_inputs_are_not_overwritten(self):
        path = self.archive()
        output = self.base / "source"
        expected = local.digest(path)
        result = local.extract_source(path, expected, output)
        self.assertEqual(local.TRUST, result["kind"])
        self.assertEqual(result, local.extract_source(path, expected, output))
        descriptor = output / "minicloud-local-source.json"
        descriptor.write_text("changed")
        with self.assertRaisesRegex(ValueError, "never overwrite"):
            local.extract_source(path, expected, output)
        self.assertEqual("changed", descriptor.read_text())

    def test_wrong_digest_prevents_even_source_directory_creation(self):
        path = self.archive()
        output = self.base / "source"
        with self.assertRaisesRegex(ValueError, "SHA256"):
            local.extract_source(path, "b" * 64, output)
        self.assertFalse(output.exists())

    def test_traversal_links_duplicates_and_oversized_members_are_rejected(self):
        for name, kind, size in (("../outside", tarfile.REGTYPE, 1),
                                 ("linked", tarfile.SYMTYPE, 0),
                                 ("minicloud-local-source.json", tarfile.REGTYPE, 1),
                                 ("large", tarfile.REGTYPE, 65 * 1024**2)):
            with self.subTest(name=name):
                member = tarfile.TarInfo(name)
                member.type, member.size = kind, size
                member.linkname = "/etc/passwd" if kind == tarfile.SYMTYPE else ""
                if size > 1:
                    with self.assertRaisesRegex(ValueError, "bounds"):
                        list(local.source_members([member]))
                    continue
                path = self.archive(member)
                with self.assertRaisesRegex(ValueError, "Unsafe"):
                    local.extract_source(path, local.digest(path), self.base / "source")
        self.assertFalse((self.base / "outside").exists())

    def test_image_checkpoint_requires_exact_native_content_and_revision(self):
        reference = "sha256:" + "a" * 64
        images = {name: reference for name in ("api", "dashboard", "worker")}
        value = {"Id": reference, "Os": "linux", "Architecture": "amd64",
                 "Config": {"Labels": {"org.opencontainers.image.revision": "b" * 40}}}
        with mock.patch.object(local.subprocess, "check_output", return_value=json.dumps([value]).encode()):
            local.verify_images(images, "linux/amd64", "b" * 40)
            with self.assertRaisesRegex(ValueError, "differs"):
                local.verify_images(images, "linux/arm64", "b" * 40)
            with self.assertRaisesRegex(ValueError, "differs"):
                local.verify_images(images, "linux/amd64", "c" * 40)

    def test_local_package_pins_content_ids_and_refuses_changed_replay(self):
        release = local.module("local_release_fixture", ROOT / "scripts/server-release/release.py")
        metadata = {"serverVersion": "0.0.1-local.1", "sourceRevision": "a" * 40,
                    "cliCompatibility": {"protocolVersion": 1, "minimumVersion": None,
                                         "maximumExclusiveVersion": None, "sourceRevisions": ["a" * 40]}}
        images = {name: "sha256:" + str(index) * 64
                  for index, name in enumerate(("api", "dashboard", "worker"), 1)}
        output = self.base / "assets"
        manifest = local.package_native(ROOT, output, metadata, images, "d" * 64, "linux/amd64", release)
        self.assertEqual(local.TRUST, manifest["artifactTrust"])
        self.assertEqual(["linux/amd64"], manifest["architectures"])
        self.assertEqual("d" * 64, manifest["sourceArchiveSha256"])
        self.assertEqual(images, manifest["images"])
        with tarfile.open(output / manifest["bundle"]["name"]) as archive:
            compose = archive.extractfile("compose.yml").read().decode()
            self.assertNotIn("ghcr.io/muniventures/minicloud-", compose)
            self.assertIn(images["api"], compose)
        self.assertEqual(manifest, local.package_native(ROOT, output, metadata, images, "d" * 64, "linux/amd64", release))
        with self.assertRaisesRegex(ValueError, "Existing local assets differ"):
            local.package_native(ROOT, output, metadata, images, "e" * 64, "linux/amd64", release)
