using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

/// <summary>Detached response data, with one deployment per environment and no collection joins.</summary>
public sealed class AppSummaryReader(MinicloudDbContext db)
{
    public async Task<EnvironmentDetailResponse?> GetAsync(string organizationId, string appId, CancellationToken ct)
    {
        var target = db.Apps.Where(x => x.OrganizationId == organizationId && x.Id == appId);
        var scope = db.Apps.Where(x => x.Id == appId || x.ParentAppId == appId ||
            (x.ApplicationId != null && target.Select(t => t.ApplicationId).Contains(x.ApplicationId)));
        var apps = await ReadEnvironmentsAsync(organizationId, scope, ct);
        var app = apps.SingleOrDefault(x => x.Id == appId);
        if (app is null) return null;
        var sshLocked = await db.ServerInstances.AnyAsync(x => x.AppId == appId, ct) ||
            await db.RuntimeServers.AnyAsync(x => x.AppId == appId, ct) ||
            await db.Deployments.AnyAsync(x => x.AppId == appId &&
                (x.ProviderCreationAttempted ||
                 (x.SshRegistrationState == "unknown" || x.SshRegistrationState == "registering") ||
                 (x.SshSnapshotTakenAt != null &&
                  (x.Status == DeploymentStatus.Queued || x.Status == DeploymentStatus.WaitingForBuild ||
                   x.Status == DeploymentStatus.Dispatching || x.Status == DeploymentStatus.Provisioning ||
                   x.Status == DeploymentStatus.Deploying || x.Status == DeploymentStatus.Verifying))), ct);
        return app.ToResponse(sshLocked);
    }

    // Three sequential queries; the caller owns authorization. Scope is additionally tenant-filtered here.
    public async Task<List<MinicloudApp>> ReadEnvironmentsAsync(string organizationId, IQueryable<MinicloudApp> scope, CancellationToken ct)
    {
        var apps = await scope.AsNoTracking().Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new MinicloudApp
            {
                Id = x.Id, OrganizationId = x.OrganizationId, Name = x.Name, Slug = x.Slug,
                ParentAppId = x.ParentAppId, BranchName = x.BranchName, EnvironmentName = x.EnvironmentName,
                EnvironmentKind = x.EnvironmentKind, SourceBranch = x.SourceBranch, SourceEnvironmentId = x.SourceEnvironmentId,
                ApplicationId = x.ApplicationId, PlanId = x.PlanId, Plan = x.Plan, Database = x.Database,
                SshAccessMode = x.SshAccessMode, SshProviderAccountId = x.SshProviderAccountId, SshKeyId = x.SshKeyId,
                SshKeyName = x.SshKeyName, SshFingerprint = x.SshFingerprint, SshKeyType = x.SshKeyType,
                SshRegistrationState = x.SshRegistrationState, SshRevision = x.SshRevision,
                DeletionStatus = x.DeletionStatus, DeletionError = x.DeletionError, CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt,
                SelectedPlan = x.SelectedPlan == null ? null : new ProviderPlan { Name = x.SelectedPlan.Name, ProviderAccountId = x.SelectedPlan.ProviderAccountId },
                Application = x.Application == null ? null : new MinicloudApplication
                {
                    Id = x.Application.Id, OrganizationId = x.Application.OrganizationId,
                    DefaultEnvironmentId = x.Application.DefaultEnvironmentId
                }
            }).ToListAsync(ct);
        if (apps.Count == 0) return apps;
        var ids = apps.Select(x => x.Id).ToArray();
        var eligible = db.Deployments.AsNoTracking().Where(x => x.OrganizationId == organizationId && ids.Contains(x.AppId));
        IQueryable<Deployment> latest;
        if (db.Database.IsSqlite())
        {
            // SQLite stores DateTimeOffset as text. Normalize the UTC second, then compare
            // fractional seconds separately to preserve sub-millisecond precision and offsets.
            // JSON keeps the ID set in one parameter rather than SQLite's parameter-per-ID limit.
            var idsJson = System.Text.Json.JsonSerializer.Serialize(ids);
            latest = db.Deployments.FromSqlInterpolated($"""
                SELECT d.* FROM apps a
                JOIN deployments d ON d.id = (SELECT d2.id FROM deployments d2
                    WHERE d2.organization_id = a.organization_id AND d2.app_id = a.id AND d2.asset_release_id IS NULL
                    ORDER BY datetime(d2.created_at) DESC,
                      CASE WHEN instr(d2.created_at, '.') > 0
                        THEN CAST('0.' || substr(d2.created_at, instr(d2.created_at, '.') + 1) AS REAL) ELSE 0 END DESC,
                      d2.id DESC LIMIT 1)
                WHERE a.organization_id = {organizationId} AND a.id IN (SELECT value FROM json_each({idsJson}))
                """).AsNoTracking();
        }
        else
        {
            var latestIds = db.Apps.Where(x => x.OrganizationId == organizationId && ids.Contains(x.Id))
                .Select(a => db.Deployments.Where(d => d.OrganizationId == organizationId && d.AppId == a.Id && d.AssetReleaseId == null)
                    .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id).Select(d => d.Id).FirstOrDefault());
            latest = eligible.Where(d => latestIds.Contains(d.Id));
        }
        var deployments = await latest.Select(d => new Deployment
        {
            Id = d.Id, AppId = d.AppId, Status = d.Status, WebsiteUrl = d.WebsiteUrl, CompletedAt = d.CompletedAt
        }).ToListAsync(ct);
        var domains = await db.Domains.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && ids.Contains(d.AppId) && d.Status != DomainBindingService.StatusDisabled)
            .Select(d => new Domain { Id = d.Id, AppId = d.AppId, Hostname = d.Hostname, SslStatus = d.SslStatus, Status = d.Status })
            .ToListAsync(ct);
        var deploymentsByApp = deployments.ToLookup(x => x.AppId, StringComparer.Ordinal);
        var domainsByApp = domains.ToLookup(x => x.AppId, StringComparer.Ordinal);
        var children = apps.Where(x => x.ParentAppId != null).ToLookup(x => x.ParentAppId!, StringComparer.Ordinal);
        var siblings = apps.Where(x => x.ApplicationId != null).ToLookup(x => x.ApplicationId!, StringComparer.Ordinal);
        foreach (var app in apps)
        {
            app.Deployments = deploymentsByApp[app.Id].ToList();
            app.Domains = domainsByApp[app.Id].ToList();
            app.Branches = children[app.Id].ToList();
            if (app.Application is not null)
                app.Application.Environments = siblings[app.Application.Id].ToList();
        }
        return apps;
    }
}
