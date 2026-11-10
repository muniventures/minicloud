# 02 — Complete existing runtime-agent upgrades

Status: specification only; no agent upgrades or hosted mutations authorized by this document.
Date: 2026-11-10

## Outcome

Existing eligible runtime servers run the agent matching the released canonical API.
Complete the interrupted rollout through the existing deployment path, preserving
application services and server identity. Fix only a reproduced blocker.

## Evidence and uncertainty

The release handoff records six nonfixture installed agents: five previously online
and one already offline. The first redispatch returned HTTP 400 without a retained
error body. The next target failed a lifecycle/ownership guard before dispatch.
These observations do not establish either failure's cause or a completed rollout.
Refresh this inventory before implementation; it is historical evidence, not a target list.

Current source uses `/api/runtime/*`. Normal deployment redispatch can redeploy
application services; it is not an agent-only operation. An HTTP 400 mapping exists
for provider-catalog errors, but that does not prove the observed request failed there.

## Current entry points

| Source | Relevant behavior |
| --- | --- |
| [DeploymentsController.cs:362](../../../modules/api/Controllers/DeploymentsController.cs#L362) | Dispatch authorization, lifecycle fences, service merge, reservation and enqueue; HTTP 400 mapping at line 427. |
| [RuntimeAgentDeployment.cs:49](../../../modules/worker/Pipeline/Steps/RuntimeAgentDeployment.cs#L49) | Target/ownership checks; fingerprint observation and bounded host action at line 122. |
| [RuntimeAgentDeployment.cs:173](../../../modules/worker/Pipeline/Steps/RuntimeAgentDeployment.cs#L173) | Wait for current credential contact before revoking older credentials. |
| [SshDeployStep.cs:112](../../../modules/worker/Pipeline/Steps/SshDeployStep.cs#L112) | Prepare agent as part of deployment; completion at line 204. |
| [RuntimeApiClient.cs:8](../../../modules/runtime-agent/RuntimeApiClient.cs#L8) | Canonical log, heartbeat, status and routing requests. |
| [RuntimeAgentDeploymentTests.cs:15](../../../modules/tests/Minicloud.Worker.Tests/Pipeline/RuntimeAgentDeploymentTests.cs#L15) | Credential rotation, observed host actions and ownership protections. |

## Minimal implementation

1. Reconcile current server/deployment identity and agent contact with the saved
   rollout intent. Resolve ambiguous prior requests by reading current state;
   do not replay them automatically or select an obsolete successful deployment.
2. Capture the structured error and current guard inputs for one eligible target,
   using existing diagnostics. Exclude active deployment/preparation/asset jobs,
   related database operations, deletion and changed ownership.
3. If a product defect is reproduced, add a focused regression test and repair that
   path. If the guard correctly protects newer user work, leave it intact and
   select the current eligible target only after that work has finished.
4. Use the existing dispatch/install path for one target. Confirm agent contact,
   application health and unchanged configuration before advancing serially.
5. Record each outcome privately, including ineligible targets and the separately
   preexisting offline agent. Keep credentials and owned resource IDs out of this repo.

## Acceptance criteria

- The original HTTP 400 and guard outcome are explained by retained evidence, or
  explicitly recorded as unreproduced; no guessed cause drives a code change.
- Every currently eligible target has current canonical heartbeat and status;
  log/routing requests work where those capabilities are configured.
- Previously online targets remain healthy. The preexisting offline target has a
  separate observed disposition and is not counted as a new regression.
- Service definitions, domains, volumes, provider server identity and application
  membership remain unchanged; current tokens work before old tokens are revoked.
- Focused tests prove any changed branch and retain lost-ownership/cross-app fences.
- Any active or changed target is skipped with a reason; ambiguous intent is reconciled
  before another write. An unresolved required target leaves this item incomplete.

## Dependencies and parallel ownership

Begin source investigation in parallel with the other repair lanes. Consume useful
error evidence from repair 01; do not wait for unrelated database repairs to inspect
this path. Hosted rollout follows that evidence and one verified target, with one
operator controlling writes. Repair 05 can proceed independently.

This lane exclusively owns required changes to `DeploymentsController.cs`, `RuntimeAgentDeployment.cs`,
`RuntimeAgentVerification.cs`, `SshDeployStep.cs`, their focused tests, and any
proven necessary agent/template fix. Implement diagnostics task D20 in this same
lane; do not assign a second concurrent writer to `SshDeployStep.cs`.

## Non-goals and stop condition

No agent-only upgrade API, compatibility routes, fleet orchestrator, new retry
policy, provider redesign or unrelated offline-host recovery. Stop when the
eligible inventory is reconciled and healthy, and focused regression checks pass.
