# Public candidate handoff (T80 #37)

## Selected identifiers, not a qualified release

- Server candidate: **`0.0.1-rc.1`**, unpublished metadata only.
- CLI source: **`2a5ae0ccea6c3dd680b4146586ec0668c60c8b93`**, actual canonical
  public `main` containing the merged authentication, SSH and numbered-deployment
  clients. Public prerequisite repairs #70–#75 and ledger #76 were already
  merged at this source. This is not a self-reference to the metadata commit.
- `packaging/server/release.json` is the strict schema-1 pipeline input, not a
  generated/signed release manifest. The existing producer adds the actual
  checked-out server source SHA and real product-image digests at release time.
- CLI protocol `1`, only the source SHA above in `sourceRevisions`, and both
  version bounds `null`: an explicit candidate allowlist, **not measured runtime
  compatibility** or a claim that arbitrary CLI versions work.
- Latest application migration: `20261111142623_DeploymentNumbers`.
  Identity and configuration schema versions are both `1`, the initial bundled
  `identity.schema.json` and `configuration.schema.json` contracts (including
  the merged optional Google configuration); JSON Schema draft identifiers and
  Kratos's own software version are not these Minicloud schema versions.

The public-only scope decision in #37 supersedes the older private-integration
prerequisite: #36 is backlog, not passed; #44 is later optional-provider/auth
validation, not a code-completion blocker. No private source, readiness commit
or private candidate identifier was inspected or recreated.

The customer workflow defaults to the selected CLI SHA. Existing CLI installer
names/interfaces are unchanged; their historical default remains separate.
Use the explicit `MINICLOUD_VERSION` override in the [CLI guide](cli.md).

## Dependency index evidence

On 2026-11-12, anonymous read-only HTTPS requests to
`https://registry-1.docker.io/v2/<repository>/manifests/<tag>` returned the
following multi-architecture indexes. For each, SHA-256 of the **raw response
bytes** matched `Docker-Content-Digest`, and the index listed both `linux/amd64`
and `linux/arm64`. No image layers were downloaded and no images were pulled/run.
The release input pins index digests, not these mutable discovery tags.

| Dependency / discovery tag | Index digest | Required platforms |
| --- | --- | --- |
| `docker.io/postgres:16-alpine` | `sha256:aa6cb33cf4c19c765cefcf48d96176258800870a9a1e930ad126200f9d825139` | `linux/amd64`, `linux/arm64` |
| `docker.io/openbao/openbao:2.2.1` | `sha256:62f582f85e405ef00d48f678c19863d096a2bc0c05e0e5a9bbccd676a1c7316c` | `linux/amd64`, `linux/arm64` |
| `docker.io/oryd/kratos:v1.3.1` | `sha256:fe2428f103a6240c064b6ea77d3088610865c55fcebccb30355857eccdc25b4b` | `linux/amd64`, `linux/arm64` |
| `docker.io/caddy:2.6.2-alpine` | `sha256:25a0097607868fb05a89a5ab9fea2f2ea4cecdc89d887d7dcee8c778a21b9e1f` | `linux/amd64`, `linux/arm64` |

Version provenance: PostgreSQL follows `compose.dev.yml` and the database
initializer contract; OpenBao follows `bundle/openbao-handoff.md` and its
focused contract; Kratos follows `bundle/kratos.yml`; Caddy preserves the
existing `scripts/github-actions/minicloud-deploy/test-caddy-permissions.py`
baseline. This is not a dependency-upgrade or vulnerability qualification.

Raw index responses, response headers and timestamped resolution summaries
remain only in git-ignored `.local/artifacts/t80-registry/` in the implementation
worktree. No credentials or anonymous authorization tokens are recorded.
Index membership proves neither runnable platform images nor stack compatibility,
publisher provenance, security support or registry availability at installation.

## Deferred validation and release gates

No manual tests, builds, dependency installs, CI reruns, image pulls/runs,
credentials, hosted changes, deployment, tags or publication were performed.
The focused checked-in candidate/payload/source-pin regression is authored
**unrun**. Automatic PR CI is separate evidence, not T90/T91 acceptance.

After explicit authorization, the focused commands already documented in
[server packaging](../packaging/server/README.md#focused-checks-and-remaining-evidence)
remain:

```sh
python3 -B -m unittest discover -s scripts/server-release -p 'test_*.py' -v
python3 -B -m unittest discover -s scripts/self-hosting -p 'test_*.py' -v
dotnet test modules/tests/Minicloud.Tests/Minicloud.Tests.csproj \
  --configuration Release --artifacts-path .local/artifacts/t80-dotnet \
  --results-directory .local/artifacts/t80-test-results \
  --filter 'FullyQualifiedName~ServerReleaseMetadataTests|FullyQualifiedName~Instance_discovery' -m:1
```

T90 #38 must still establish combined/runtime compatibility of this exact
source/protocol pairing, real PostgreSQL/OpenBao/Kratos/Compose behavior,
identity/auth flows, recovery and both control-plane/target architecture
combinations. Existing backend CI fixture failures are not repaired or
presented as passing by this metadata change. T91 #39 owns disposable-host
acceptance; #44 retains later auth/optional-provider validation.

Real two-architecture product builds, agent ELF verification, signing/provenance,
public GHCR visibility and anonymous pulls, installation and recovery acceptance
remain later authorized gates. No `server-v0.0.1-rc.1` tag, assets or product
image digests are supplied or claimed. The metadata allowlist does not satisfy
the historical tested-only qualification requirement: record actual evidence
before publication rather than weakening the validator or inferring a pass.
