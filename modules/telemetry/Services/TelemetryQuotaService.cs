using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Telemetry.Data;

namespace Minicloud.Telemetry.Services;

public sealed record TelemetryQuotaDecision(bool Admitted, string? Reason, TimeSpan? RetryAfter);

public sealed class TelemetryQuotaService(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    IOptions<TelemetryOptions> options)
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _dailyGate = new(1, 1);

    public async Task<TelemetryQuotaDecision> ReserveAsync(
        string appId,
        string organizationId,
        long byteCount,
        CancellationToken cancellationToken)
    {
        if (byteCount < 0)
        {
            return new TelemetryQuotaDecision(false, "Invalid byte count.", null);
        }

        var limits = options.Value;
        var burst = Math.Max(1, limits.PerAppBurstBytes);
        var sustained = Math.Max(1, limits.PerAppSustainedBytesPerSecond);

        if (byteCount > burst)
        {
            return new TelemetryQuotaDecision(
                false,
                $"Request size {byteCount} bytes exceeds the per-request burst budget of {burst} bytes; send smaller batches.",
                TimeSpan.FromSeconds(5));
        }

        var bucket = _buckets.GetOrAdd(appId, _ => new TokenBucket(burst));
        TimeSpan? retryAfter = null;
        var now = timeProvider.GetUtcNow();
        lock (bucket.Sync)
        {
            bucket.Refill(now, sustained, burst);
            if (bucket.Available < byteCount)
            {
                var deficit = byteCount - bucket.Available;
                retryAfter = TimeSpan.FromSeconds(Math.Min(300, Math.Max(1, (deficit + sustained - 1) / sustained)));
            }
            else
            {
                bucket.Available -= byteCount;
            }
        }

        if (retryAfter is not null)
        {
            return new TelemetryQuotaDecision(false, "Per-app ingest rate exceeded.", retryAfter);
        }

        await _dailyGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return await ReserveDailyAsync(appId, organizationId, byteCount, limits, cancellationToken);
                }
                catch (DbUpdateException) when (attempt == 0)
                {
                    // Cross-process insert race on the day row: re-read and retry once.
                }
            }

            return new TelemetryQuotaDecision(false, "Telemetry budget store is temporarily unavailable.", TimeSpan.FromSeconds(5));
        }
        finally
        {
            _dailyGate.Release();
        }
    }

    private async Task<TelemetryQuotaDecision> ReserveDailyAsync(
        string appId,
        string organizationId,
        long byteCount,
        TelemetryOptions limits,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        var day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var now = timeProvider.GetUtcNow();

        var row = await db.DailyUsages
            .SingleOrDefaultAsync(x => x.AppId == appId && x.Day == day, cancellationToken);
        if (row is null)
        {
            row = new TelemetryDailyUsage
            {
                Id = Guid.NewGuid().ToString("N"),
                OrganizationId = organizationId,
                AppId = appId,
                Day = day,
                AdmittedBytes = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.DailyUsages.Add(row);
        }
        else if (!string.Equals(row.OrganizationId, organizationId, StringComparison.Ordinal))
        {
            return new TelemetryQuotaDecision(false, "Telemetry budget ownership mismatch.", null);
        }

        var appAdmitted = row.AdmittedBytes;
        if (appAdmitted + byteCount > limits.PerAppDailyBytes)
        {
            return new TelemetryQuotaDecision(false, "Per-app daily admission budget exceeded.", RetryAfterUntilNextDay(now));
        }

        var orgAdmitted = await db.DailyUsages
            .Where(x => x.OrganizationId == organizationId && x.Day == day)
            .SumAsync(x => (long?)x.AdmittedBytes, cancellationToken) ?? 0;
        if (orgAdmitted + byteCount > limits.PerOrganizationDailyBytes)
        {
            return new TelemetryQuotaDecision(false, "Per-organization daily admission budget exceeded.", RetryAfterUntilNextDay(now));
        }

        var globalAdmitted = await db.DailyUsages
            .Where(x => x.Day == day)
            .SumAsync(x => (long?)x.AdmittedBytes, cancellationToken) ?? 0;
        if (globalAdmitted + byteCount > limits.WholeStackDailyBytes)
        {
            return new TelemetryQuotaDecision(false, "Whole-stack daily admission budget exceeded.", RetryAfterUntilNextDay(now));
        }

        row.AdmittedBytes += byteCount;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new TelemetryQuotaDecision(true, null, null);
    }

    private static TimeSpan RetryAfterUntilNextDay(DateTimeOffset now)
    {
        var nextDay = now.UtcDateTime.Date.AddDays(1);
        var delay = nextDay - now.UtcDateTime;
        return delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay;
    }

    private sealed class TokenBucket(long capacity)
    {
        public readonly object Sync = new();
        public long Available = capacity;
        public DateTimeOffset LastRefill;
        private bool _initialized;

        public void Refill(DateTimeOffset now, long sustainedPerSecond, long capacityCap)
        {
            if (!_initialized)
            {
                LastRefill = now;
                _initialized = true;
                return;
            }

            var elapsedSeconds = (now - LastRefill).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            var refill = (long)(elapsedSeconds * sustainedPerSecond);
            if (refill > 0)
            {
                Available = Math.Min(capacityCap, Available + refill);
                LastRefill = now;
            }
        }
    }
}
