# Self-hosting Minicloud

The packaged self-hosting stack is under active development. T40's server
release foundation is merged, but no server images or release bundle have been
published or qualified yet. Do not create a `server-v*` release or expose a
host based on these source assets. Once a versioned release is available, use
its signed installer and release documentation; never build a production host
from a checkout or substitute floating image tags.

The public control-plane installer, setup flow and coordinated maintenance
now have source implementations. Installation and maintenance have not been
qualified on a real host. The [maintenance guide](server-maintenance.md)
documents `backup`, `upgrade`, `restore`, `restore-confirm` and `transfer`.
These operations fence and drain through the maintenance authority and never
stop accepted work.

`transfer`, and the transfer inside `restore`, revoke an old host only when it
next reaches this database. An old host running on its own restored database
copy, or an unreachable host that still holds live provider credentials, is
**not** stopped. You must shut it down or rotate those credentials yourself.
The independently encrypted recovery-dependency archives (`*-recovery`) are not
full backups. The
[versioned installer/manual guide](server-installer.md) documents exact-release
verification, protected inputs, shared staged Compose operations and resumable
readiness. It is not a claim that a server release exists.
`compose.self-host.yml` is the release-stack
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

This section describes the source bundle consumed by the installer and its
manual staged primitives. It is not yet a qualified installation procedure.

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
| `MINICLOUD_GOOGLE_OIDC_ENABLED` | Defaults to `false`; the installer derives it only from explicit `googleOidc.enabled`. Shared API/Kratos switch, not a qualification assertion. |
| `MINICLOUD_SECRETS_DIR` | Root-owned directory (`0700`) holding the files below. |
| `MINICLOUD_INSTALLATION_ID` | Persistent non-secret installation identity from host metadata, shared by API and bootstrap inputs. Never regenerate it on restart. |
| `MINICLOUD_HOST_ID` | Optional non-secret host-local maintenance identity from `<root>/host-id`, written by the packaged maintenance tooling and never backed up. Leave empty for manual Compose; `transfer` binds the maintenance authority to it. |
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
  password must match `kratos-db-password`, URL-encoded. Explicit Google opt-in
  also imports the single fixed Google provider JSON here; never dump this file
  or a rendered Compose environment into logs.
- `openbao-server.crt`, `openbao-server.key` and `openbao-ca.crt`: a private CA
  and a server certificate whose SAN includes `openbao`. The API and worker
  trust this CA only for OpenBao, and public HTTPS keeps the system trust store.
- `openbao-approle/role-id`, `openbao-approle/secret-id`: written by
  `openbao-configure`.
- `deploy-ssh-public-key`, plus `ssh/deploy-key` and `ssh/host-pins`. Each
  `host-pins` line is `<host> SHA256:<fingerprint>`, and the worker refuses
  unpinned hosts.

### Optional packaged Google OIDC

Password-only installation remains the default. Omitting `googleOidc`, or
setting it to `{"enabled":false}`, needs no Google input. Do not put Google
credentials in public configuration, dashboard runtime config, release assets,
shell arguments or issue/PR bodies.

For an explicitly opted-in **new installation**, before `preflight`/`prepare`,
add this non-secret object to the protected configuration:

```json
"googleOidc": {
  "enabled": true,
  "credentialsFile": "/etc/minicloud-operator/google-oidc.json"
}
```

The credential input must be a canonical absolute, nonsymlink root-owned
`0600`/`0400` JSON file in a root-owned `0700` directory, with protected
ancestors. Keep it outside installation, recovery and release-asset directories.
Use `google-oidc.credentials.example.json` only as a **synthetic shape example**;
replace its values privately with an operator-provided Google **web** client:

```json
{
  "clientId": "synthetic-client.apps.googleusercontent.com",
  "clientSecret": "synthetic-not-a-google-secret"
}
```

Only these two unique fields are accepted. Client IDs must end in
`.apps.googleusercontent.com`; secrets must contain only ASCII letters, digits,
`-` and `_` (1–256 characters), preventing dotenv interpolation/injection.
The installer imports the credentials into protected `secrets/kratos.env`,
alongside the original DSN/cookie/cipher secrets. It does not put credential
values in host metadata or process arguments. Both Kratos services consume this
protected file; neither API nor dashboard receives the provider secret.

The shared opt-in switch enables API Google navigation and Kratos OIDC
together. Compose derives the callback base from the validated API origin.
The only Google redirect URI is:

```text
<apiUrl>/api/account/oidc/self-service/methods/oidc/callback/google
```

The fixed, read-only `google-oidc.mapper.jsonnet` maps a verified Google email
to `traits.email`; arbitrary providers/mappers/redirect overrides are not
accepted. Keep password login enabled, public registration disabled and
Kratos private. Sign in with a password first and link Google in account
settings; Google login only admits identities already bound to Minicloud.
The reverse proxy must preserve the public API Host for callback validation.

Keep the original configuration **and** credential input available unchanged
for staged commands, resume, upgrade and restore. Completed credentials are
validated, never silently rewritten or regenerated; duplicate/unknown
`kratos.env` keys and provider mismatches fail closed. Back up the input
independently in protected operator custody; encrypted installation backups
also contain the imported provider secret. Restore that external input at its
original path before invoking the helper. Existing password-only configurations
remain valid unchanged. The installer still requires exact original
configuration matching: this option does **not** add an in-place enable/disable
or credential-rotation workflow for an established installation. Never edit
installation metadata to bypass that boundary.

Enabling this operator option does **not** claim live qualification. The current
API contract has `GoogleEnabled`, not a separate `GoogleQualified` setting.
Live Google sign-in/link/422 behavior, separate-origin HTTPS/CORS/CSRF, proxy
Host handling, SMTP recovery and browser/Grafana recovery revocation remain
deferred in [#44](https://github.com/muniventures/minicloud/issues/44).
The prior disposable Kratos v1.3.1 checks are not a live Google qualification.

### Startup order

1. `database-initialize` creates or verifies the `minicloud` and `kratos` roles
   and databases under a session advisory lock. It refuses unexpected database
   owners and elevated/inherited service-role privileges before creating state.
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
installer invokes this producer and the browser setup transport consumes the
same durable state. These implementations do not make the source bundle a
qualified installation.

### Protected host bootstrap interface

The opt-in `api-bootstrap` maintenance service uses the same API image and
application database as the running API. It publishes no port and does not
start the API, worker or OpenBao administration. The installer/manual caller
must preserve one `installationId` and original stable `operationId` in host
metadata. Set `MINICLOUD_INSTALLATION_ID` to that same installation identity;
all mutating request files use both IDs. The API reads the operation identity
from the shared durable bootstrap record, not another environment value.

Before invoking it, prepare root-owned `0700` directories
`$MINICLOUD_SECRETS_DIR/bootstrap/input` and `bootstrap/output`. Input
`request.json` must be a regular, non-symlink root-owned `0600`/`0400` file;
the input directory is mounted read-only. Output is mounted separately
read-write; the producer creates each new token file exclusively as `0600`.
Never put passwords/tokens in environment variables, arguments or query strings.
Use the fields and replay rules in the [bootstrap contract](bootstrap.md).

Source bundle interface examples (not a qualified installation procedure):

```sh
# Default command: bootstrap status --input-file <protected container path>
docker compose --profile maintenance run --rm -T api-bootstrap

# Override only the API arguments; preserve its dotnet entry point.
docker compose --profile maintenance run --rm -T api-bootstrap \
  bootstrap prepare --input-file /run/minicloud-bootstrap/input/request.json \
  --output-file /run/minicloud-bootstrap/output/setup-token-1

# Equivalent protected redirected stdin; never echo a token to construct it.
docker compose --profile maintenance run --rm -T api-bootstrap \
  bootstrap status --stdin < "$MINICLOUD_SECRETS_DIR/bootstrap/input/request.json"
```

`rotate-token` also requires a new `--output-file`. `complete` consumes protected
setup fields; completed host replay accepts the original IDs alone and never
resets credentials. No seed logic is implemented by Compose. The service gates
on successful application migrations but deliberately not healthy Kratos:
ready host replay must remain usable without contacting identity services.
Start/wait for healthy Kratos before operations that require it; otherwise the
shared command returns sanitized nonzero failures. Do not equate service exit
or `/health` alone with onboarding completion: inspect shared bootstrap status
and preserve awaiting-DNS/TLS, awaiting-operator, ready and failed distinctions.

The same entry point exposes `maintenance <status|fence|drain-status|release|transfer>`
for upgrade, restore and host-transfer fencing. See
[server maintenance authority](server-maintenance-authority.md). Operators
normally run these through the installer's `maintenance` operations described
in the [maintenance guide](server-maintenance.md) rather than directly.

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
The Agent healthcheck verifies the current sink credential against OpenBao
over TLS; a retained nonempty token file alone cannot advertise readiness.

OpenBao starts sealed after every host or container restart. Until an operator
unseals it, the agent cannot authenticate and secret operations fail as
unavailable (`SecretStoreUnavailableException`). Other data stays intact.

To rotate the AppRole secret ID:

1. Generate a new one with the root or an operator token.
2. Replace `openbao-approle/secret-id`.
3. Restart `openbao-agent`.

The policy does not cover the optional Grafana operator secret path.
