# Grafana file provisioning (T10 placeholder)

T10 intentionally provisions **no** static data sources or dashboards here.

Per-organization Grafana organizations and per-app Loki data sources are
provisioned dynamically by the Minicloud API (T20) through the private
operator path, using desired-state records and idempotent retries. A static
data source in this directory would bypass the app-bound credential model,
so this directory stays empty until T20 adds its provisioning client.

The bind mount (`./observability/grafana/provisioning:/etc/grafana/provisioning:ro`)
exists so local Grafana starts with the same provisioning path layout as
production.
