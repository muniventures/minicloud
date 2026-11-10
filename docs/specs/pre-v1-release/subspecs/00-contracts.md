# T00 — Freeze shared contracts and ownership

Repository: `muniventures/minicloud`. Owner: one contract/integration agent.
Depends on: none. Can run in parallel with: read-only investigation of every lane.
Status: not started. This planning package supplies defaults; T00 verifies and commits the shared contract baseline before feature implementation diverges.

## Deliverable

- Confirm `main` includes PR #17. Inventory current API/DTOs, migrations, release workflows and the completed application/environment model; do not recreate them from stale private snapshots.
- Adopt T20's source-only CLI path. Keep server installation free of a source checkout or build SDK on the control-plane VPS. Document separate server and CLI versions, exact source revision selection, release trust and canonical `/api` routes.
- Freeze T30's sessions/bootstrap contract, T50's environment SSH selection and T60's organization/date deployment-number contract before their consumers implement against them. API-key scopes, machine tokens and opaque runtime/operation identities retain their existing authorization boundaries.
- Record the exact public DTO/route contracts in the corresponding task files and assign migration numbers/owners. Keep changes additive within this implementation sequence where necessary for independently building PRs; do not add long-term dual routes, dual auth or compatibility layers.
- Assign exclusive ownership for `ControlPlaneModels.cs`, `MinicloudDbContext.cs`, its model snapshot, shared DTO/mapping files, dashboard route/config files and CLI command partials. The plan serializes shared backend model writers: T30 → T50 → T60.
- Confirm server installer asset/channel names are independent of the old CLI `install.sh`, and no workflow discovers a server release through repository-wide `releases/latest`.

## Acceptance

Cloud-agent tasks reference public in-repository contracts and identifiable files. There are no unresolved CLI binary/source contradictions, guessed route aliases, overlapping model/snapshot writers or hidden private-file prerequisites. Tests/builds for any contract code included in T00 are reported in its PR. This is not a live inventory, release publication or hosted authorization task.

## Issue prompt

Implement T00 from this file and the [feature overview](../1.pre-v1-release.md). Verify the shared contracts against current public source, update the affected task contracts without expanding scope, and submit one prerequisite PR. Do not start the downstream implementations or perform hosted actions.
