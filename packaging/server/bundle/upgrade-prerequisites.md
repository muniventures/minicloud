# Upgrade and full-recovery prerequisites

**No upgrade or full-recovery path is enabled by this source bundle.** Missing
production ownership interfaces are a release blocker, not operator workarounds.

Before enabling coordinated maintenance, the lifecycle owner must provide:

1. A durable installation-bound fence checked atomically by every management
   write admission path, including API, scheduled work and worker successors.
2. A callable complete drain that accounts for queued, running, retrying,
   uncertain and externally accepted provisioning/deployment/database/deletion/
   DNS work. A lost worker heartbeat or an empty deployment queue is not proof.
   Existing accepted work must finish or yield a retryable refusal, not be killed.
3. Persistent worker startup/restart and production-transfer fencing. A restored
   credential, receipt or host-local file alone cannot prove the previous worker
   lost authority. Recovery without the old host requires independently enforced
   authority transfer; it must not rely on cooperative shutdown of a lost host.

The maintenance implementation must then integrate these interfaces with:

- independently verified exact target assets, declared/tested source-to-target
  application/identity/configuration and CLI compatibility, capacity and downtime;
- a complete encrypted coordinated capture of both databases, OpenBao persisted
  state, independent recovery dependencies, artifacts, installation metadata,
  configuration/credentials/deployment SSH keys and TLS state;
- a successful authenticated backup/recovery rehearsal before migrations,
  retained source release assets and a persistent failure fence across all
  migration/activation failures, including installer/manual entry points;
- isolated full-restore verification before volume mutation; independently
  fenced old-worker authority before any production writes;
- readiness beyond container health, including sessions, inventory, secret
  decryption and worker connectivity, without managing customer VPSs.

Independent recovery-dependency archives are useful but satisfy none of these
full-backup gates. Never use them as a pre-upgrade backup. Do not downgrade
images unless actual schema/data compatibility has been proven. Otherwise keep
writes fenced and use a qualified coordinated recovery path.
