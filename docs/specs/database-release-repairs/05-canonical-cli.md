# 05 — Deliver the matching canonical CLI

Status: specification only; no release publication authorized by this document.
Date: 2026-11-10

## Outcome

Users can install an identifiable CLI built from the canonical application/environment
client and use it against the released API. Finish delivery through the existing
release mechanism. Removing prebuilt CLI distribution remains a separate pre-v1 item.

## Evidence and uncertainty

The release handoff records an installed v4 CLI using old routes and a locally built
candidate; matching publication and installation were not completed. A temporary DLL
is not a supported delivery path. This specification makes no claim about current
remote tags, release assets or a user's installed executable; inspect those when
implementing. Released product baseline: `05547e5dc7397f66cc0099b0f86df3cdb2570d90`.

Current client source uses `/api/apps` and `/api/environments/{id}`. Its project
default version is `3.1.0`; tagged release builds override it. Prove the delivered
version and source revision from the built artifact, not that default or the handoff.

## Current entry points

| Source | Relevant behavior |
| --- | --- |
| [MinicloudApiClient.cs:31](../../../modules/cli/Api/MinicloudApiClient.cs#L31) | Discovery and canonical application/environment routes. |
| [Minicloud.Cli.csproj:8](../../../modules/cli/Minicloud.Cli.csproj#L8) | Default assembly/package version. |
| [cli-release.yml:26](../../../.github/workflows/cli-release.yml#L26) | Tagged version and main ancestry; build matrix at line 43. |
| [cli-release.yml:94](../../../.github/workflows/cli-release.yml#L94) | Versioned publish; tag-only release at line 180 and required asset checks at line 201. |
| [install.sh:30](../../../install.sh#L30), [install.ps1:14](../../../install.ps1#L14) | Existing archive names, latest/pinned selection and checksum verification. |
| [verify-cli-release.py:113](../../../scripts/verify-cli-release.py#L113) | Packaged version/help and authenticated canonical API smoke check. |
| [CliApplicationApiClientTests.cs:13](../../../modules/tests/Minicloud.Cli.Tests/CliApplicationApiClientTests.cs#L13) | Distinct application and environment endpoint contract. |
| [Upgrade guide:53](../../application-environment-upgrade.md#L53) | Canonical IDs and matching-client release requirement. |

## Minimal implementation

1. Identify the approved source revision containing the released contracts and any
   required repair fixes. Check existing release metadata before choosing the next
   version; do not reuse a published version or assume a particular tag is absent.
2. Run the existing CLI contract tests and packaged smoke verification against that
   revision. Repair only failures preventing the matching client from working.
3. Remove Intel Mac as a mandatory build/release dependency. Align the existing
   matrix, required archive list and asset counts so publication does not wait for
   `osx-x64`. Keep checksums and provenance checks for every distributed artifact.
   An authorized repository operator must also remove only that job's required
   status-check context while preserving the other checks. Do not leave a required
   job that the workflow no longer emits, or rely on repeated temporary waivers.
4. Use the existing tagged release workflow, preserving public installer names and
   archive conventions. Manual workflow runs produce CI versions and do not publish
   a normal release. Publication requires the separately applicable release authority.
5. Install the resulting client through the supported installer on a clean supported
   host, then verify its resolved executable, version, source and canonical API use.
   Update only stale delivery instructions needed to reproduce that result.

## Acceptance criteria

- The published version identifies the intended revision and contains the canonical
  client. Its default advertised installation path resolves to that version.
- Existing CLI tests pass, including application/environment target separation.
- The packaged verifier passes for each remaining distributed release target:
  Linux x64/arm64, macOS arm64 and Windows x64. Intel Mac is not a release gate.
- Installation verifies checksums; release provenance validation remains enabled.
- A clean installation can discover an instance, authenticate and list canonical
  applications/environments. Use disposable credentials; do not retain them in logs.
- A deployment to a disposable explicit environment works through the existing CLI
  flow during repair 06 acceptance; no old root-ID or `/v1` alias is introduced.
- Documentation states the actual supported targets/version and does not promise
  an Intel artifact that was not published. An uninstalled local build is insufficient.

## Dependencies and parallel ownership

This lane can start immediately and run independently of agent, provider and
database repairs. Package against the final approved API contracts; coordinate the
single live CLI deployment check with repair 06 instead of duplicating fixtures.

This lane exclusively owns `.github/workflows/cli-release.yml`, required installer
delivery fixes, `scripts/verify-cli-release.py`, CLI contract tests and the delivery
sections of `docs/cli.md`. Change CLI source only for a reproduced contract failure.
Coordinate any shared upgrade-guide edit with its owner. Other lanes must not edit
release workflow or installer files concurrently.

## Non-goals and stop condition

No new package manager, installer rewrite, old-client compatibility, CLI command
features or prebuilt-removal implementation. Stop after reproducible supported
installation and matching-client acceptance; future distribution policy stays deferred.
