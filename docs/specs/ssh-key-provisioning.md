# SSH keys during VPS provisioning

Status: specification only; not implemented or provider-qualified.
Date: 2026-11-10.

## Outcome

When creating an app with an initial environment, or adding an environment, users can select an existing SSH key or add a new public key inline. Minicloud registers a new key with the selected cloud provider automatically before creating the VPS. Users never need to visit the provider console or enter a provider key ID.

An app is a logical grouping; its environment owns the server and SSH selection. Creating the app/environment continues to save configuration only. Registration and allocation run in the worker when the first deployment needs a new VPS. The UI explains: “This key will be installed when the server is created.”

## Scope and baseline

- Support Hetzner Cloud, DigitalOcean, and Vultr through their native creation-time key selection.
- Keep Minicloud's deployment credential separate from the user's access key. Minicloud continues connecting as `minicloud` using its operator-managed private key. Adding a user key must not replace this credential or require the user's private key.
- First release selects one user key per environment. Provider contracts accept lists so the implementation does not encode a one-key provider limitation.
- User access is optional: **Minicloud access only** preserves existing behavior and is the default for older clients that omit the field.
- Existing VPS access, root-password management, key generation/download, key rotation/revocation on running servers, and per-server deployment-key generation are outside this release.

Current source: `modules/common/Providers/Models/CreateServerRequest.cs` has one ambiguous `SshKeyIdOrPublicKey` string; all three adapters put it into a provider key array. `modules/worker/Pipeline/Steps/ResolveProvisionServerStep.cs` passes an empty value and installs the deployment public key through cloud-init. `modules/common/Providers/IVpsProviderClient.cs` has no key inventory or registration methods. This feature adds user access; it does not claim to resolve deployment-key isolation or the separate host-key trust configuration gap.

## Creation experience

After selecting a plan, show **SSH access** with:

1. **Minicloud access only** — deployment access remains available without adding a personal key.
2. **Use existing key** — searchable keys from the provider account/project selected by the plan. Show name, SHA256 fingerprint, and key type; never require raw IDs. Include keys created outside Minicloud.
3. **Add new key** — name and an OpenSSH public-key text field. Helper text: “Paste your public key, for example from ~/.ssh/id_ed25519.pub. Keep your private key on your computer.”

The form shows the selected provider account as supporting context. Registration is not a separate user action or setup screen. A newly added key appears in subsequent existing-key lists after successful registration. Multiple keys may share a display name; fingerprint and immutable identity distinguish them.

Changing the plan within the same provider account retains the selection. Changing accounts clears an existing-key selection and requires reselection; a new public-key draft may remain, with the new destination account shown. Late inventory responses must not populate another account's selector.

Show loading, empty, unavailable, and permission-denied inventory states distinctly. A failed lookup must not imply an empty key list. Keep the form draft on errors. Inventory failures block selecting an existing key, but users can choose Minicloud-only access or submit a validated new public key for deferred registration.

Environment details show the saved selection, fingerprint, provider account, registration state, and whether a server has been created. Before the first provisioning reservation, users can change the selection. A reservation's selection is immutable. After a failed attempt, allow reconfiguration only when the worker has reconciled the outcome, confirmed no VPS exists, and closed the reservation with no active allocation. Save a new selection revision and use a new reservation on retry. Block edits while allocation is active or ambiguous and after a VPS exists; explain that changing this form cannot update an existing VPS. Show the provider image's verified SSH username with the server address once available; never assume every image uses `root`.

## Ownership and persistence

Keys are scoped to an organization and its specific provider-account record, including that record's external account/project scope. Never search or reuse registrations across accounts merely because the provider type or display name matches.

Persist:

| Record | Required information |
| --- | --- |
| Environment SSH selection | Environment and organization IDs; provider-account ID; mode (`none`, `existing`, `new`); immutable local key reference or validated new-key draft; revision. |
| Key registration | Local ID; organization/account IDs; provider key ID/reference; canonical public key and SHA256 fingerprint; display name/type; registration state and timestamps. External inventory can be materialized into this same record shape. |
| Provisioning snapshot | Selection revision, account/scope identity, canonical fingerprint/public key, resolved provider reference, registration outcome, and allocation identity. |

Use account plus fingerprint to deduplicate concurrent registration requests. Public-key material is public data but remains organization-authorized metadata; do not dump it or provider credentials into deployment logs. Private user keys are never accepted or stored. The existing deployment private-key storage remains unchanged.

An existing-key selection must resolve against the selected account's inventory and contain retrievable, valid public-key material. Recheck authorization and external key identity before allocation. If an external key has disappeared, block allocation with a clear reselection/re-add error; never silently substitute another key or omit requested access.

## API and CLI contract

Extend initial-environment and environment creation/update requests with an optional `sshAccess` discriminated object:

```json
{"mode":"none"}
{"mode":"existing","keyId":"<Minicloud key reference>"}
{"mode":"new","name":"Work laptop","publicKey":"ssh-ed25519 <base64> laptop"}
```

Reject mixed modes, unknown fields, private-key blocks, malformed/unsupported key material, authorization-option prefixes, and multiple keys in one field. Parse and validate the actual key blob, derive its type/fingerprint, and normalize whitespace/comments without changing the key. Initial allowlist: Ed25519 and RSA of at least 3072 bits, subject to verified provider/image support. Names are trimmed, 1–128 characters, with no control characters; public-key input is capped at 16 KiB.

Provide an authenticated provider-account key-list endpoint returning local selection references, names, types, fingerprints, and registration state. Only backend adapters handle provider-specific references. Reuse current organization roles and API-key scopes: listing requires account read access; creating an environment with a new key requires environment creation and provider-key registration permission under the existing authorization model. Enforce the same checks for later provisioning retries; define any new permission explicitly during implementation rather than granting registration to every account reader.

CLI app/environment creation supports equivalent existing-key or public-key-file input and an optional new-key name. Upload only the public key. Noninteractive omission means Minicloud-only access. No private material or SSH selection belongs in per-service YAML; this is environment provisioning configuration. Existing-client requests remain valid.

## Worker sequence and failure handling

1. Resolve the environment's plan and provider account. Validate ownership, enabled/new-allocation state, current SSH selection, and image capability before external mutations. Persist an immutable reservation using the existing deployment lifecycle and environment mutation lock.
2. For `existing`, confirm the provider key reference still matches the selected fingerprint. For `new`, find the same fingerprint in that account and reuse it; otherwise register the public key with the provider. Persist the returned reference before attempting VPS creation.
3. Pass the resolved provider references as a list in the VPS creation request. Keep cloud-init responsible for the separate `minicloud` deployment user and its authorized deployment key. Verify both mechanisms work together on each supported image; native key injection must not be overwritten by cloud-init's user setup.
4. Continue the normal server readiness, host-identity verification, deployment, and runtime registration flow. User-key registration does not establish trust in the server's SSH host key and must never bypass configured host verification.

Retries resume the same reservation and persisted key identity. On registration timeout/ambiguous response or concurrent duplicate error, reconcile the provider inventory by fingerprint before issuing another registration. Fail closed while the outcome cannot be determined. A registration failure creates no VPS and never falls back to Minicloud-only access. An allocation timeout uses existing server discovery/reconciliation before another create call.

If registration succeeds but allocation fails or is cancelled, retain the key registration for retry and future selection. Cancellation after an external call reconciles its result. Environment deletion, cancellation, or a failed deployment never deletes an account-wide public key; it may be shared with other servers. Provider-list edits/deletion are not access revocation on running VPSs. This release offers no provider-key deletion operation.

Before provisioning, revalidate account scope and credential generation using the established provider-account ownership rules. Token rotation in the same scope is supported; replacing credentials with another project/account must not redirect registration or allocation. Existing environments with servers retain their access unchanged. Older environments without servers default to `none`; no automatic registration or remote rewrite runs during migration. Automated preview creation defaults to Minicloud-only access unless its request explicitly supplies a selection; it must not silently inherit a personal access key.

Report concise progress such as **Registering SSH key**, **Creating server**, and **SSH key ready**. Include stable failure codes for invalid key, wrong account, unavailable inventory, insufficient provider permission, registration failure, and missing/changed external key. Redact upstream errors. Audit who selected/added the key, its fingerprint/account, and the resulting registration/allocation IDs, without private material or provider tokens.

## Provider integration

| Provider | Native registration and creation |
| --- | --- |
| Hetzner Cloud | Register a public key in the selected project using `/ssh_keys`; supply registered references in server creation's `ssh_keys`. Resolve actual identifier types from the current API schema during implementation. |
| DigitalOcean | Register using `POST /v2/account/keys`; send registered IDs or fingerprints in droplet creation's `ssh_keys`. Keys belong to the selected team scope. |
| Vultr | Register using `POST /v2/ssh-keys`; send registered IDs in instance creation's key array. The adapter currently serializes `sshkey_id`; verify this against the current upstream schema during implementation. |

All three support multiple keys at creation. References checked on 2026-11-10: [Hetzner API](https://docs.hetzner.cloud/reference/cloud), [Hetzner server creation](https://docs.hetzner.com/cloud/servers/getting-started/creating-a-server/), [DigitalOcean droplets](https://docs.digitalocean.com/reference/api/reference/droplets/), [DigitalOcean SSH keys](https://docs.digitalocean.com/reference/api/reference/ssh-keys/), [Vultr instance creation](https://docs.vultr.com/reference/vultr-cli/instance/create), [Vultr key registration](https://docs.vultr.com/public/doc-assets/pdfs/collection_item/platform-other-ssh-keys-add-ssh-keys.pdf). These establish provider capabilities, not live Minicloud qualification.

## Acceptance and verification

- App-with-initial-environment and add-environment forms both offer existing/new keys inline; no provider console step or provider ID entry is required.
- Existing inventory includes provider-created keys, displays fingerprints, isolates accounts/organizations, and handles stale responses and permission failures without losing form input.
- Saving an environment allocates no VPS and performs no registration. First provisioning registers a new key before allocation; selecting an existing key skips registration.
- Provider request tests prove selected key references reach each creation payload and the deployment public key remains in the `minicloud` user's cloud-init configuration.
- Validation and authorization tests reject malformed/private keys, cross-organization/account references, insufficient permissions, and edits during active/ambiguous allocation or after a VPS exists. Prove failed reservations can be closed and selections revised only after reconciliation confirms no VPS exists. Omitted selections preserve old-client behavior.
- Retry/concurrency tests cover duplicate fingerprints, ambiguous registration/allocation outcomes, token rotation, cancellation after registration, and registration-success/allocation-failure. No case silently drops requested access, repeats a known allocation, or deletes a shared key.
- Tests cover account-changing plans, externally removed keys, preview defaults, migration of provisioned/unprovisioned environments, and read-only details for existing servers.
- For each supported provider and supported default Linux image, provision a disposable VPS with an existing key and with an inline-added key. Prove personal-key SSH access using the documented image user, managed `minicloud` SSH/deployment access, and enforced configured host trust. Verify retries reuse the registration, then tear down the VPS through normal deletion. Reuse a dedicated test public key; never expose its private half in artifacts.
- Report unit/integration checks separately from billable provider qualification. No implementation or hosted changes are authorized by this document alone.
