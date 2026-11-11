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

## Stack reference

This section describes the source bundle that the installer (T43) will
prepare. It is not yet a supported manual installation procedure.

### Operator inputs

The bundle reads these values from the release environment. The image
references (`MINICLOUD_*_IMAGE`) come from the generated, digest-pinned
`images.env`.

| Variable | Purpose |
| --- | --- |
| `API_URL` | Public HTTPS API origin, such as `https://api.cloud.example.com`. |
| `CONSOLE_URL` | Public HTTPS dashboard origin. It is also used for CORS, the exact-origin CSRF check and Kratos return URLs. |
| `API_HOSTNAME`, `CONSOLE_HOSTNAME` | The Caddy site hostnames for the API and dashboard. These are also reserved from app routing. |
| `APP_DOMAIN` | The managed application domain suffix. |
| `MANAGE_DNS_RECORDS` | Optional. Defaults to `false`. |
| `MINICLOUD_SOURCE_URL` | Optional. The public source link shown in the dashboard. |
| `MINICLOUD_SECRETS_DIR` | Root-owned directory (`0700`) holding the files below. |
| `OPENBAO_ROOT_TOKEN_FILE` | Used only by the `openbao-configure` maintenance service. Unset during normal operation. |

The dashboard container receives only `MINICLOUD_API_URL`,
`MINICLOUD_APP_DOMAIN` and `MINICLOUD_SOURCE_URL`. At startup it validates them
and writes `runtime-config.json`, so one image serves any installation.

### Protected files

Create each file under `MINICLOUD_SECRETS_DIR` with mode `0600`, using
independent random values:

- `postgres-admin-password`, `app-db-password`, `kratos-db-password`
- `secret-encryption-key`
- `kratos.env`: credentials only. See `secrets.env.example`. The `DSN`
  password must match `kratos-db-password`, URL-encoded.
- `openbao-server.crt`, `openbao-server.key` and `openbao-ca.crt`: a private CA
  and a server certificate whose SAN includes `openbao`. The API and worker
  trust this CA only for OpenBao, and public HTTPS keeps the system trust store.
- `openbao-approle/role-id`, `openbao-approle/secret-id`: written by
  `openbao-configure`.
- `deploy-ssh-public-key`, plus `ssh/deploy-key` and `ssh/host-pins`. Each
  `host-pins` line is `<host> SHA256:<fingerprint>`, and the worker refuses
  unpinned hosts.

### Startup order

1. `database-initialize` creates or verifies the `minicloud` and `kratos` roles
   and databases. It refuses databases that exist with an unexpected owner.
2. `kratos-migrate` and `api-migrate` run once, then exit. `api-migrate` holds a
   PostgreSQL advisory lock, so concurrent runs serialize. It refuses a
   populated database that has no Minicloud migration history, and a database
   migrated by a newer release.
3. `kratos`, `api`, `worker`, `dashboard` and `caddy` start after their
   dependencies are healthy or complete. They start in that order.

Each `docker compose up` re-runs initialization and migrations idempotently,
so upgrades do not depend on first-volume scripts.

First-operator seeding uses the API executable's shared
[`bootstrap` host entry point](bootstrap.md), after migrations. The packaged
installer and browser setup transport are still separate follow-up work;
this command does not make the source bundle a qualified installation.

### OpenBao lifecycle

OpenBao uses persistent file storage and TLS 1.3 on the private network. After
the first start, initialize it once. Store the unseal keys and root token
off-host, never in OpenBao or in its data volume backup.

```sh
bao_cli="bao -address=https://openbao:8200 -ca-cert=/run/secrets/openbao-ca.crt"
docker compose exec openbao sh -c "$bao_cli operator init"
docker compose exec openbao sh -c "$bao_cli operator unseal"  # repeat until unsealed
```

Then run the configuration service, pointing `OPENBAO_ROOT_TOKEN_FILE` at a
temporary protected copy of the root token:

```sh
docker compose --profile maintenance run --rm openbao-configure
```

This service is idempotent. It creates the KV v2 mount, the scoped `minicloud`
policy and the AppRole (with a 24-hour periodic token), and writes the AppRole
credentials. Delete the temporary root-token copy afterwards.

`openbao-agent` exchanges the AppRole credentials for a periodic token and
renews it. It writes the token to a memory-backed volume, which the API and
worker read on each request.

OpenBao starts sealed after every host or container restart. Until an operator
unseals it, the agent cannot authenticate and secret operations fail as
unavailable (`SecretStoreUnavailableException`). Other data stays intact.

To rotate the AppRole secret ID:

1. Generate a new one with the root or an operator token.
2. Replace `openbao-approle/secret-id`.
3. Restart `openbao-agent`.

The policy does not cover the optional Grafana operator secret path.
