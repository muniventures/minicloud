namespace Minicloud.Common.Services;

public sealed class TelemetryClientOptions
{
    public const string SectionName = "Telemetry";

    public string TelemetryServiceUrl { get; set; } = "http://telemetry:8080";
    public string IngestionUrl { get; set; } = "http://localhost:8088/otlp/v1/logs";
    public string AccessOrigin { get; set; } = "http://localhost:8088";
    public string ManagementApiKey { get; set; } = "minicloud-telemetry-management-dev-key";
    public string AuthSigningKey { get; set; } = "minicloud-telemetry-auth-signing-dev-key-must-be-long";
    public int SyncIntervalSeconds { get; set; } = 15;
}
