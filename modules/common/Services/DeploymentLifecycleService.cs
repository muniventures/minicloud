using System.Text.Json;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Validation;

namespace Minicloud.Common.Services;

public sealed class DeploymentLifecycleService(IIdGenerator ids, TimeProvider timeProvider)
{
    public const string ManuallyQueuedMessage = "Deployment manually queued.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<DeploymentStatus, ISet<DeploymentStatus>> AllowedTransitions =
        new Dictionary<DeploymentStatus, ISet<DeploymentStatus>>
        {
            [DeploymentStatus.WaitingForBuild] = TerminalTransitions(DeploymentStatus.Queued),
            [DeploymentStatus.Queued] = TerminalTransitions(DeploymentStatus.Dispatching, DeploymentStatus.Provisioning),
            [DeploymentStatus.Dispatching] = TerminalTransitions(DeploymentStatus.Provisioning, DeploymentStatus.Deploying),
            [DeploymentStatus.Provisioning] = TerminalTransitions(DeploymentStatus.Deploying),
            [DeploymentStatus.Deploying] = TerminalTransitions(DeploymentStatus.Verifying),
            [DeploymentStatus.Verifying] = TerminalTransitions(DeploymentStatus.Succeeded),
            [DeploymentStatus.Succeeded] = new HashSet<DeploymentStatus>(),
            [DeploymentStatus.Failed] = new HashSet<DeploymentStatus>(),
            [DeploymentStatus.Canceled] = new HashSet<DeploymentStatus>()
        };

    public Deployment CreateQueued(MinicloudApp app, CreateDeploymentRequest request)
    {
        ControlPlaneValidation.TryParseEnum<DatabaseMode>(request.Database, out var database);

        var now = timeProvider.GetUtcNow();
        var deployment = new Deployment
        {
            Id = ids.NewId("dep"),
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            Database = database,
            Status = DeploymentStatus.Queued,
            CommitSha = request.CommitSha,
            CreatedAt = now,
            UpdatedAt = now,
            RequestedServiceNamesJson = JsonSerializer.Serialize(request.Services.Select(x => x.Name)),
            Services = request.Services.Select(CreateService).ToList()
        };

        AppendEvent(deployment, EventLevel.Info, "Deployment queued.", now);
        return deployment;
    }

    public void AddRetainedServices(Deployment deployment, IReadOnlyList<DeploymentServiceRequest> desired)
    {
        var names = deployment.Services.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var service in desired.Where(x => !names.Contains(x.Name))) { deployment.Services.Add(CreateService(service)); }
    }

    private DeploymentService CreateService(DeploymentServiceRequest service) => new DeploymentService
    {
        Id = ids.NewId("svc"),
        Name = service.Name,
        Image = service.Image ?? string.Empty,
        ArtifactId = service.ArtifactId,
        ImagePublicationId = service.ImagePublicationId,
        Sha256 = service.Sha256,
        StorageJson = service.Storage is { Count: > 0 } ? JsonSerializer.Serialize(service.Storage, JsonOptions) : null,
        AssetMountsJson = service.AssetMounts is { Count: > 0 } ? JsonSerializer.Serialize(service.AssetMounts, JsonOptions) : null,
        Port = service.Port,
        Public = service.Public,
        Path = service.Path,
        HealthPath = service.HealthPath,
        EnvJson = service.Env is { Count: > 0 } ? JsonSerializer.Serialize(service.Env, JsonOptions) : null,
        SecretEnvJson = service.SecretEnv is { Count: > 0 } ? JsonSerializer.Serialize(service.SecretEnv, JsonOptions) : null
    };

    public DeploymentTransitionResult TryTransition(
        Deployment deployment,
        DeploymentStatus nextStatus,
        string message,
        string? failureCode = null,
        string? failureMessage = null)
    {
        if (!CanTransition(deployment.Status, nextStatus))
        {
            return DeploymentTransitionResult.Invalid(deployment.Status, nextStatus);
        }

        var now = timeProvider.GetUtcNow();
        var previousStatus = deployment.Status;
        deployment.Status = nextStatus;
        deployment.UpdatedAt = now;

        if (previousStatus != DeploymentStatus.WaitingForBuild && nextStatus is not (DeploymentStatus.Queued or DeploymentStatus.WaitingForBuild) && deployment.StartedAt is null)
        {
            deployment.StartedAt = now;
        }

        if (nextStatus is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled)
        {
            deployment.CompletedAt = now;
        }

        if (nextStatus is DeploymentStatus.Failed)
        {
            deployment.FailureCode = failureCode ?? "internal_error";
            deployment.FailureMessage = failureMessage ?? message;
        }

        AppendEvent(
            deployment,
            nextStatus is DeploymentStatus.Failed ? EventLevel.Error : EventLevel.Info,
            message,
            now);

        return DeploymentTransitionResult.Applied(deployment.Status);
    }

    public void ResetForManualDispatch(Deployment deployment)
    {
        var now = timeProvider.GetUtcNow();
        deployment.Status = DeploymentStatus.Queued;
        deployment.CancellationRequestedAt = null;
        deployment.CancellationRequestedBy = null;
        deployment.WorkerId = null;
        deployment.OwnershipToken = null;
        deployment.OwnershipAcquiredAt = null;
        deployment.GithubRunId = null;
        deployment.GithubRunUrl = null;
        deployment.FailureCode = null;
        deployment.FailureMessage = null;
        deployment.StartedAt = null;
        deployment.CompletedAt = null;
        deployment.UpdatedAt = now;
        AppendEvent(deployment, EventLevel.Info, ManuallyQueuedMessage, now);
    }

    public static bool CanTransition(DeploymentStatus currentStatus, DeploymentStatus nextStatus) =>
        AllowedTransitions.TryGetValue(currentStatus, out var nextStatuses) && nextStatuses.Contains(nextStatus);

    private void AppendEvent(Deployment deployment, EventLevel level, string message, DateTimeOffset createdAt) =>
        deployment.Events.Add(new DeploymentEvent
        {
            Id = ids.NewId("evt"),
            Level = level,
            Message = message,
            CreatedAt = createdAt
        });

    private static ISet<DeploymentStatus> TerminalTransitions(params DeploymentStatus[] statuses) =>
        statuses.Concat([DeploymentStatus.Failed, DeploymentStatus.Canceled]).ToHashSet();
}

public sealed record DeploymentTransitionResult(
    bool Succeeded,
    DeploymentStatus CurrentStatus,
    DeploymentStatus? RejectedStatus)
{
    public static DeploymentTransitionResult Applied(DeploymentStatus currentStatus) =>
        new(true, currentStatus, null);

    public static DeploymentTransitionResult Invalid(DeploymentStatus currentStatus, DeploymentStatus rejectedStatus) =>
        new(false, currentStatus, rejectedStatus);
}
