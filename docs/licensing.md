# Licensing

| License | Minicloud source covered |
| --- | --- |
| [MPL-2.0](../LICENSES/MPL-2.0.txt) | `modules/cli/`, `modules/contracts/`, `modules/tests/Minicloud.Cli.Tests/`, CLI installers, CLI release and customer deploy workflows, and CLI packaging and verification scripts. |
| [AGPL-3.0-only](../LICENSES/AGPL-3.0-only.txt) | API, dashboard, worker, runtime agent, telemetry, server-side `modules/common/`, server tests, deployment/runtime tooling, and other Minicloud-authored files. |

The CLI references only the MPL client contracts project. That project contains
wire types and validation with no project or package dependencies. The AGPL
control plane references the same contracts project and its separate server
implementation. The MPL files are available under MPL 2.0 without an Exhibit B
restriction on secondary licensing. See [MPL section 3.3](https://www.mozilla.org/en-US/MPL/2.0/#distribution-of-a-larger-work).

The CLI archives include their license, dependency notices, and a link to the
corresponding source tag. Hosted Minicloud dashboard builds expose a Source code
link. For a modified instance available over a network, configure
`MINICLOUD_SOURCE_URL` for Compose or `VITE_MINICLOUD_SOURCE_URL` at dashboard
build time to point to the corresponding source for that running version.
See [AGPL section 13](https://www.gnu.org/licenses/agpl-3.0.html#section13).

Third-party code and base images carry their own terms. Keep upstream notices
with redistributed builds.
