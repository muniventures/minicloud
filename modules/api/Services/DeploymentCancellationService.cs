using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class DeploymentCancellationService(
    MinicloudDbContext db, ControlPlaneAuthorization authorization, AuditEventService audit, TimeProvider clock)
{
    public async Task<(int Status, string? Code)> RequestAsync(ControlPlanePrincipal principal, string id, CancellationToken ct)
    {
        var candidate = await db.Deployments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (candidate is null || !await authorization.CanAccessAppAsync(principal.ToActorIdentity(), candidate.AppId, OrganizationAction.CreateDeployments, ct))
        {
            return (404, "deployment_not_found");
        }
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var deployment = await DeploymentMutation.LockAsync(db, id, ct);
            if (deployment is null) { return (404, (string?)"deployment_not_found"); }
            if (deployment.Status == DeploymentStatus.Canceled) { return (200, (string?)null); }
            if (DeploymentMutation.IsTerminal(deployment.Status)) { return (409, (string?)"deployment_already_finished"); }
            if (deployment.CancellationRequestedAt is not null) { return (202, (string?)null); }
            var now = clock.GetUtcNow();
            deployment.CancellationRequestedAt = now;
            deployment.CancellationRequestedBy = AuditEventService.ActorFor(principal.ToActorIdentity());
            deployment.UpdatedAt = now;
            var waitingForBuild = deployment.Status == DeploymentStatus.WaitingForBuild;
            if ((waitingForBuild || deployment.Status == DeploymentStatus.Queued) && deployment.OwnershipToken is null)
            {
                deployment.Status = DeploymentStatus.Canceled;
                deployment.CompletedAt = now;
            }
            db.DeploymentEvents.Add(new DeploymentEvent
            {
                Id = Guid.NewGuid().ToString("N"), DeploymentId = id, CreatedAt = now, Level = EventLevel.Info,
                Message = deployment.Status == DeploymentStatus.Canceled
                    ? waitingForBuild ? "Local build preparation canceled." : "Queued deployment canceled."
                    : "Deployment cancellation requested; waiting for execution to stop."
            });
            await audit.AppendAsync(deployment.OrganizationId, deployment.AppId, deployment.CancellationRequestedBy,
                "deployment.cancel_requested", id, ct);
            await transaction.CommitAsync(ct);
            return (deployment.Status == DeploymentStatus.Canceled ? 200 : 202, (string?)null);
        });
    }
}
