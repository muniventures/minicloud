using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Pipeline;

public sealed class CustomDomainDnsWorker(IServiceScopeFactory scopes, ILogger<CustomDomainDnsWorker> logger, MaintenanceParticipant? maintenance = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (maintenance is null) await RunPassAsync(stoppingToken);
                else await maintenance.RunActivityAsync("custom_domain_verification", requireOpen: true, RunPassAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Customer DNS pass failed; retrying. Failure type: {FailureType}", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        string[] registrationIds;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
            registrationIds = await db.CustomDomainRegistrations
                .Where(x => x.AppId != null && (x.LastCheckedAt == null || x.LastCheckedAt < cutoff))
                .OrderBy(x => x.LastCheckedAt != null).ThenBy(x => x.LastCheckedAt).ThenBy(x => x.Id)
                .Take(20).Select(x => x.Id).ToArrayAsync(stoppingToken);
        }
        await Parallel.ForEachAsync(registrationIds, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (id, ct) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var row = await db.CustomDomainRegistrations.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is not null)
            {
                await scope.ServiceProvider.GetRequiredService<CustomDomainRegistrationService>().VerifyAsync(row, ct);
            }
        });
        string[] ids;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
            ids = await db.Domains.Where(x => x.Kind == DomainBindingService.KindCustomDomain &&
                x.Status != DomainBindingService.StatusDisabled && x.App.DeletionStatus == null &&
                !db.Deployments.Any(d => d.AppId == x.AppId && d.OwnershipToken != null) &&
                (x.LastCheckedAt == null || x.LastCheckedAt < cutoff))
                .OrderBy(x => x.LastCheckedAt != null).ThenBy(x => x.LastCheckedAt).ThenBy(x => x.Id).Take(20).Select(x => x.Id).ToArrayAsync(stoppingToken);
        }
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, async (id, ct) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var binding = await db.Domains.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (binding is not null)
            {
                await scope.ServiceProvider.GetRequiredService<CustomDomainVerificationService>().VerifyAsync(binding, ct);
            }
        });
    }
}
