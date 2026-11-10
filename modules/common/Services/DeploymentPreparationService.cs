using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class DeploymentPreparationService(MinicloudDbContext db, DeploymentLifecycleService lifecycle, TimeProvider timeProvider)
{
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(5);

    public async Task<T> LockedAsync<T>(string appId, Func<Task<T>> action, CancellationToken ct) =>
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (db.Database.IsNpgsql())
            {
                var applicationId = await db.Apps.Where(x => x.Id == appId).Select(x => x.ApplicationId).SingleAsync(ct);
                if (applicationId is not null)
                    await db.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {applicationId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
                await db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {appId} FOR UPDATE").AsNoTracking().SingleAsync(ct);
            }
            var result = await action();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        });

    public async Task<bool> HasActiveAsync(string appId, string? exceptId, CancellationToken ct) =>
        await db.Deployments.AnyAsync(d => d.AppId == appId && d.Id != exceptId &&
            (d.OwnershipToken != null || d.Status == DeploymentStatus.WaitingForBuild || d.Status == DeploymentStatus.Queued ||
             d.Status == DeploymentStatus.Dispatching || d.Status == DeploymentStatus.Provisioning ||
             d.Status == DeploymentStatus.Deploying || d.Status == DeploymentStatus.Verifying), ct);

    public bool IsExpired(Deployment deployment) => deployment.UpdatedAt < timeProvider.GetUtcNow() - HeartbeatTimeout;

    public Deployment Start(MinicloudApp app, string id, IReadOnlyList<string> serviceNames)
    {
        var now = timeProvider.GetUtcNow();
        return new Deployment
        {
            Id = id, AppId = app.Id, OrganizationId = app.OrganizationId, Database = app.Database,
            Status = DeploymentStatus.WaitingForBuild, CreatedAt = now, UpdatedAt = now,
            RequestedServiceNamesJson = JsonSerializer.Serialize(serviceNames),
            Events = [new DeploymentEvent { Id = Guid.NewGuid().ToString("N"), Level = EventLevel.Info,
                Message = "Waiting for the CLI to build and publish local images.", CreatedAt = now }]
        };
    }

    public bool Matches(Deployment deployment, IEnumerable<string> names) =>
        (JsonSerializer.Deserialize<string[]>(deployment.RequestedServiceNamesJson ?? "[]") ?? [])
        .ToHashSet(StringComparer.Ordinal).SetEquals(names);

    public void Heartbeat(Deployment deployment) => deployment.UpdatedAt = timeProvider.GetUtcNow();

    public bool Finish(Deployment deployment, string status, string stage = "build")
    {
        if (deployment.Status != DeploymentStatus.WaitingForBuild) { return false; }
        return status switch
        {
            "failed" => lifecycle.TryTransition(deployment, DeploymentStatus.Failed,
                stage == "build" ? "CLI local image build or publication failed. Inspect the CLI build log for details." :
                "CLI deployment preparation failed during " + stage + ". Inspect the CLI output for details.",
                stage == "build" ? "local_build_failed" : "local_preparation_failed").Succeeded,
            "canceled" => lifecycle.TryTransition(deployment, DeploymentStatus.Canceled, "CLI image preparation was canceled.").Succeeded,
            "expired" => lifecycle.TryTransition(deployment, DeploymentStatus.Failed,
                "The CLI stopped reporting local build progress. Run deploy again.", "local_build_abandoned").Succeeded,
            _ => false
        };
    }
}
