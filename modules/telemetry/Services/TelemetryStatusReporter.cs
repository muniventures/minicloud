using System.Collections.Concurrent;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Telemetry.Services;

public sealed class TelemetryStatusReporter
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastAccepted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TelemetryCleanupStatusItem> _cleanups = new(StringComparer.Ordinal);

    public void RecordLastAccepted(string appId, DateTimeOffset timestamp)
    {
        _lastAccepted.AddOrUpdate(
            appId,
            timestamp,
            (_, existing) => timestamp > existing ? timestamp : existing);
    }

    public void RecordLastUsed(string credentialId, DateTimeOffset timestamp)
    {
        _lastUsed.AddOrUpdate(
            credentialId,
            timestamp,
            (_, existing) => timestamp > existing ? timestamp : existing);
    }

    public void RecordCleanupStatus(
        string jobId, string appId, string tenantId, string status, string? error, DateTimeOffset? completedAt)
    {
        _cleanups[jobId] = new TelemetryCleanupStatusItem
        {
            JobId = jobId,
            AppId = appId,
            TenantId = tenantId,
            Status = status,
            LastError = error,
            CompletedAt = completedAt
        };
    }

    public TelemetryStatusReport SnapshotAndClear(TimeProvider timeProvider)
    {
        var report = new TelemetryStatusReport
        {
            ReportedAt = timeProvider.GetUtcNow()
        };

        foreach (var key in _lastAccepted.Keys.ToArray())
        {
            if (_lastAccepted.TryRemove(key, out var timestamp))
            {
                report.AppStatuses.Add(new TelemetryAppUsageStatus
                {
                    AppId = key,
                    LastAcceptedAt = timestamp
                });
            }
        }

        foreach (var key in _lastUsed.Keys.ToArray())
        {
            if (_lastUsed.TryRemove(key, out var timestamp))
            {
                report.CredentialStatuses.Add(new TelemetryCredentialUsageStatus
                {
                    CredentialId = key,
                    LastUsedAt = timestamp
                });
            }
        }

        foreach (var key in _cleanups.Keys.ToArray())
        {
            if (_cleanups.TryRemove(key, out var item))
            {
                report.CleanupStatuses.Add(item);
            }
        }

        return report;
    }
}
