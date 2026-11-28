using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Selfrun.Common.Data;

namespace Selfrun.Worker.Power;

internal sealed class EnvironmentPowerOperationLease : IAsyncDisposable
{
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider clock;
    private readonly string operationId;
    private readonly string ownerToken;
    private readonly TimeSpan duration;
    private readonly TimeSpan interval;
    private readonly ILogger<EnvironmentPowerOperationLease>? logger;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationTokenSource lost = new();
    private readonly Task heartbeat;

    public EnvironmentPowerOperationLease(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        string operationId,
        string ownerToken,
        TimeSpan duration,
        TimeSpan interval,
        ILogger<EnvironmentPowerOperationLease>? logger = null)
    {
        this.scopes = scopes;
        this.clock = clock;
        this.operationId = operationId;
        this.ownerToken = ownerToken;
        this.duration = duration;
        this.interval = interval;
        this.logger = logger;
        heartbeat = HeartbeatAsync();
    }

    public CancellationToken Lost => lost.Token;

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(interval, clock, stop.Token);
                var now = clock.GetUtcNow();
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SelfrunDbContext>();
                var owned = db.EnvironmentPowerOperations
                    .Where(x => x.Id == operationId && x.OwnershipToken == ownerToken &&
                        (x.Status == "running" || x.Status == "cancelling"));
                if (!db.Database.IsNpgsql())
                {
                    var leaseExpiresAt = await owned.Select(x => x.LeaseExpiresAt).SingleOrDefaultAsync(stop.Token);
                    if (leaseExpiresAt is null || leaseExpiresAt <= now)
                    {
                        lost.Cancel();
                        return;
                    }
                }

                var lease = db.EnvironmentPowerOperations
                    .Where(x => x.Id == operationId && x.OwnershipToken == ownerToken &&
                        (x.Status == "running" || x.Status == "cancelling"));
                if (db.Database.IsNpgsql())
                    lease = lease.Where(x => x.LeaseExpiresAt > now);
                var affected = await lease
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.LeaseExpiresAt, now + duration)
                        .SetProperty(x => x.UpdatedAt, now), stop.Token);
                if (affected != 1)
                {
                    lost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger?.LogWarning("Sleep operation {OperationId} lease heartbeat failed ({FailureType}).",
                operationId, error.GetType().Name);
            lost.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await heartbeat;
        stop.Dispose();
        lost.Dispose();
    }
}
