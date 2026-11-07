using System.Collections.Concurrent;

namespace Minicloud.Common.Services;

/// <summary>
/// T30 fail-fast concurrency gate for application-log reads. Concurrency is bounded
/// per app and stack-wide so one customer's exploration cannot starve ingestion or
/// other tenants. Callers that fail to enter receive a retryable 429; ordinary
/// control-plane operations never wait on this gate.
/// </summary>
public sealed class TelemetryQueryLimiter
{
    private readonly SemaphoreSlim _total = new(
        TelemetryLimits.MaxConcurrentQueriesTotal, TelemetryLimits.MaxConcurrentQueriesTotal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perApp = new(StringComparer.Ordinal);

    public bool TryEnter(string appId)
    {
        if (!_total.Wait(0))
        {
            return false;
        }

        var scoped = _perApp.GetOrAdd(
            appId, _ => new SemaphoreSlim(TelemetryLimits.MaxConcurrentQueriesPerApp, TelemetryLimits.MaxConcurrentQueriesPerApp));
        if (!scoped.Wait(0))
        {
            _total.Release();
            return false;
        }

        return true;
    }

    public void Release(string appId)
    {
        if (_perApp.TryGetValue(appId, out var scoped))
        {
            scoped.Release();
        }

        _total.Release();
    }
}
