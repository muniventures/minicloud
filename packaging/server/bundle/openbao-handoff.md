# OpenBao runtime handoff

`openbao.hcl` uses a persistent file backend and TLS-only listeners. The
container must be attached only to the private control-plane network; do not
publish either listener port on the host. Mount persistent storage at
`/openbao/data` and the server certificate/key at the configured read-only
secret paths. The server certificate must be valid for `openbao`.

Initialize and unseal OpenBao outside this initializer. Keep recovery keys and
the root-token file in protected operator-managed paths, separately backed up
from the OpenBao data volume. Run `openbao-init.sh` through the Compose
`openbao-configure` maintenance service, which supplies `openbao-policy.hcl`
and receives the root credential only through `OPENBAO_ROOT_TOKEN_FILE`.
Persist and protect the `openbao-approle/` role-ID and secret-ID output
directory; do not store those credentials in OpenBao itself.

Run the Agent with `openbao-agent.hcl`. Mount the role and secret ID files
read-only at the configured Agent paths and mount the token sink directory as a
memory-backed shared volume that only the Agent mounts read-write; the API and
worker mount it read-only. The OpenBao server and Agent run as root
(`BAO_SKIP_DROP_ROOT`) so they can read root-only secret files. Mount that token file read-only into each consumer as
`OpenBao__TokenFile`, and trust the private CA through
`OpenBao__CaCertificateFile` rather than replacing the system trust store; do
not configure a root-token fallback. Preserve the
external role/secret ID files and recovery keys during backup and recovery.

The Agent healthcheck runs `openbao-ready.sh`, which reads the sink through a
protected file and checks the current token against OpenBao over verified TLS.
A nonempty sink alone is not readiness: consumers can keep the memory-backed
volume mounted while the Agent restarts. A sealed/unreachable server or revoked
token makes this check fail without printing the token or lookup response.
Compose startup gates consumers on this check; sealing a running server does
not stop existing containers, and secret operations must report unavailable.
Unseal the existing server using the separately protected recovery material;
do not initialize it again or replace its persistent volume.

Focused source checks use `python3 -B -m unittest discover -s
scripts/self-hosting -p 'test_*.py'`. The optional isolated dependency checks
require already-cached `postgres:16-alpine` / `openbao/openbao:2.2.1` images;
enable them with `MINICLOUD_TEST_CACHED_POSTGRES=1` and
`MINICLOUD_TEST_CACHED_OPENBAO=1`. They never pull images, publish ports or use
provider credentials, and remove only their uniquely named containers/volumes.
Set `MINICLOUD_TEST_COMPOSE_BINARY` to an installed Compose executable to check
the rendered source/bundle startup and exposure model without starting a stack.
These checks do not qualify Kratos, public routing/TLS or the full packaged
stack.
