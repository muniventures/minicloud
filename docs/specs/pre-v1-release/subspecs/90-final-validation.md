# T90/T91 — Final integrated validation

Owner: **Codex**, after implementation PRs are merged. These are not Copilot implementation assignments.
Status: not started. Cloud-agent focused checks and GitHub CI do not replace this gate.

## T90 — Local combined integration

Depends on: all T00–T80 implementation/handoff tasks merged in their owning repositories, with explicit no-defect/evidence-gap results where applicable. Verify actual PR merge SHAs and fresh source; do not use merely submitted branches, unpushed private changes or stale build artifacts.

1. Record the selected public/private commits and complete task/CI status. Inspect file integration and migration ordering; stop on missing contracts or implementation rather than silently coding a replacement.
2. Run combined affected .NET/API/worker/CLI, runtime Python and dashboard checks, production builds/typechecks and existing integration fixtures. Exercise populated PostgreSQL and SQLite upgrades with all new migrations together, not just independent empty-schema tests.
3. Verify Kratos/browser sessions, exact-origin cookies/CORS/CSRF, outage versus unauthenticated behavior, source-built CLI consent/login and unchanged scoped machine authentication. Exercise concurrent/interrupted operator setup and recovery with synthetic identities.
4. Verify SSH selection/account isolation and reservation snapshots together with initial deployment, existing DB bindings, secrets, domains and storage. Test unknown allocation outcomes without real provider purchase.
5. Verify organization/day deployment numbering across normal, prepared/local-build and asset flows, concurrent insertion/backfill, cancellation and redispatch; API/dashboard/CLI use numbers with explicit organization while runtime/queue/artifact identities remain opaque.
6. Verify source-only CLI install/build and reusable customer workflow behavior, separate server release discovery, multi-architecture bundle definitions, tamper rejection and no accidental executable-archive publication.
7. Exercise installer/manual configuration parity, universal dashboard startup config, persistence/restart, upgrade failure fencing and complete coordinated backup/restore in isolated local fixtures. Include already implemented Apps/Plans/environment behavior as regression checks without reopening those projects.

A failure is reported against its owning task with a minimal repro; the user assigns follow-up implementation. Codex does not dispatch their Copilot sessions or silently expand into feature implementation. Revalidate the integrated candidate after the resulting PR merges. Only T90 passing admits the hosted stage.

## T91 — Controlled hosted/disposable-host qualification

Depends on: T90 passed; separately applicable release/deployment/resource authority and current scoped credentials. This specification performs no deployment or paid calls. Publish/pull candidate artifacts only through the established authorized release process, then test those exact assets; documentation must not reference assets that do not exist.

- Refresh current owned fixture inventory. Historical credentials were revoked and old resource IDs/statuses are not present-state proof. Reconcile and remove the retained Vultr fixture before fresh DB allocations; never select resources by a prefix or inferred ownership.
- Follow the existing [DB hosted matrix](../../database-release-repairs/06-hosted-acceptance.md): backup/download, exact offline and hosted restored rows before seeding destinations, denied/allowed access, shared-neighbor isolation, private networking, explicit migration/fence/policy preservation and normal/scoped redeploy. Preserve the existing seven-VPS DB ceiling; do not allocate more hosts for parallel agents.
- Reproduce/diagnose the historical agent rejection and Vultr access case on current authorized targets; evaluate actual provider/agent outcomes. A synthetic no-defect result or readiness repair does not close these historical acceptance items by itself.
- Qualify the documented fresh/manual self-hosted install on both supported architectures, unrelated hostname/runtime configuration, HTTPS first operator and source-built CLI flow. Measure complete-stack capacity and upgrade/backup headroom before claiming supported minimums. Agree the disposable-host budget first; do not silently add it to the DB fixture allocation or provision to accelerate parallelism.
- Verify real provider SSH inventory/registration/selection on the three supported providers, immutable reservations, separate Minicloud access and correct server username; reuse owned qualification resources where safe. No user private keys enter the platform.
- Perform the existing [self-hosting acceptance gates](../../v1.0-self-hosted-installation.md#v10-acceptance-gates), including cold restart, upgrade from declared supported versions, coordinated isolated replacement-host restore, identity/session recovery, private admin ports and release provenance. Do not substitute database-only restore or health 200 for recovered data and usable secrets.
- One operator serializes mutations sharing consumers, policies, source/target databases, provisioning reservations or control-plane state. Parallelize independent offline reads/verification, not shared hosted writes. Keep applications serving independently during control-plane maintenance.
- Teardown exact owned fixtures through supported APIs, await terminal states, verify provider VPS/volume/network absence, then revoke temporary keys and remove secret files. Report any retained resource explicitly.

## Completion record

Keep raw logs, screenshots, SQL/results, dumps and resource ledgers under owning repositories' ignored `.local/artifacts/`. Commit only a concise sanitized maintained result summary linking implementation PRs/SHAs, passed gates and material gaps.

Statuses are **implementation merged**, **local integration passed**, **hosted qualification passed**, or **blocked** with a concrete reason. Production cutover/release publication is a separately controlled operator action, not an implication of a green PR or this planning document. No final gate is marked complete before its evidence exists.
