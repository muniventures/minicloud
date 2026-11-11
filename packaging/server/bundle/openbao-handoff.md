# OpenBao runtime handoff

`openbao.hcl` uses a persistent file backend and TLS-only listeners. The
container must be attached only to the private control-plane network; do not
publish either listener port on the host. Mount persistent storage at
`/openbao/data` and the server certificate/key at the configured read-only
secret paths. The server certificate must be valid for `openbao`.

Initialize and unseal OpenBao outside this initializer. Keep recovery keys and
the root-token file in protected operator-managed paths, separately backed up
from the OpenBao data volume. Run `scripts/self-hosting/openbao-init.sh` with
`OPENBAO_POLICY_FILE` pointing at `openbao-policy.hcl` and the root credential
available only through `OPENBAO_ROOT_TOKEN_FILE`. Persist and protect the
initializer's role-ID and secret-ID output directory; do not store those
credentials in OpenBao itself.

Run the Agent with `openbao-agent.hcl`. Mount the role and secret ID files
read-only at the configured Agent paths and mount the token sink directory as a
shared volume with permissions allowing only the Agent to write and the API and
worker consumers to read. Mount that token file read-only into each consumer as
`OpenBao__TokenFile`; do not configure a root-token fallback. Preserve the
external role/secret ID files and recovery keys during backup and recovery.
