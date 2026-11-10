# T10 — Complete demonstrated database/runtime defects

Repository: `muniventures/minicloud`. Owner: one DB/runtime agent.
Depends on: T00. Can run in parallel with: T20 and T30; later image/installation work on separate files.
Status: remaining causes unconfirmed; merged repairs are baseline.

## Existing implementation to preserve

[PR #17](https://github.com/muniventures/minicloud/pull/17) already implements sanitized operation errors, sparse/failure log flushing, failed resource transitions and bounded access-container readiness. Use the [six DB repair specs](../../database-release-repairs/00-execution-plan.md) for behavior and test cases; do not replay their original implementation sequence wholesale.

## Remaining code work

1. Exercise canonical agent deployment/redispatch and lifecycle guards with synthetic states. Capture the structured rejection code, including nested `error.code`. Do not weaken ownership, active-operation or deletion guards to make the historical HTTP 400 disappear.
2. Exercise failed database creation → normal deletion → access reconciliation, delayed PostgreSQL readiness, verified/unverified rollback, interrupted ownership and explicit retry. Check actual remote database/user existence rather than treating access rollback as data restoration.
3. Repair a reproduced failure at its existing boundary and add the focused regression. If the current source passes and the historical cause cannot be reproduced, report “no code defect reproduced” with the missing live evidence; do not invent a provider-specific branch. Such a result may complete the implementation task, but leaves qualification open at T90/T91.
4. Keep reusable, credential-free assertions in existing tests. Retained private operator-helper fixes are not available to cloud agents and are not a product-runtime dependency. Use supported APIs for app-only cleanup; any maintained helper change belongs in T70, without creating a runner framework.

## Ownership

Potential files: `modules/worker/Databases/`, relevant `modules/worker/Pipeline/Steps/` and runtime-agent files, implicated `modules/api/Controllers/DeploymentsController.cs`, `scripts/database-platform/` and their existing tests. Edit only the implicated subset. Auth, shared model/schema and SSH provider allocation files belong to other lanes; request a handoff if reproduction proves a necessary overlap. A DB migration requires an explicit reserved migration slot, not a concurrent snapshot edit.

## Acceptance and handoff

- Focused regressions retain source/neighbor data, backup retention, strict access, lease and tenant ownership; failed operations do not masquerade as active work or success.
- Provide synthetic repro inputs, exact changed boundary and test results. Identify any unresolved historical case separately.
- No cloud-provider mutation, customer app redeployment, old credential reuse, live fixture retry or paid-resource cleanup occurs in this task. Codex owns those checks after merge.

Issue prompt: implement only T10's remaining demonstrated defects on current `main`; preserve PR #17, follow the linked existing repair contracts and submit one focused PR with regression results and explicit evidence gaps.
