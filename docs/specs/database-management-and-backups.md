# Database management and backups

Status: specification only; not implemented or qualified by this document.
Date: 2026-11-09 UTC.

## Outcome

Users manage databases from the portal alongside applications. A database can run inside an application VPS, on a shared database server, or on a dedicated database server. Database placement does not change the available backup, status, and recovery features for the same engine.

Keep the first release simple: consistent database snapshots, configurable schedules and retention, encrypted off-server storage, and explicit restore. Do not implement clustering, replicas, automatic failover, continuous WAL archiving, or point-in-time recovery.

## Confirmed requirements

- Preserve the current model where an application has a database on its own VPS. Add backup capabilities without requiring relocation, a new application connection, or adoption of standalone hosting.
- Add independently managed shared and dedicated database servers. An organization may own multiple servers of either kind.
- A shared server holds multiple PostgreSQL databases. A dedicated server holds one user-managed PostgreSQL database; PostgreSQL's required maintenance/template databases do not count toward this limit. Shared means sharing within the organization, not pooling unrelated customers on one server.
- Initially support PostgreSQL for standalone hosting. SQLite stays application-local and gains consistent backups and restore; remote/shared/dedicated SQLite hosting is deferred.
- Support internal access through a Minicloud-managed encrypted network across VPSs and providers. Also offer provider-native private networking where supported and explicitly configured public access.
- Users configure backup settings; provide reasonable defaults.
- Existing databases remain running. Offer explicit migration to shared or dedicated hosting rather than moving live data automatically.
- Keep engine-specific behavior behind an extensible boundary so another engine can be added without redefining hosting modes or backup policy.
- This task produces the specification only. Implementation, provisioning, migration, and deployment require a later execution instruction.

## Verified starting point

These paths describe existing source, not the proposed feature:

| Area | Current behavior and source |
| --- | --- |
| Database ownership | `MinicloudApp` and `Deployment` contain an application-bound `DatabaseMode`; supported values include PostgreSQL, SQLite, and none. See `modules/common/Models/ControlPlaneModels.cs`. |
| PostgreSQL runtime | `scripts/github-actions/minicloud-deploy/remote-deploy.sh` generates a `postgres:16-alpine` service and persistent data directory beside application services. |
| SQLite runtime | The same script mounts the application SQLite directory and injects `sqlite:////data/sqlite/app.db`. SQLite is an embedded file, not a remote database service. |
| Portal | `modules/dashboard/app/modules/Apps/CreateAppModule.tsx` selects a database when creating an app. No standalone database-management section exists. |
| Provisioning | `modules/common/Providers/IVpsProviderClient.cs` provides server find/create/status/delete operations. Its contract does not provide private-network or firewall lifecycle management. |
| Storage | `docs/durable-storage.md` documents Hetzner application volumes and explicitly excludes automatic backup and restore. |
| Deferred platform draft | Local branch `feat/database-recovery` has unfinished configuration admission, identity binding, key custody, and encrypted repository transport. Its checkpoint explicitly excludes completed execution, persistence migrations, and hosted acceptance. Reuse only after review; do not merge the draft wholesale. |

The separate Munipages implementation supplies evidence for daily encrypted logical backups and isolated restores. It is not a released Minicloud platform capability. Product code and public documentation must remain self-contained; do not import private application IDs, credentials, endpoints, or files from another checkout.

## Product model and terminology

### Database

A database is an organization-owned resource with a stable ID, display name, engine/version, placement, runtime binding, operational status, access settings, and backup policy. It is listed in the portal regardless of placement.

Placements:

| Placement | Ownership and lifecycle |
| --- | --- |
| Application VPS | Attached to the existing application environment and runtime. Enrollment records the current database; it does not create, move, reinitialize, or rename it. Application deletion remains the host lifecycle boundary. |
| Shared server | Attached to a shared database server. Each database has its own database identity and credentials. Removing one database does not remove the server or another database. |
| Dedicated server | Attached to a dedicated database server. At most one active database is admitted. The create flow can create the server and database together. |

SQLite appears as an application-hosted database and supports backups and file-based recovery. It does not offer remote connection modes or standalone placement. PostgreSQL backup parity applies across all three placements.

### Database server

A database server is an organization-owned VPS configured for database hosting. It has a user-chosen name, mode (`shared` or `dedicated`), engine/version, named provider plan, resolved provider-account/resource identity, region, storage, access capabilities, and observed status.

Use **Database servers** in the portal; do not expose a cluster-management concept. The first release runs one PostgreSQL service per database server. Separate logical databases and login roles provide isolation within a shared server; this is not a claim of per-database CPU, memory, disk, or process isolation.

Do not impose an arbitrary product count of shared databases. Validate real capacity, configured safety limits, and provider quotas. A full server reports a capacity error and lets the user grow it where supported or select/create another server. Do not silently provision another paid server or redistribute databases.

### Backup repository

A repository is an organization-owned off-server destination for encrypted backup artifacts and manifests. Several source databases may use it, with source-specific credentials and namespaces. The source database host and its repository must be independent VPS resources; a second container or disk on the same host does not qualify as off-server protection.

The initial implementation uses a Minicloud-managed repository service with persistent registered storage, reusing supported provisioning/storage contracts. Repositories can serve source databases across providers over authenticated HTTPS. Provider-specific storage support is a capability, not an assumption; the initial managed-volume implementation can remain Hetzner-backed. External S3 and other repository adapters are future additions.

## Portal workflows

Add **Databases** navigation with database inventory and **Database servers**. Show application-hosted databases in the same inventory, with links to their application/environment. Display actual state: creating, ready, failed, deleting; show unconfigured/unhealthy backups explicitly.

### Create a shared server

1. Enter a name; choose PostgreSQL version, named plan, and supported storage settings.
2. Choose the server's network capabilities and review provider support and recurring resource costs.
3. Create asynchronously and display progress or an actionable failure.
4. Once ready, add databases with their own names, credentials, access rules, and backup policies. Repeat on this server or create additional servers.

### Create a database

1. Choose shared or dedicated hosting. Shared selects an existing ready shared server or opens server creation. Dedicated selects its own plan/storage and creates one server for this database.
2. Choose a database name and application bindings or authorized clients.
3. Configure access and backups. Review any server, storage, repository, and private-network costs before creation.
4. Return connection instructions and provisioning progress. Do not expose a working connection or healthy backup claim before the corresponding operation succeeds.

Database details contain **Overview**, **Connections**, and **Backups**. Include access changes, credential rotation, backup-now, policy editing, backup history, restore, and explicit migration. SQL browsing, arbitrary SQL execution, and a generic remote shell are outside this feature.

### Existing application databases

Discover/register the existing database from the environment's authoritative database mode, runtime, and storage binding. Enrollment and enabling backups are explicit, data-preserving operations. Existing apps with no backup configuration continue running and display **Backups not configured**; they do not become protected merely because the control-plane schema changes.

Keep the existing `database: postgres` and `database: sqlite` contracts working. New and existing application-hosted databases can use the same backup settings, history, backup-now, and recovery operations as standalone databases. For new creation, backup configuration is enabled by default, but selecting/provisioning its repository and reviewing costs are part of the create flow. Users may explicitly disable it.

The new portal/API create flow explicitly submits that default policy after destination and cost review. Legacy clients or requests that omit backup configuration retain their existing behavior and do not create a paid repository, enable backups, or change a previously configured policy implicitly.

An application may opt into a standalone PostgreSQL database by binding selected services to it. Store credentials as secret references and deliver them only to selected services. Preserve legacy application configuration until the user explicitly changes a binding or completes migration. Normal deploys, scoped deploys, and application-image rollback must preserve the selected database binding and must not overwrite it with the old local connection or reinitialize a database.

## Access and connectivity

| Mode | Meaning |
| --- | --- |
| Internal only — default | Authorized Minicloud applications connect through managed encrypted connectivity. This works across VPSs/providers; membership alone does not grant access to every database. Connections to this database through a public path are denied. |
| Provider private network | Reachability uses a selected provider-native VNet/VPC/private network. Offer only when that provider account, region, server topology, and Minicloud integration support it. |
| Public | Explicit public database endpoint with TLS, database authentication, and user-managed IP/CIDR allowlists. Default to no admitted public clients; opening to all addresses requires an explicit user choice. |

Internal is a platform-managed access path; provider private network is a different optional access path. The user selects a primary mode and may retain selected internal application bindings when enabling another mode. Display every effective endpoint and admitted client path; changing modes must not leave an old public path open invisibly.

Server listener/firewall exposure is the union of its databases' authorized paths; database access rules remain independent. A shared server with a public-enabled database may expose its PostgreSQL port, while an internal-only neighbor must still reject connections through that public path, even with valid neighbor credentials. Enforce database-specific connection rules using verified client/network identity; a proxy must not disguise public connections as authorized private clients. A server with no public-enabled databases exposes no public database port. Removing the last public-enabled database/path closes that exposure.

For application-hosted PostgreSQL, use the same access controls and explicit opt-in for new remote paths without breaking its existing local application connections. SQLite does not expose these modes.

The private-network mechanism is an implementation decision to be reviewed before coding; this spec does not select a new vendor, package, or hosted dependency. The contract requires organization isolation, per-database client authorization, peer/key revocation, encrypted transit, restart recovery, and routing that avoids interfering with customer/provider networks. No connectivity path may silently fall back to public access. Provider capability must be verified from the actual adapter/account; unsupported choices are disabled with an explanation.

Changing access is an asynchronous, audited operation. Revoke the old path, apply the new rules, and verify permitted and denied reachability. Preserve a recoverable configuration when application fails and report the observed exposure. A timeout must not be reported as a successful security change.

## Backup policy

Backups are database-scoped. On a shared server, backup or restore of one PostgreSQL database must not copy unrelated databases into the customer-visible artifact or modify them.

User settings and initial defaults:

| Setting | Default | Behavior |
| --- | --- | --- |
| Enabled | Yes in the new create flow; explicit opt-in for existing databases | Disabling stops new scheduled captures. Explain the retained-artifact policy before applying the change. Legacy omitted configuration preserves existing behavior. |
| Frequency | Every 24 hours | Configurable interval; validate supported limits and display the effective schedule and next due time. |
| Retention | 30 days | Configurable retention period. Expiry is applied by the repository after artifact completeness checks. |
| Minimum complete copies | 2 | Configurable, at least 1 while policy is enabled. Show that this minimum may retain older backups beyond the age policy. |
| Destination | User-selected compatible repository | Offer repository creation if none exists. No unapproved paid destination is created silently. |
| Failure notifications | Portal status/events | Users can select notification destinations supported by the platform. No external notification is claimed until configured and delivery-tested. |

Keep encryption and authentication mandatory. Credentials, plaintext keys, arbitrary shell commands, and raw filesystem paths are not ordinary policy fields. Preserve runtime safety limits; report effective values rather than silently ignoring invalid input. PITR is absent from the first-release policy and portal, including an inactive or misleading toggle.

On initial enablement, capture a backup as soon as configuration is ready. Schedule subsequent backups from their due times; coalesce missed runs instead of starting overlapping captures. Allow one capture/restore/migration mutation for the same database at a time. Failed backups retry with bounded exponential backoff, initially 1 minute and capped at 15 minutes, without overlapping scheduled or manual jobs.

Record an expiry for each completed backup from the retention policy at capture time; retention changes apply to future captures. While the policy is enabled, the minimum-complete-copy floor can protect copies beyond expiry. On disablement or source deletion, stop captures and, by default, remove that floor: existing artifacts remain until their recorded expiry, and already-expired copies become eligible for the next retention pass. Preview those consequences. Offer an explicit **Keep last copies until manually deleted** choice for users who want the configured minimum preserved indefinitely. Retain each encryption-key version until its final referencing artifact is removed, including manually retained copies.

### Capture and storage

1. Resolve and revalidate the database, host, engine, destination, actor, and operation ownership before starting work. Do not provision a replacement database host as a backup side effect.
2. PostgreSQL uses a compatible `pg_dump` custom-format logical snapshot. Store the platform-managed ownership metadata needed for recovery; do not export unrelated users' credentials or global roles.
3. SQLite uses its online backup API or an equivalent supported consistent snapshot operation against the registered app-local file. Do not copy a live `.db` file alone or claim that copying a directory is a consistent backup. Bound busy/locked retries and publish only a completed snapshot.
4. Encrypt source-side and upload over HTTPS with a source-specific credential. Include authenticated source identity, engine/version, backup ID, capture timestamps, encryption-key version, artifact size, and integrity information in the manifest. Do not leave plaintext snapshots on persistent disk or include secrets in manifests/events.
5. Commit a completed backup only after artifact and authenticated manifest upload succeed. Incomplete uploads are not listed as restorable points and are cleaned up safely.
6. The repository owns retention. Source upload credentials cannot overwrite or delete completed backups. Keep bounded capacity reserves and return explicit full/unavailable failures rather than deleting required recovery points or falling back to a local directory.

Minicloud's secret system holds source-side encryption material and repository access secrets. Keys must be recoverable after loss of the source VPS and versioned so rotation does not make retained backups unreadable. The repository must not receive plaintext decryption keys. A documented protected recovery-key export/escrow procedure is part of delivery; never return keys through inventory/history APIs.

### Status and honest recovery claims

Show policy state, last attempt/result, last complete backup, next due time, artifact size, repository state, retention, and failure reason. A successful capture does not prove a successful restore. Track last restore-test result separately. Repository readiness is not source backup freshness.

A healthy 24-hour schedule offers up to roughly 24 hours of data loss; failures can increase that. Show current backup age and flag overdue protection after the configured interval plus a documented grace period. Do not claim a 30-day recovery window until that history exists. Do not promise an RTO from a local restore drill.

## Restore

The default is **Restore as a new database**, with a user-chosen name and compatible destination. Show expected resource/storage costs and required capacity before starting. A restored database has fresh credentials and is internal-only by default, with no application traffic or public exposure until explicitly configured.

- PostgreSQL: authenticate the manifest, verify source identity and integrity, decrypt, create an empty target, and restore using a compatible engine/client. Avoid flags that select the original database name or clean the live source. Verify schema/data health before returning ready.
- Shared hosting: create the new database on an eligible shared server with capacity, or let the user choose another server. Source and neighboring databases remain unchanged.
- Dedicated hosting: restoration uses a new dedicated server or an explicitly selected empty dedicated server. Do not temporarily place a second active database on a dedicated server.
- Application-hosted PostgreSQL: restore to a separately named target on the same runtime where safe and supported, or to a standalone destination. The app's active database binding stays unchanged until an explicit cutover. Transitional recovery targets do not silently change the environment's existing database mode.
- SQLite: restore a verified snapshot into an isolated new file/target for validation or download. Replacing the active file is a separate, explicitly destructive operation with application writers stopped and a rollback copy retained. Never restore over an actively written file.

An in-place restore, if exposed, is a separate destructive flow with clear data-loss wording, target confirmation, writer fencing, a fresh pre-restore backup, and explicit cutover/rollback steps. Implementing default restore-as-new is required; an in-place automation shortcut is not.

Temporary plaintext, files, and temporary recovery resources are cleaned after success or failure; preserve the successfully restored destination database. Failed restores leave the source serving and report whether any destination resource remains and incurs charges. Restoring application data does not reverse external payments, publications, or other side effects; connection cutover and application reconciliation remain explicit.

## Explicit migration

Offer migration from an application-hosted PostgreSQL database to a selected shared/dedicated destination. Reuse the verified backup/restore primitives; do not automatically move existing databases during rollout.

Present the source, target, estimated costs, affected application services, and expected maintenance. A consistent copy alone does not establish a lossless migration while writes continue: fence writers, capture the final snapshot, restore/verify the target, and explicitly update selected application secret bindings before reopening writes. Preserve the source and rollback information until the user chooses cleanup. Do not delete the application VPS merely because its database moved. SQLite-to-PostgreSQL conversion is outside this release.

## Lifecycle, ownership, and security

- All servers, databases, repositories, backup policies/artifacts, application bindings, and operations are organization-scoped. Validate source and destination ownership independently; knowledge of an ID or another organization's endpoint grants no authority.
- Use separate runtime/database credentials for each shared PostgreSQL database. Prevent a database user from creating roles/databases, becoming an administrator, or accessing a neighbor through connection grants. Do not claim this is hostile-code process isolation; database creation, privileged extensions, and restore inputs require controlled platform operations.
- Provider account and server/storage identity are pinned to operations. Revalidate ownership and relevant revisions before side effects and after secret resolution. Use existing durable queue/lease and host-mutation locks where applicable; do not hold HTTP requests open during provisioning or database work.
- Model ready, queued, running, failed, cancelling, and completed states from actual operations. Operations are retry-safe and must not duplicate a paid server, database, or backup after an ambiguous response.
- Block server deletion while it contains databases. Deleting a database and deleting its dedicated server are explicit choices with cost/retention previews; do not infer permission to remove a repository or shared host.
- Retained backups, recovery keys, and minimal source metadata survive database/app/host deletion according to the expiry/manual-retention policy above or an explicit authorized purge. Show retained resources and ongoing charges. Block repository deletion while it is referenced or holds protected backups unless the user explicitly changes policy and confirms the consequences.
- Credential display/rotation and backup download require dedicated permission and audit events. Keep database connection secrets out of lists, logs, URLs, support bundles, and ordinary status responses.

## Implementation boundaries

The product implementation belongs in this public repository. Hosted deployment workflows remain in the separate services repository. Review the deferred recovery branch against current main and the newer agreed scope before reusing code; do not introduce its old isolation or PITR ambitions as prerequisites.

Use a small engine boundary for capabilities, connection metadata, consistent capture, restore validation, and lifecycle operations. Start with PostgreSQL and application-local SQLite; unsupported engine/placement/access combinations fail validation. Avoid a plugin framework or generalized orchestration layer before another engine needs it.

Proposed resource boundaries, to refine during implementation:

- Database server: placement capacity, provider/host identity, storage, and access capabilities.
- Database: engine, owner/application/server binding, credentials, access and lifecycle.
- Backup policy and backup artifact: schedule, retention, source, destination, key version and observed completeness.
- Asynchronous operation: create/delete, access changes, backup, restore, migration, progress and failure.

Portal actions use authenticated API/service boundaries; the worker owns provider/runtime operations. Reuse named provider plans, existing organization authorization, secret storage, audited operations, and worker ownership patterns. Define public API/CLI contracts and migrations together with their tests during implementation. This specification does not assert that any proposed endpoint already exists.

## Acceptance criteria

| ID | Required evidence |
| --- | --- |
| A01 | One organization creates two shared and two dedicated servers through the portal; another organization cannot list, modify, connect to, restore, or delete their databases. |
| A02 | A shared server admits multiple named PostgreSQL databases with distinct credentials. Attempts to connect to a neighbor database or gain administration fail. A dedicated server rejects a second active user database, including concurrent creates; required engine maintenance/template databases are excluded from the count. |
| A03 | Existing app-hosted PostgreSQL and SQLite keep their data and application configuration after enrollment. Backup enablement creates no replacement source host and does not move data. Legacy requests omitting backup settings preserve current policy and never silently provision a repository. |
| A04 | The same PostgreSQL policy, backup-now, history, failure status, and restore-as-new flows work for application, shared, and dedicated placement. No placement requires migration to enable protection. |
| A05 | SQLite captures are consistent under concurrent writes and recover successfully; no remote SQLite connection or standalone-hosting option is exposed. |
| A06 | Schedule/retention changes take effect; overdue/failed status is visible. Restart, duplicate delivery, cancellation, capacity pressure, interrupted uploads, and retries do not produce false completed backups or overlapping database mutations. |
| A07 | Ciphertext and manifests reach an independent host. Wrong keys, tampering, wrong-source manifests, and cross-source credentials are rejected. Source credentials cannot overwrite/delete complete backups. Losing the source host does not lose access to retained keys or artifacts. |
| A08 | Repository retention honors recorded expiry and the enabled-policy minimum, excludes partial uploads, and preserves required backups under disk pressure. Disablement/deletion applies the selected expiry/manual-retention choice, with key versions preserved until the last referencing artifact is removed. Initial history is shown honestly. |
| A09 | An actual backup from every supported engine/placement restores into an isolated target and passes schema/data checks. The source and shared neighbors remain unchanged; failed restores do not switch application traffic. |
| A10 | Internal connections work between authorized application/database VPSs on different providers. Unbound applications and other organizations are denied. A wholly internal-only server exposes no public database port. On a shared server with a public-enabled neighbor, valid internal-only database credentials still fail over the public path; restart and peer revocation preserve these rules. |
| A11 | Provider-native access is offered only for implemented/verified combinations. Public TLS/allowlist rules permit selected clients, deny others, and close old exposure after access changes. Unsupported private networking never falls back to public access. |
| A12 | Explicit migration preserves application/neighbor data, fences concurrent writers, validates the target before cutover, and retains rollback information without deleting the application host. Later normal/scoped deploys and application-image rollback preserve the chosen database connection. |
| A13 | Deletion blocks unsafe host/repository removal, preserves retained backup metadata/keys, and accurately reports remaining paid resources. |
| A14 | No PITR, clustering, replication, automatic failover, SQL browser, arbitrary execution endpoint, remote SQLite server, or automatic existing-database relocation is introduced by this release. |

## Delivery and verification sequence

1. **Contracts and persistence:** database/server identity and ownership, existing-database enrollment, backup policy/artifact/operation state, capability validation, and migrations preserving existing rows. Keep current application/CLI behavior working.
2. **Backup parity:** implement encrypted off-server capture, scheduling/status/retention, key recovery and restore-as-new for existing PostgreSQL/SQLite and standalone PostgreSQL targets. Validate isolation, integrity failures, capacity and recovery before claiming protection.
3. **Standalone hosting and connections:** shared/dedicated provisioning and database/credential lifecycle, provider plan/storage integration, managed private connectivity, provider capability gates and explicit public access. Validate with disposable hosts, including two providers for internal access.
4. **Portal and migration:** inventory, create/detail workflows, application bindings, backups/restores, explicit migration, safe deletion and retained-resource visibility. Keep all three PostgreSQL placements equivalent in the product UI.
5. **Qualification:** run targeted service/contract/runtime tests, applicable .NET/dashboard suites and build checks, then separately authorized disposable hosted end-to-end acceptance. Record each engine/placement/provider result, teardown, costs and remaining gaps. Production rollout is a separate operation.

These are implementation stages, not scope reductions. Completion requires the supported placement/engine matrix and the acceptance criteria above. Source tests, disposable provider qualification, production deployment, ongoing schedule history, and measured recovery time are separate claims.

## Upstream behavior references

- [PostgreSQL 16 pg_dump](https://www.postgresql.org/docs/16/app-pgdump.html): consistent single-database logical snapshots and version constraints.
- [PostgreSQL 16 pg_restore](https://www.postgresql.org/docs/16/app-pgrestore.html): target selection and restore behavior; clone-style recovery must not inadvertently select/clean the original database.
- [SQLite Online Backup API](https://www.sqlite.org/backup.html): consistent snapshots, incremental operation, and busy/locked handling.
