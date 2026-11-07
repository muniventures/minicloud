using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Minicloud.Common.Services;

/// <summary>
/// T50 API gateway: prepares queued deployments for minicloud-worker.
/// The API no longer dispatches GitHub workflows; the worker claims
/// <c>Queued</c> rows via PostgreSQL <c>FOR UPDATE SKIP LOCKED</c>.
/// T90 removed the decommissioned GitHub dispatch client and options.
/// </summary>
public sealed class DeploymentOrchestrator(
    DeploymentLifecycleService lifecycle,
    IAppSecretService secrets,
    ILogger<DeploymentOrchestrator> logger,
    IOptions<ManagedDomainOptions>? managedDomains = null)
{
    public async Task<DeploymentDispatchResult> EnqueueAsync(
        Organization organization,
        MinicloudApp app,
        Deployment deployment,
        string? requestedPostgresPassword,
        IReadOnlyList<DeploymentServiceRequest>? requestedServices,
        IReadOnlyList<Domain>? domainBindings,
        CancellationToken cancellationToken)
    {
        var domain = domainBindings?.OrderBy(x => x.ServiceName, StringComparer.Ordinal).FirstOrDefault()?.Hostname
            ?? DomainBindingService.DefaultHostname(organization.Slug, app.Slug,
                requestedServices?.FirstOrDefault(x => x.Public)?.Name ?? "web",
                managedDomains?.Value.Suffix ?? DomainBindingService.MinicloudSuffix);
        deployment.Domain = domain;

        var postgresSecret = deployment.Database == DatabaseMode.Postgres
            ? await secrets.EnsurePostgresPasswordAsync(app, requestedPostgresPassword, cancellationToken)
            : null;

        logger.LogInformation("Deployment {DeploymentId} queued for minicloud-worker (app {AppId}).", deployment.Id, app.Id);
        return new DeploymentDispatchResult(postgresSecret is { WasGenerated: true } ? postgresSecret.Value : null);
    }

    [Obsolete("GitHub dispatch was removed in T50; deployments stay Queued for minicloud-worker.")]
    public Task<DeploymentDispatchResult> DispatchAsync(
        Organization organization,
        MinicloudApp app,
        Deployment deployment,
        string? requestedPostgresPassword,
        IReadOnlyList<DeploymentServiceRequest>? requestedServices,
        IReadOnlyList<Domain>? domainBindings,
        string? runtimeApiKey,
        CancellationToken cancellationToken) =>
        EnqueueAsync(organization, app, deployment, requestedPostgresPassword, requestedServices, domainBindings, cancellationToken);

    [Obsolete("GitHub dispatch was removed in T50; deployments stay Queued for minicloud-worker.")]
    public Task<DeploymentDispatchResult> DispatchAsync(
        Organization organization,
        MinicloudApp app,
        Deployment deployment,
        string? requestedPostgresPassword,
        IReadOnlyList<DeploymentServiceRequest>? requestedServices,
        string? runtimeApiKey,
        CancellationToken cancellationToken) =>
        EnqueueAsync(organization, app, deployment, requestedPostgresPassword, requestedServices, domainBindings: null, cancellationToken);

    [Obsolete("GitHub dispatch was removed in T50; deployments stay Queued for minicloud-worker.")]
    public Task<DeploymentDispatchResult> DispatchAsync(
        Organization organization,
        MinicloudApp app,
        Deployment deployment,
        string? requestedPostgresPassword,
        IReadOnlyList<DeploymentServiceRequest>? requestedServices,
        CancellationToken cancellationToken) =>
        EnqueueAsync(organization, app, deployment, requestedPostgresPassword, requestedServices, domainBindings: null, cancellationToken);

    [Obsolete("GitHub dispatch was removed in T50; deployments stay Queued for minicloud-worker.")]
    public Task<DeploymentDispatchResult> DispatchAsync(
        Organization organization,
        MinicloudApp app,
        Deployment deployment,
        string? requestedPostgresPassword,
        CancellationToken cancellationToken) =>
        EnqueueAsync(organization, app, deployment, requestedPostgresPassword, requestedServices: null, domainBindings: null, cancellationToken);

    [Obsolete("GitHub dispatch was removed in T50; deployments stay Queued for minicloud-worker.")]
    public Task<DeploymentDispatchResult> DispatchAsync(Organization organization, MinicloudApp app, Deployment deployment, CancellationToken cancellationToken) =>
        EnqueueAsync(organization, app, deployment, requestedPostgresPassword: null, requestedServices: null, domainBindings: null, cancellationToken);

    [Obsolete("GitHub polling was removed in T50; minicloud-worker owns deployment lifecycle transitions.")]
    public Task SyncRunAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        logger.LogDebug("SyncRunAsync is decommissioned; deployment {DeploymentId} status is owned by minicloud-worker.", deployment.Id);
        return Task.CompletedTask;
    }

    public static string DeploymentDomain(string organizationSlug, string appSlug)
    {
        var label = $"{organizationSlug}-{appSlug}".ToLowerInvariant();
        if (label.Length > 63)
        {
            label = label[..63].TrimEnd('-');
        }

        return $"{label}.app.muni.dev";
    }
}

public sealed record DeploymentDispatchResult(string? GeneratedPostgresPassword);
