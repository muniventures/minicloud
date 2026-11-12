# Exact CLI delivery

The dedicated `cli-release.yml` source implementation restores future CLI
binary publication. **No new binaries were published or qualified by this
change.** Historical releases remain immutable and are not retroactively
authenticated by the new workflow. CLI `v*` and server `server-v*` are distinct;
neither installer uses repository-wide `latest`.

## Binary installation

Select an actual newly published signed **CLI** tag:

```sh
MINICLOUD_INSTALL_MODE=binary MINICLOUD_VERSION=v<exact-version> sh install.sh
```

```powershell
$env:MINICLOUD_INSTALL_MODE = "binary"
$env:MINICLOUD_VERSION = "v<exact-version>"
.\install.ps1
```

Acquire/review these canonical installer scripts over HTTPS or from a reviewed
source checkout before execution. Binary mode is the default, but an exact tag
is mandatory: there is deliberately no fabricated current version, automatic
downgrade, source fallback, SDK installation or server-channel selection.
Unsigned historical tags/missing new assets fail without replacing an installed
CLI. `MINICLOUD_REPO` is accepted only as `muniventures/minicloud`.

Supported targets are `linux-x64`, `linux-arm64`, `osx-arm64`, `win-x64`.
There is no Intel macOS target. Outputs are self-contained, **untrimmed**
single files with native runtime components included, plus license/notices and
exact source metadata. No separate .NET runtime/SDK is needed. Native OS
dependencies still apply: use supported glibc Linux (Ubuntu 24.04 is the
initial host baseline), system ICU/OpenSSL/zlib/libstdc++, Apple Silicon macOS
15+ and Windows 11 x64. Alpine/musl, Rosetta and cross-OS execution are not
qualified targets. Native four-platform clean-machine qualification remains
outstanding; cross-publishing is not evidence that these ran.

Unix installation needs Python 3 and trusted system CA roots; Windows uses
PowerShell/.NET HTTP/ZIP APIs. Installers acquire Cosign **2.6.5** from the exact
official GitHub release and authenticate it using architecture-specific SHA-256
pins embedded in the reviewed installer. These pins were read from the official
`cosign_checksums.txt`; changing them requires independent upstream review.
This bootstrap copy is isolated in an owner-only staging directory, not
installed as a system tool. No unverified helper/CLI executes.

Before extraction, the installer verifies:

1. Sigstore production issuer `https://token.actions.githubusercontent.com`,
   public production trust root/transparency inclusion and the exact identity
   `https://github.com/muniventures/minicloud/.github/workflows/cli-release.yml@refs/tags/<selected-tag>`.
   No wildcard identity or bypass flags are permitted.
2. The complete signed `checksums.txt` inventory: exactly the four versioned
   archives and `cli-provenance.json`. Selected archive and provenance hashes
   must match; adjacent unsigned checksums alone are never authentication.
3. Signed provenance schema/tag/canonical workflow/full source SHA, selected RID
   and per-file inventory; archive entries must be regular, listed, unique,
   path-safe and bounded. No link/traversal/type bypass is extracted.
4. Embedded `cli-release.json`, native executable `--version` with exact source,
   and `--help`, before replacing `minicloud`/`minicloud.exe`.

Replacement is atomic on the installation filesystem. Windows retains the
previous executable as a uniquely named `.minicloud-previous-*.exe`;
replacement failure (including a locked executable) leaves it working.
Versioned notices live under `.minicloud/<tag>-<source>/`. Existing instance,
authentication and application configuration are not touched.
`MINICLOUD_INSTALL_DIR` retains its existing default: `/usr/local/bin` on Unix
(sudo when required) or `%LOCALAPPDATA%\Minicloud\bin` on Windows. The directory
and ancestors must be trusted, not shared writable paths/reparse links.
Unix users may select `$HOME/.local/bin` and add it to PATH; Windows preserves
the existing user-PATH integration.

## Explicit exact source mode

Git and the .NET **10 SDK** remain supported; no binary verification failure
silently builds source:

```sh
MINICLOUD_INSTALL_MODE=source \
MINICLOUD_VERSION=<full-lowercase-canonical-main-commit-sha> sh install.sh
```

Use the same environment variables with `install.ps1`. Exact CLI `v*` source
tags are also accepted, pinned before/after acquisition against canonical
`main`. Branches, `latest`, private repositories and server tags are rejected.
The historical exact source default
`7416e1408ecd9d11f58c638f0caf1c28e5fa6e3f` is preserved **in source mode**;
select a merged source revision containing seed to obtain the new command.
Source builds are self-contained/untrimmed single files with version/SHA
metadata and version/help checks before atomic executable replacement.
No SDK is installed for the user.

The reusable source-built customer deployment workflow and its inputs/default
source SHA are unchanged. Existing customer workflows need not switch channels.
The old source-only T20 milestone and historical publication claims remain
historical facts; this new implementation deliberately supersedes its
future-binary prohibition when merged.

## Maintainer release boundary

Only canonical `main` commits selected by a new exact `vMAJOR.MINOR.PATCH`
with optional prerelease can enter publication. Actions are SHA-pinned, SDK
**10.0.404** and Cosign **2.6.5** are pinned. The workflow cross-publishes all
four RIDs, packages MPL/.NET/YamlDotNet notices and source links, signs the
inventory with GitHub OIDC, verifies it, and publishes **new** assets only,
with `make_latest=false`. The `cli-release` environment must have maintainer
approvals/tag restrictions configured before authorized use; no protections
are configured or changed by this source implementation.

Run native clean-machine installer/security regressions on each target and
separately qualify every provider/server-architecture lifecycle before claiming
seed support. Current candidate server metadata/publication is a genuine gate,
not a completed prerequisite. Merging this feature is not permission to tag,
publish, provision, purchase, deploy or claim those checks passed.
