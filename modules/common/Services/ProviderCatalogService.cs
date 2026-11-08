using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Common.Services;

public sealed class ProviderCatalogException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class ProviderCatalogService(MinicloudDbContext db, IIdGenerator ids, TimeProvider clock,
    ISecretStore? secrets = null, IOptions<ProviderCatalogOptions>? policy = null)
{
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 128 || name.Any(char.IsControl))
            throw new ProviderCatalogException("plan_name_invalid", "Use a nonempty plan name of up to 128 characters without control characters.");
        try { return name.Trim().Normalize(NormalizationForm.FormC).ToUpperInvariant(); }
        catch (ArgumentException) { throw new ProviderCatalogException("plan_name_invalid", "The plan name contains invalid Unicode."); }
    }

    public async Task<ProviderPlan> ResolveAsync(string organizationId, string name, CancellationToken ct, bool requireEnabled = true)
    {
        var key = NormalizeName(name);
        var claim = await db.ProviderPlanNames.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.NormalizedName == key, ct);
        if (claim is { Retired: true })
            throw new ProviderCatalogException("plan_name_retired", "This plan name was renamed or retired. Select its current name from the plan catalog.");
        var plan = await db.ProviderPlans.Include(x => x.ProviderAccount)
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.NormalizedName == key, ct)
            ?? throw new ProviderCatalogException("plan_not_found", "No plan with this name exists in the selected organization.");
        if (requireEnabled) EnsureEnabled(plan);
        return plan;
    }

    public async Task BindAsync(MinicloudApp app, string name, CancellationToken ct)
    {
        var plan = await ResolveAsync(app.OrganizationId, name, ct, requireEnabled: false);
        if (app.PlanId == plan.Id)
        {
            if (!await db.ServerInstances.AnyAsync(x => x.AppId == app.Id, ct)) EnsureEnabled(plan);
            await CheckCredentialsAsync(plan.ProviderAccount, ct);
            app.SelectedPlan = plan;
            return;
        }
        EnsureEnabled(plan);
        await CheckCredentialsAsync(plan.ProviderAccount, ct);
        if (await db.ServerInstances.AnyAsync(x => x.AppId == app.Id, ct)
            || await db.RuntimeServers.AnyAsync(x => x.AppId == app.Id, ct)
            || await db.AppStorageVolumes.AnyAsync(x => x.AppId == app.Id, ct)
            || await db.Deployments.AnyAsync(x => x.AppId == app.Id && (x.ProviderCreationAttempted || x.OwnershipToken != null
                || x.Status == DeploymentStatus.WaitingForBuild || x.Status == DeploymentStatus.Queued
                || x.Status == DeploymentStatus.Dispatching || x.Status == DeploymentStatus.Provisioning
                || x.Status == DeploymentStatus.Deploying || x.Status == DeploymentStatus.Verifying), ct))
            throw new ProviderCatalogException("plan_change_requires_migration", "This environment has resources or a provisioning reservation. Changing its plan requires an explicit migration.");
        app.PlanId = plan.Id;
        app.SelectedPlan = plan;
        app.UpdatedAt = clock.GetUtcNow();
    }

    public async Task ReserveAsync(MinicloudApp app, Deployment deployment, CancellationToken ct)
    {
        // Legacy rows are handled by the explicit import path; they cannot resolve custom names.
        if (app.PlanId is null)
        {
            if (policy is not null && !policy.Value.AllowLegacyConfiguration)
                throw new ProviderCatalogException("legacy_provider_import_required", "Import this environment into an organization provider account and plan before deploying.");
            return;
        }
        var accountId = await db.ProviderPlans.Where(x => x.Id == app.PlanId && x.OrganizationId == app.OrganizationId)
            .Select(x => x.ProviderAccountId).SingleAsync(ct);
        await LockAccountAsync(db, app.OrganizationId, accountId, ct);
        var plan = await LockedPlanAsync(app.OrganizationId, app.PlanId, ct);
        await CheckCredentialsAsync(plan.ProviderAccount, ct);
        var server = await db.ServerInstances.SingleOrDefaultAsync(x => x.AppId == app.Id, ct);
        if (server is null) EnsureEnabled(plan);
        else if (server.OrganizationId != app.OrganizationId || server.ProviderAccountId != plan.ProviderAccountId
            || (server.PlanId is not null && server.PlanId != plan.Id))
            throw new ProviderCatalogException("provider_binding_inconsistent", "The registered server does not match this environment's provider account.");
        if (deployment.ProviderAccountId is not null)
        {
            if (deployment.PlanId != plan.Id || deployment.ProviderAccountId != plan.ProviderAccountId)
                throw new ProviderCatalogException("provider_binding_inconsistent", "The deployment already has a different placement reservation.");
            return;
        }
        if (plan.ProvisioningLockedAt is null)
        {
            plan.ProvisioningLockedAt = clock.GetUtcNow();
            plan.Revision++;
            plan.UpdatedAt = clock.GetUtcNow();
        }
        deployment.PlanId = plan.Id;
        deployment.PlanRevision = plan.Revision;
        deployment.ProviderAccountId = plan.ProviderAccountId;
        deployment.ProviderType = plan.ProviderAccount.ProviderType.ToString();
        deployment.ProviderRegion = plan.Region;
        deployment.ProviderSku = plan.Sku;
        deployment.ProviderImage = plan.Image;
        deployment.ProviderBuildPlatform = plan.BuildPlatform;
    }

    public async Task<ProviderPlan> CreateAsync(CreateProviderPlanRequest request, CancellationToken ct)
    {
        var account = await LockAccountAsync(db, request.OrganizationId, request.ProviderAccountId, ct);
        if (!account.Enabled) throw new ProviderCatalogException("provider_disabled", "Enable the provider account before adding a plan.");
        var name = request.Name is null ? Guid.NewGuid().ToString() : request.Name.Trim();
        var key = NormalizeName(name);
        await CheckNameAsync(request.OrganizationId, key, null, ct);
        ValidatePlacement(request.Region, request.Sku, request.BuildPlatform, request.Image, account.ProviderType);
        var now = clock.GetUtcNow();
        var plan = new ProviderPlan
        {
            Id = ids.NewId("plan"), OrganizationId = request.OrganizationId, ProviderAccountId = account.Id,
            ProviderAccount = account, Name = name, NormalizedName = key, Region = request.Region.Trim(), Sku = request.Sku.Trim(),
            Image = string.IsNullOrWhiteSpace(request.Image) ? DefaultImage(account.ProviderType) : request.Image.Trim(),
            BuildPlatform = request.BuildPlatform, Enabled = request.Enabled, CreatedAt = now, UpdatedAt = now
        };
        db.ProviderPlans.Add(plan);
        db.ProviderPlanNames.Add(new ProviderPlanName { OrganizationId = plan.OrganizationId, NormalizedName = key, PlanId = plan.Id });
        return plan;
    }

    public async Task UpdateAsync(ProviderPlan plan, UpdateProviderPlanRequest request, CancellationToken ct)
    {
        if (plan.Revision != request.Revision) throw Stale();
        var region = request.Region?.Trim() ?? plan.Region;
        var sku = request.Sku?.Trim() ?? plan.Sku;
        var image = request.Image?.Trim() ?? plan.Image;
        var platform = request.BuildPlatform ?? plan.BuildPlatform;
        ValidatePlacement(region, sku, platform, image, plan.ProviderAccount.ProviderType);
        if (plan.ProvisioningLockedAt is not null && (region != plan.Region || sku != plan.Sku || image != plan.Image || platform != plan.BuildPlatform))
            throw new ProviderCatalogException("plan_mapping_locked", "This plan has been reserved for provisioning. Duplicate it to change its mapping.");
        if (request.Name is not null)
        {
            var key = NormalizeName(request.Name);
            if (key != plan.NormalizedName)
            {
                await CheckNameAsync(plan.OrganizationId, key, plan.Id, ct);
                var previous = await db.ProviderPlanNames.SingleAsync(x => x.OrganizationId == plan.OrganizationId && x.NormalizedName == plan.NormalizedName, ct);
                previous.Retired = true;
                var next = await db.ProviderPlanNames.SingleOrDefaultAsync(x => x.OrganizationId == plan.OrganizationId && x.NormalizedName == key, ct);
                if (next is null) db.ProviderPlanNames.Add(new ProviderPlanName { OrganizationId = plan.OrganizationId, NormalizedName = key, PlanId = plan.Id });
                else next.Retired = false;
                plan.NormalizedName = key;
            }
            plan.Name = request.Name.Trim();
        }
        plan.Region = region; plan.Sku = sku; plan.Image = image; plan.BuildPlatform = platform;
        plan.Enabled = request.Enabled ?? plan.Enabled;
        plan.UpdatedAt = clock.GetUtcNow(); plan.Revision++;
    }

    public async Task DeleteAsync(ProviderPlan plan, CancellationToken ct)
    {
        if (await db.Apps.AnyAsync(x => x.PlanId == plan.Id, ct)
            || await db.Deployments.AnyAsync(x => x.PlanId == plan.Id, ct)
            || await db.ServerInstances.AnyAsync(x => x.PlanId == plan.Id, ct))
            throw new ProviderCatalogException("plan_in_use", "This plan is referenced by an environment or resource. Disable it instead.");
        foreach (var claim in await db.ProviderPlanNames.Where(x => x.OrganizationId == plan.OrganizationId && x.PlanId == plan.Id).ToListAsync(ct)) claim.Retired = true;
        db.ProviderPlans.Remove(plan);
    }

    public async Task<ProviderPlan> LockedPlanAsync(string organizationId, string id, CancellationToken ct)
    {
        IQueryable<ProviderPlan> query = db.ProviderPlans;
        if (db.Database.IsNpgsql()) query = db.ProviderPlans.FromSqlInterpolated($"SELECT * FROM provider_plans WHERE organization_id = {organizationId} AND id = {id} FOR UPDATE");
        var plan = await query.Include(x => x.ProviderAccount).SingleAsync(x => x.OrganizationId == organizationId && x.Id == id, ct);
        // A selection lookup may already be tracking this row before waiting on its
        // lock. Refresh after acquisition so reservation cannot snapshot stale fields.
        if (db.Database.IsNpgsql()) await db.Entry(plan).ReloadAsync(ct);
        return plan;
    }

    public static async Task<ProviderAccount> LockAccountAsync(MinicloudDbContext db, string organizationId, string id, CancellationToken ct)
    {
        IQueryable<ProviderAccount> query = db.ProviderAccounts;
        if (db.Database.IsNpgsql()) query = db.ProviderAccounts.FromSqlInterpolated(
            $"SELECT * FROM provider_accounts WHERE organization_id = {organizationId} AND id = {id} FOR UPDATE");
        var account = await query.SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct)
            ?? throw new ProviderCatalogException("provider_not_found", "Provider account was not found in this organization.");
        await db.Entry(account).ReloadAsync(ct);
        return account;
    }

    private async Task CheckNameAsync(string organizationId, string key, string? currentId, CancellationToken ct)
    {
        if (await db.ProviderPlanNames.AnyAsync(x => x.OrganizationId == organizationId && x.NormalizedName == key && x.PlanId != currentId, ct))
            throw new ProviderCatalogException("plan_name_taken", "This plan name is already used or reserved in this organization.");
    }

    public static void EnsureEnabled(ProviderPlan plan)
    {
        if (!plan.Enabled) throw new ProviderCatalogException("plan_disabled", "This plan is disabled for new provisioning.");
        if (!plan.ProviderAccount.Enabled) throw new ProviderCatalogException("provider_disabled", "This provider account is disabled for new provisioning.");
    }

    public static ProviderCatalogException Stale() => new("revision_conflict", "This item changed. Reload it before saving.");

    private async Task CheckCredentialsAsync(ProviderAccount account, CancellationToken ct)
    {
        if (secrets is null) return; // Pure catalog callers can supply no store; hosts register one.
        if (account.CredentialStoreProvider != OpenBaoSecretStore.ProviderName || string.IsNullOrWhiteSpace(account.CredentialPath))
            throw new SecretStoreUnavailableException("Provider account credentials are unavailable.");
        var value = await secrets.GetAsync(new StoredSecretReference(account.CredentialStoreProvider, account.CredentialPath, account.CredentialVersion), ct);
        if (string.IsNullOrWhiteSpace(value)) throw new SecretStoreUnavailableException("Provider account credentials are empty.");
    }
    public static string? DefaultImage(CloudProvider type) => type switch
    {
        CloudProvider.Hetzner => "ubuntu-24.04", CloudProvider.DigitalOcean => "ubuntu-24-04-x64", _ => null
    };

    private static void ValidatePlacement(string region, string sku, string platform, string? image, CloudProvider provider)
    {
        if (provider == CloudProvider.Vultr && !string.IsNullOrWhiteSpace(image)
            && (!int.TryParse(image.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var osId) || osId <= 0))
            throw new ProviderCatalogException("plan_mapping_invalid", "Vultr plan OS overrides must be positive numeric OS IDs.");
        if (string.IsNullOrWhiteSpace(region) || region.Length > 128 || region.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(sku) || sku.Length > 128 || sku.Any(char.IsControl)
            || image is { Length: > 128 } || image?.Any(char.IsControl) == true
            || platform is not ("linux/amd64" or "linux/arm64"))
            throw new ProviderCatalogException("plan_mapping_invalid", "Specify a region, provider SKU, supported image and linux/amd64 or linux/arm64 platform.");
    }
}
