using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Maintenance;

public sealed class RuntimeLogRetentionWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<RuntimeLogRetentionWorker> logger,
    MaintenanceParticipant? maintenance = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                if (maintenance is null) await PruneAsync(stoppingToken);
                else await maintenance.RunActivityAsync("runtime_log_retention", requireOpen: true, PruneAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Runtime log retention failed; the next maintenance pass will retry.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task PruneAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var cutoff = clock.GetUtcNow() - RuntimeIngestionLimits.LogRetention;
        if (db.Database.IsSqlite())
        {
            var records = await db.RuntimeLogRecords.ToListAsync(cancellationToken);
            db.RuntimeLogRecords.RemoveRange(records.Where(record => record.ObservedAt < cutoff));
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await db.RuntimeLogRecords.Where(record => record.ObservedAt < cutoff).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
