using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;
using static Minicloud.Common.Providers.ProviderServiceCollectionExtensions;

namespace Minicloud.Worker.Pipeline.Steps;

public sealed partial class SshDeployStep
{
    private async Task<Dictionary<string, RuntimeStorageMount[]>> ResolveStorageAsync(PipelineStepContext context, Deployment deployment, CancellationToken ct)
    {
        var result = new Dictionary<string, RuntimeStorageMount[]>(StringComparer.Ordinal);
        if (!deployment.Services.Any(x => StorageMounts.Read(x.StorageJson).Count > 0)) { return result; }
        if (context.Provider != CloudProvider.Hetzner) { throw new PipelineStepException("storage_provider_unsupported", "Durable storage is currently supported only on Hetzner."); }
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var server = await db.ServerInstances.SingleAsync(x => x.Id == context.ServerInstanceId && x.AppId == context.AppId, ct);
        if (server.OrganizationId != context.OrganizationId || server.ProviderAccountId != context.ProviderAccountId ||
            !string.Equals(server.Provider, "Hetzner", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(server.ProviderServerId))
        { throw new PipelineStepException("storage_server_conflict", "Storage server identity does not match the deployment."); }
        var client = server.ProviderAccountId is null
            ? scope.ServiceProvider.GetRequiredService<VpsProviderClientResolver>()(context.Provider)
            : await scope.ServiceProvider.GetRequiredService<ProviderAccountClientFactory>().CreateAsync(context.OrganizationId, server.ProviderAccountId, ct);
        var provider = client as IStorageVolumeProvider
            ?? throw new PipelineStepException("storage_provider_unsupported", "Provider does not support customer storage.");
        var allowAllocation = server.ProviderAccountId is null || await db.ProviderAccounts.AnyAsync(x => x.Id == server.ProviderAccountId &&
            x.OrganizationId == context.OrganizationId && x.Enabled, ct);
        var service = new AppStorageService(db, provider);
        try
        {
            var validation = StorageValidation.Validate(deployment.Services.Select(x => (x.Name, (IReadOnlyDictionary<string, Minicloud.Common.Contracts.StorageMountRequest>?)StorageMounts.Read(x.StorageJson),
                (IEnumerable<string>)AssetReleaseService.Mounts(x).Values.Select(m => m.MountPath))), deployment.Database == DatabaseMode.Sqlite);
            if (!validation.IsValid) { throw new InvalidOperationException("Invalid storage configuration: " + string.Join(" ", validation.Errors.Select(x => x.Message))); }
            foreach (var configured in deployment.Services.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var mounts = new List<RuntimeStorageMount>();
                foreach (var (name, mount) in StorageMounts.Read(configured.StorageJson).OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    await EnsureOwnershipAsync(context, ct);
                    mounts.Add(await service.EnsureAsync(context.AppId, name, mount, server.ProviderServerId!, server.Region, ct,
                        server.ProviderAccountId is null ? null : context.OrganizationId, server.ProviderAccountId, allowAllocation));
                }
                result[configured.Name] = mounts.ToArray();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not PipelineStepException)
        { throw new PipelineStepException("storage_provision_failed", "Durable storage provisioning failed. Check the selected provider account and retained volume identity."); }
        return result;
    }
}
