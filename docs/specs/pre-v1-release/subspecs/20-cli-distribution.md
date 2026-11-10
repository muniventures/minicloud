# T20 — Remove prebuilt CLI distribution

Status: ready for implementation after T00. Documentation only; no release assets have been changed.

## Assignment

- **Repository:** public `muniventures/minicloud`.
- **Depends on:** T00's source-version, release-channel and CLI/server compatibility contract.
- **Can run in parallel with:** T10 DB repair investigation and T30 identity implementation. Coordinate with T40 on release naming and source metadata; their implementation files remain separate.
- **Owner:** one CLI distribution agent. Own `.github/workflows/cli-release.yml`, `.github/workflows/customer-deploy.yml`, `install.sh`, `install.ps1`, `scripts/verify-cli-release.py`, delivery sections of `docs/cli.md`, `docs/github-actions.md` and directly affected README/installation links and focused tests.
- **Shared-file rule:** reserve workflow, installer and delivery-document edits with the integration owner. T40 owns the new server-release workflow; T43 owns the distinct `install-server.sh`. Neither edits T20's files concurrently. T52/T62 retain command-feature ownership; T80 updates final source pins after their merges.

## Outcome and boundary

Stop publishing and depending on Minicloud-provided CLI executables. Keep the CLI source, commands, canonical API contracts and reusable customer-deployment workflow. Users and ephemeral CI runners build the existing `modules/cli/Minicloud.Cli.csproj` with .NET 10 from an identified canonical source revision. No new package manager, NuGet/npm package, containerized CLI product or command family is required.

This supersedes the **future binary-publication requirement** in [DB repair R5](../../database-release-repairs/05-canonical-cli.md). It does not undo the already merged removal of Intel Mac builds or require another binary release before removal. Published historical tags, checksums and assets remain immutable; no deletion of old releases is part of this task.

## Current source

| Path | Existing dependency to replace |
| --- | --- |
| `.github/workflows/cli-release.yml` | Four platform build jobs publish self-contained archives, checksum assets and a GitHub binary release. |
| `install.sh`, `install.ps1` | Download platform archives from a pinned release or repository-wide `releases/latest`. |
| `.github/workflows/customer-deploy.yml` | `Install Minicloud CLI` downloads `minicloud-linux-x64.tar.gz`; `minicloud_version` defaults to `latest`. |
| `modules/cli/Minicloud.Cli.csproj` | Existing .NET 10 executable referencing `modules/contracts`; source material must include that dependency. |
| `scripts/verify-cli-release.py` | Existing version/help and mocked canonical API smoke assertions to retain for source-built output. |

## Implementation contract

1. Preserve existing installer paths as **CLI** entry points and preserve configuration inputs where applicable. Replace binary acquisition with clear, documented source-build behavior; never repurpose these paths as server installers. Require .NET 10 and fail before modifying an existing installation when prerequisites or source verification fail. Do not silently install an SDK or require elevated writes when a user-selected install directory works.
2. T00 records the supported source selector and initial default before implementation: an exact 40-character canonical commit SHA or an explicit CLI version tag resolved once to its commit. Replace the workflow's floating `latest` default with a reviewed exact source pin. T80 advances that pin to a merged revision containing the final auth/SSH/numbered-deployment client before final validation. Reject unrecognized selectors; never treat `server-v*`, a branch name or repository-wide latest release as a CLI source version. Print the resolved revision without credentials.
3. Obtain source from the canonical public repository into a separate build directory, never over the customer's checkout. A Git checkout must verify its resolved HEAD equals the expected full SHA. An archive must verify a trusted published SHA256 before extraction and must resolve to that same revision; do not trust a checksum supplied only by an unverified adjacent download. Use one of these existing source acquisition paths, not a new distribution service.
4. Publish the existing CLI project with the version and source revision from that pin. For example, `dotnet publish modules/cli/Minicloud.Cli.csproj --configuration Release --output <temporary-output>` is the build entry point; choose explicit runtime settings and install all required output files so the result runs on the documented target. Preserve the public `minicloud` command and Windows executable naming. Do not advertise an untested architecture or require an Intel Mac CI build.
5. Build in temporary storage, verify version/help before replacing an installed CLI, and clean up only this invocation's temporary files. Preserve prior installed output on acquisition/build failure. Document SDK versus runtime requirements for the chosen publish mode.
6. Update the reusable customer workflow to build that pinned source with a pinned `actions/setup-dotnet` action on its ephemeral runner. Keep the customer's source checkout and working directory intact. Preserve workflow inputs, token masking, generated configuration, instance selection, service scope and the existing CLI deploy invocation. Do not replace the CLI with direct API/SSH deployment or introduce a hosted default instance.
7. Remove CLI binary archive/publication and installer-asset publication jobs. Keep source build, CLI contract tests, version/help and mocked API smoke checks in CI. Source/version metadata may continue using the existing CLI version sequence; server releases use independent `server-v*` tags and explicit server discovery. No source pin may accidentally resolve from a server release.
8. Align supported installation instructions, workflow input examples and `scripts/verify-cli-release.py` terminology with source builds. A developer workstation/CI runner may need source and an SDK; installing the packaged **server** must not need either. List known callers from a repository search before changing installer inputs, and hand off private workflow callers without editing historical private product modules.

## Acceptance criteria and handoff

- A clean supported workstation builds and installs a version-pinned CLI, then version/help and canonical instance/auth/list smoke checks pass; build output identifies the selected source revision.
- A disposable customer-workflow test builds the CLI separately from customer source, creates the same config and reaches the same mocked canonical API path. No prebuilt archive or repository-wide latest download is requested.
- Invalid/moved selectors, wrong archive digest, missing SDK, failed build and incompatible server fail with actionable messages and preserve existing installation/customer files. Secrets stay out of logs and source metadata.
- A normal PR still runs CLI source/contract/smoke checks. A server tag cannot create a CLI binary release, and CLI changes cannot select server assets. No Intel Mac job is required.
- The agent runs focused local checks for changed installer/workflow/client behavior and records exact commands/results in its PR. Cloud agents do not publish releases, alter branch protection, provision hosts or use production credentials.
- Hand off the source pin/version resolution, changed callers and test results to T40 and the integration owner. Codex owns the final clean-machine, cross-feature and hosted integration tests after the implementation PRs merge; a focused pass is not release qualification.

## Copy-ready Copilot task

Implement T20 from this document on current public `main`, preserving the existing CLI/API behavior. Stop publishing prebuilt CLI binaries and migrate installers plus the reusable customer workflow to a verified, version-pinned build of the existing CLI source. Keep CI and historical releases. Stay within the ownership boundary, include focused regression checks, and open one reviewable PR with unresolved callers or evidence gaps explicitly listed. Do not deploy, publish, change repository protection, or start final hosted acceptance.
