# T70 — Private hosted-operations integration

Implementation repository: `muniventures/minicloud-services`, not this public product repository.
Status: readiness code exists locally; remote handoff and further integration not started.
Depends on: T20, T30, T31, T40–T44, T50–T52 and T60–T62 merged; T10's code result recorded. Read-only workflow/contract preparation may run earlier. Can run in parallel with: final documentation review, not production mutations.

## Access and prerequisite

The user will trigger Copilot sessions from another GitHub account. That account must have private-repository access before this task can start; public-fork access does not grant it. Do not copy private source, fixtures, IDs, credentials or workflow secrets into the public repository to work around access.

Local private readiness commit `7751939` already checks the pinned running API and health before strict reconciliation. It is deliberately unpushed. The user/integration owner must make the approved readiness change reachable through the authorized private PR process before the cloud agent consumes it. This specification does not lift the no-push instruction or ask an agent to recreate invisible local work. A missing handoff is a blocker to resolve explicitly, not evidence the fix is absent.

## Deliverable and ownership

- Own only hosted workflow/configuration and existing maintenance/recovery scripts plus their tests. Retained private `modules/` are historical; product code belongs in public `minicloud`.
- Preserve the existing readiness patch, exact rehearsal/live inventory equality, candidate digest, operation owner and ingress fence. No ignored fields, forced reconciled state or stale-data rollback after ambiguous migration.
- Consume the merged public Kratos/session/setup/configuration/release contracts. Wire private identity/dependency configuration and shared public seed/migration primitives; do not create a second bootstrap implementation or ship a Firebase fallback.
- Keep the existing operator's explicit identity binding, new password setup, application/environment ownership, API keys, provider records, encryption/SSH keys and data-preserving backup/restore path. Codex later verifies an isolated rehearsal before any live cutover.
- Pin the final integrated product candidate/release assets, including SSH and numbered-deployment contracts. Never deploy an earlier public baseline merely because its image is available. Update hosted CLI callers to T20's source-only policy.
- If retaining an operator helper is necessary, land only reusable credential-free behavior/assertions in existing maintained private scripts/tests. The app-only cleanup and bounded nested error-code behavior must not depend on a workstation's ignored helpers. Supported direct APIs are sufficient where no helper is retained; no new runner framework is required.
- Produce an exact staged upgrade/rollback/recovery runbook and scoped fixture/credential requirements. No real identifiers or secrets belong in the committed runbook.

## Acceptance

Focused workflow/command-fixture tests prove configuration propagation, one shared bootstrap contract, strict readiness/fences and no public secret/admin exposure. A merged private PR and its tests are prerequisites for final migration acceptance; they are not evidence of a real deployment. Do not provision resources, deploy, seed the real operator, revoke keys or retry old cleanup in this implementation task.

## Assignment prompt

Implement T70 in the private repository after explicit access and the readiness handoff are available. Read that repository's own instructions and this task's public contracts. Preserve the already written fix, change only hosted integration, run focused checks and submit one private PR. The user controls Copilot sessions and PR merging; Codex performs final integration validation afterward.
