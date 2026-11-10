# Server release assets (T40)

This is the **server** channel, independent of source-built CLI delivery. No
server assets have been published or qualified by this change. Existing CLI
installers and the reusable customer deployment workflow retain their meaning.

The tag-only publication job in `.github/workflows/server-release.yml` accepts
`server-vMAJOR.MINOR.PATCH[-prerelease]` from canonical `main`. Pull requests run
synthetic contract tests without registry/signing credentials. Publication
requires the maintainer-controlled `server-release` environment, GHCR write
permission and GitHub OIDC; configure environment approvals/tag restrictions
before enabling releases. Neither running tests nor merging T40 authorizes
creating a release tag, publishing images or deploying anything.

## Required handoffs

The workflow deliberately fails **before registry login or image publication**
until all of these are present in the selected source revision:

- T41's worker Dockerfile declares `ARG MINICLOUD_AGENT_RID` and uses it for the
  embedded agent publish. T40 passes `linux-x64` for `linux/amd64` and
  `linux-arm64` for `linux/arm64`, then copies the executable out of each worker
  image and checks the actual ELF machine type. This does not qualify managing
  customer VPSs of another architecture.
- T41's dashboard accepts validated public configuration at startup, with no
  `VITE_*` build arguments. Root build contexts, API/dashboard port 8080 and the
  existing .NET entry points remain the frozen image interface. T40 does not
  edit the existing Dockerfiles or Compose definition.
- T41/T43/T44 supply `packaging/server/bundle/` with `compose.yml`,
  `configuration.schema.json`, `configuration.example.json`, `install-server.sh`,
  `maintenance.sh`, `release-notes.md` and `upgrade-prerequisites.md`, plus any
  supporting scripts/configuration. Only regular files/directories are allowed.
  Keep operator secrets and generated execution output out of these assets.
- The integration owner/T80 supplies `packaging/server/release.json` with real
  tested compatibility, schema versions and resolved dependency digests. No
  placeholder final release version, source pin, digest or compatibility range
  is committed here.

Compose must reference exactly the three product images and the four dependency
images. Each image may be a literal manifest-pinned digest or the corresponding
`${MINICLOUD_API_IMAGE:?required}`, `${MINICLOUD_DASHBOARD_IMAGE:?required}`,
`${MINICLOUD_WORKER_IMAGE:?required}`, `${MINICLOUD_POSTGRES_IMAGE:?required}`,
`${MINICLOUD_OPENBAO_IMAGE:?required}`, `${MINICLOUD_KRATOS_IMAGE:?required}` or
`${MINICLOUD_CADDY_IMAGE:?required}` placeholder (plain `${NAME}` also works).
Packaging substitutes only these image values and rejects source builds,
floating/unlisted images or missing manifest images. Other installation inputs
remain uninterpolated; packaging never needs operator credentials.

## Manifest schema 1

`scripts/server-release/release.py` is the executable, strict manifest/input
validator. Unknown fields, duplicate JSON keys, partial CLI intervals,
noncanonical product repositories and unsupported architectures are rejected.

| Field | Required value |
| --- | --- |
| `schemaVersion` | Integer `1` |
| `serverVersion` | Exact semantic version without the `server-v` prefix |
| `architectures` | `["linux/amd64", "linux/arm64"]`, in that order |
| `dependencies` | Object with exactly `postgres`, `openbao`, `kratos`, `caddy`; each value is an OCI reference ending in `@sha256:<64 lowercase hex>` |
| `applicationMigration` | Exact latest EF migration ID present in this source revision |
| `identitySchemaVersion`, `configurationSchemaVersion` | Positive integers from the owning task |
| `cliCompatibility` | Exactly `protocolVersion: 1`, `minimumVersion`, `maximumExclusiveVersion`, `sourceRevisions` |

These are **all** the input fields in `release.json`. Both CLI version bounds
may be `null` when the nonempty list of full lowercase source SHAs identifies
tested clients. Otherwise both bounds must be semantic versions forming a
nonempty half-open interval. Source SHAs must be unique. Compatibility means
matching protocol **and** (explicit source SHA **or** version interval), not
matching the server's version number. T80 owns the final tested claims.

The producer adds `releaseTag`, the exact checked-out `sourceRevision`, and
`images: {api, dashboard, worker}` with canonical
`ghcr.io/muniventures/minicloud-<component>@sha256:<digest>` values. The workflow
builds both platforms separately, assembles each multi-architecture index,
records the exact source revision in image labels, then signs the immutable
index and attaches source/workflow provenance. Dependency indexes must also
contain both supported platforms.

The final detached `server-manifest.json` additionally contains
`bundle: {name, sha256}` and `files: {relativePath: sha256}`. The archive is
`minicloud-server-<VERSION>.tar.gz`. To avoid a circular archive checksum, it
contains `server-release.json` (the identical manifest core without `bundle`
and `files`) rather than the detached archive-binding manifest. The verifier
requires byte-for-byte agreement and validates every archive member without
extracting it; links, traversal, extra/missing and duplicate members fail.
Keep the detached manifest alongside the archive when installing.

The bundle includes generated `images.env` and digest-rendered Compose. The API
must mount `server-release.json` read-only and set
`ServerRelease__ManifestPath` to its container path. This is verified package
metadata, not a configurable compatibility claim. Invalid/missing configured
files fail API startup. A source build with no configured file reports no
packaged version or compatibility claims. Anonymous `GET /api/instance` retains
`product`, `protocolVersion`, `appDomain` and adds `serverVersion`,
`sourceRevision`, `cliCompatibility`; it does not expose image/dependency data.

## Trust and exact-release verification

Install Cosign v2.6.5 independently from its official distribution. Trust is
Sigstore's public production TUF trust root, Fulcio issuer
`https://token.actions.githubusercontent.com`, the **exact** certificate
identity
`https://github.com/muniventures/minicloud/.github/workflows/server-release.yml@refs/tags/<selected-server-tag>`,
and transparency/inclusion verification. Never disable certificate, transparency,
or inclusion checks, use a wildcard workflow identity, or trust a checksum
solely because it was downloaded next to an archive.

For an actual published `$tag`, download these assets from its **exact** release
URL into `$assets` (the repository's ignored `.local/artifacts/` for development):
`server-manifest.json`, `minicloud-server-<VERSION>.tar.gz`, `install-server.sh`,
`verify-server-release.py`, `checksums.txt`, `checksums.txt.sigstore.json`.
Do not execute the downloaded installer or verifier before independent
verification. From the download directory:

```sh
cosign verify-blob --new-bundle-format \
  --bundle checksums.txt.sigstore.json \
  --certificate-identity "https://github.com/muniventures/minicloud/.github/workflows/server-release.yml@refs/tags/$tag" \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  checksums.txt
sha256sum --check --strict checksums.txt
python3 verify-server-release.py verify --directory . --tag "$tag" --anonymous-pulls
```

The verifier requires Python 3, Cosign, Docker Engine, Compose/Buildx and network
access to GitHub/GHCR, Sigstore's trust-root/transparency infrastructure and the
pinned dependency registries. It verifies the signed asset inventory before
parsing package metadata, checks each product signature/provenance against the
same source and exact workflow identity, verifies both platform definitions,
and optionally pulls every image/platform with a fresh empty Docker auth
configuration. Release CI makes those anonymous pulls mandatory; it never
changes GHCR visibility to conceal a permission failure. The maintainer must
enable public package visibility before a release can pass that gate.

Only then may T43's installer perform installation/preflight. Its reserved URL is
`https://github.com/muniventures/minicloud/releases/download/server-v<VERSION>/install-server.sh`.
The verifier does not install, extract, start services or mutate customer VPSs.
Image verification/pulls use the local Docker cache but perform no deployments.

Server GitHub releases use `make_latest=false` (`gh --latest=false`), with
prereleases marked accordingly. Optional `server-channel/stable.json` discovery
is not published by this workflow; exact release selection needs no channel.
Never use repository-wide latest-release discovery or CLI executable archives.

## Focused checks and remaining evidence

From the repository root:

```sh
python3 -B -m unittest discover -s scripts/server-release -p 'test_*.py' -v
dotnet test modules/tests/Minicloud.Tests/Minicloud.Tests.csproj \
  --configuration Release --artifacts-path .local/artifacts/t40-dotnet \
  --results-directory .local/artifacts/t40-test-results \
  --filter 'FullyQualifiedName~ServerReleaseMetadataTests|FullyQualifiedName~Instance_discovery' -m:1
```

Synthetic tests cover tampering, mixed releases, provenance/source disagreement,
architecture definitions, worker RID/ELF checks, compatibility shape, Compose
pins, anonymous-pull configuration and missing handoffs. They do **not** prove
real multi-architecture image builds, OIDC signing, live transparency inclusion,
registry visibility/anonymous pulls, universal dashboard operation, successful
installation, or final compatibility/hosted qualification. Those checks require
the merged handoffs and authorized release/validation stages.
