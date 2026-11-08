using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Dns;
using Minicloud.Worker.Pipeline.Steps;
using static Minicloud.Common.Providers.ProviderServiceCollectionExtensions;

namespace Minicloud.Worker.Deletion;

public sealed class AppDeletionProcessor(
    MinicloudDbContext db, VpsProviderClientResolver providers,
    ICloudflareDnsClient dns, ILogger<AppDeletionProcessor> logger, IOptions<ManagedDomainOptions>? managedDomains = null,
    ProviderAccountClientFactory? accountClients = null)
{
    private readonly ManagedDomainOptions _managedDomains = managedDomains?.Value ?? new();
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var query = db.Apps.Where(x => x.DeletionStatus == "queued" && !db.Apps.Any(child => child.ParentAppId == x.Id && child.DeletionStatus == "queued"));
            if (db.Database.IsNpgsql())
            {
                query = db.Apps.FromSqlRaw("SELECT * FROM apps WHERE deletion_status = 'queued' AND NOT EXISTS (SELECT 1 FROM apps child WHERE child.parent_app_id = apps.id AND child.deletion_status = 'queued') ORDER BY parent_app_id NULLS LAST, id FOR UPDATE SKIP LOCKED LIMIT 1");
            }
            var app = (await query.ToListAsync(ct)).OrderBy(x => x.ParentAppId is null).FirstOrDefault();
            if (app is null)
            {
                return false;
            }
            try
            {
                if (await db.Apps.AnyAsync(x => x.ParentAppId == app.Id, ct))
                {
                    throw new InvalidOperationException("Branch cleanup is incomplete. Retry deletion after resolving branch failures.");
                }
                if (await db.Deployments.AnyAsync(x => x.AppId == app.Id &&
                    x.Status != DeploymentStatus.Queued && x.Status != DeploymentStatus.Canceled &&
                    x.Status != DeploymentStatus.Succeeded && x.Status != DeploymentStatus.Failed, ct))
                {
                    throw new InvalidOperationException("An active deployment must finish before deletion.");
                }
                await DeleteInfrastructureAsync(app, ct);
                if (db.Database.IsNpgsql()) { await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({Minicloud.Common.Registry.ImagePublicationService.LockKey})", ct); }
                var audits = await db.AuditEvents.Where(x => x.AppId == app.Id).ToListAsync(ct);
                foreach (var audit in audits)
                {
                    audit.AppId = null;
                    audit.App = null;
                }
                db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    OrganizationId = app.OrganizationId,
                    Actor = "worker",
                    Action = "app.deleted",
                    Details = app.Id,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                db.RuntimeLogRecords.RemoveRange(await db.RuntimeLogRecords.Where(x => x.AppId == app.Id).ToListAsync(ct));
                db.RuntimeStatusSnapshots.RemoveRange(await db.RuntimeStatusSnapshots.Where(x => x.AppId == app.Id).ToListAsync(ct));
                var publications = await db.ImagePublications.Where(x => x.AppId == app.Id).ToListAsync(ct);
                foreach (var publication in publications.Where(x => x.ManifestDigest != null))
                {
                    db.RegistryGarbageCandidates.Add(new RegistryGarbageCandidate
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Repository = publication.Repository,
                        ManifestDigest = publication.ManifestDigest!,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
                db.RegistrySessions.RemoveRange(await db.RegistrySessions.Where(x => x.AppId == app.Id).ToListAsync(ct));
                db.DeploymentServices.RemoveRange(await db.DeploymentServices.Where(x => x.Deployment.AppId == app.Id).ToListAsync(ct));
                db.ImagePublications.RemoveRange(publications);
                db.Apps.Remove(app);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Deletion failed for application {AppId}", app.Id);
                // Roll back local deletes while retaining all resource identities for retry.
                await transaction.RollbackAsync(ct);
                await transaction.DisposeAsync();
                db.ChangeTracker.Clear();
                var failed = await db.Apps.SingleAsync(x => x.Id == app.Id, ct);
                failed.DeletionStatus = "failed";
                failed.DeletionError = "Application cleanup failed. Retry deletion; contact support if it continues.";
                await db.SaveChangesAsync(ct);
                return true;
            }
            await transaction.CommitAsync(ct);
            return true;
        });
    }

    public async Task DeleteInfrastructureAsync(MinicloudApp app, CancellationToken ct)
    {
        var servers = await db.ServerInstances.Where(x => x.AppId == app.Id).ToListAsync(ct);
        var runtimes = await db.RuntimeServers.Where(x => x.AppId == app.Id).ToListAsync(ct);
        var registrations = servers.Select(x => (x.OrganizationId, x.ProviderAccountId, x.Provider, x.ProviderServerId))
            .Concat(runtimes.Select(x => (x.OrganizationId, x.ProviderAccountId, x.Provider, x.ProviderServerId))).ToList();
        var targets = new HashSet<(string? AccountId, CloudProvider Provider, string Id)>();
        foreach (var registration in registrations)
        {
            if (registration.OrganizationId != app.OrganizationId || string.IsNullOrWhiteSpace(registration.ProviderServerId) ||
                !Enum.TryParse<CloudProvider>(registration.Provider, true, out var provider) || !Enum.IsDefined(provider))
            {
                throw new InvalidOperationException("Invalid server ownership or identity.");
            }
            targets.Add((registration.ProviderAccountId, provider, registration.ProviderServerId));
        }
        if (targets.Count > 1)
        {
            throw new InvalidOperationException("Conflicting server registrations require reconciliation.");
        }
        if (targets.Count == 0)
        {
            await VerifyNoUnregisteredServerAsync(app, ct);
        }
        foreach (var target in targets)
        {
            if (await db.ServerInstances.AnyAsync(x => x.AppId != app.Id && x.ProviderAccountId == target.AccountId && x.Provider.ToLower() == target.Provider.ToString().ToLower() && x.ProviderServerId == target.Id, ct) ||
                await db.RuntimeServers.AnyAsync(x => x.AppId != app.Id && x.ProviderAccountId == target.AccountId && x.Provider.ToLower() == target.Provider.ToString().ToLower() && x.ProviderServerId == target.Id, ct))
            {
                throw new InvalidOperationException("Server is registered to another application.");
            }
        }
        var domains = await db.Domains.Where(x => x.AppId == app.Id).ToListAsync(ct);
        foreach (var domain in domains)
        {
            if (domain.OrganizationId != app.OrganizationId || (domain.Kind != DomainBindingService.KindCustomDomain && !_managedDomains.Contains(domain.Hostname)) ||
                await db.Domains.AnyAsync(x => x.AppId != app.Id && x.Hostname == domain.Hostname, ct))
            {
                throw new InvalidOperationException("Domain ownership does not match.");
            }
        }
        foreach (var target in targets)
        {
            if (app.PlanId is not null && target.AccountId is null)
            { throw new InvalidOperationException("Registered server has no provider account; reconcile ownership before deletion."); }
            var client = target.AccountId is null ? providers(target.Provider) :
                await (accountClients ?? throw new InvalidOperationException("Account client resolution is unavailable."))
                    .CreateAsync(app.OrganizationId, target.AccountId, ct);
            if (client.Provider != target.Provider) { throw new InvalidOperationException("Registered provider type does not match its account."); }
            try { await client.DeleteServerAsync(target.Id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new InvalidOperationException("Provider server deletion failed. Check the registered account and retry cleanup."); }
        }
        foreach (var domain in domains)
        {
            if (domain.Kind == DomainBindingService.KindMinicloudSubdomain && _managedDomains.ManageDnsRecords)
            {
                await dns.DeleteDnsRecordsAsync(domain.Hostname, ct);
            }
        }
    }

    private async Task VerifyNoUnregisteredServerAsync(MinicloudApp app, CancellationToken ct)
    {
        var deployments = await db.Deployments.Where(x => x.AppId == app.Id).ToListAsync(ct);
        if (app.PlanId is null && deployments.All(x => x.ProviderAccountId is null && !x.ProviderCreationAttempted))
        {
            if (deployments.Any(x => x.Status != DeploymentStatus.Canceled && x.Status != DeploymentStatus.Queued))
            { throw new InvalidOperationException("Deployed application has no verifiable server registration. Reconcile infrastructure before deletion."); }
            return;
        }
        if (deployments.Any(x => x.Status == DeploymentStatus.Succeeded))
        {
            throw new InvalidOperationException("Deployed application has no verifiable server registration. Reconcile infrastructure before deletion.");
        }
        if (deployments.Any(x => x.ProviderCreationAttempted))
        {
            // Empty discovery is not proof that an ambiguous allocation failed.
            // Retain the reservation until the original scope is reconciled.
            throw new InvalidOperationException("A server allocation is unresolved. Reconcile its original provider account before deleting the application.");
        }
        var attempts = deployments.Where(x => x.Status != DeploymentStatus.Queued &&
            (x.Status != DeploymentStatus.Canceled || x.StartedAt != null || x.ProviderCreationAttempted)).ToList();
        if (attempts.Count == 0) { return; }

        var organizationSlug = await db.Organizations.Where(x => x.Id == app.OrganizationId).Select(x => x.Slug).SingleAsync(ct);
        var serverName = ResolveProvisionServerStep.BuildServerName(organizationSlug, app.Slug);
        var placements = attempts.Select(x => (x.ProviderAccountId, x.ProviderType)).Distinct();
        foreach (var placement in placements)
        {
            IVpsProviderClient client;
            if (placement.ProviderAccountId is not null)
            {
                if (!Enum.TryParse<CloudProvider>(placement.ProviderType, true, out var provider) || !Enum.IsDefined(provider))
                { throw new InvalidOperationException("Deployment provider identity requires reconciliation before deletion."); }
                client = await (accountClients ?? throw new InvalidOperationException("Account client resolution is unavailable."))
                    .CreateAsync(app.OrganizationId, placement.ProviderAccountId, ct);
                if (client.Provider != provider)
                { throw new InvalidOperationException("Deployment provider type does not match its account."); }
            }
            else
            { throw new InvalidOperationException("Deployment has no provider account; reconcile ownership before deletion."); }
            if (await client.FindServerAsync(serverName, ct) is not null)
            {
                throw new InvalidOperationException("Unregistered server exists for this application. Reconcile infrastructure before deletion.");
            }
        }
    }
}
