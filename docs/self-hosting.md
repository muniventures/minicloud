# Self-hosting Minicloud

The Compose setup runs the control plane on one Linux host and provisions application VPSs in your configured Hetzner, DigitalOcean, or Vultr accounts. Use a single Firebase operator account and domains you control. DNS records can be created manually with any provider; Cloudflare automation is optional. The setup uses source builds on the application VPS. The CLI's local image build mode requires a separately configured registry.

## Prepare the control plane

1. Choose a main service hostname for the dashboard (for example `cloud.example.com`) and its API hostname (`api.cloud.example.com`), and an application subdomain suffix inside your DNS zone. Set API_HOSTNAME and CONSOLE_HOSTNAME to the hostnames in API_URL and CONSOLE_URL. Set the API and dashboard DNS records to the control-plane host. Configure a reverse proxy there to terminate TLS and forward to loopback ports 3210 and 3200, respectively.
2. Create a Firebase project and web app, enable your sign-in method, and authorize the dashboard hostname. Create an operator account and copy its Firebase user ID. The API admits only the configured operator ID in this setup.
3. Prepare a persistent OpenBao instance reachable from the containers. Enable KV v2 at your chosen mount path. Supply a scoped renewable token with access to Minicloud's paths and permission to renew itself. Back up the database, OpenBao data, artifact volume, and stable encryption key together.
4. Create a dedicated deployment SSH key under `local/ssh/deploy-key`, restrict it to its owner, and create an empty `local/ssh/host-pins` file. Supply the matching public key. Add cloud provider credentials through the dashboard after startup; they are stored in OpenBao rather than installation-global provider token settings. Supply DNS tokens only if using Cloudflare automation. The provisioned deploy user can administer Docker and the server, so protect its private key as a root credential.
5. Copy `.env.example` to `.env` and fill in the settings. Use a random database password and a separate stable encryption key. Keep `.env` and `local/ssh/` on the control-plane host.

```sh
chmod 600 .env local/ssh/deploy-key
docker compose -f compose.self-host.yml up -d --build
docker compose -f compose.self-host.yml ps
curl --fail https://api.cloud.example.com/health
```

## Deploy an app

Sign in to the dashboard, open **Providers**, add an account and a named plan,
then create an app using that plan. Several accounts of the same provider can
coexist. See [Providers and plans](providers.md), including the explicit import
procedure for existing installations. Point the CLI at
the main service hostname; it derives the API by prefixing `api.` and validates
`GET /api/instance` before saving the instance. The API returns the installation's
application domain, so the CLI does not need a separate domain setting.

```sh
minicloud instance add personal cloud.example.com
minicloud login
minicloud init
```

Alternatively, run `minicloud login` on a fresh installation and enter the main
service URL when prompted. Use `minicloud login --token <your-api-key>` for an
API key created in the dashboard. Each instance has its own saved token.
Use `minicloud instance list` and `minicloud instance use <name>` to switch.
Configure API DNS and TLS at `api.<main-service-hostname>` before connecting.
The API must include the instance-info endpoint introduced with this CLI version.

Set `buildMode: source` for services that build from a `sourcePath`, then run
`minicloud deploy`. On initial provisioning the worker refuses SSH until the
VPS host key is trusted. Use the provider console to verify the server identity
and obtain the server's OpenSSH SHA256 host key fingerprints. Add one line per
trusted key to `local/ssh/host-pins` in the form
`<server-ip> SHA256:<fingerprint>`, then retry the deployment. Trust only keys
obtained through an independent channel. Review `minicloud status` and
`minicloud logs` after the deployment.

For GitHub Actions, set the reusable workflow's `minicloud_api_url` input to
your API origin, set `buildMode: source` on each source-backed service, and
supply a dedicated Minicloud key. See the [workflow reference](github-actions.md).

## DNS and HTTPS

The default `MANAGE_DNS_RECORDS=false` never creates or deletes provider records.
On first provisioning, inspect deployment events for each generated hostname and
server IP. Create DNS-only A records (or AAAA for IPv6) at your DNS provider.
Remove stale A/AAAA records pointing elsewhere. The worker waits for propagation;
if it times out, create the records and retry. Each app has its own VPS: a single
wildcard pointing at the control plane cannot route all apps. Branch apps and
additional managed aliases need records pointing at their own app server.

Application VPSs require public TCP 80 and 443 for Caddy and certificate issuance.
Restrictive CAA records must permit Caddy's ACME issuer. Do not use a CDN proxy
while qualifying DNS and certificates. Configure TLS for the control-plane API
and dashboard separately. Allow the dashboard origin in Firebase Authentication's
authorized domains and any enabled OAuth provider's redirect configuration.

For automatic generated-host DNS, set `MANAGE_DNS_RECORDS=true` and supply
`CLOUDFLARE_ZONE_ID` for the zone containing APP_DOMAIN and a scoped DNS-edit token
in `CLOUDFLARE_API_TOKEN`. This automation covers generated hostnames only.

To use a custom service domain, register the apex or subdomain in the app's Domains
page. Publish the returned persistent TXT proof at
`_minicloud-verification.<registered-domain>`, verify ownership, and attach a
hostname to a public service. Point that hostname's A/AAAA records at the app VPS.
Every resolved address must match the server. No customer DNS token is required.
Unbinding removes the runtime route; remove DNS records manually when no longer
needed. Retain TXT proof while keeping the registration.

API and dashboard hosts, enabled telemetry hosts, and APP_DOMAIN are reserved
for the installation. Additional reservations use `ManagedDomains__ReservedHostnames`.
A reservation covers its descendants, without reserving unrelated names in the
operator's zone. Keep these settings consistent across API and worker.

Telemetry is optional and is not installed by this Compose file. If deployed,
set `Telemetry__TelemetryServiceUrl`, management/signing keys and the telemetry
service's `Telemetry__AccessOrigin`. Set `TELEMETRY_INGESTION_URL`,
`TELEMETRY_ACCESS_ORIGIN`, `TELEMETRY_HOSTNAME` and `TELEMETRY_ACCESS_HOSTNAME`
for the control plane. Proxy both HTTPS hosts to telemetry and keep Loki/Grafana
private. The dashboard displays the configured ingestion URL returned by the API.
Do not enable application logs before configuring this separate stack.

Live authentication, unrelated-domain deployment, certificate issuance, custom
binding and route removal still require qualification on a disposable installation.
