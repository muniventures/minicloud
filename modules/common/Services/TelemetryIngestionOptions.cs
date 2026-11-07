namespace Minicloud.Common.Services;

public sealed class TelemetryIngestionOptions
{
    public const string SectionName = "Telemetry";

    public string LokiBaseAddress { get; set; } = "http://loki:3100";
    public string Environment { get; set; } = "production";
    public int UpstreamTimeoutSeconds { get; set; } = 10;
    public int MaxConcurrentIngestions { get; set; } = 64;

    public long PerAppDailyBytes { get; set; } = TelemetryLimits.PerAppDailyBytes;
    public long PerOrganizationDailyBytes { get; set; } = TelemetryLimits.PerOrganizationDailyBytes;
    public long WholeStackDailyBytes { get; set; } = TelemetryLimits.WholeStackDailyBytes;
    public long PerAppBurstBytes { get; set; } = TelemetryLimits.PerAppBurstBytes;
    public long PerAppSustainedBytesPerSecond { get; set; } = TelemetryLimits.PerAppSustainedBytesPerSecond;
}
