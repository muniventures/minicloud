using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

// Explicit operator assignment of legacy resources. The selected account must
// already have an OpenBao credential; import only performs provider reads.
public sealed class ProviderLegacyImportService(
    MinicloudDbContext db, ProviderCatalogService catalog, ProviderAccountClientFactory clients,
    IOptions<ProviderPlanOptions> options, TimeProvider clock)
{
    public async Task<ProviderLegacyImportResult> ImportAsync(ProviderLegacyImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OrganizationId) || string.IsNullOrWhiteSpace(request.ProviderAccountId) ||
            request.AppBindings is null || request.RetainedVolumeIds is null ||
            request.AppBindings.Any(x => x is null || string.IsNullOrWhiteSpace(x.AppId)) ||
            request.AppBindings.Select(x => x.AppId).Distinct(StringComparer.Ordinal).Count() != request.AppBindings.Count ||
            request.RetainedVolumeIds.Any(string.IsNullOrWhiteSpace) ||
            request.RetainedVolumeIds.Distinct(StringComparer.Ordinal).Count() != request.RetainedVolumeIds.Count)
            throw Error("legacy_import_invalid", "Provide an organization, account, unique app bindings and unique retained volume IDs.");

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            // Retries reload the exact manifest rather than retaining half-applied state.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Match admission's app -> account -> plan lock order. Sorting
                // prevents two multi-app imports from locking apps in reverse.
                var importApps = new Dictionary<string, MinicloudApp>(StringComparer.Ordinal);
                foreach (var binding in request.AppBindings.OrderBy(x => x.AppId, StringComparer.Ordinal))
                {
                    IQueryable<MinicloudApp> appQuery = db.Apps;
                    if (db.Database.IsNpgsql()) appQuery = db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {binding.AppId} FOR UPDATE");
                    var app = await appQuery.Include(x => x.Organization).SingleOrDefaultAsync(
                        x => x.Id == binding.AppId && x.OrganizationId == request.OrganizationId, ct)
                        ?? throw Error("legacy_import_ownership_conflict", "An import app was not found in the selected organization.");
                    importApps.Add(app.Id, app);
                }
                IQueryable<ProviderAccount> accountQuery = db.ProviderAccounts;
                if (db.Database.IsNpgsql()) accountQuery = db.ProviderAccounts.FromSqlInterpolated(
                    $"SELECT * FROM provider_accounts WHERE id = {request.ProviderAccountId} AND organization_id = {request.OrganizationId} FOR UPDATE");
                var account = await accountQuery.SingleOrDefaultAsync(x => x.Id == request.ProviderAccountId && x.OrganizationId == request.OrganizationId, ct)
                    ?? throw Error("provider_not_found", "The import account does not belong to this organization.");
                if (db.Database.IsNpgsql())
                {
                    var selectedIds = importApps.Values.Where(x => x.PlanId is not null).Select(x => x.PlanId!).ToArray();
                    var names = request.AppBindings.Select(x => ProviderCatalogService.NormalizeName(
                        x.PlanName ?? ControlPlaneValidation.ToWireValue(importApps[x.AppId].Plan))).ToArray();
                    var planIds = await db.ProviderPlans.Where(x => x.OrganizationId == request.OrganizationId &&
                        (selectedIds.Contains(x.Id) || names.Contains(x.NormalizedName))).OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(ct);
                    foreach (var id in planIds) await catalog.LockedPlanAsync(request.OrganizationId, id, ct);
                }
                var client = await clients.CreateAsync(request.OrganizationId, account.Id, ct);
                if (client.Provider != account.ProviderType) throw Error("provider_binding_inconsistent", "The provider client does not match the import account.");

                var createdPlans = 0;
                var boundApps = 0;
                var boundServers = 0;
                var boundRuntimes = 0;
                var boundVolumes = 0;
                var volumeIds = request.RetainedVolumeIds.ToHashSet(StringComparer.Ordinal);
                foreach (var binding in request.AppBindings)
                {
                    var app = importApps[binding.AppId];
                    if (app.DeletionStatus is not null || await db.Deployments.AnyAsync(x => x.AppId == app.Id &&
                        (x.OwnershipToken != null || x.Status == DeploymentStatus.WaitingForBuild || x.Status == DeploymentStatus.Queued ||
                         x.Status == DeploymentStatus.Dispatching || x.Status == DeploymentStatus.Provisioning ||
                         x.Status == DeploymentStatus.Deploying || x.Status == DeploymentStatus.Verifying), ct))
                        throw Error("legacy_import_active_work", "Pause admissions and finish active deployment/deletion work before import.");

                    var servers = await db.ServerInstances.Where(x => x.AppId == app.Id).ToListAsync(ct);
                    var runtimes = await db.RuntimeServers.Where(x => x.AppId == app.Id).ToListAsync(ct);
                    if (servers.Count > 1) throw Error("legacy_import_ownership_conflict", "Multiple registered servers require explicit reconciliation before import.");
                    if (servers.Count == 0 && runtimes.Count > 0)
                        throw Error("legacy_import_resource_unverified", "A runtime without registered server placement requires region/account reconciliation before import.");
                    var resources = servers.Select(x => (x.OrganizationId, x.ProviderAccountId, x.Provider, x.ProviderServerId))
                        .Concat(runtimes.Select(x => (x.OrganizationId, x.ProviderAccountId, x.Provider, x.ProviderServerId))).ToList();
                    foreach (var resource in resources)
                    {
                        if (resource.OrganizationId != request.OrganizationId ||
                            (resource.ProviderAccountId is not null && resource.ProviderAccountId != account.Id) ||
                            !Enum.TryParse<CloudProvider>(resource.Provider, true, out var type) || type != account.ProviderType ||
                            string.IsNullOrWhiteSpace(resource.ProviderServerId))
                            throw Error("legacy_import_ownership_conflict", "A server/runtime has conflicting organization, account or provider identity.");
                    }
                    if (resources.Select(x => x.ProviderServerId).Distinct(StringComparer.Ordinal).Count() > 1)
                        throw Error("legacy_import_ownership_conflict", "Server and runtime registrations identify different resources.");
                    if (resources.Count > 0)
                    {
                        var resourceId = resources[0].ProviderServerId!;
                        var name = $"minicloud-{app.Organization.Slug}-production-{app.Slug}".ToLowerInvariant();
                        var found = await client.FindServerAsync(name, ct);
                        if (found is null || found.Id != resourceId || found.Name != name ||
                            (account.ProviderType == CloudProvider.Hetzner && !HasLabels(found.Labels,
                                ("managed-by", "minicloud"), ("customer", app.Organization.Slug), ("app", app.Slug), ("env", "production"))))
                            throw Error("legacy_import_resource_unverified", "The original server could not be verified in the selected account. No ownership was changed.");
                        if (await db.ServerInstances.AnyAsync(x => x.AppId != app.Id && x.ProviderAccountId == account.Id && x.ProviderServerId == resourceId, ct) ||
                            await db.RuntimeServers.AnyAsync(x => x.AppId != app.Id && x.ProviderAccountId == account.Id && x.ProviderServerId == resourceId, ct))
                            throw Error("legacy_import_ownership_conflict", "This resource is already assigned to another app in the selected account.");
                    }
                    else if (await db.Deployments.AnyAsync(x => x.AppId == app.Id && x.Status != DeploymentStatus.Canceled, ct))
                        throw Error("legacy_import_resource_unverified", "Deployment history without a registered server requires resource reconciliation before import.");

                    ProviderPlan plan;
                    if (app.PlanId is not null)
                    {
                        plan = await db.ProviderPlans.Include(x => x.ProviderAccount).SingleAsync(x => x.Id == app.PlanId && x.OrganizationId == request.OrganizationId, ct);
                        if (plan.ProviderAccountId != account.Id || (binding.PlanName is not null && plan.NormalizedName != ProviderCatalogService.NormalizeName(binding.PlanName)))
                            throw Error("legacy_import_ownership_conflict", "An imported app already has a different provider account or named plan.");
                    }
                    else
                    {
                        var mapping = ProviderPlanMapping.FromAppPlan(app.Plan, options.Value);
                        if (mapping.Provider != account.ProviderType || servers.Any(x => x.Region != mapping.Region))
                            throw Error("legacy_import_mapping_conflict", "The effective legacy plan does not match the registered provider/region. Reconcile its mapping before import.");
                        var name = binding.PlanName ?? ControlPlaneValidation.ToWireValue(app.Plan);
                        var key = ProviderCatalogService.NormalizeName(name);
                        plan = (await db.ProviderPlans.Include(x => x.ProviderAccount).SingleOrDefaultAsync(x =>
                            x.OrganizationId == request.OrganizationId && x.NormalizedName == key, ct))!;
                        var platform = binding.BuildPlatform ?? servers.SingleOrDefault()?.BuildPlatform;
                        if (plan is null)
                        {
                            if (platform is not ("linux/amd64" or "linux/arm64"))
                                throw Error("legacy_import_platform_unknown", "Provide buildPlatform in the manifest when the existing server has no verified Linux platform.");
                            plan = await catalog.CreateAsync(new CreateProviderPlanRequest(request.OrganizationId, account.Id,
                                mapping.Region, mapping.Sku, name, BuildPlatform: platform), ct);
                            await db.SaveChangesAsync(ct);
                            createdPlans++;
                        }
                        if (plan.ProviderAccountId != account.Id || plan.Region != mapping.Region || plan.Sku != mapping.Sku ||
                            (platform is not null && plan.BuildPlatform != platform))
                            throw Error("legacy_import_mapping_conflict", "The requested plan name already identifies a different account or placement.");
                        app.PlanId = plan.Id;
                        app.SelectedPlan = plan;
                        app.UpdatedAt = clock.GetUtcNow();
                        boundApps++;
                    }
                    if (resources.Count > 0 && plan.ProvisioningLockedAt is null)
                    { plan.ProvisioningLockedAt = clock.GetUtcNow(); plan.Revision++; }
                    foreach (var server in servers)
                    {
                        if (server.PlanId is not null && server.PlanId != plan.Id)
                            throw Error("legacy_import_ownership_conflict", "The server already references a different plan.");
                        if (server.ProviderAccountId is null || server.PlanId is null) boundServers++;
                        server.ProviderAccountId = account.Id;
                        server.PlanId = plan.Id;
                        // Historical SKU/image are unknown unless already recorded. Never infer
                        // an existing server's actual configuration from today's plan options.
                    }
                    foreach (var runtime in runtimes)
                    {
                        if (runtime.ProviderAccountId is null) boundRuntimes++;
                        runtime.ProviderAccountId = account.Id;
                    }
                    foreach (var id in await db.AppStorageVolumes.Where(x => x.AppId == app.Id).Select(x => x.Id).ToListAsync(ct)) volumeIds.Add(id);
                }

                // Make account bindings visible to the attachment checks below;
                // the enclosing transaction still rolls everything back on failure.
                await db.SaveChangesAsync(ct);
                foreach (var id in volumeIds)
                {
                    var volume = await db.AppStorageVolumes.SingleOrDefaultAsync(x => x.Id == id, ct)
                        ?? throw Error("legacy_import_volume_not_found", "A retained volume in the manifest was not found.");
                    if ((volume.OrganizationId is not null && volume.OrganizationId != request.OrganizationId) ||
                        (volume.ProviderAccountId is not null && volume.ProviderAccountId != account.Id) ||
                        account.ProviderType != CloudProvider.Hetzner || client is not IStorageVolumeProvider storage)
                        throw Error("legacy_import_ownership_conflict", "A retained volume has conflicting account, organization or provider ownership.");
                    var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == volume.AppId, ct);
                    if (app is not null && (app.OrganizationId != request.OrganizationId ||
                        !request.AppBindings.Any(x => x.AppId == app.Id)))
                        throw Error("legacy_import_ownership_conflict", "Include the retained volume's existing app in this organization import.");
                    var remote = volume.ProviderVolumeId is null
                        ? await storage.FindVolumeAsync(volume.Id, ct) : await storage.GetVolumeAsync(volume.ProviderVolumeId, ct);
                    if (remote is null || remote.Name != volume.Id || remote.Region != volume.Region ||
                        (volume.ProviderVolumeId is not null && remote.Id != volume.ProviderVolumeId) ||
                        !HasLabels(remote.Labels, ("managed-by", "minicloud"), ("app-id", volume.AppId), ("storage-name", volume.Name), ("purpose", "customer-storage")))
                        throw Error("legacy_import_resource_unverified", "A retained volume could not be verified in the selected account. No ownership was changed.");
                    if (remote.ServerId is not null && !await db.ServerInstances.AnyAsync(x => x.AppId == volume.AppId &&
                        x.OrganizationId == request.OrganizationId && x.ProviderAccountId == account.Id && x.ProviderServerId == remote.ServerId, ct))
                        throw Error("legacy_import_ownership_conflict", "A retained volume is attached to an unverified server; reconcile it before import.");
                    if (volume.OrganizationId is null || volume.ProviderAccountId is null) boundVolumes++;
                    volume.OrganizationId = request.OrganizationId;
                    volume.ProviderAccountId = account.Id;
                    volume.Provider = account.ProviderType.ToString().ToLowerInvariant();
                    volume.ProviderVolumeId = remote.Id;
                }
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return new ProviderLegacyImportResult(createdPlans, boundApps, boundServers, boundRuntimes, boundVolumes);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                db.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private static bool HasLabels(IReadOnlyDictionary<string, string>? labels, params (string Key, string Value)[] expected) =>
        labels is not null && expected.All(x => labels.TryGetValue(x.Key, out var value) && value == x.Value);

    private static ProviderCatalogException Error(string code, string message) => new(code, message);
}
