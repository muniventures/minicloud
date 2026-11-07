namespace Minicloud.Common.Services;

public static class TelemetryLimits
{
    public const string ReservedHeadersSecretName = "MINICLOUD_OTLP_LOGS_HEADERS";

    public const long MaxEncodedBytes = 1 * 1024 * 1024;
    public const long MaxDecompressedBytes = 4 * 1024 * 1024;
    public const int MaxRecordsPerRequest = 500;
    public const int MaxBodyBytesPerRecord = 16 * 1024;
    public const int MaxAttributesBytesPerRecord = 16 * 1024;
    public const int MaxAttributeCountPerRecord = 64;
    public const int MaxAttributeNestingDepth = 8;

    public const long PerAppSustainedBytesPerSecond = 64 * 1024;
    public const long PerAppBurstBytes = 1 * 1024 * 1024;
    public const long PerAppDailyBytes = 100 * 1024 * 1024;
    public const long PerOrganizationDailyBytes = 250 * 1024 * 1024;
    public const long WholeStackDailyBytes = 1024 * 1024 * 1024;

    public const int MaxQueryEntries = 1000;
    public const long MaxQueryResponseBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxQueryRange = TimeSpan.FromDays(7);
    public const int MaxConcurrentQueriesPerApp = 2;
    public const int MaxConcurrentQueriesTotal = 8;

    public static readonly TimeSpan Retention = TimeSpan.FromDays(2);
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CredentialCacheTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultCredentialLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan MaxCredentialLifetime = TimeSpan.FromDays(365);
    public const int MaxActiveCredentialsPerService = 2;

    public static readonly TimeSpan GrafanaLaunchCodeTtl = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan GrafanaSessionIdle = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan GrafanaSessionAbsolute = TimeSpan.FromHours(1);
    public static readonly TimeSpan AuthorizationCacheTtl = TimeSpan.FromSeconds(30);

    // Outage tolerance: synchronized valid credentials work up to 60 minutes after confirmed checkpoint.
    public static readonly TimeSpan MaxIngestionOutageAllowance = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan CheckpointHeartbeatInterval = TimeSpan.FromSeconds(15);
}
