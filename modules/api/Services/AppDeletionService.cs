using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class AppDeletionService(
    MinicloudDbContext db,
    ControlPlaneAuthorization authorization,
    AppServiceSecretService secrets,
    TelemetryService telemetry,
    AuditEventService audit,
    DeploymentArtifactStorageService artifacts)
{
    public async Task<(int Status, string Code, string Message)?> RequestAsync(
        ControlPlanePrincipal principal, string appId, string confirmation, CancellationToken ct)
    {
        var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == appId, ct);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.WriteApps, ct))
        {
            return (404, "app_not_found", "Application was not found.");
        }
        if (!string.Equals(confirmation, app.Slug, StringComparison.Ordinal))
        {
            return (400, "confirmation_required", "Type the application slug to confirm deletion.");
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var query = db.Apps.Where(x => x.Id == appId || x.ParentAppId == appId);
            if (db.Database.IsNpgsql())
            {
                query = db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {appId} OR parent_app_id = {appId} ORDER BY id FOR UPDATE");
            }
            var apps = await query.ToListAsync(ct);
            if (apps.Count == 0)
            {
                return ((int Status, string Code, string Message)?)(404, "app_not_found", "Application was not found.");
            }
            if (apps.Any(x => x.OrganizationId != app.OrganizationId))
            {
                return ((int Status, string Code, string Message)?)(409, "ownership_conflict", "Application ownership requires reconciliation.");
            }
            foreach (var target in apps)
            {
                await db.Entry(target).ReloadAsync(ct);
            }
            if (apps.All(x => x.DeletionStatus == "queued"))
            {
                return null;
            }
            var ids = apps.Select(x => x.Id).ToArray();
            var deployments = await db.Deployments.Where(x => ids.Contains(x.AppId)).ToListAsync(ct);
            if (deployments.Any(x => x.OwnershipToken is not null ||
                                     x.Status is DeploymentStatus.Dispatching or DeploymentStatus.Provisioning or DeploymentStatus.Deploying or DeploymentStatus.Verifying))
            {
                return ((int Status, string Code, string Message)?)(409, "deployment_active", "Wait for active deployments to finish before deleting this application.");
            }
            foreach (var deployment in deployments.Where(x => x.Status is DeploymentStatus.Queued or DeploymentStatus.WaitingForBuild))
            {
                deployment.Status = DeploymentStatus.Canceled;
                deployment.CompletedAt = DateTimeOffset.UtcNow;
                deployment.UpdatedAt = deployment.CompletedAt.Value;
            }
            foreach (var target in apps)
            {
                target.DeletionStatus = "preparing";
                target.DeletionError = null;
            }
            await db.SaveChangesAsync(ct);
            try
            {
                foreach (var target in apps)
                {
                    foreach (var artifact in await db.DeploymentArtifacts.Where(x => x.AppId == target.Id).ToListAsync(ct))
                    {
                        artifacts.DeleteContent(artifact);
                    }
                    await secrets.DeleteAllAsync(target.Id, ct);
                    await telemetry.TombstoneAppAsync(target.Id, ct);
                    var tokens = await db.RuntimeTokens.Where(x => x.RuntimeServer.AppId == target.Id).ToListAsync(ct);
                    foreach (var session in await db.RegistrySessions.Where(x => x.AppId == target.Id && x.RevokedAt == null).ToListAsync(ct))
                    { session.RevokedAt = DateTimeOffset.UtcNow; session.Revision++; }
                    foreach (var token in tokens)
                    {
                        token.RevokedAt = DateTimeOffset.UtcNow;
                    }
                    target.DeletionStatus = "queued";
                }
            }
            catch (Exception ex) when (ex is SecretStoreUnavailableException or IOException or UnauthorizedAccessException)
            {
                foreach (var target in apps)
                {
                    target.DeletionStatus = "failed";
                    target.DeletionError = "Stored data cleanup failed. Retry deletion when storage is available.";
                }
            }
            await audit.AppendAsync(app.OrganizationId, app.Id, AuditEventService.ActorFor(principal.ToActorIdentity()), "app.delete_requested", app.Slug, ct);
            await transaction.CommitAsync(ct);
            return null;
        });
    }
}
