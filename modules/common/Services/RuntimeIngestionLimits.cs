namespace Minicloud.Common.Services;

public static class RuntimeIngestionLimits
{
    public const int MaxBatchRecords = 500;
    public const int MaxLineLength = 16 * 1024;
    public const int MaxSourceLength = 128;
    public const int MaxCursorLength = 256;
    public static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LogRetention = TimeSpan.FromDays(2);
    public const int PerAppRetentionRows = 50_000;
}
