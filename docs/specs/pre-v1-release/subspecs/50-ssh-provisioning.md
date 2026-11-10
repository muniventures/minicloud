# T50–T52 — SSH keys during VPS provisioning

Repository: `muniventures/minicloud`. Status: not started.
Authority: [existing SSH provisioning specification](../../ssh-key-provisioning.md), including provider identity, key validation, reservations and unknown-allocation outcomes. No separate SSH lifecycle project is included.

| Task | Deliverable | Hard dependencies | Parallel work |
| --- | --- | --- | --- |
| T50 | Contracts, persistence, inventory/registration adapters and worker allocation | T00, T30 backend merged | T10, T20, T31, T40/T41 outside shared files |
| T51 | App/environment SSH selection and status in dashboard | T50 merged | T52 and installation screens with separate files |
| T52 | CLI app/environment SSH selection | T50, T20 merged | T51 |

## T50 — Backend/providers/worker

Own environment SSH models/DTOs/forward migration and model snapshot after T30's handoff; provider key inventory/registration adapters; environment create/update APIs and provisioning reservation/worker changes. T60 receives the shared model/snapshot files only after T50 merges. Preserve the merged DB and application/environment behavior.

### Frozen T00 customer contract

Use the existing application/environment routes in `modules/api/Controllers/ApplicationsController.cs`, not the historical flat app-creation request:

| Route / DTO | Additive contract |
| --- | --- |
| `POST /api/apps` / `CreateInitialEnvironmentRequest` nested in `CreateApplicationRequest.initialEnvironment` | Optional `sshAccess`; omission is `{"mode":"none"}`. No key inheritance from another environment. |
| `POST /api/apps/{applicationId}/environments` / `CreateEnvironmentRequest` | Same optional `sshAccess`, including explicit preview creation. Automated omitted selections default to none. |
| `PATCH /api/apps/{applicationId}/environments/{environmentId}` / `UpdateEnvironmentRequest` | Omission preserves selection. Explicit `sshAccess` replaces it, including mode `none`; require its `expectedRevision` to match the persisted selection revision. Check the same transaction/reservation lock as allocation. |
| `GET /api/providers/{providerAccountId}/ssh-keys?organizationId=...` | `IReadOnlyList<ProviderSshKeyResponse>`; authorize supplied organization and account membership before inventory. No raw provider references or credentials. |
| Existing `ApplicationEnvironmentResponse` in create/list/detail | Add `sshAccess:EnvironmentSshAccessResponse` and nullable `sshConnection:SshConnectionResponse`. Existing `EnvironmentDetailResponse` receives the same fields for operational clients. No alternate SSH environment endpoint. |

Add `modules/contracts/Contracts/SshAccessDtos.cs`. `SshAccessRequest` is the strict discriminated JSON union already specified in the [original API contract](../../ssh-key-provisioning.md#api-and-cli-contract): `{"mode":"none"}`, `{"mode":"existing","keyId":"<local-id>"}`, or `{"mode":"new","name":"...","publicKey":"..."}`. Only PATCH adds required nonnegative `expectedRevision` to those shapes. Initial selection revision is 0 for none, incremented on an explicit saved change; existing environments migrate to none/revision 0. Reject mixed/unknown fields, including direct provider IDs.

`ProviderSshKeyResponse { id, providerAccountId, name, fingerprint, keyType, registrationState }` uses immutable local `id`, SHA256 fingerprint, `keyType:"ed25519"|"rsa"`, and `registrationState:"discovered"|"pending"|"registering"|"ready"|"failed"|"unknown"`. Only retrievable validated public keys are selectable inventory; an unavailable inventory returns an error, never `[]`. Pending drafts are not advertised as selectable external keys.

`EnvironmentSshAccessResponse { mode, providerAccountId, keyId?, name?, fingerprint?, keyType?, revision, registrationState, selectionLocked }` uses the same states plus `none` and `not_registered` for no-key/new-draft selections. It never contains private keys, provider credentials or raw provider references. `SshConnectionResponse { address, username }` is null until a VPS and its verified image login user are known; never guess `root`. New-key public material stays in the validated backend draft; clients retain unsaved form drafts themselves.

Authorization reuses existing actions/scopes, with no implicit scope expansion: environment writes require `WriteApps` / `apps:write`; inventory/existing-key selection also requires `ReadProviders` / `providers:read`; a new-key selection/registration additionally requires `ManageProviders` / `providers:write` (owner/admin for human actors). Persist the selecting actor identity and revalidate current membership/key validity, required permissions and account credential generation before external mutation. Existing CLI consent scopes do not silently acquire provider registration permission; insufficiently scoped keys get an actionable 403.

Errors use existing `ErrorResponse`: 400 `ssh_key_invalid`, 404 `ssh_key_not_found` for absent/cross-account local key references, 403 `forbidden` for denied organization/action, 409 `ssh_selection_locked` or `ssh_selection_revision_conflict`, 409 `ssh_key_changed` for missing/changed external material, 503 `ssh_inventory_unavailable`, and sanitized 502 `ssh_registration_failed` for confirmed provider rejection. Ambiguous registration persists `unknown` and blocks allocation pending reconciliation; it is not reported as a successful save/allocation. Provider-account permission denial must remain distinguishable from inventory outage.

Provider adapters replace `CreateServerRequest.SshKeyIdOrPublicKey` with `IReadOnlyList<string> SshKeyReferences` containing provider-native references resolved only by adapters. Native JSON array names/types remain adapter-owned and must be checked against upstream schemas by T50; they are not public client contracts. Minicloud's separate deployment key stays in cloud-init. No public registration/delete endpoint is added: registration is deferred to allocation.

CLI T52 fixes flags as mutually exclusive `--ssh-key <local-key-id>` or `--ssh-public-key-file <path>`, plus `--ssh-key-name <name>` only with a new public-key file. Use the existing provider inventory API for discovery; no provider ID input, private-key upload or SSH selection in service YAML.

Requirements:

- Persist one optional user-key selection per environment: `none`, `existing` local key reference, or `new` name/public key. Default omission remains Minicloud access only; this explicitly specified optional contract is not a legacy route/auth fallback.
- List existing provider-account/project keys with name, SHA256 fingerprint, type and immutable local reference. Existing/new selections are scoped to exact organization and provider account; never use display name or provider type alone as identity.
- Validate the actual single OpenSSH key blob: Ed25519 or RSA at least 3072 bits, capped input, normalized public material, no private key, authorization options or multiple-key field. Preserve the existing deployment credential and cloud-init access.
- Implement Hetzner, DigitalOcean and Vultr native key lookup/registration and list-valued creation references. Reuse existing HTTP/admission/reservation boundaries and explicit registration permissions. Inventory denial/outage is not an empty list.
- Defer external registration until first provisioning. Recheck authority, fingerprint, account scope and image support; deduplicate concurrent registration by account/fingerprint. Persist registration before allocation, with one immutable selection snapshot per allocation reservation.
- Prevent edits during active/ambiguous allocation and after a VPS exists. An unknown create outcome requires reconciliation before retry; do not allocate another VPS or silently omit the requested key. Only a reconciled absent server permits a new selection/revision/reservation.
- Preserve user-owned provider keys on failed allocation and later app deletion; do not add running-server rotation, revocation, private-key management or per-server Minicloud credential generation.

Acceptance: focused key parsing, tenant/account isolation, permission, deduplication, plan/account changes, delayed inventory, provider request shapes, reservation immutability, unknown outcome and retained deployment-key tests. Use mocked provider responses; do not register real keys or provision hosts.

## T51 — Dashboard

Own existing app/initial-environment creation and environment settings forms, their SSH-key API/models and focused tests. Avoid global route/auth/runtime-config files; coordinate any necessary handoff. T61 follows because it shares app/environment detail files.

- After plan selection offer Minicloud-only, existing key, or new public key. Show destination account, fingerprint/type, loading/empty/denied/unavailable states and the deferred-install explanation.
- Account changes clear incompatible selections; stale responses cannot repopulate another account's picker. Keep valid drafts on save errors.
- Display selection/registration/reservation state and the verified image SSH username/address. Respect immutable allocation; never imply this form updates a running VPS.
- Use existing components and current App → Environment → Services navigation. No Apps/Plans redesign.

Acceptance: focused form, default, account-switch race, error, permission and allocation-lock tests plus dashboard typecheck/build.

## T52 — CLI

Own existing app/environment creation/update command partials and API requests/tests after T20's CLI source-distribution work. T62 follows for deployment command/model ownership.

- Support equivalent existing-key reference or public-key-file input plus optional name. Send only validated public material, never a user private key.
- Noninteractive omission means Minicloud-only. Resolve exact organization/account/environment, preserve explicit errors, and keep SSH selection out of service YAML.
- Explain deferred registration and immutable allocated selections without adding key-generation, remote SSH or running-server key commands.

Acceptance: focused argument/public-file validation, mutual exclusion, omitted default, authorization, account and request-shape tests.

## Assignment prompts

Assign one task ID per PR. Implement only T50, T51 or T52, starting from its merged prerequisites and the linked complete SSH spec. Include focused checks and hand off shared files explicitly. No live provider calls, releases or hosted qualification; Codex tests real provider behavior after all required implementation merges.
