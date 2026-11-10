# Pre-v1 release backlog

Both items are required before v1. Status: planned.

1. **Remove our prebuilt CLI.**
2. **Improve self-hosting installation.** Follow the existing [self-hosting installation specification](specs/v1.0-self-hosted-installation.md).

## Deferred follow-ups

Status: deferred; acceptance outcomes below remain unverified.

- Complete hosted database backup, download and restore acceptance with data verification, network connectivity checks, and migration acceptance.
- Review lifecycle states observed during acceptance: failed database creation remained `creating`; server deletion remained `deleting` until a normal retry. Causes are unconfirmed.
- Improve generic or buffered operation diagnostics.
- Resolve canonical CLI publication and distribution, pending the decision on prebuilt CLI removal.

## Release follow-ups observed on November 10

- Existing-agent upgrade was not completed: redispatch returned HTTP 400 and the next target hit a lifecycle guard. Investigate before v1.
- One staging Vultr database/server remains: database deletion returned `database_access_change_rolled_back`. Complete cleanup after repair; exact owned IDs remain in the private acceptance state.
- The cleanup helper rejects a fixture set with no saved databases; application-only cleanup required direct normal API calls.
- Live migration reconciliation initially saw the old schema, then matched the rehearsal exactly on recheck. Investigate readiness timing while retaining strict reconciliation.
