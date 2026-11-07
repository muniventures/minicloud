using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class DeploymentServiceSetService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<DeploymentServiceRequest>> BuildMergedServicesAsync(
        MinicloudDbContext db,
        string appId,
        IReadOnlyList<DeploymentServiceRequest> requestedServices,
        CancellationToken cancellationToken)
    {
        var active = await BuildActiveServicesByNameAsync(db, appId, cancellationToken);
        foreach (var service in requestedServices)
        {
            active[service.Name] = service;
        }

        return active.Values
            .OrderByDescending(x => x.Public)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public Task<IReadOnlyList<DeploymentServiceRequest>> BuildMergedServicesAsync(
        MinicloudDbContext db,
        string appId,
        IReadOnlyList<DeploymentService> requestedServices,
        CancellationToken cancellationToken) =>
        BuildMergedServicesAsync(db, appId, requestedServices.Select(ToRequest).ToArray(), cancellationToken);

    public async Task<IReadOnlyList<DeploymentServiceRequest>> BuildActiveServicesAsync(
        MinicloudDbContext db,
        string appId,
        CancellationToken cancellationToken)
    {
        var active = await BuildActiveServicesByNameAsync(db, appId, cancellationToken);
        return active.Values
            .OrderByDescending(x => x.Public)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<Dictionary<string, DeploymentServiceRequest>> BuildActiveServicesByNameAsync(
        MinicloudDbContext db,
        string appId,
        CancellationToken cancellationToken)
    {
        var appSlug = await db.Apps.AsNoTracking()
            .Where(x => x.Id == appId)
            .Select(x => x.Slug)
            .SingleAsync(cancellationToken);
        var successfulDeployments = await db.Deployments
            .AsNoTracking()
            .Include(x => x.Services)
            .ThenInclude(x => x.Artifact)
            .Where(x => x.AppId == appId && x.Status == DeploymentStatus.Succeeded && x.AssetReleaseId == null)
            .ToDateTimeOffsetOrderedListAsync(db, x => x.CreatedAt, x => x.CreatedAt, descending: false, take: null, cancellationToken);

        var active = new Dictionary<string, DeploymentServiceRequest>(StringComparer.Ordinal);
        foreach (var deployment in successfulDeployments)
        {
            foreach (var service in deployment.Services.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                active[service.Name] = ToRequest(service, appSlug, deployment.Id);
            }
        }

        return active;
    }

    private static DeploymentServiceRequest ToRequest(
        DeploymentService service,
        string appSlug,
        string deploymentId)
    {
        var request = ToRequest(service);
        return string.IsNullOrWhiteSpace(service.ArtifactId)
            ? request
            : request with
            {
                Image = service.Image?.StartsWith($"minicloud/{appSlug}-{service.Name}:", StringComparison.Ordinal) == true
                    ? service.Image : $"minicloud/{appSlug}-{service.Name}:{deploymentId}".ToLowerInvariant()
            };
    }

    private static DeploymentServiceRequest ToRequest(DeploymentService service) =>
        new(
            service.Name,
            service.Image,
            service.Port,
            service.Public,
            service.Path,
            service.HealthPath,
            DeserializeEnv(service.EnvJson),
            DeserializeEnv(service.SecretEnvJson),
            service.ArtifactId,
            service.Sha256 ?? service.Artifact?.Sha256,
            service.AssetMountsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, AssetMountRequest>>(service.AssetMountsJson, JsonOptions),
            service.ImagePublicationId);

    private static IReadOnlyDictionary<string, string>? DeserializeEnv(string? envJson)
    {
        if (string.IsNullOrWhiteSpace(envJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(envJson, JsonOptions);
    }
}
