namespace Minicloud.Common.Services;

/// <summary>
/// T20 Grafana configuration. Grafana and Loki stay private: the API reaches
/// Grafana only over <see cref="BaseAddress"/> (loopback/private network) and Loki
/// only through the query gateway using Telemetry.LokiBaseAddress.
/// Pinned versions (evidence: compose.observability.yml):
/// Grafana OSS 11.6.5, Loki 3.5.2.
/// </summary>
public sealed class GrafanaOptions
{
    public const string SectionName = "Grafana";

    public const string PinnedGrafanaVersion = "11.6.5";
    public const string PinnedLokiVersion = "3.5.2";

    public string BaseAddress { get; set; } = "http://grafana:3000";
    public string ExpectedVersion { get; set; } = PinnedGrafanaVersion;
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// OpenBao path holding the Grafana server-admin password used ONLY by the
    /// private provisioning client. Never exposed through a public route.
    /// </summary>
    public string OperatorSecretPath { get; set; } = "ops/grafana/server-admin";

    /// <summary>
    /// Audience bound into launch codes (one value per environment, e.g.
    /// production/staging). A code issued for another audience never verifies.
    /// </summary>
    public string Audience { get; set; } = "production";

    public string ProxyCookieName { get; set; } = "mc_grafana_proxy";

    /// <summary>
    /// Public base URL of the query gateway baked into provisioned Loki data
    /// sources (e.g. https://api.cloud.muni.dev/v1/grafana/query-gateway).
    /// Empty falls back to the current request host.
    /// </summary>
    public string QueryGatewayBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Allowed Origin values for the POST-only exchange/logout endpoints (CSRF).
    /// Empty in non-development environments fails closed.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
}
