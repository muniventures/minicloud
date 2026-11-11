# Self-hosting Minicloud

The packaged self-hosting stack is under active development. T40's server
release foundation is merged, but no server images or release bundle have been
published or qualified yet. Do not create a `server-v*` release or expose a
host based on these source assets. Once a versioned release is available, use
its signed installer and release documentation; never build a production host
from a checkout or substitute floating image tags.

The public control-plane installer, setup flow, upgrade and recovery procedures
are being completed in T42–T44. `compose.self-host.yml` is the release-stack
entry point and includes the versioned Compose definition in
`packaging/server/bundle/compose.yml`. It requires an exact release's digest
`images.env`, runtime `server-release.json`, validated host configuration and
root-protected secret files under `MINICLOUD_SECRETS_DIR`. The installer will
create and validate this state before starting services. See the [packaged
installation contract](specs/v1.0-self-hosted-installation.md) and [server
release trust/interface](../packaging/server/README.md) for implementation
status and non-negotiable boundaries.

For local development only, start the synthetic PostgreSQL and OpenBao
development services:

```sh
docker compose -f compose.dev.yml up -d postgres openbao openbao-init
```

`compose.dev.yml` uses a disposable OpenBao development root token and must
never be used for a hosted installation. Run the API/dashboard from source using
the commands in the repository [README](../README.md); the production stack's
OpenBao, Kratos and Caddy configuration is separate.

Server containers expose only the dashboard/API behind Caddy on TCP 80/443.
PostgreSQL, OpenBao and Kratos administration stay on the private Compose
network. Database credentials, encryption keys, OpenBao AppRole material and
deployment SSH keys are mounted from protected files, not image build
arguments or public dashboard configuration. OpenBao recovery material and
encrypted backups must remain independently recoverable off-host; container
restart is not a substitute for an unseal or recovery procedure.
