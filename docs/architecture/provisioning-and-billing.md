# Provisioning and billing architecture

Municloud manages the VPS under the hood. Customers should not need to create a VPS, connect their own provider account, or understand the provider surface.

## Product decision

Municloud is the infrastructure merchant of record for V1:

- Municloud chooses the VPS provider.
- Municloud provisions servers in Municloud-owned provider accounts.
- Municloud pays the provider.
- Municloud bills the customer the server cost plus a markup.
- Municloud owns lifecycle operations: create, bootstrap, suspend, resize where supported, rebuild, and destroy.

This is a stronger product promise than bring-your-own-VPS and creates more operational responsibility.

## V0 stance

V0 should rehearse the same model, but for internal use:

- choose one default provider first, while keeping workflows provider-aware
- store provider API tokens in GitHub Actions secrets
- run `municloud-provision.yml` manually with `workflow_dispatch`
- create the VPS through the provider API
- pass cloud-init/user-data that installs the Municloud runtime
- resolve server ID, IP, region, type, and provider metadata dynamically from provider labels
- deploy through the normal GitHub Actions deploy workflow

V0 has no CLI and no customer-facing billing. It should still record provider cost inputs so V1 pricing can be modeled.

## V1 stance

V1 moves provisioning into the hosted control plane:

1. Customer signs up and selects a plan.
2. Customer connects GitHub and chooses a repository.
3. Customer selects a simple deployment tier, not a raw VPS SKU.
4. Municloud maps the tier to a provider, region, and server type.
5. Municloud creates the VPS in a Municloud-owned provider account.
6. Cloud-init installs the runtime and registers the server.
7. GitHub Actions builds the image.
8. Municloud deploys to the managed VPS.
9. Municloud bills monthly for base VPS cost plus markup.

Customers can see the plan, region, included resources, and monthly price. They should not need to see provider IDs unless needed for support.

## Provider strategy

Start with one default provider, but keep the workflow/provider contract generic. Recommended candidates:

- Hetzner: very low cost, strong fit for the "boring bill" promise, fewer regions in the US.
- DigitalOcean: friendlier API/product surface, broader developer trust, higher base cost.
- Vultr: good global coverage and simple API.

Use a provider adapter boundary from day one, but only implement one provider until V0/V1 prove the flow.

Adapter responsibilities:

- create SSH key or inject cloud-init access
- create firewall
- create server
- attach tags/labels
- read server state
- destroy server
- read or model price
- handle provider rate limits and transient failures

## Plan abstraction

Do not expose raw provider SKUs as the primary product surface. Expose Municloud plans:

```text
Starter
  1 small VPS
  1 app
  fixed region choices
  basic logs and rollback

Dev
  larger VPS
  multiple apps
  longer deploy history

Pro
  larger VPS or multiple VPSs later
  backups and alerts later
```

Internally map each Municloud plan to provider SKU, region, disk, bandwidth assumptions, and target margin.

## Pricing model

For V1, price should be simple and predictable:

```text
customer_price = provider_monthly_cost + municloud_markup
```

Track:

- provider
- provider server ID
- provider SKU
- provider listed monthly cost
- Municloud plan
- customer monthly price
- markup amount
- provisioning date
- deletion date
- billing status

Decide whether markup is a fixed dollar amount, a percentage, or a tiered bundle. Fixed markup is easier to explain early.

## Billing lifecycle

Provisioning should be gated by billing state:

- trial/private beta entitlement, or
- valid payment method and active subscription

Server lifecycle should follow subscription lifecycle:

- payment failed -> grace period
- grace expired -> suspend app or power off server where supported
- cancellation -> schedule deletion after data retention window
- deletion -> destroy provider server and stop billing

Never silently destroy customer data without a clear retention policy and repeated warnings.

## Cloud-init bootstrap

Provisioned servers should be born ready. Use cloud-init/user-data to:

- create `municloud` runtime user
- install Docker and Compose
- install Caddy
- configure firewall
- create `/opt/municloud`
- install runtime files
- register with the control plane using a one-time token
- start a lightweight runtime agent if V1 uses one

V0 can embed this bootstrap in the provisioning workflow. V1 should generate cloud-init from the control plane.

## Runtime connectivity

Prefer outbound connectivity from the VPS to the control plane:

- avoids opening a public management API on the VPS
- avoids storing SSH private keys for every customer deploy path
- works behind stricter provider firewalls

V0 can use SSH from GitHub Actions for speed. V1 should evaluate an outbound runtime agent with short-lived tokens for deployment commands, status, logs, and rollback.

## Operational risks

Municloud-owned VPS billing creates real obligations:

- abuse and spam risk
- provider account suspension risk
- unpaid customer cost risk
- data deletion and retention requirements
- support responsibility for server incidents
- region capacity and provider outage handling
- tax/accounting complexity for resold infrastructure

V1 should start with a private beta, strict limits, one provider, one or two plans, and manual admin override tools.
