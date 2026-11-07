using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Minicloud.Worker.Deletion;

public sealed class AppDeletionWorker(IServiceScopeFactory scopes, ILogger<AppDeletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (await scope.ServiceProvider.GetRequiredService<AppDeletionProcessor>().ProcessNextAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Application deletion processing failed.");
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
