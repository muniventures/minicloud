# SPDX-License-Identifier: MPL-2.0
import hashlib
import json
from pathlib import Path
import shutil
import tarfile
import unittest
import uuid
import zipfile

from package import package, RIDS, safe


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(".local/artifacts") / ("cli-package-tests-" + uuid.uuid4().hex)
        self.root.mkdir(parents=True)
        self.payload = self.root / "payload"
        for rid in RIDS:
            directory = self.payload / rid
            (directory / "licenses").mkdir(parents=True)
            for name in ("minicloud.exe" if rid == "win-x64" else "minicloud",
                         "LICENSE.txt", "SOURCE.txt", "licenses/YamlDotNet-MIT.txt",
                         "licenses/dotnet-LICENSE.TXT", "licenses/dotnet-THIRD-PARTY-NOTICES.TXT"):
                (directory / name).write_bytes(b"synthetic package fixture, not an executable")

    def tearDown(self):
        shutil.rmtree(self.root)

    def test_inventory_binds_all_four_archives_and_exact_source_workflow(self):
        output = self.root / "release"
        package(self.payload, output, "v4.0.0-rc.1", "a" * 40)
        provenance = json.loads((output / "cli-provenance.json").read_text())
        self.assertEqual(set(RIDS), set(provenance["artifacts"]))
        self.assertEqual("a" * 40, provenance["sourceRevision"])
        self.assertTrue(provenance["workflow"].endswith("/cli-release.yml"))
        self.assertEqual(5, len((output / "checksums.txt").read_text().splitlines()))
        for rid, artifact in provenance["artifacts"].items():
            path = output / artifact["name"]
            self.assertEqual(artifact["sha256"], hashlib.sha256(path.read_bytes()).hexdigest())
            if rid == "win-x64":
                with zipfile.ZipFile(path) as archive:
                    members = {item.filename: archive.read(item) for item in archive.infolist()}
                    self.assertTrue(all((item.external_attr >> 16) & 0o170000 == 0o100000 for item in archive.infolist()))
            else:
                with tarfile.open(path) as archive:
                    members = {item.name: archive.extractfile(item).read() for item in archive}
            self.assertEqual(artifact["files"], {name: hashlib.sha256(data).hexdigest() for name, data in members.items()})

    def test_server_channel_latest_and_reused_output_are_rejected(self):
        for tag in ("server-v4.0.0", "latest", "main"):
            with self.assertRaises(ValueError):
                package(self.payload, self.root / "bad", tag, "a" * 40)
        output = self.root / "release"
        output.mkdir()
        (output / "historical").write_text("do not replace")
        with self.assertRaises(ValueError):
            package(self.payload, output, "v4.0.0", "a" * 40)
        self.assertEqual("do not replace", (output / "historical").read_text())

    def test_links_and_partial_runtime_are_not_publishable(self):
        (self.payload / "linux-x64" / "link").symlink_to("minicloud")
        with self.assertRaises(ValueError):
            package(self.payload, self.root / "linked", "v4.0.0", "a" * 40)
        (self.payload / "linux-x64" / "link").unlink()
        (self.payload / "linux-x64" / "unexpected.dll").write_bytes(b"not bundled")
        with self.assertRaises(ValueError):
            package(self.payload, self.root / "partial", "v4.0.0", "a" * 40)

    def test_unsafe_archive_paths(self):
        for name in ("../minicloud", "/minicloud", "a//b", "C:minicloud", "a\\b", "./minicloud", "CON", "a/NUL.txt", "a."):
            self.assertFalse(safe(name), name)


if __name__ == "__main__":
    unittest.main()
