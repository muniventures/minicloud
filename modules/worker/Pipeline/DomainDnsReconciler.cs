using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Pipeline;

public sealed class DomainDnsReconciler(
    MinicloudDbContext db, ICloudflareDnsClient dns, TimeProvider timeProvider, ILogger<DomainDnsReconciler> logger,
    IOptions<ManagedDomainOptions>? managedDomains = null)
{
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        var candidates = await db.Domains
            .Where(d => d.Kind == DomainBindingService.KindMinicloudSubdomain &&
                d.Status != DomainBindingService.StatusDisabled && d.App.DeletionStatus == null &&
                (d.TargetIp == null || d.DnsStatus != DomainBindingService.DnsVerified))
            .OrderBy(d => d.Id).Take(100).ToListAsync(cancellationToken);
        foreach (var domain in candidates)
        {
            var options = managedDomains?.Value ?? new();
            if (!options.Contains(domain.Hostname))
            {
                continue;
            }
            if (await db.Deployments.AnyAsync(d => d.AppId == domain.AppId && d.OwnershipToken != null, cancellationToken))
            {
                continue;
            }
            var servers = await db.ServerInstances.AsNoTracking()
                .Where(s => s.AppId == domain.AppId && s.OrganizationId == domain.OrganizationId)
                .ToListAsync(cancellationToken);
            if (servers.Count != 1 || string.IsNullOrWhiteSpace(servers[0].ProviderServerId) || !IPAddress.TryParse(servers[0].IpAddress, out _))
            {
                continue;
            }
            var server = servers[0];
            if (await db.ServerInstances.AnyAsync(s => s.ProviderAccountId == server.ProviderAccountId && s.Provider.ToLower() == server.Provider.ToLower() && s.ProviderServerId == server.ProviderServerId &&
                (s.AppId != domain.AppId || s.OrganizationId != domain.OrganizationId), cancellationToken) ||
                await db.RuntimeServers.AnyAsync(s => s.ProviderAccountId == server.ProviderAccountId && s.Provider.ToLower() == server.Provider.ToLower() && s.ProviderServerId == server.ProviderServerId &&
                (s.AppId != domain.AppId || s.OrganizationId != domain.OrganizationId), cancellationToken))
            {
                continue;
            }
            var services = await new DeploymentServiceSetService().BuildActiveServicesAsync(db, domain.AppId, cancellationToken);
            if (!services.Any(s => s.Name == domain.ServiceName && s.Public))
            {
                continue;
            }
            var hostname = domain.Hostname;
            var serviceName = domain.ServiceName;
            try
            {
                if (options.ManageDnsRecords)
                {
                    await dns.UpsertDnsRecordAsync(domain.Hostname, server.IpAddress!, cancellationToken);
                }
                await dns.WaitForPropagationAsync(domain.Hostname, server.IpAddress!, TimeSpan.FromSeconds(30), cancellationToken);
                await db.Entry(domain).ReloadAsync(cancellationToken);
                if (db.Entry(domain).State == EntityState.Detached || domain.Hostname != hostname || domain.ServiceName != serviceName || domain.Status == DomainBindingService.StatusDisabled)
                {
                    continue;
                }
                if (await db.Deployments.AnyAsync(d => d.AppId == domain.AppId && d.OwnershipToken != null, cancellationToken) ||
                    !await db.ServerInstances.AnyAsync(s => s.Id == server.Id && s.AppId == domain.AppId &&
                        s.OrganizationId == domain.OrganizationId && s.ProviderAccountId == server.ProviderAccountId && s.ProviderServerId == server.ProviderServerId && s.IpAddress == server.IpAddress, cancellationToken))
                {
                    continue;
                }
                domain.TargetIp = server.IpAddress;
                domain.DnsStatus = DomainBindingService.DnsVerified;
                domain.UpdatedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Provider response bodies and credentials do not belong in customer diagnostics.
                logger.LogWarning("DNS reconciliation failed for binding {BindingId}; retrying on the next pass. Failure type: {FailureType}",
                    domain.Id, ex.GetType().Name);
            }
        }
    }
}

public sealed class DomainDnsWorker(IServiceScopeFactory scopes, ILogger<DomainDnsWorker> logger, MaintenanceParticipant? maintenance = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Managed DNS changes originate external writes, so they pause while maintenance holds the fence.
                Func<CancellationToken, Task> pass = async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<DomainDnsReconciler>().ReconcileAsync(ct);
                };
                if (maintenance is null) await pass(stoppingToken);
                else await maintenance.RunActivityAsync("managed_dns_reconcile", requireOpen: true, pass, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Domain DNS pass failed; retrying. Failure type: {FailureType}", ex.GetType().Name);
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
