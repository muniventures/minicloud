# Pre-v1 release backlog

The [pre-v1 completion feature](specs/pre-v1-release/1.pre-v1-release.md) is the current grouping and its [execution plan](specs/pre-v1-release/0.execution-plan.md) defines ordering and parallel Copilot assignments. It includes the remaining DB work, the two release items below, required Kratos authentication, and the explicitly selected SSH-key provisioning and sequential deployment-number features. Already merged work is baseline; final integration validation belongs to Codex after implementation PRs merge.

Both items are required before v1. Status: planned.

1. **Remove our prebuilt CLI.**
2. **Improve self-hosting installation.** Follow the existing [self-hosting installation specification](specs/v1.0-self-hosted-installation.md).

## Deferred follow-ups

Status: deferred; acceptance outcomes below remain unverified.

The database-release repairs have a separate [ordered specification and parallel execution plan](specs/database-release-repairs/00-execution-plan.md).

- Complete hosted database backup, download and restore acceptance with data verification, network connectivity checks, and migration acceptance.
- Review lifecycle states observed during acceptance: failed database creation remained `creating`; server deletion remained `deleting` until a normal retry. Causes are unconfirmed.
- Improve generic or buffered operation diagnostics.
- Resolve canonical source-built CLI delivery under [T20](specs/pre-v1-release/subspecs/20-cli-distribution.md); do not schedule a new prebuilt binary publication as a prerequisite to removing that distribution.

## Release follow-ups observed on November 10

- Existing-agent upgrade was not completed: redispatch returned HTTP 400 and the next target hit a lifecycle guard. Investigate before v1.
- One staging Vultr database/server remains: database deletion returned `database_access_change_rolled_back`. Complete cleanup after repair; exact owned IDs remain in the private acceptance state.
- The cleanup helper rejects a fixture set with no saved databases; application-only cleanup required direct normal API calls.
- Live migration reconciliation initially saw the old schema, then matched the rehearsal exactly on recheck. Investigate readiness timing while retaining strict reconciliation.
