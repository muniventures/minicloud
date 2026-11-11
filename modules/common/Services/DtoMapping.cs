using System.Text.Json;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public static class DtoMapping
{
    public static OrganizationResponse ToResponse(this Organization organization) =>
        new(organization.Id, organization.Name, organization.Slug, organization.CreatedAt);

    public static EnvironmentDetailResponse ToResponse(this MinicloudApp app, bool sshSelectionLocked = false)
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

        var branchSources = app.Application?.Environments.Count > 0
            ? app.Application.Environments.Where(x => x.BranchName is not null)
            : app.Branches.Where(x => x.BranchName is not null);

        var branches = (app.BranchName is not null ? [] : branchSources)
            .DistinctBy(x => x.Id)
            .OrderBy(x => x.BranchName, StringComparer.Ordinal)
            .Select(x => new AppBranchResponse(
                x.Id,
                x.Name,
                x.Slug,
                x.BranchName!,
                PlanName(x),
                ControlPlaneValidation.ToWireValue(x.Database),
                x.Deployments.Where(deployment => deployment.AssetReleaseId == null).OrderByDescending(deployment => deployment.CreatedAt).FirstOrDefault()?.WebsiteUrl,
                x.CreatedAt,
                x.Domains.Where(domain => domain.Status != DomainBindingService.StatusDisabled)
                    .OrderBy(domain => domain.Hostname, StringComparer.Ordinal)
                    .Select(domain => $"https://{domain.Hostname}").ToArray(), x.PlanId))
            .ToArray();

        var persistentEnvironments = app.BranchName is not null || string.Equals(app.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal)
            ? []
            : (app.Application?.Environments.Count > 0
                ? app.Application.Environments.Where(x => x.BranchName is null && !string.Equals(x.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal) && !string.Equals(x.EnvironmentKind, EnvironmentKinds.Internal, StringComparison.Ordinal))
                : new[] { app }.Concat(app.Branches.Where(x => x.BranchName is null && x.EnvironmentName is not null)))
                .DistinctBy(x => x.Id)
                .Select(x => new AppEnvironmentResponse(x.Id, x.EnvironmentName ?? "production",
                    PlanName(x), ControlPlaneValidation.ToWireValue(x.Database), x.DeletionStatus, x.PlanId))
                .OrderBy(x => x.Name, StringComparer.Ordinal)
                .ToArray();

        return new EnvironmentDetailResponse(
            app.Id,
            app.OrganizationId,
            app.Name,
            app.Slug,
            app.ParentAppId,
            app.BranchName,
            PlanName(app),
            ControlPlaneValidation.ToWireValue(app.Database),
            domains,
            branches,
            latestDeployment,
            app.DeletionStatus,
            app.DeletionError,
            app.EnvironmentName ?? (app.ParentAppId is null ? "production" : null),
            persistentEnvironments,
            app.PlanId,
            app.ApplicationId ?? app.Application?.Id,
            app.Application?.DefaultEnvironmentId,
            ToSshAccessResponse(app, sshSelectionLocked),
            null);
    }

    public static ApplicationEnvironmentResponse ToResponse(
        this MinicloudApp env,
        string applicationId,
        EnvironmentRuntimeObservationResponse? runtimeObservation = null,
        bool placementLocked = false,
        bool sshSelectionLocked = false)
    {
        var latestDeployment = env.Deployments
            .Where(x => x.AssetReleaseId == null)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id, StringComparer.Ordinal)
            .Select(x => new LatestDeploymentResponse(
                x.Id,
                ControlPlaneValidation.ToWireValue(x.Status),
                x.WebsiteUrl,
                x.CompletedAt))
            .FirstOrDefault();

        var domains = env.Domains
            .Where(x => x.Status != DomainBindingService.StatusDisabled)
            .OrderBy(x => x.Hostname, StringComparer.Ordinal)
            .Select(x => new AppDomainResponse(x.Id, x.Hostname, $"https://{x.Hostname}", x.SslStatus, true))
            .ToArray();

        var kind = env.BranchName is not null || string.Equals(env.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal)
            ? EnvironmentKinds.Preview
            : EnvironmentKinds.Persistent;

        var displayName = !string.IsNullOrWhiteSpace(env.EnvironmentName)
            ? env.EnvironmentName
            : kind == EnvironmentKinds.Preview
                ? (env.BranchName ?? env.Slug)
                : "production";

        return new ApplicationEnvironmentResponse(
            env.Id,
            applicationId,
            env.OrganizationId,
            displayName,
            kind,
            env.Slug,
            PlanName(env),
            env.PlanId,
            ControlPlaneValidation.ToWireValue(env.Database),
            env.SourceBranch ?? env.BranchName,
            env.SourceEnvironmentId ?? (kind == EnvironmentKinds.Preview ? env.ParentAppId : null),
            env.DeletionStatus,
            env.DeletionError,
            placementLocked,
            domains,
            latestDeployment,
            runtimeObservation ?? new EnvironmentRuntimeObservationResponse("unprovisioned", false, false, null, null, 0, 0),
            env.CreatedAt,
            env.UpdatedAt,
            ToSshAccessResponse(env, sshSelectionLocked),
            null);
    }

    private static EnvironmentSshAccessResponse ToSshAccessResponse(MinicloudApp env, bool placementLocked) =>
        new(env.SshAccessMode,
            env.SshProviderAccountId ?? env.SelectedPlan?.ProviderAccountId,
            env.SshAccessMode == "existing" ? env.SshKeyId : null,
            env.SshAccessMode == "none" ? null : env.SshKeyName,
            env.SshAccessMode == "none" ? null : env.SshFingerprint,
            env.SshAccessMode == "none" ? null : env.SshKeyType,
            env.SshRevision,
            env.SshRegistrationState,
            placementLocked);

    public static ApplicationResponse ToResponse(
        this MinicloudApplication application,
        IReadOnlyDictionary<string, EnvironmentRuntimeObservationResponse>? runtimeObservations = null,
        IReadOnlySet<string>? placementLockedEnvironmentIds = null,
        IReadOnlySet<string>? sshLockedEnvironmentIds = null)
    {
        var orderedEnvironments = application.Environments
            .Where(x => !string.Equals(x.EnvironmentKind, EnvironmentKinds.Internal, StringComparison.Ordinal))
            .OrderBy(x => (x.BranchName is not null || string.Equals(x.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal)) ? 1 : 0)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => x.ToResponse(
                application.Id,
                runtimeObservations is not null && runtimeObservations.TryGetValue(x.Id, out var obs) ? obs : null,
                placementLockedEnvironmentIds is not null && placementLockedEnvironmentIds.Contains(x.Id),
                sshLockedEnvironmentIds is not null && sshLockedEnvironmentIds.Contains(x.Id)))
            .ToArray();

        var persistentCount = orderedEnvironments.Count(x => x.Kind == EnvironmentKinds.Persistent);
        var previewCount = orderedEnvironments.Count(x => x.Kind == EnvironmentKinds.Preview);

        return new ApplicationResponse(
            application.Id,
            application.OrganizationId,
            application.Name,
            application.Slug,
            application.DefaultEnvironmentId,
            application.DeletionStatus,
            application.DeletionError,
            orderedEnvironments.Length,
            persistentCount,
            previewCount,
            orderedEnvironments,
            application.CreatedAt,
            application.UpdatedAt);
    }

    public static string PlanName(MinicloudApp app) => app.SelectedPlan?.Name ?? app.PlanId ?? ControlPlaneValidation.ToWireValue(app.Plan);

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
                .Max(x => (DateTimeOffset?)x.CreatedAt),
            deployment.App.ApplicationId,
            deployment.AppId);
    }
}
