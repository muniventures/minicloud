# Design

## Source of truth

Status: Active for the Plans and Provider accounts redesign; not a replacement
for unrelated dashboard modules. Date: 2026-11-09.

- Detailed contract: [Plan management UX](docs/specs/plan-management-ux.md).
- Domain contract: [Provider accounts and named plans](docs/specs/provider-accounts-and-plans.md).
- The user approved a plan-first collection using existing toolbar, table and
  centered modal patterns, and requested dashboard implementation.
- Evidence reviewed: `PageScaffold`, `CollectionToolbar`, `collectionQuery`,
  `ApiKeysModule`, `TeamSettingsModule`, `AppsModule`, `ProvidersModule`,
  `providersApi`, dashboard routes and provider/plan API contracts.
- [Approved visual direction](docs/specs/assets/plan-management-ux.png). The
  detailed spec controls behavior where illustrative controls differ from the API.

## Brand

Practical, restrained and operational. Preserve the dashboard shell and existing
theme. Avoid decorative metrics, large promotional headers and new design systems.

## Product goals

Make plans easy to find, create and maintain across provider accounts. Account
configuration is secondary. Success means one clear plan collection, one primary
action and familiar forms; no repeated account cards or long action-button rows.
Do not change provisioning, pricing, credentials, resource lifecycle or CLI behavior.

## Personas and jobs

- Organization owners/admins maintain plans and provider connections.
- Other authorized members discover plans for app creation and CLI configuration.
- Users with several accounts of one provider must distinguish placements reliably.

## Information architecture

`/plans` is the primary collection and sidebar item. `/plans/accounts` is the
secondary provider-account collection. Preserve `/providers` as a redirect to
`/plans`. Add/edit use dialogs; neither collection needs a details drawer or new
detail-page interaction. See the detailed spec for return navigation and filters.

## Design principles

Plans first; reuse existing interactions; disclose advanced settings on demand;
keep unavailable information unavailable; communicate locked placement accurately.
Preserve organization boundaries and visible operation/error feedback.

## Visual language

Use current theme tokens, typography, spacing and radii. Follow `PageScaffold`
title sizing and the API Keys Card/table treatment. Use existing status, border,
muted-text and destructive tokens. Icons use the current Lucide set. Motion is
limited to existing loading feedback and respects reduced-motion preferences.

## Components

Reuse `PageScaffold`, `CollectionToolbar`, `useCollectionQuery`, `compareRecords`,
Card/Table, Dialog, AlertDialog, DropdownMenu, Disclosure and existing form controls.
No bespoke toolbar, persistent inspector, alternate filter bar or new component
dependency. These collections use tables; do not add a Cards/List switch here.

## Accessibility

Target WCAG 2.2 AA. Use labeled fields, semantic table headers, textual statuses,
named icon buttons, keyboard-accessible menus and visible focus. Dialogs trap focus
and restore it to their trigger. Announce errors and pending operations; do not
make plain rows behave as undisclosed edit buttons. Locked fields need an
accessible explanation. Never rely on hover or color alone.

## Responsive behavior

Reuse current responsive table and toolbar behavior. Controls wrap without hiding
the primary action. Preserve labeled values and accessible row actions on mobile.
Dialogs fit the viewport and scroll internally; paired fields stack on narrow
screens. Long account/plan names wrap without overlapping actions.

## Interaction states

Distinguish initial loading, populated collection, no records, no matches,
refreshing, read errors, form errors, stale revisions, saving and successful saves.
Retain useful rows during refresh; clear organization-specific data on organization
switch. Failed writes keep the form open. Never turn missing data into an empty
success state or imply that a credential check proves available provider quota.

## Content voice

Use Plans, Provider accounts, Add plan, Edit, Duplicate plan and Save changes.
Use Server type as the user-facing label for the provider SKU. Use Location for
region codes. Keep provider accounts distinct from Minicloud API keys. Avoid
backend identifiers and secret-store implementation details in ordinary forms.

## Implementation constraints

This is a dashboard presentation change using existing React/shadcn components
and APIs. No database migration, provider catalog integration or runtime/CLI change
is required. Existing authorization, revision checks, naming rules and immutable
placements remain authoritative. Preserve unrelated concurrent source changes.
Implementation must validate the detailed acceptance criteria and dashboard checks;
mockups alone are not implementation evidence.

## Open questions

No blocking design questions. Live provider SKU/location catalogs, pricing and
capacity discovery remain outside this redesign; no owner action is required for
those deferred capabilities.
