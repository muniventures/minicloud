namespace Minicloud.Common.Services;

public sealed class TelemetryClientOptions
{
    public const string SectionName = "Telemetry";

    public string TelemetryServiceUrl { get; set; } = "http://telemetry:8080";
    public string IngestionUrl { get; set; } = "https://telemetry.cloud.muni.dev/otlp/v1/logs";
    public string AccessOrigin { get; set; } = "https://logs.cloud.muni.dev";
    public string ManagementApiKey { get; set; } = "minicloud-telemetry-management-dev-key";
    public string AuthSigningKey { get; set; } = "minicloud-telemetry-auth-signing-dev-key-must-be-long";
    public int SyncIntervalSeconds { get; set; } = 15;
}
