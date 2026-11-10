# Apps data fetch performance

The canonical `/api/apps` handler uses detached application metadata and a shared
summary reader: three batched reads for environment metadata, the latest non-asset
deployment per environment, and active domains. Runtime and placement observations
add a bounded number of queries. Full deployment history is not materialized.

Local PostgreSQL 17.11 measurements, Release/.NET 10.0.5, loopback TCP:

| Environments / deployment records | Warm handler p95 | Five-client p95 |
| --- | --- | --- |
| 18 / 180 | 6.8 ms | 22.9 ms |
| 18 / 18,000 | 6.2 ms | 9.8 ms |
| 150 / 150,000 | 11.2 ms | 21.7 ms |

No concurrent request errors occurred. These are handler plus JSON serialization
measurements, excluding authentication, HTTP transport, and browser rendering.
They are local results, not production guarantees. The fixture had no runtime
servers; snapshot behavior is covered separately by regression tests.

The reproducible benchmark source lives in `tools/app-summary-benchmark`. Run it
from the repository root against a **fresh, disposable local PostgreSQL database**.
The executable only accepts a local database named `apps_perf*`, rejects a nonempty
public schema, applies product migrations and inserts synthetic data. It does not
drop or clear databases.

```sh
docker run --name minicloud-apps-perf --rm -d \
  -e POSTGRES_PASSWORD=synthetic-perf -e POSTGRES_DB=apps_perf \
  -p 127.0.0.1:55439:5432 postgres:17-alpine
# Wait until this readiness check succeeds.
docker exec minicloud-apps-perf pg_isready -U postgres
MINICLOUD_PERF_DATABASE='Host=127.0.0.1;Port=55439;Database=apps_perf;Username=postgres;Password=synthetic-perf' \
  dotnet run --project tools/app-summary-benchmark/AppSummaryBenchmark.csproj -c Release
docker stop minicloud-apps-perf
```

Generated SQL, execution plans and captured console output belong in git-ignored
`.local/artifacts/apps-data-fetch/`. The tool defaults to that output location;
`MINICLOUD_PERF_OUTPUT` can select another local artifact directory. Do not copy
these outputs into `docs/` or stage them. Keep only reusable tooling and this
maintained summary in source control.

Regression coverage checks tenant authorization, latest non-asset selection,
timezone/sub-millisecond ordering, domain filtering, bounded query counts,
cancellation, and absence of tracking/writes or serialization-time database access.
PostgreSQL qualification uses the actual migrations and generated query plans;
SQLite tests cover additional asset-only/null/tie and snapshot-history cases.
