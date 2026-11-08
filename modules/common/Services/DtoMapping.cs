using System.Text.Json;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public static class DtoMapping
{
    public static OrganizationResponse ToResponse(this Organization organization) =>
        new(organization.Id, organization.Name, organization.Slug, organization.CreatedAt);

    public static AppResponse ToResponse(this MinicloudApp app)
    {
        var latestDeployment = app.Deployments
            .Where(x => x.AssetReleaseId == null)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new LatestDeploymentResponse(
                x.Id,
                ControlPlaneValidation.ToWireValue(x.Status),
                x.WebsiteUrl,
                x.CompletedAt))
            .FirstOrDefault();

        var domains = app.Domains
            .Where(x => x.Status != DomainBindingService.StatusDisabled)
            .OrderBy(x => x.Hostname)
            .Select(x => new AppDomainResponse(x.Id, x.Hostname, $"https://{x.Hostname}", x.SslStatus, true))
            .ToList();

        var branches = app.Branches
            .Where(x => x.BranchName is not null)
            .OrderBy(x => x.BranchName, StringComparer.Ordinal)
            .Select(x => new AppBranchResponse(
                x.Id,
                x.Name,
                x.Slug,
                x.BranchName!,
                ControlPlaneValidation.ToWireValue(x.Plan),
                ControlPlaneValidation.ToWireValue(x.Database),
                x.Deployments.Where(deployment => deployment.AssetReleaseId == null).OrderByDescending(deployment => deployment.CreatedAt).FirstOrDefault()?.WebsiteUrl,
                x.CreatedAt,
                x.Domains.Where(domain => domain.Status != DomainBindingService.StatusDisabled)
                    .OrderBy(domain => domain.Hostname, StringComparer.Ordinal)
                    .Select(domain => $"https://{domain.Hostname}").ToArray()))
            .ToArray();

        return new AppResponse(
            app.Id,
            app.OrganizationId,
            app.Name,
            app.Slug,
            app.ParentAppId,
            app.BranchName,
            ControlPlaneValidation.ToWireValue(app.Plan),
            ControlPlaneValidation.ToWireValue(app.Database),
            domains,
            branches,
            latestDeployment,
            app.DeletionStatus,
            app.DeletionError,
            app.EnvironmentName ?? (app.ParentAppId is null ? "production" : null),
            app.BranchName is not null ? [] : new[] { app }.Concat(app.Branches.Where(x => x.EnvironmentName is not null))
                .Select(x => new AppEnvironmentResponse(x.Id, x.EnvironmentName ?? "production",
                    ControlPlaneValidation.ToWireValue(x.Plan), ControlPlaneValidation.ToWireValue(x.Database), x.DeletionStatus))
                .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray());
    }

    public static ApiKeyResponse ToResponse(this ApiKey apiKey)
    {
        var scopes = JsonSerializer.Deserialize<string[]>(apiKey.ScopesJson) ?? [];
        return new ApiKeyResponse(
            apiKey.Id,
            apiKey.Name,
            apiKey.KeyPrefix,
            scopes,
            apiKey.LastUsedAt,
            apiKey.ExpiresAt,
            apiKey.RevokedAt,
            apiKey.CreatedAt);
    }

    public static DeploymentResponse ToResponse(this Deployment deployment, string consoleBaseUrl)
    {
        var urlsByService = deployment.App.Domains
            .Where(x => x.Status != DomainBindingService.StatusDisabled)
            .GroupBy(x => x.ServiceName, StringComparer.Ordinal)
            .ToDictionary(
                x => x.Key,
                x => (IReadOnlyList<string>)x
                    .OrderBy(domain => domain.Hostname, StringComparer.Ordinal)
                    .Select(domain => $"https://{domain.Hostname}")
                    .ToArray(),
                StringComparer.Ordinal);

        return new(
            deployment.Id,
            deployment.AppId,
            deployment.OrganizationId,
            ControlPlaneValidation.ToWireValue(deployment.Database),
            ControlPlaneValidation.ToWireValue(deployment.Status),
            deployment.WebsiteUrl,
            $"{consoleBaseUrl.TrimEnd('/')}/apps/{Uri.EscapeDataString(deployment.AppId)}/deployments/{Uri.EscapeDataString(deployment.Id)}",
            deployment.FailureCode,
            deployment.FailureMessage,
            deployment.Services.Select(x => new DeploymentServiceResponse(
                x.Name,
                x.Image,
                x.Port,
                x.Public,
                x.Path,
                x.HealthPath,
                x.Public ? urlsByService.GetValueOrDefault(x.Name, []) : [],
                x.Sha256 ?? x.Artifact?.Sha256, AssetReleaseService.Mounts(x), StorageMounts.Read(x.StorageJson))).ToArray(),
            deployment.CreatedAt,
            deployment.StartedAt,
            deployment.CompletedAt,
            deployment.AssetReleaseId,
            deployment.CancellationRequestedAt,
            deployment.Events
                .Where(x => x.Message == DeploymentLifecycleService.ManuallyQueuedMessage)
                .Max(x => (DateTimeOffset?)x.CreatedAt));
    }
}
