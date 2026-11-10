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
    public async Task<(int Status, string Code, string Message)?> RequestApplicationDeletionAsync(
        ControlPlanePrincipal principal, string applicationId, string confirmation, CancellationToken ct)
    {
        var application = await db.Applications.SingleOrDefaultAsync(x => x.Id == applicationId, ct);
        if (application is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), application.OrganizationId, OrganizationAction.WriteApps, ct))
        {
            return (404, "app_not_found", "Application was not found.");
        }
        if (!string.Equals(confirmation, application.Slug, StringComparison.Ordinal) &&
            !string.Equals(confirmation, application.Name, StringComparison.Ordinal))
        {
            return (400, "confirmation_required", "Type the application slug to confirm deletion.");
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockApplicationAsync(application.Id, ct);
            var query = db.Apps.Where(x => x.ApplicationId == application.Id);
            if (db.Database.IsNpgsql())
            {
                query = db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE application_id = {application.Id} ORDER BY id FOR UPDATE");
            }
            var apps = await query.ToListAsync(ct);
            if (apps.Any(x => x.OrganizationId != application.OrganizationId))
            {
                return ((int Status, string Code, string Message)?)(409, "ownership_conflict", "Application ownership requires reconciliation.");
            }
            foreach (var target in apps)
            {
                await db.Entry(target).ReloadAsync(ct);
            }

            if (apps.Count == 0)
            {
                db.Applications.Remove(application);
                await audit.AppendAsync(application.OrganizationId, null, AuditEventService.ActorFor(principal.ToActorIdentity()), "app.deleted", application.Slug, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return null;
            }

            if (apps.All(x => x.DeletionStatus == "queued") && application.DeletionStatus == "queued")
            {
                return null;
            }

            application.DeletionStatus = "preparing";
            var error = await PrepareTargetsForDeletionAsync(apps, ct);
            if (error is not null)
            {
                return error;
            }

            application.DeletionStatus = apps.Any(x => x.DeletionStatus == "failed") ? "failed" : "queued";
            application.DeletionError = apps.FirstOrDefault(x => x.DeletionError != null)?.DeletionError;
            application.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(application.OrganizationId, apps[0].Id, AuditEventService.ActorFor(principal.ToActorIdentity()), "app.delete_requested", application.Slug, ct);
            await transaction.CommitAsync(ct);
            return null;
        });
    }

    public async Task<(int Status, string Code, string Message)?> RequestEnvironmentDeletionAsync(
        ControlPlanePrincipal principal, string applicationId, string environmentId, string confirmation, CancellationToken ct)
    {
        var application = await db.Applications.SingleOrDefaultAsync(x => x.Id == applicationId, ct);
        if (application is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), application.OrganizationId, OrganizationAction.WriteApps, ct))
        {
            return (404, "app_not_found", "Application was not found.");
        }

        var environment = await db.Apps.SingleOrDefaultAsync(x => x.Id == environmentId, ct);
        if (environment is null || environment.OrganizationId != application.OrganizationId || environment.ApplicationId != application.Id)
        {
            return (404, "environment_not_found", "Environment was not found under this application.");
        }

        var expectedName = environment.EnvironmentName ?? "production";
        if (!string.Equals(confirmation, expectedName, StringComparison.Ordinal) &&
            !string.Equals(confirmation, environment.Slug, StringComparison.Ordinal))
        {
            return (400, "confirmation_required", $"Type '{expectedName}' to confirm environment deletion.");
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await LockApplicationAsync(application.Id, ct);
            var query = db.Apps.Where(x => x.ApplicationId == application.Id || x.ParentAppId == environment.Id || x.Id == environment.Id);
            if (db.Database.IsNpgsql())
            {
                query = db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE application_id = {application.Id} OR parent_app_id = {environment.Id} OR id = {environment.Id} ORDER BY id FOR UPDATE");
            }
            var family = await query.ToListAsync(ct);
            foreach (var member in family)
            {
                await db.Entry(member).ReloadAsync(ct);
            }

            var target = family.SingleOrDefault(x => x.Id == environment.Id);
            if (target is null)
            {
                return ((int Status, string Code, string Message)?)(404, "environment_not_found", "Environment was not found.");
            }
            if (target.DeletionStatus == "queued")
            {
                return null;
            }

            var error = await PrepareTargetsForDeletionAsync([target], ct);
            if (error is not null)
            {
                return error;
            }

            var siblings = family.Where(x => x.Id != target.Id).ToList();
            var dependentChildren = siblings.Where(x => x.ParentAppId == target.Id).ToList();
            if (dependentChildren.Count > 0)
            {
                var nextPersistentRoot = siblings
                    .Where(x => x.BranchName is null && !string.Equals(x.EnvironmentKind, EnvironmentKinds.Preview, StringComparison.Ordinal) && x.DeletionStatus is null)
                    .OrderBy(x => x.CreatedAt)
                    .ThenBy(x => x.Id, StringComparer.Ordinal)
                    .FirstOrDefault();

                if (nextPersistentRoot is not null)
                {
                    nextPersistentRoot.ParentAppId = null;
                    nextPersistentRoot.ParentApp = null;
                    foreach (var child in dependentChildren.Where(x => x.Id != nextPersistentRoot.Id))
                    {
                        child.ParentAppId = nextPersistentRoot.Id;
                        child.ParentApp = nextPersistentRoot;
                    }
                }
                else
                {
                    foreach (var child in dependentChildren)
                    {
                        child.SourceEnvironmentId ??= target.Id;
                        child.ParentAppId = null;
                        child.ParentApp = null;
                    }
                }
            }

            if (application.DefaultEnvironmentId == target.Id)
            {
                application.DefaultEnvironmentId = ApplicationEnvironmentMigrationService.SelectNextDefaultEnvironmentId(siblings);
                application.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(application.OrganizationId, target.Id, AuditEventService.ActorFor(principal.ToActorIdentity()), "environment.delete_requested", expectedName, ct);
            await transaction.CommitAsync(ct);
            return null;
        });
    }

    private async Task LockApplicationAsync(string? applicationId, CancellationToken ct)
    {
        if (applicationId is null) return;
        var application = db.Database.IsNpgsql()
            ? await db.Applications.FromSqlInterpolated($"SELECT * FROM applications WHERE id = {applicationId} FOR UPDATE").SingleAsync(ct)
            : await db.Applications.SingleAsync(x => x.Id == applicationId, ct);
        await db.Entry(application).ReloadAsync(ct);
    }

    private async Task<(int Status, string Code, string Message)?> PrepareTargetsForDeletionAsync(IReadOnlyList<MinicloudApp> apps, CancellationToken ct)
    {
        var ids = apps.Select(x => x.Id).ToArray();
        foreach (var target in apps)
        {
            var blocker = await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, target.Id, ct);
            if (blocker is not null)
            {
                return (409, "database_resource_in_use", blocker);
            }
        }
        var deployments = await db.Deployments.Where(x => ids.Contains(x.AppId)).ToListAsync(ct);
        if (deployments.Any(x => x.OwnershipToken is not null ||
                                 x.Status is DeploymentStatus.Dispatching or DeploymentStatus.Provisioning or DeploymentStatus.Deploying or DeploymentStatus.Verifying))
        {
            return (409, "deployment_active", "Wait for active deployments to finish before deleting this application.");
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
                {
                    session.RevokedAt = DateTimeOffset.UtcNow;
                    session.Revision++;
                }
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
        return null;
    }
}
