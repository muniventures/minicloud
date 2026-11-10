# Minicloud API

Minicloud uses one unversioned first-party namespace: `/api`.
The prelaunch application/environment cutover deliberately removes old product
API routes and identity aliases. Clients and server must be updated together.

- `/api/apps` lists/creates applications with environment summaries.
- `/api/apps/{applicationId}` reads/updates/deletes an application.
- `/api/apps/{applicationId}/environments` lists/creates its environments.
- `/api/apps/{applicationId}/environments/{environmentId}` reads/updates/deletes
  one environment, with ownership and lifecycle checks.
- `/api/environments/{environmentId}` returns the runtime descriptor used by
  operational clients. Its telemetry, logs, storage and image actions use the
  same explicit environment identity. Plan and preview actions also target this
  environment resource; they do not resolve application aliases.
- Other product resources use `/api`, including providers, plans, deployments,
  secrets, runtime ingestion, registry credentials and CLI authentication.

Anonymous `GET /api/instance` returns `product`, `protocolVersion` and `appDomain`.
Packaged servers additionally report `serverVersion`, `sourceRevision` and
`cliCompatibility` from verified release metadata. Compatibility includes the
protocol, optional half-open version interval and explicit source revisions;
it does not imply that CLI and server versions match. Source builds without
release metadata leave these additional fields null. See the
[server release contract](../packaging/server/README.md).

Application creation accepts nested `initialEnvironment` configuration. Unknown
flat creation fields are rejected. Configuration `appId` is a canonical
application ID. Deployments require environment selection when multiple active
persistent environments exist. No client retries against an older API, resolves
runtime IDs as application aliases, or implicitly chooses the dashboard default
for CLI deployment.

Every request retains organization authorization and resource ownership checks.
Existing Firebase users authenticate without database writes. Summary reads
select only the latest non-asset deployment and active domains; no full history
graph or response cache is needed.

Docker Registry, OTLP, OpenBao, Loki and provider version identifiers belong to
those external protocols and remain unchanged. They are not parallel Minicloud
product APIs. Stored migration metadata preserves existing resources without
publishing identity-resolution aliases.
