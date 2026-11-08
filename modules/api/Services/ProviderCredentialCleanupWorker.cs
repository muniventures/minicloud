using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class ProviderCredentialCleanupWorker(IServiceScopeFactory scopes, ILogger<ProviderCredentialCleanupWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ProviderAccountService>().CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Raw secret-store/provider errors may contain paths or response bodies.
                logger.LogWarning("Provider credential cleanup will retry after {FailureType}.", ex.GetType().Name);
            }
        }
    }
}
