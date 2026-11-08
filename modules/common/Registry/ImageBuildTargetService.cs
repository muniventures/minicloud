using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Registry;

public sealed class ImageBuildTargetService(MinicloudDbContext db, IOptions<RegistryOptions> registry, IOptions<ProviderPlanOptions> plans)
{
    public async Task<ImageBuildTarget> ResolveAsync(MinicloudApp app, CancellationToken ct)
    {
        var servers = await db.ServerInstances.AsNoTracking().Where(x => x.AppId == app.Id).ToListAsync(ct);
        var runtimes = await db.RuntimeServers.AsNoTracking().Where(x => x.AppId == app.Id).ToListAsync(ct);
        if (servers.Any(x => x.OrganizationId != app.OrganizationId) || runtimes.Any(x => x.OrganizationId != app.OrganizationId))
        {
            throw new RegistryImageValidationException(ImagePublicationErrors.PlatformUnknown, "Server ownership is inconsistent.");
        }
        string? platform;
        object identity;
        if (servers.Count > 0 || runtimes.Count > 0)
        {
            if (servers.Count != 1 || string.IsNullOrWhiteSpace(servers[0].ProviderServerId) ||
                runtimes.Any(x => x.ProviderAccountId != servers[0].ProviderAccountId || !string.Equals(x.Provider, servers[0].Provider, StringComparison.OrdinalIgnoreCase) || x.ProviderServerId != servers[0].ProviderServerId) ||
                servers[0].BuildPlatformObservedAt is null)
            {
                throw new RegistryImageValidationException(ImagePublicationErrors.PlatformUnknown, "Server platform is not verified; deploy source once to record the target architecture.");
            }
            var server = servers[0];
            platform = server.BuildPlatform;
            identity = new { version = 1, app.Id, app.OrganizationId, app.Plan, app.PlanId, serverId = server.Id, server.ProviderAccountId, server.Provider, server.ProviderServerId, server.Region, platform };
        }
        else
        {
            if (await db.Deployments.AnyAsync(x => x.AppId == app.Id && x.Status == DeploymentStatus.Succeeded, ct))
            {
                throw new RegistryImageValidationException(ImagePublicationErrors.PlatformUnknown, "Existing deployment has no verified server registration.");
            }
            if (app.PlanId is not null)
            {
                var plan = await db.ProviderPlans.AsNoTracking().Include(x => x.ProviderAccount)
                    .SingleOrDefaultAsync(x => x.Id == app.PlanId && x.OrganizationId == app.OrganizationId, ct);
                if (plan is null || plan.ProviderAccount.OrganizationId != app.OrganizationId || !plan.Enabled || !plan.ProviderAccount.Enabled)
                {
                    throw new RegistryImageValidationException(ImagePublicationErrors.PlatformUnknown, "The selected plan or provider account is unavailable.");
                }
                platform = plan.BuildPlatform;
                identity = new { version = 2, app.Id, app.OrganizationId, app.PlanId,
                    plan.ProviderAccountId, plan.ProviderAccount.ProviderType, plan.Region, plan.Sku, plan.Image, platform };
            }
            else
            {
                var mapping = ProviderPlanMapping.FromAppPlan(app.Plan, plans.Value);
                registry.Value.PlannedPlatforms.TryGetValue($"{mapping.Provider}_{mapping.Sku}", out platform);
                identity = new { version = 1, app.Id, app.OrganizationId, app.Plan, mapping.Provider, mapping.Region, mapping.Sku, platform };
            }
        }
        if (!ImagePublicationValidation.IsPlatform(platform))
        {
            throw new RegistryImageValidationException(ImagePublicationErrors.PlatformUnknown, "The target Linux platform is not configured or verified.");
        }
        return new ImageBuildTarget(RegistryImageValidator.Digest(JsonSerializer.SerializeToUtf8Bytes(identity))[7..], platform!);
    }
}
