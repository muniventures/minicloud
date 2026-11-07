# Self-hosting Minicloud

The Compose setup runs the control plane on one Linux host and provisions an application VPS in your Hetzner account. Use a single Firebase operator account and a Cloudflare-managed DNS zone for application hostnames. The setup uses source builds on the application VPS. The CLI's local image build mode requires a separately configured registry.

## Prepare the control plane

1. Choose separate HTTPS hostnames for the API and dashboard, and an application subdomain suffix inside your DNS zone. Set the API and dashboard DNS records to the control-plane host. Configure a reverse proxy there to terminate TLS and forward to loopback ports 3210 and 3200, respectively.
2. Create a Firebase project and web app, enable your sign-in method, and authorize the dashboard hostname. Create an operator account and copy its Firebase user ID. The API admits only the configured operator ID in this setup.
3. Prepare a persistent OpenBao instance reachable from the containers. Enable KV v2 at your chosen mount path. Supply a scoped renewable token with access to Minicloud's paths and permission to renew itself. Back up the database, OpenBao data, artifact volume, and stable encryption key together.
4. Create a dedicated deployment SSH key under `local/ssh/deploy-key`, restrict it to its owner, and create an empty `local/ssh/host-pins` file. Supply the matching public key and dedicated provider and DNS tokens. The provisioned deploy user can administer Docker and the server, so protect its private key as a root credential.
5. Copy `.env.example` to `.env` and fill in the settings. Use a random database password and a separate stable encryption key. Keep `.env` and `local/ssh/` on the control-plane host.

```sh
chmod 600 .env local/ssh/deploy-key
docker compose -f compose.self-host.yml up -d --build
docker compose -f compose.self-host.yml ps
curl --fail https://api.example.com/health
```

## Deploy an app

Sign in to the dashboard, create an app on the Hetzner plan, and create an API key for your CLI. Set `MINICLOUD_API_URL` before adding the token. Set a separate `XDG_CONFIG_HOME` if you also use a different Minicloud instance.

```sh
export MINICLOUD_API_URL=https://api.example.com
minicloud login --token <your-api-key>
minicloud init
```

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
