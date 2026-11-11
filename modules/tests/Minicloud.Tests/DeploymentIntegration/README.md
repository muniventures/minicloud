# Deployment integration tests

Current implementation is a **component smoke**, not the complete C/R/A/S suite. It builds the production API and worker images, starts isolated PostgreSQL and OpenBao containers, and migrates an empty database. Two packaged worker processes verify unique claims, independent-app admission, same-app FIFO/exclusion, deletion admission exclusion, collided provider-server refusal, stale ownership recovery without replay, and rejection of an old owner's late finalization. Probe jobs intentionally have no deployable services; their later pipeline outcome is not a successful deployment assertion.

The smoke pauses PostgreSQL for three seconds while both workers hold independent
claims, resumes it, and verifies recovery without duplicate claim events. This tests
database connectivity interruption, not remote SSH process quiescence.

Run the normal backend test suite from the repository root; the deployment checks run automatically:

```sh
mkdir -p .local/artifacts/deployment-tests/tmp
TMPDIR="$PWD/.local/artifacts/deployment-tests/tmp" \
  GIT_CEILING_DIRECTORIES="$PWD/.local/artifacts" \
  dotnet test Minicloud.slnx --configuration Release \
  --artifacts-path "$PWD/.local/artifacts/deployment-tests/build" \
  --results-directory "$PWD/.local/artifacts/deployment-tests/results"
```

The backend suite needs a running local Docker daemon and Python 3.9+. Its xUnit tests invoke the smoke and manifest checks automatically. The smoke creates a random Compose project with a private, external-network-isolated network and fresh container storage. It removes only that project's containers, volumes, network and images in `finally`; the xUnit test also requests run-owned cleanup if the child process fails or times out. Image acquisition and builds happen before the isolated scenario executes. It does not use provider, Cloudflare, SSH or production credentials. Worker tests resolve production registrations and send provider lookup requests to local HTTP listeners; they do not contact cloud endpoints.

The component runner generates independent synthetic PostgreSQL, OpenBao and
secret-encryption credentials for each process. API and worker share that run's
`TEST_SECRET_ENCRYPTION_KEY`; Compose rejects a missing or empty value. These
fixtures must not rely on production defaults: an empty configured encryption
key is deliberately rejected. For direct Compose inspection, supply all three
`TEST_POSTGRES_PASSWORD`, `TEST_BAO_TOKEN` and `TEST_SECRET_ENCRYPTION_KEY` values.
Generated Compose overlays, certificates and nested CLI builds stay under the
ignored `.local/artifacts/deployment-integration/<run-id>/` directory.
The Git ceiling prevents synthetic source directories from inheriting the
owning repository's artifact ignore rule when the CLI selects build inputs.

`cases.json` lists all 47 matrix IDs and 265 required group/variant results. `validate_coverage.py` checks it against the source matrix. To evaluate completion, pass a JSON evidence array to `--evidence`; every group/variant requires a passed result with revision, image digests, run ID, evidence and successful cleanup. An omitted, failed or skipped result blocks full coverage. Current component smoke output is intentionally not accepted as full-case evidence.

The connected CLI → API → worker → captured SSH request → local Docker runtime journey, prior-schema migration, complete provider HTTP mocks, persistent OpenBao modes, rollback and browser assertions remain to be implemented. The backend CI check runs these partial tests by default; it is not yet a complete release gate. No claim of full integration coverage should be based on the component smoke alone.

Cancellation regression checks also include
`DeploymentCancellationPostgresTests`: a disposable PostgreSQL 16 container
verifies migration application, cancel/claim races, duplicate audit suppression,
and cancel/health-finalization races. It removes its own container in `finally`.
The worker and API cancellation tests separately cover tenant/write permissions,
HTTP acknowledgements, pending cleanup, unknown remote shutdown and provisioned
server identity retention.

For connected SSH cancellation, configure `MINICLOUD_TEST_SSH_PORT`,
`MINICLOUD_TEST_SSH_PRIVATE_KEY` and optionally `MINICLOUD_TEST_SSH_USERNAME`
against a disposable local OpenSSH host with Bash and `setsid`, then run
`dotnet test modules/tests/Minicloud.Worker.Tests --filter FullyQualifiedName~CancelStopsRemoteBuildGroupAndWaitsForTermCleanup`.
This checks a TERM-resistant independent build process group and a six-second
cleanup handler; completion requires cleanup to finish and the build group to
exit. Without the SSH environment this connected test does not execute its fixture.
