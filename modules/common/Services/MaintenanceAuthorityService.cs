using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class MaintenanceException(string code, string message, bool refused) : Exception(message)
{
    public string Code { get; } = code;
    /// <summary>True for an authority decision (exit 3); false for invalid input or infrastructure errors.</summary>
    public bool Refused { get; } = refused;
}

public sealed record MaintenanceAuthorityStatus(
    string? InstallationId,
    long Epoch,
    string State,
    string? HolderOperationId,
    string? HostId,
    string? LocalHostId,
    bool LocalHostAuthorized,
    string? TransferOperationId,
    DateTimeOffset? FencedAt,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? TransferredAt,
    DateTimeOffset UpdatedAt);

public sealed record MaintenanceWorkCategory(string Category, string State, int Count, IReadOnlyList<string> Ids, bool BlocksDrain);

public sealed record MaintenanceParticipantStatus(string Id, string Role, string? HostId, long Epoch, bool Current,
    DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt);

public sealed record MaintenanceDrainStatus(
    MaintenanceAuthorityStatus Authority,
    string Phase,
    bool Drained,
    int BlockingCount,
    IReadOnlyList<MaintenanceWorkCategory> Work,
    IReadOnlyList<MaintenanceParticipantStatus> Participants,
    DateTimeOffset ObservedAt);

/// <summary>
/// Operator-side maintenance authority: fence, release, transfer and drain status. All mutations
/// lock the singleton authority row so they are ordered against API admissions and worker claims.
/// </summary>
public sealed class MaintenanceAuthorityService(MinicloudDbContext db, TimeProvider clock, string? localHostId)
{
    public const int MaxListedIds = 50;
    private static readonly TimeSpan ParticipantStaleAfter = TimeSpan.FromMinutes(5);

    public string? LocalHostId { get; } = localHostId is null || MaintenanceAuthorityOptions.IsValidId(localHostId)
        ? localHostId
        : throw new ArgumentException("Invalid maintenance host id.", nameof(localHostId));

    public async Task<MaintenanceAuthorityStatus> StatusAsync(string installationId, CancellationToken ct = default)
    {
        Validate(installationId, "installationId");
        var record = await MaintenanceAuthorityStore.ReadAsync(db, MaintenanceAuthorityLock.None, ct) ?? throw Unavailable();
        await EnsureInstallationAsync(record, installationId, ct);
        return Describe(record);
    }

    public Task<MaintenanceAuthorityStatus> FenceAsync(string installationId, string operationId, CancellationToken ct = default) =>
        MutateAsync(installationId, operationId, record =>
        {
            EnsureLocalHostAuthorized(record);
            if (record.State == MaintenanceAuthorityStates.Fenced)
            {
                if (record.HolderOperationId == operationId) return false;
                throw Refused("maintenance_fence_held", "Another maintenance operation holds the fence.");
            }
            if (record.HolderOperationId == operationId)
                throw Refused("maintenance_operation_completed", "This maintenance operation already released its fence; use a new operation id.");
            var now = clock.GetUtcNow();
            record.State = MaintenanceAuthorityStates.Fenced;
            record.HolderOperationId = operationId;
            record.FencedAt = now;
            record.ReleasedAt = null;
            return true;
        }, ct);

    public Task<MaintenanceAuthorityStatus> ReleaseAsync(string installationId, string operationId, CancellationToken ct = default) =>
        MutateAsync(installationId, operationId, record =>
        {
            EnsureLocalHostAuthorized(record);
            if (record.HolderOperationId != operationId)
                throw Refused("maintenance_holder_mismatch", "Only the maintenance operation that holds the fence can release it.");
            if (record.State == MaintenanceAuthorityStates.Open) return false;
            record.State = MaintenanceAuthorityStates.Open;
            record.ReleasedAt = clock.GetUtcNow();
            return true;
        }, ct);

    /// <summary>
    /// Binds installation authority to <paramref name="hostId"/> under a new epoch. Every process
    /// that registered an older epoch or runs with a different host id is refused from then on.
    /// The authority stays fenced by this operation until the same operation releases it.
    /// </summary>
    public Task<MaintenanceAuthorityStatus> TransferAsync(string installationId, string operationId, string? hostId, CancellationToken ct = default)
    {
        var target = hostId ?? LocalHostId ?? throw Invalid("hostId is required (or configure Maintenance:HostId on this host).");
        Validate(target, "hostId");
        if (LocalHostId is not null && LocalHostId != target)
            throw Refused("maintenance_host_mismatch", "Transfer must name this host's configured Maintenance:HostId.");
        return MutateAsync(installationId, operationId, record =>
        {
            if (record.TransferOperationId == operationId)
            {
                if (record.HostId == target) return false;
                throw Refused("maintenance_operation_conflict", "This transfer operation id was already used for a different host.");
            }
            if (record.HolderOperationId == operationId)
                throw Refused("maintenance_operation_conflict", "This operation id was already used for a fence; use a new operation id.");
            var now = clock.GetUtcNow();
            record.Epoch++;
            record.HostId = target;
            record.State = MaintenanceAuthorityStates.Fenced;
            record.HolderOperationId = operationId;
            record.TransferOperationId = operationId;
            record.TransferredAt = now;
            record.FencedAt = now;
            record.ReleasedAt = null;
            return true;
        }, ct);
    }

    public async Task<MaintenanceDrainStatus> DrainStatusAsync(string installationId, CancellationToken ct = default)
    {
        Validate(installationId, "installationId");
        var record = await MaintenanceAuthorityStore.ReadAsync(db, MaintenanceAuthorityLock.None, ct) ?? throw Unavailable();
        await EnsureInstallationAsync(record, installationId, ct);
        var now = clock.GetUtcNow();
        var work = new List<MaintenanceWorkCategory>();

        var deployments = await db.Deployments.AsNoTracking()
            .Where(x => x.OwnershipToken != null || x.Status == DeploymentStatus.Queued || x.Status == DeploymentStatus.WaitingForBuild ||
                x.Status == DeploymentStatus.Dispatching || x.Status == DeploymentStatus.Provisioning ||
                x.Status == DeploymentStatus.Deploying || x.Status == DeploymentStatus.Verifying)
            .Select(x => new { x.Id, x.Status, x.OwnershipToken })
            .ToListAsync(ct);
        static bool Terminal(DeploymentStatus status) => status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled;
        Add(work, "deployments", "queued", deployments.Where(x => x.Status == DeploymentStatus.Queued).Select(x => x.Id), true);
        Add(work, "deployments", "waiting_for_build", deployments.Where(x => x.Status == DeploymentStatus.WaitingForBuild).Select(x => x.Id), true);
        Add(work, "deployments", "running", deployments.Where(x => !Terminal(x.Status) && x.Status is not (DeploymentStatus.Queued or DeploymentStatus.WaitingForBuild)).Select(x => x.Id), true);
        // A terminal deployment that still holds ownership has an unconfirmed remote outcome.
        Add(work, "deployments", "uncertain", deployments.Where(x => Terminal(x.Status) && x.OwnershipToken != null).Select(x => x.Id), true);

        var operations = await db.DatabaseOperations.AsNoTracking()
            .Where(x => x.Status == "queued" || x.Status == "running" || x.Status == "cancelling" || x.Status == "failed")
            .Select(x => new { x.Id, x.Status, x.AttemptCount, x.LeaseExpiresAt, x.ErrorCode, x.DatabaseId, x.ServerId, x.UpdatedAt })
            .ToListAsync(ct);
        var completedTargets = await db.DatabaseOperations.AsNoTracking()
            .Where(x => x.Status == "completed")
            .Select(x => new { x.DatabaseId, x.ServerId, x.UpdatedAt })
            .ToListAsync(ct);
        Add(work, "database_operations", "queued", operations.Where(x => x.Status == "queued" && x.AttemptCount == 0).Select(x => x.Id), true);
        Add(work, "database_operations", "retrying", operations.Where(x => x.Status == "queued" && x.AttemptCount > 0).Select(x => x.Id), true);
        Add(work, "database_operations", "leased", operations.Where(x => x.Status == "running" && x.LeaseExpiresAt > now).Select(x => x.Id), true);
        Add(work, "database_operations", "cancelling", operations.Where(x => x.Status == "cancelling" && x.LeaseExpiresAt > now).Select(x => x.Id), true);
        Add(work, "database_operations", "uncertain", operations.Where(x =>
            ((x.Status is "running" or "cancelling") && (x.LeaseExpiresAt is null || x.LeaseExpiresAt <= now)) ||
            (x.Status == "failed" && x.ErrorCode is not null && x.ErrorCode.EndsWith("_reconciliation_required", StringComparison.Ordinal) &&
             !completedTargets.Any(c => c.DatabaseId == x.DatabaseId && c.ServerId == x.ServerId && c.UpdatedAt > x.UpdatedAt)))
            .Select(x => x.Id), true);

        var deletions = await db.Apps.AsNoTracking()
            .Where(x => x.DeletionStatus == "queued" || x.DeletionStatus == "preparing")
            .Select(x => new { x.Id, x.DeletionStatus }).ToListAsync(ct);
        Add(work, "app_deletions", "queued", deletions.Where(x => x.DeletionStatus == "queued").Select(x => x.Id), true);
        Add(work, "app_deletions", "preparing", deletions.Where(x => x.DeletionStatus == "preparing").Select(x => x.Id), true);

        var publications = await db.ImagePublications.AsNoTracking()
            .Where(x => x.Status == ImagePublicationStatus.Pending || x.Status == ImagePublicationStatus.Uploading)
            .Select(x => new { x.Id, x.ExpiresAt }).ToListAsync(ct);
        Add(work, "image_publications", "uploading", publications.Where(x => x.ExpiresAt > now).Select(x => x.Id), true);

        var activities = await db.MaintenanceActivities.AsNoTracking()
            .Select(x => new { x.Id, x.ExpiresAt }).ToListAsync(ct);
        Add(work, "admitted_activities", "active", activities.Where(x => x.ExpiresAt > now).Select(x => x.Id), true);
        Add(work, "admitted_activities", "uncertain", activities.Where(x => x.ExpiresAt <= now).Select(x => x.Id), true);

        Add(work, "managed_dns", "pending", await db.Domains.AsNoTracking()
            .Where(d => d.Kind == DomainBindingService.KindMinicloudSubdomain && d.Status != DomainBindingService.StatusDisabled &&
                d.App.DeletionStatus == null && (d.TargetIp == null || d.DnsStatus != DomainBindingService.DnsVerified))
            .Select(x => x.Id).ToListAsync(ct), false);
        Add(work, "custom_domain_verification", "pending", await db.CustomDomainRegistrations.AsNoTracking()
            .Where(x => x.Status == "pending").Select(x => x.Id).ToListAsync(ct), false);
        Add(work, "provider_credential_cleanup", "pending", await db.ProviderCredentialCleanups.AsNoTracking()
            .Select(x => x.Id).ToListAsync(ct), false);
        Add(work, "telemetry_outbox", "pending", (await db.TelemetryAuthorityOutboxes.AsNoTracking()
            .Where(x => x.ProcessedAt == null).Select(x => x.Sequence).ToListAsync(ct)).Select(x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)), false);

        var participants = (await db.MaintenanceParticipants.AsNoTracking().ToListAsync(ct))
            .Where(x => x.LastSeenAt > now - ParticipantStaleAfter)
            .OrderBy(x => x.Role).ThenBy(x => x.RegisteredAt)
            .Select(x => new MaintenanceParticipantStatus(x.Id, x.Role, x.HostId, x.Epoch,
                x.Epoch == record.Epoch && (record.HostId is null || record.HostId == x.HostId), x.RegisteredAt, x.LastSeenAt))
            .ToList();

        var blocking = work.Where(x => x.BlocksDrain).Sum(x => x.Count);
        var drained = blocking == 0;
        var phase = record.State == MaintenanceAuthorityStates.Open ? "open" : drained ? "drained" : "draining";
        return new(Describe(record), phase, drained, blocking, work, participants, now);
    }

    private static void Add(List<MaintenanceWorkCategory> work, string category, string state, IEnumerable<string> ids, bool blocksDrain)
    {
        var all = ids.Order(StringComparer.Ordinal).ToList();
        if (all.Count == 0) return;
        work.Add(new(category, state, all.Count, all.Take(MaxListedIds).ToList(), blocksDrain));
    }

    private async Task<MaintenanceAuthorityStatus> MutateAsync(string installationId, string operationId,
        Func<MaintenanceAuthorityRecord, bool> mutate, CancellationToken ct)
    {
        Validate(installationId, "installationId");
        Validate(operationId, "operationId");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        MaintenanceAuthorityRecord record;
        try
        {
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '15s'", ct);
            }
            record = await MaintenanceAuthorityStore.ReadAsync(db, MaintenanceAuthorityLock.Update, ct) ?? throw Unavailable();
        }
        catch (Exception ex) when (IsLockTimeout(ex))
        {
            throw Refused("maintenance_authority_busy", "The maintenance authority is locked by in-flight admissions; retry.");
        }
        await EnsureInstallationAsync(record, installationId, ct);
        if (mutate(record))
        {
            record.InstallationId ??= installationId;
            record.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Describe(record);
    }

    private static bool IsLockTimeout(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: "55P03" }) return true;
        }
        return false;
    }

    private async Task EnsureInstallationAsync(MaintenanceAuthorityRecord record, string installationId, CancellationToken ct)
    {
        var installations = await db.BootstrapRecords.AsNoTracking().Select(x => x.InstallationId).Take(2).ToListAsync(ct);
        if (installations.Count != 1 || installations[0] != installationId ||
            (record.InstallationId is not null && record.InstallationId != installationId))
        {
            throw Refused("installation_mismatch", "The installation id does not match this database.");
        }
    }

    private void EnsureLocalHostAuthorized(MaintenanceAuthorityRecord record)
    {
        if (record.HostId is not null && record.HostId != LocalHostId)
            throw Refused("maintenance_host_not_authorized", "Installation authority is bound to a different host; transfer it before maintenance.");
    }

    private MaintenanceAuthorityStatus Describe(MaintenanceAuthorityRecord record) => new(
        record.InstallationId, record.Epoch, record.State, record.HolderOperationId, record.HostId, LocalHostId,
        record.HostId is null || record.HostId == LocalHostId, record.TransferOperationId,
        record.FencedAt, record.ReleasedAt, record.TransferredAt, record.UpdatedAt);

    private static void Validate(string? value, string name)
    {
        if (!MaintenanceAuthorityOptions.IsValidId(value))
            throw Invalid($"{name} must be 1-128 characters of letters, digits, '.', '_', ':' or '-'.");
    }

    private static MaintenanceException Invalid(string message) => new("invalid_input", message, refused: false);
    private static MaintenanceException Refused(string code, string message) => new(code, message, refused: true);
    private static MaintenanceException Unavailable() =>
        new("maintenance_authority_unavailable", "The maintenance authority record is missing; run database migrations.", refused: false);
}
