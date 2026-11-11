using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Dns;
using static Minicloud.Common.Providers.ProviderServiceCollectionExtensions;

namespace Minicloud.Worker.Deletion;

public sealed class AppDeletionProcessor(
    MinicloudDbContext db, VpsProviderClientResolver providers,
    ICloudflareDnsClient dns, ILogger<AppDeletionProcessor> logger, IOptions<ManagedDomainOptions>? managedDomains = null,
    ProviderAccountClientFactory? accountClients = null, MaintenanceParticipant? maintenance = null)
{
    private readonly ManagedDomainOptions _managedDomains = managedDomains?.Value ?? new();
    private readonly MaintenanceParticipant _maintenance = maintenance ?? MaintenanceParticipant.CreateLocal("worker", TimeProvider.System);
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Queued deletions are accepted work and continue while fenced. This transaction spans remote
            // deletes, so the authority row is checked without a share lock to avoid blocking fence/transfer.
            if (!await _maintenance.CanClaimAsync(db, ct, lockRow: false))
            {
                return false;
            }
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
                var databaseBlocker = await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, app.Id, ct, allowManagedServerDeletion: true);
                if (databaseBlocker is not null) { throw new InvalidOperationException(databaseBlocker); }
                var sourceDatabases = await db.ManagedDatabases.Where(x => x.AppId == app.Id).ToListAsync(ct);
                foreach (var source in sourceDatabases)
                {
                    source.Status = "deleted";
                    source.Revision++;
                    foreach (var policy in await db.DatabaseBackupPolicies.Where(x => x.DatabaseId == source.Id).ToListAsync(ct))
                    {
                        policy.Enabled = false;
                        policy.Status = "source-deleted";
                        policy.NextDueAt = null;
                    }
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
                db.ManagedDatabaseBindings.RemoveRange(await db.ManagedDatabaseBindings.Where(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId && x.Status == "revoked").ToListAsync(ct));
                if (app.ApplicationId is not null)
                {
                    var application = await db.Applications.SingleOrDefaultAsync(x => x.Id == app.ApplicationId, ct);
                    if (application is not null)
                    {
                        var remainingEnvironments = await db.Apps
                            .Where(x => x.ApplicationId == application.Id && x.Id != app.Id)
                            .ToListAsync(ct);
                        if (application.DefaultEnvironmentId == app.Id)
                        {
                            application.DefaultEnvironmentId = ApplicationEnvironmentMigrationService.SelectNextDefaultEnvironmentId(remainingEnvironments);
                        }
                        if (application.DeletionStatus is not null)
                        {
                            if (remainingEnvironments.Count == 0)
                            {
                                db.Applications.Remove(application);
                            }
                            else if (remainingEnvironments.Any(x => x.DeletionStatus == "failed") && remainingEnvironments.All(x => x.DeletionStatus != "queued"))
                            {
                                application.DeletionStatus = "failed";
                                application.DeletionError = remainingEnvironments.First(x => x.DeletionStatus == "failed").DeletionError
                                    ?? "Application cleanup failed. Retry deletion; contact support if it continues.";
                            }
                        }
                    }
                }
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
                if (failed.ApplicationId is not null)
                {
                    var failedApplication = await db.Applications.SingleOrDefaultAsync(x => x.Id == failed.ApplicationId, ct);
                    if (failedApplication is not null && failedApplication.DeletionStatus is not null)
                    {
                        failedApplication.DeletionStatus = "failed";
                        failedApplication.DeletionError = failed.DeletionError;
                    }
                }
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
}
