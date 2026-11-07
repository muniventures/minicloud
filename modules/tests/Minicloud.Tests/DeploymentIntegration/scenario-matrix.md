# Required integration scenario matrix

Status: Planned. No case below is implemented or verified by this document.

C = real control-plane workflow with external boundary mocks. R = real local runtime using captured workflow artifacts/payload. A = production external clients against local mocks. S = local services deployment with mocked cloud/host boundaries. Combined labels require both kinds of evidence.

No case contacts cloud providers, Cloudflare, a remote SSH host or public ACME. Server inventory, IP readiness and DNS records below refer to mock state/request history. Exercise supported provider adapters against fixtures, not live accounts. Each parameterized variant needs a separate result; always include known-incident combinations.

| ID | Tier | Scenario | Required assertions |
| --- | --- | --- | --- |
| PK01 | C,S | Packaged startup | Launch built API/worker images with production configuration; migration completes, worker polls PostgreSQL and claims a probe job. Process existence is insufficient. |
| PK02 | C,A | Dependency graph | Resolve all production provider clients through the running worker, then exercise HTTP operations; detect ambiguous constructors and missing dependencies. |
| PK03 | C,S | Migrations | Empty and prior-schema PostgreSQL upgrade; pending model fails before release; preserve seeded data and verify migration history. |
| HP01 | C,R | First source deployment | Actual CLI upload through API/queue/worker; SSH adapter executes captured request locally; real image/version endpoint and terminal succeeded. Use local TLS or a health transport adapter to the real local app, never public DNS/ACME. |
| HP02 | C,R,A | Same-app redeploy | Deploy v2 to v1 server; provider ID unchanged and create-call count zero; endpoint changes to v2. |
| HP03 | C,R | Partial deployment | Update one of six mixed public/private services; unchanged services retain correct image/config/secrets and continue working. |
| HP04 | C,R | Database modes | Run separate none, SQLite and Postgres fixtures; data survives redeploy and container restart; DB failure surfaces accurately. |
| HP05 | C,R,A | Branches | Main and branch have distinct scoped state; branch redeploy reuses its server; destroying branch leaves main untouched. |
| AR01 | C,R | Nested Dockerfiles | Actual .NET multi-project fixture, nested Dockerfile, root/parent build context, non-default filename; COPY dependencies resolve. |
| AR02 | C,R | Image mapping | Artifact with nonempty GHCR placeholder builds/runs local tag; compose image equals build output; no GHCR login/pull/push. |
| AR03 | C,R | Mixed image sources | Artifact and image-only services coexist using a local registry; only image-only service is pulled; local private-registry auth failure is actionable. No real GHCR credentials. |
| AR04 | C,R | Artifact integrity | Missing/expired archive, interrupted upload, corrupted bytes/checksum; no activation or success; errors identify artifact/service. |
| AR05 | C,R | Archive/path safety | Traversal, absolute paths, symlinks, escaping manifest paths and malformed manifest are rejected; canary outside extraction root unchanged. |
| AR06 | C,R | Build failure | Real invalid Dockerfile and failing RUN; nonzero exit, useful per-service logs and prior release still serves traffic. |
| AR07 | C,R | Resource exhaustion | Bounded local target disk-full and memory-pressure tests; terminal failure, worker remains usable, subsequent valid job succeeds. |
| AR08 | R | Caching | Second build uses cache; changed source invalidates appropriate layers; identical archive/config behavior is explicit, not timing-only. |
| SR01 | C,A | Runtime-only association | Seed existing VPS plus RuntimeServer but no ServerInstance; reuse provider ID, persist association, no create call. |
| SR02 | C,A | Established naming | Discover minicloud-<customer>-production-<app> with ownership labels; reject wrong customer/env and ambiguous matches. |
| SR03 | C,A | Conflicting/incomplete records | Conflicting IDs, duplicate rows, missing ID, cross-app ID; fail before provider create, DNS mutation or SSH deployment. |
| SR04 | C,A | Missing established server | Prior success but no resolvable server; provider 404, permission error, transient outage or absent IP never silently create replacement. |
| SR05 | C,A | Provisioning readiness | Mock provider transitions existing initializing server to ready or timeout; no second create request. Simulate bootstrap readiness separately from connectivity; test the behavior supported by code and record missing readiness checks explicitly. |
| SR06 | A | Provider bootstrap request contract | Inspect real client request serialization: public key is in generated cloud-init for minicloud, never a provider-managed key ID; names/ownership/environment correct. Simulated responses parse correctly. No actual server bootstrap or SSH connection. |
| SR07 | C,A | Plan/identity changes | Recorded server provider wins over changed plan; long/colliding names and mismatched ownership fail before mutations. |
| QU01 | C | Concurrent workers | Two packaged workers race for queued rows in real PostgreSQL; one claim per job; same-app executions never overwrite each other. |
| QU02 | C | Queue progress | Long-running/failing job followed by another app job; verify documented concurrency/timeout behavior and eventual pickup. |
| QU03 | C,R | Worker interruption | Kill/restart after claim, during provision/build and after activation before finalization; bounded recovery or explicit failed state, no orphaned active job or duplicate server-create request. |
| QU04 | C | Database outage | Disconnect/reconnect PostgreSQL during claim/heartbeat/finalization; recover without duplicate side effects or false success. |
| QU05 | C | Cancel/delete race | If cancellation is supported, cancel at each stage; delete queued/active app; no subsequent resurrection or deployment onto deleted resources. |
| DN01 | C,A | DNS and HTTPS boundary handling | Real Cloudflare client against mock HTTP creates/reuses only scoped records. Fake DNS answers cover correct/wrong IP and propagation timeout. Simulate TLS/ACME failure at the appropriate boundary and verify bounded status/error handling. No public record or certificate changes. |
| DN02 | R | Routing isolation | Real generated Compose/Caddy config excludes private services and maps public hosts/paths correctly; real local Caddy validation rejects invalid candidate config and previous config stays intact. Exercise local routing where supported; public TLS issuance and host service commands may be mocked. |
| DN03 | C,R,A | Cutover safety | Failed build/health on existing app cannot redirect traffic to an unready server; compare DNS/Caddy before and after. |
| SS01 | C | Remote transport boundary | Recording ISshDeploymentClient receives exact artifact bytes, script, payload, target and commands; injected disconnect, authentication, permission and transport-security errors reach the correct failed state without secret leakage. Tests prove application handling, not SSH protocol or host-key validation. |
| SS02 | C,R | Manifest precedence | Archive metadata reaches actual docker build command without worker defaults overriding it; root Dockerfile fallback still works when appropriate. |
| SE01 | C,R | Secrets | Real OpenBao save/resolve/injection; scoped sentinel visible only inside intended service; absent from logs, API reads and artifacts. |
| SE02 | C,S | Secret-store outage | Sealed/unavailable vault, invalid/expired token and missing secret; no partial activation; useful failure and recovery on explicit retry. |
| SE03 | C | Authorization | Cross-organization app/artifact/secret/deployment references rejected; no provider/SSH side effects; runtime tokens cannot impersonate other apps. |
| RO01 | C,R | Health rollback | Deploy unhealthy v2 after healthy v1; health checks run for required services; supported rollback restores verified v1, not just exit code zero. |
| RO02 | C,R | Rollback failure | Break restore operation; report failed recovery explicitly, preserve diagnostic evidence and never claim previous version restored. |
| RO03 | C,R | First-deploy failure | No previous release: report failure without claiming rollback restored an app; successful retry reuses reserved server. |
| OB01 | C,R | Visibility | CLI/API/browser agree on status, stage, service logs and URLs; client reconnect does not duplicate submission or lose terminal result. |
| OB02 | C,R,A | Failure classification | Preflight, registry, artifact, SSH, build, DB, DNS and health errors retain distinct actionable causes rather than all becoming docker_build_failed. |
| DE01 | C,A | App teardown | Delete scoped app/branches; provider/DNS mock history shows only owned IDs/records removed. Preserve canary app; retry partial cleanup; real DB/artifact/OpenBao state and mock inventory return to baseline. |
| SD01 | S,C,R | Fresh local platform | Fresh local volumes initialize actual DB/OpenBao/API/worker/dashboard via Minicloud setup logic; retain generated test bootstrap credentials securely; customer workflow then runs with mocked external boundaries and local runtime. |
| SD02 | S,C,A | Reused local volumes | Recreate local platform containers with retained volumes; accounts/data/secrets survive and token renewal works; customer redeploy retains registered mock server ID with zero create requests. No provider volume operations. |
| SD03 | S | Fresh secret store | New local persistent OpenBao volume with stale test bootstrap credentials exercises documented replacement behavior; restart succeeds with newly generated test credentials. No operator secrets needed. |
| SD04 | S | Worker release health | Broken framework image or nonfunctional queue consumer fails rollout gate even if container briefly runs; no green services deploy with dead worker. |
| SD05 | S | Optional telemetry | Loki/Grafana failure does not break required deployment; required DB/OpenBao failures do block it. |

## Fixture set and dimensions

- A six-service synthetic fixture: static UI, Node service, two .NET APIs sharing projects with a background worker, and Python API; public/private mix and nested manifests. Use tiny synthetic applications, no customer code/data.
- Minimal single-service fixture for fast health, registry-denial and cache checks.
- Version endpoints and persistent sentinel records prove which release/data is actually active.
- Parameterize Linux target architecture for every supported published platform; do not claim ARM support from an amd64-only run.
- Database modes none/SQLite/Postgres; new/existing/runtime-only/conflicting server state; artifact/external/mixed sources; full/partial redeploy; main/branch; healthy/unhealthy rollout.
- Fixtures with prior successful history, incomplete registration and mismatched provider IDs must match the persisted data shapes in repository models/migrations.

## Evidence contract

Every case includes candidate revision/image digests, CLI version, run ID, timings, redacted logs, API events, independent SQL assertions, mock request history and local cleanup results. R cases additionally record archive hashes, captured payload provenance, actual image/version and retained-data evidence. The harness must not fix the payload before executing it.

For no-GHCR assertions, block external registry traffic and record attempted requests; a grep-only log check is insufficient. Prepare base images and packages locally before execution; a cached application image must not hide a missing build or pull dependency. Local registry tests cover external-image behavior without accessing external accounts.

C mock success is not R build success. A request contract is not proof that a real cloud provider accepts it. These limits are intentional: live external infrastructure testing is outside this feature.
