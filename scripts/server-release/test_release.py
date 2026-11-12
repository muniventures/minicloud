# SPDX-License-Identifier: MPL-2.0
import base64
import copy
import io
import json
from pathlib import Path
import re
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import release


TAG = "server-v1.2.3-rc.1"
SOURCE = "a" * 40
HASH = "b" * 64


def release_input():
    return {
        "schemaVersion": 1,
        "serverVersion": "1.2.3-rc.1",
        "architectures": release.ARCHITECTURES.copy(),
        "dependencies": {name: f"registry.example.invalid/{name}@sha256:{HASH}" for name in release.DEPENDENCIES},
        "applicationMigration": "20261110100000_Example",
        "identitySchemaVersion": 1,
        "configurationSchemaVersion": 1,
        "cliCompatibility": {
            "protocolVersion": 1, "minimumVersion": None, "maximumExclusiveVersion": None,
            "sourceRevisions": [SOURCE],
        },
    }


def images():
    return {name: f"ghcr.io/muniventures/minicloud-{name}@sha256:{HASH}" for name in release.COMPONENTS}


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        artifacts = Path(__file__).resolve().parents[2] / ".local/artifacts"
        artifacts.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="server-release-tests-", dir=artifacts)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.config = self.root / "release.json"
        self.config.write_bytes(release.json_bytes(release_input()))
        self.payload = self.root / "bundle"
        self.payload.mkdir()
        for name in release.REQUIRED_PAYLOAD:
            (self.payload / name).write_text("# Synthetic release fixture\n")
        for name in ("configuration.schema.json", "configuration.example.json"):
            (self.payload / name).write_text("{}\n")
        self.compose = {"services": {
            name: {"image": "${MINICLOUD_" + name.upper() + "_IMAGE:?required}"}
            for name in (*release.COMPONENTS, *release.DEPENDENCIES)
        }}
        (self.payload / "compose.yml").write_text(json.dumps(self.compose))
        for component in ("worker", "dashboard"):
            path = self.root / f"modules/{component}"
            path.mkdir(parents=True)
        (self.root / "modules/worker/Dockerfile").write_text(
            'FROM example\nARG MINICLOUD_AGENT_RID\nRUN dotnet publish -r "$MINICLOUD_AGENT_RID"\n'
            'COPY --from=runtime-agent /agent/linux-x64/Minicloud.RuntimeAgent /app/runtime-agent/linux-x64/minicloud-agent\n'
            'COPY --from=runtime-agent /agent/linux-arm64/Minicloud.RuntimeAgent /app/runtime-agent/linux-arm64/minicloud-agent\n')
        (self.root / "modules/dashboard/Dockerfile").write_text("FROM example\n")
        migrations = self.root / "modules/common/Migrations"
        migrations.mkdir(parents=True)
        (migrations / f"{release_input()['applicationMigration']}.cs").write_text("// Synthetic\n")
        self.images_path = self.root / "images.json"
        self.images_path.write_bytes(release.json_bytes(images()))
        self.output = self.root / ".local/artifacts/output"

    def package(self):
        with patch.object(release, "run", return_value=json.dumps(self.compose)):
            release.package(self.root, self.config, self.payload, TAG, SOURCE, self.images_path, self.output)
        return release.read_json(self.output / "server-manifest.json")

    def test_checked_in_candidate_tracks_workflow_source_and_current_payload(self):
        root = Path(__file__).resolve().parents[2]
        workflow = (root / ".github/workflows/customer-deploy.yml").read_text()
        selector = re.search(
            r"(?m)^      minicloud_version:\n(?:        .*\n)*?        default: ([0-9a-f]{40})$",
            workflow,
        )
        self.assertIsNotNone(selector)
        source = selector.group(1)
        config = root / "packaging/server/release.json"
        selected = release.read_json(config)
        candidate = release.preflight(
            root, config, root / "packaging/server/bundle",
            f"server-v{selected['serverVersion']}", source,
        )
        revisions = candidate["cliCompatibility"]["sourceRevisions"]
        self.assertIn(source, revisions)
        self.assertIn("4fa46a46e0d492d0e9ae41e1cd9b8a8188170443", revisions)
        self.assertEqual(len(revisions), len(set(revisions)))
        for revision in revisions:
            self.assertRegex(revision, r"^[0-9a-f]{40}$")
        self.assertIsNone(candidate["cliCompatibility"]["minimumVersion"])
        self.assertIsNone(candidate["cliCompatibility"]["maximumExclusiveVersion"])

    def resign_inventory(self):
        manifest = release.read_json(self.output / "server-manifest.json")
        names = [manifest["bundle"]["name"], "server-manifest.json", "install-server.sh", "verify-server-release.py"]
        (self.output / "checksums.txt").write_text("".join(
            f"{release.sha256((self.output / name).read_bytes())}  {name}\n" for name in sorted(names)))

    def test_roundtrip_manifest_bundle_runtime_and_pinned_compose(self):
        manifest = self.package()
        with patch.object(release, "run", return_value="") as command:
            self.assertEqual(manifest, release.verify_assets(self.output, TAG))
        arguments = command.call_args.args[0]
        self.assertIn(release.identity(TAG), arguments)
        self.assertIn(release.ISSUER, arguments)
        self.assertIn("--new-bundle-format", arguments)
        self.assertFalse(any("insecure" in arg or "ignore" in arg for arg in arguments))
        with tarfile.open(self.output / manifest["bundle"]["name"]) as archive:
            compose = json.load(archive.extractfile("compose.yml"))
        self.assertEqual(set(images().values()) | set(manifest["dependencies"].values()),
                         {service["image"] for service in compose["services"].values()})

    def test_bundle_is_reproducible_and_outputs_cannot_escape_ignored_artifacts(self):
        manifest = self.package()
        self.output = self.root / ".local/artifacts/repeated"
        repeated = self.package()
        self.assertEqual(manifest, repeated)
        with patch.object(release, "run", return_value=json.dumps(self.compose)):
            with self.assertRaisesRegex(release.ReleaseError, "ignored .local/artifacts"):
                release.package(self.root, self.config, self.payload, TAG, SOURCE, self.images_path, self.root / "not-ignored")

    def test_signature_failure_stops_before_parsing_assets(self):
        self.package()
        (self.output / "server-manifest.json").write_text("not JSON")
        with patch.object(release, "run", side_effect=subprocess.CalledProcessError(1, ["cosign"])):
            with self.assertRaises(subprocess.CalledProcessError):
                release.verify_assets(self.output, TAG)

    def test_tampering_with_every_signed_asset_is_rejected(self):
        manifest = self.package()
        for name in ("server-manifest.json", manifest["bundle"]["name"], "install-server.sh", "verify-server-release.py"):
            with self.subTest(asset=name):
                path = self.output / name
                original = path.read_bytes()
                path.write_bytes(original + b"tamper")
                with patch.object(release, "run", return_value=""):
                    with self.assertRaisesRegex(release.ReleaseError, "checksum mismatch"):
                        release.verify_assets(self.output, TAG)
                path.write_bytes(original)

    def test_mixed_release_and_manifest_bundle_binding_are_rejected(self):
        manifest = self.package()
        for field, value in (("releaseTag", "server-v2.0.0"), ("sourceRevision", "main"),
                             ("architectures", ["linux/amd64"]), ("bundle", {"name": manifest["bundle"]["name"], "sha256": "0" * 64})):
            with self.subTest(field=field):
                changed = copy.deepcopy(manifest)
                changed[field] = value
                (self.output / "server-manifest.json").write_bytes(release.json_bytes(changed))
                self.resign_inventory()
                with patch.object(release, "run", return_value=""):
                    with self.assertRaises(release.ReleaseError):
                        release.verify_assets(self.output, TAG)
        (self.output / "server-manifest.json").write_bytes(release.json_bytes(manifest))

    def test_signed_inventory_must_be_complete_unique_and_safe(self):
        self.package()
        path = self.output / "checksums.txt"
        original = path.read_text()
        for value in (original + original, original.splitlines()[0] + "\n",
                      original + f"{HASH}  ../escape\n", original.replace("  ", " ")):
            with self.subTest(inventory=value):
                path.write_text(value)
                with patch.object(release, "run", return_value=""):
                    with self.assertRaises(release.ReleaseError):
                        release.verify_assets(self.output, TAG)

    def test_tar_links_traversal_and_extra_files_are_rejected_without_extraction(self):
        manifest = self.package()
        bundle = self.output / manifest["bundle"]["name"]
        for name, kind in (("../escape", tarfile.REGTYPE), ("/escape", tarfile.REGTYPE),
                           ("unexpected", tarfile.REGTYPE), ("compose.yml", tarfile.SYMTYPE)):
            with self.subTest(name=name, kind=kind):
                with tarfile.open(bundle, "w:gz") as archive:
                    member = tarfile.TarInfo(name)
                    member.type = kind
                    archive.addfile(member, io.BytesIO())
                manifest["bundle"]["sha256"] = release.sha256(bundle.read_bytes())
                (self.output / "server-manifest.json").write_bytes(release.json_bytes(manifest))
                self.resign_inventory()
                with patch.object(release, "run", return_value=""):
                    with self.assertRaises(release.ReleaseError):
                        release.verify_assets(self.output, TAG)
        self.assertFalse((self.root / "escape").exists())

    def test_input_rejects_bad_versions_shapes_compatibility_and_floating_dependencies(self):
        original = release_input()
        variants = [
            {"serverVersion": "2.0.0"}, {"schemaVersion": 2}, {"architectures": ["linux/arm64"]},
            {"dependencies": {name: "postgres:17" for name in release.DEPENDENCIES}},
            {"identitySchemaVersion": 0}, {"configurationSchemaVersion": True},
            {"applicationMigration": "latest"}, {"extra": "not allowed"},
        ]
        for change in variants:
            with self.subTest(change=change):
                self.config.write_bytes(release.json_bytes({**original, **change}))
                with self.assertRaises(release.ReleaseError):
                    release.load_input(self.config, TAG, SOURCE)
        for change in ({"protocolVersion": 2}, {"sourceRevisions": []}, {"sourceRevisions": ["latest"]},
                       {"sourceRevisions": [SOURCE, SOURCE]}, {"sourceRevisions": [SOURCE[:7]]},
                       {"sourceRevisions": [SOURCE.upper()]},
                       {"minimumVersion": "1.0.0"}, {"minimumVersion": "2.0.0", "maximumExclusiveVersion": "1.0.0"},
                       {"minimumVersion": "1.0.0-01", "maximumExclusiveVersion": "2.0.0"}):
            with self.subTest(change=change):
                with self.assertRaises(release.ReleaseError):
                    release.validate_compatibility({**original["cliCompatibility"], **change})

    def test_semver_order_and_source_or_interval_compatibility(self):
        release.validate_compatibility({
            "protocolVersion": 1, "sourceRevisions": [], "minimumVersion": "1.0.0-rc.2",
            "maximumExclusiveVersion": "1.0.0-rc.10",
        })
        self.assertLess(release.version_key("1.0.0-rc.10"), release.version_key("1.0.0"))
        for tag in ("v1.0.0", "server-channel", "server-v01.0.0", "server-v1.0.0-01", "server-v1.0.0;echo bad"):
            with self.subTest(tag=tag):
                with self.assertRaises(release.ReleaseError):
                    release.version_from_tag(tag)

    def test_preflight_requires_t41_handoff_and_complete_downstream_assets(self):
        release.preflight(self.root, self.config, self.payload, TAG, SOURCE)
        worker = self.root / "modules/worker/Dockerfile"
        original = worker.read_text()
        worker.write_text("RUN dotnet publish -r linux-x64\n")
        with self.assertRaisesRegex(release.ReleaseError, "T41 handoff"):
            release.preflight(self.root, self.config, self.payload, TAG, SOURCE)
        worker.write_text(original.replace(
            'COPY --from=runtime-agent /agent/linux-arm64/Minicloud.RuntimeAgent /app/runtime-agent/linux-arm64/minicloud-agent\n', ''))
        with self.assertRaisesRegex(release.ReleaseError, "both linux-x64 and linux-arm64"):
            release.preflight(self.root, self.config, self.payload, TAG, SOURCE)
        worker.write_text(original)
        dashboard = self.root / "modules/dashboard/Dockerfile"
        dashboard.write_text("ARG VITE_FIREBASE_API_KEY\n")
        with self.assertRaisesRegex(release.ReleaseError, "startup configuration"):
            release.preflight(self.root, self.config, self.payload, TAG, SOURCE)
        dashboard.write_text("FROM example\n")
        (self.payload / "maintenance.sh").unlink()
        with self.assertRaisesRegex(release.ReleaseError, "Missing bundle assets"):
            release.preflight(self.root, self.config, self.payload, TAG, SOURCE)

    def test_preflight_rejects_payload_symlinks_stale_migrations_and_duplicate_json_keys(self):
        (self.payload / "link").symlink_to(self.config)
        with self.assertRaisesRegex(release.ReleaseError, "links"):
            release.payload_files(self.payload)
        (self.payload / "link").unlink()
        (self.root / "modules/common/Migrations/20261111100000_Newer.cs").write_text("// Synthetic")
        with self.assertRaisesRegex(release.ReleaseError, "latest migration"):
            release.preflight(self.root, self.config, self.payload, TAG, SOURCE)
        self.config.write_text('{"schemaVersion":1,"schemaVersion":2}')
        with self.assertRaisesRegex(release.ReleaseError, "Duplicate"):
            release.read_json(self.config)

    def test_compose_cannot_build_or_use_unpinned_unlisted_or_missing_images(self):
        for mutate in ("build", "floating", "missing"):
            with self.subTest(change=mutate):
                compose = copy.deepcopy(self.compose)
                if mutate == "build":
                    compose["services"]["api"]["build"] = "."
                elif mutate == "floating":
                    compose["services"]["api"]["image"] = "example/api:latest"
                else:
                    del compose["services"]["api"]
                with patch.object(release, "run", return_value=json.dumps(compose)):
                    with self.assertRaises(release.ReleaseError):
                        release.package(self.root, self.config, self.payload, TAG, SOURCE, self.images_path, self.output)

    def test_index_requires_both_platforms_and_rejects_duplicate_or_unsupported_product_platforms(self):
        good = [{"platform": {"os": "linux", "architecture": arch}} for arch in ("amd64", "arm64")]
        attestation = {"platform": {"os": "unknown", "architecture": "unknown"},
                       "annotations": {"vnd.docker.reference.type": "attestation-manifest"}}
        with patch.object(release, "run", return_value=json.dumps({"manifests": [*good, attestation]})):
            release.verify_index(images()["worker"], True)
        for manifests in (good[:1], [*good, good[0]],
                          [*good, {"platform": {"os": "linux", "architecture": "386"}}],
                          [*good, {**attestation, "platform": {"os": "linux", "architecture": "386"}}]):
            with patch.object(release, "run", return_value=json.dumps({"manifests": manifests})):
                with self.assertRaises(release.ReleaseError):
                    release.verify_index(images()["worker"], True)
        with patch.object(release, "run", return_value=json.dumps({"manifests": [*good, {"platform": {"os": "linux", "architecture": "386"}}]})):
            release.verify_index("dependency", False)

    def test_provenance_binds_verified_subject_source_and_exact_workflow(self):
        manifest = {**release_input(), "sourceRevision": SOURCE, "releaseTag": TAG, "images": images()}
        statement = {
            "predicateType": "https://slsa.dev/provenance/v0.2",
            "subject": [{"digest": {"sha256": HASH}}], "predicate": release.provenance(manifest),
        }

        def envelope(value):
            return json.dumps({"payload": base64.b64encode(json.dumps(value).encode()).decode()})

        release.verify_provenance(envelope(statement), images()["api"], manifest, TAG)
        for field in ("builder", "source", "subject", "materials"):
            with self.subTest(field=field):
                changed = copy.deepcopy(statement)
                if field == "builder":
                    changed["predicate"]["builder"]["id"] = release.identity(TAG).replace("server-release", "other")
                elif field == "source":
                    changed["predicate"]["invocation"]["configSource"]["digest"]["sha1"] = "c" * 40
                elif field == "materials":
                    changed["predicate"]["materials"] = []
                else:
                    changed["subject"][0]["digest"]["sha256"] = "0" * 64
                with self.assertRaisesRegex(release.ReleaseError, "No verified provenance"):
                    release.verify_provenance(envelope(changed), images()["api"], manifest, TAG)

    def test_verified_attestation_for_another_source_is_not_accepted_as_image_provenance(self):
        manifest = {**release_input(), "sourceRevision": SOURCE, "releaseTag": TAG, "images": images()}
        wrong = copy.deepcopy(manifest)
        wrong["sourceRevision"] = "c" * 40
        statement = {
            "predicateType": "https://slsa.dev/provenance/v0.2",
            "subject": [{"digest": {"sha256": HASH}}], "predicate": release.provenance(wrong),
        }
        envelope = json.dumps({"payload": base64.b64encode(json.dumps(statement).encode()).decode()})
        with patch.object(release, "verify_index"), patch.object(release, "run", return_value=envelope):
            with self.assertRaisesRegex(release.ReleaseError, "No verified provenance"):
                release.verify_images(manifest, TAG, False, self.root / ".local/artifacts")

    def test_anonymous_pulls_cover_every_image_and_platform_with_empty_docker_config(self):
        manifest = {**release_input(), "images": images()}
        with patch.object(release, "verify_index"), patch.object(release, "verify_provenance"), \
                patch.object(release, "run", return_value="") as commands:
            release.verify_images(manifest, TAG, True, self.root / ".local/artifacts")
        pulls = [call for call in commands.call_args_list if call.args[0][:2] == ["docker", "pull"]]
        self.assertEqual(14, len(pulls))
        for call in pulls:
            self.assertIn(call.args[0][3], release.ARCHITECTURES)
            self.assertIn("anonymous-pull-", call.kwargs["env"]["DOCKER_CONFIG"])
            self.assertNotIn("DOCKER_AUTH_CONFIG", call.kwargs["env"])
        for call in commands.call_args_list:
            if call.args[0][0] == "cosign":
                self.assertIn(release.identity(TAG), call.args[0])

    def test_each_worker_platform_checks_both_target_agents_and_removes_container_on_failure(self):
        output = self.root / ".local/artifacts/agent"
        output.parent.mkdir(parents=True)
        for rid, machine in (("linux-x64", 62), ("linux-arm64", 183)):
            data = bytearray(20)
            data[:6] = b"\x7fELF\x02\x01"
            data[18:20] = machine.to_bytes(2, "little")
            output.with_name(f"{output.name}-{rid}").write_bytes(data)
        for architecture in ("amd64", "arm64"):
            with self.subTest(worker=architecture), patch.object(release, "run", return_value="c" * 64) as command:
                release.verify_agent(images()["worker"], architecture, output)
                copies = [call.args[0][2] for call in command.call_args_list if call.args[0][:2] == ["docker", "cp"]]
                self.assertEqual([f"{'c' * 64}:/app/runtime-agent/{rid}/minicloud-agent"
                                  for rid in ("linux-x64", "linux-arm64")], copies)
                self.assertEqual(["docker", "rm", "c" * 64], command.call_args.args[0])
        output.with_name(f"{output.name}-linux-arm64").write_bytes(
            output.with_name(f"{output.name}-linux-x64").read_bytes())
        with patch.object(release, "run", return_value="c" * 64) as command:
            with self.assertRaisesRegex(release.ReleaseError, "linux-arm64"):
                release.verify_agent(images()["worker"], "amd64", output)
            self.assertEqual(["docker", "rm", "c" * 64], command.call_args.args[0])

    def test_build_wires_both_platforms_and_verifies_all_worker_payloads_without_host_derived_rids(self):
        commands = []

        def command(args, **kwargs):
            commands.append(args)
            if args[:3] == ["docker", "buildx", "build"]:
                Path(args[args.index("--metadata-file") + 1]).write_text(
                    json.dumps({"containerimage.digest": f"sha256:{HASH}"}))
            if "--format" in args:
                return json.dumps({"digest": f"sha256:{HASH}"})
            return ""

        with patch.object(release, "run", side_effect=command), patch.object(release, "verify_index"), \
                patch.object(release, "verify_agent") as agents:
            release.build_images(self.root, TAG, SOURCE, self.root / ".local/artifacts/build")
        builds = [args for args in commands if args[:3] == ["docker", "buildx", "build"]]
        self.assertEqual(6, len(builds))
        self.assertEqual(2, agents.call_count)
        self.assertEqual(["amd64", "arm64"], [call.args[1] for call in agents.call_args_list])
        for platform in release.ARCHITECTURES:
            self.assertEqual(3, sum(args[args.index("--platform") + 1] == platform for args in builds))
        self.assertFalse(any(arg.startswith("MINICLOUD_AGENT_RID=") for args in builds for arg in args))
        self.assertEqual(images(), release.read_json(self.root / ".local/artifacts/build/images.json"))

    def test_workflow_remains_server_only_and_publication_follows_verification(self):
        workflow = (Path(__file__).resolve().parents[2] / release.WORKFLOW).read_text()
        for required in ("tags: ['server-v*']", "environment: server-release", "--latest=false",
                         "--anonymous-pulls", "--new-bundle-format", "needs: contracts"):
            self.assertIn(required, workflow)
        for forbidden in ("releases/latest", "dotnet publish", "install.ps1", "cli-release", "--insecure"):
            self.assertNotIn(forbidden, workflow)
        self.assertLess(workflow.index("Gate missing handoffs"), workflow.index("docker/login-action"))
        self.assertLess(workflow.index("--anonymous-pulls"), workflow.index("gh release create"))


if __name__ == "__main__":
    unittest.main()
