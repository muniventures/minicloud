# Apps data fetch performance

Status: Implemented locally on 2026-11-10; see [validation evidence](../performance/apps-data-fetch/README.md) for measured scope and remaining gaps.
Date: 2026-11-09 (America/New_York).
Scope: Product API read path, tests, and reproducible performance evidence.

## Current scope

The 2026-11-10 user correction removes the old version split and compatibility requirements. The canonical list is `GET /api/apps?organizationId=...`, returning applications with environment summaries; operational descriptors use `GET /api/environments/{environmentId}`. No legacy list, identity resolver, or route alias is supported.

Retain three bounded environment-summary commands (metadata, latest non-asset deployment per environment, active domains), plus one application metadata command and bounded runtime/placement queries. No deployment-history graph, N+1 calls, writes, or tracked entities in the read path. Authorization, tenant ownership, domain/status fields and deterministic latest selection remain required. The canonical API and clients must be tested together; old wire shapes need no preservation.

Validation: synthetic SQLite regressions for summary semantics, tenant isolation, history/query-budget independence, cancellation and no writes/tracking; PostgreSQL executable under `tools/app-summary-benchmark`, maintained summary under `docs/performance/apps-data-fetch`, and generated SQL/plans under git-ignored `.local/artifacts/apps-data-fetch`. Existing measurements there are historical pre-consolidation results, not claims about network/browser/production performance. Rerun the canonical handler benchmark after API consolidation. Do not introduce dependencies, speculative indexes, caching, pagination or production mutations as part of this task.
