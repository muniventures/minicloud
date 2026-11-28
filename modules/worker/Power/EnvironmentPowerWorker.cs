using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Selfrun.Common.Services;

namespace Selfrun.Worker.Power;

public sealed class EnvironmentPowerWorker(
    IServiceScopeFactory scopes,
    MaintenanceParticipant maintenance,
    TimeProvider clock,
    ILogger<EnvironmentPowerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastSnapshotReconciliation = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await maintenance.RunActivityAsync("environment_power_worker", requireOpen: false, async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<EnvironmentPowerProcessor>();
                    await processor.ProcessNextAsync(ct);
                    if (lastSnapshotReconciliation is null ||
                        clock.GetUtcNow() - lastSnapshotReconciliation >= TimeSpan.FromHours(1))
                    {
                        await processor.ReconcileSnapshotsAsync(ct);
                        lastSnapshotReconciliation = clock.GetUtcNow();
                    }
                }, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning("Environment power worker pass failed ({FailureType}).", error.GetType().Name);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
