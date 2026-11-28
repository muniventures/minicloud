using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Services;

namespace Selfrun.Worker.Power;

public sealed class EnvironmentIdleSweepProcessor(
    SelfrunDbContext db,
    IIdGenerator ids,
    TimeProvider clock,
    MaintenanceParticipant? maintenance = null)
{
    private readonly MaintenanceParticipant _maintenance = maintenance ?? MaintenanceParticipant.CreateLocal("worker", clock);

    private sealed record IdleCandidate(
        string EnvironmentId,
        int? SleepAfterMinutes,
        bool HttpTrafficIsActivity,
        DateTimeOffset? LastActivityAt,
        DateTimeOffset? LastHttpRequestAt,
        DateTimeOffset? KeepAwakeUntil);

    public async Task<int> SweepDueAsync(CancellationToken ct)
    {
        var candidates = await (
            from settings in db.EnvironmentSleepSettings.AsNoTracking()
            join app in db.Apps.AsNoTracking() on settings.EnvironmentId equals app.Id
            where settings.SleepAfterMinutes != null &&
                settings.PowerState == EnvironmentPowerStates.Awake &&
                app.DeletionStatus == null &&
                (app.EnvironmentKind == EnvironmentKinds.Persistent || app.EnvironmentKind == EnvironmentKinds.Preview)
            select new IdleCandidate(settings.EnvironmentId, settings.SleepAfterMinutes,
                settings.HttpTrafficIsActivity, settings.LastActivityAt, settings.LastHttpRequestAt, settings.KeepAwakeUntil)
        ).ToListAsync(ct);

        var queued = 0;
        foreach (var candidate in candidates)
        {
            if (!IsDue(candidate.SleepAfterMinutes, candidate.HttpTrafficIsActivity,
                    candidate.LastActivityAt, candidate.LastHttpRequestAt, candidate.KeepAwakeUntil, clock.GetUtcNow()))
            {
                continue;
            }

            if (await TryQueueSleepOperationAsync(candidate.EnvironmentId, ct))
            {
                queued++;
            }
        }

        return queued;
    }

    public async Task<bool> TryQueueSleepOperationAsync(string environmentId, CancellationToken ct)
    {
        var queued = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            if (!(await _maintenance.CheckAsync(db, requireOpen: true, ct)).IsAdmitted)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var app = db.Database.IsNpgsql()
                ? await db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {environmentId} FOR UPDATE").SingleOrDefaultAsync(ct)
                : await db.Apps.SingleOrDefaultAsync(x => x.Id == environmentId, ct);
            var settings = db.Database.IsNpgsql()
                ? await db.EnvironmentSleepSettings.FromSqlInterpolated(
                    $"SELECT * FROM environment_sleep_settings WHERE environment_id = {environmentId} FOR UPDATE").SingleOrDefaultAsync(ct)
                : await db.EnvironmentSleepSettings.SingleOrDefaultAsync(x => x.EnvironmentId == environmentId, ct);

            if (app is null || settings is null ||
                app.DeletionStatus is not null ||
                app.EnvironmentKind is not (EnvironmentKinds.Persistent or EnvironmentKinds.Preview) ||
                settings.PowerState != EnvironmentPowerStates.Awake ||
                !IsDue(settings.SleepAfterMinutes, settings.HttpTrafficIsActivity, settings.LastActivityAt,
                    settings.LastHttpRequestAt, settings.KeepAwakeUntil, clock.GetUtcNow()))
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            if (await HasConflictingWorkAsync(environmentId, ct))
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var now = clock.GetUtcNow();
            var id = ids.NewId("powerop");
            const string kind = "sleep";
            const string trigger = "idle";
            const string actorJson = "{}";
            const string status = EnvironmentPowerOperationStatuses.Queued;
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO environment_power_operations
                    (id, organization_id, environment_id, kind, trigger, actor_json, status, attempt_count, created_at, updated_at)
                VALUES
                    ({id}, {app.OrganizationId}, {environmentId}, {kind}, {trigger}, {actorJson}, {status}, 0, {now}, {now})
                ON CONFLICT (environment_id)
                    WHERE status NOT IN ('completed', 'failed', 'cancelled')
                DO NOTHING
                """, ct);

            await transaction.CommitAsync(ct);
            queued = inserted == 1;
        });

        return queued;
    }

    private async Task<bool> HasConflictingWorkAsync(string environmentId, CancellationToken ct)
    {
        var deploymentActive = await db.Deployments.AnyAsync(x =>
            x.AppId == environmentId &&
            (x.OwnershipToken != null ||
             (x.Status != DeploymentStatus.Succeeded &&
              x.Status != DeploymentStatus.Failed &&
              x.Status != DeploymentStatus.Canceled)), ct);
        if (deploymentActive)
        {
            return true;
        }

        var databaseOperationActive = await db.DatabaseOperations.AnyAsync(operation =>
            (operation.Status == "queued" || operation.Status == "running" || operation.Status == "cancelling") &&
            ((operation.DatabaseId != null && db.ManagedDatabases.Any(database =>
                database.Id == operation.DatabaseId &&
                (database.AppId == environmentId || database.RecoverySourceAppId == environmentId))) ||
             (operation.ServerId != null && db.ManagedDatabaseServers.Any(server =>
                server.Id == operation.ServerId && server.RuntimeAppId == environmentId))), ct);
        if (databaseOperationActive)
        {
            return true;
        }

        return await db.EnvironmentPowerOperations.AnyAsync(operation =>
            operation.EnvironmentId == environmentId &&
            (operation.Status == EnvironmentPowerOperationStatuses.Queued ||
             operation.Status == EnvironmentPowerOperationStatuses.Running ||
             operation.Status == EnvironmentPowerOperationStatuses.Cancelling), ct);
    }

    private static bool IsDue(
        int? sleepAfterMinutes,
        bool httpTrafficIsActivity,
        DateTimeOffset? lastActivityAt,
        DateTimeOffset? lastHttpRequestAt,
        DateTimeOffset? keepAwakeUntil,
        DateTimeOffset now)
    {
        if (sleepAfterMinutes is null)
        {
            return false;
        }

        var lastActivity = lastActivityAt;
        if (httpTrafficIsActivity && lastHttpRequestAt is { } httpActivity &&
            (lastActivity is null || httpActivity > lastActivity))
        {
            lastActivity = httpActivity;
        }

        if (lastActivity is null)
        {
            return false;
        }

        var dueAt = lastActivity.Value.AddMinutes(sleepAfterMinutes.Value);
        if (keepAwakeUntil is { } keepAwake && keepAwake > dueAt)
        {
            dueAt = keepAwake;
        }

        return now >= dueAt;
    }
}
