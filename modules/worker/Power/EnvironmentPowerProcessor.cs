using System.Data;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.Dns;
using Selfrun.Common.Providers.Extensibility;
using Selfrun.Common.Providers.Models;
using Selfrun.Common.Services;
using Selfrun.Worker.Ssh;

namespace Selfrun.Worker.Power;

public sealed class EnvironmentPowerOptions
{
    public const string SectionName = "Worker:EnvironmentPower";
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan SnapshotTimeout { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int SnapshotDeleteAttempts { get; set; } = 3;
}

public sealed class EnvironmentPowerProcessor(
    SelfrunDbContext db,
    ProviderAccountClientFactory providerClients,
    ICloudflareDnsClient dns,
    ISshDeploymentClient ssh,
    IOptions<SshDeploymentOptions> sshOptions,
    IOptions<ManagedDomainOptions> managedDomains,
    IOptions<EnvironmentPowerOptions> powerOptions,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<EnvironmentPowerProcessor>? logger = null,
    MaintenanceParticipant? maintenance = null,
    ILoggerFactory? loggerFactory = null)
{
    private const string Quiesce = "quiesce";
    private const string PowerOff = "power_off";
    private const string Snapshot = "snapshot";
    private const string PurgeSnapshot = "purge_snapshot";
    private const string RemoveDns = "remove_dns";
    private const string DeleteServer = "delete_server";
    private const string Finish = "finish";
    private const string Rollback = "rollback";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly EnvironmentPowerOptions options = powerOptions.Value;
    private readonly MaintenanceParticipant authority = maintenance ?? MaintenanceParticipant.CreateLocal("worker", clock);

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var claim = await ClaimNextAsync(cancellationToken);
        if (claim is null)
            return false;

        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var lease = new EnvironmentPowerOperationLease(
            scopes, clock, claim.Id, claim.OwnerToken, options.LeaseDuration, options.HeartbeatInterval,
            loggerFactory?.CreateLogger<EnvironmentPowerOperationLease>());
        using var leaseRegistration = lease.Lost.Register(ownerCancellation.Cancel);

        try
        {
            await ExecuteAsync(claim, ownerCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (lease.Lost.IsCancellationRequested)
        {
            logger?.LogWarning("Sleep operation {OperationId} stopped after its lease was fenced.", claim.Id);
        }
        catch (UnauthorizedAccessException)
        {
            logger?.LogWarning("Sleep operation {OperationId} stopped after ownership changed.", claim.Id);
        }
        catch (Exception error)
        {
            await HandleFailureAsync(claim, error, cancellationToken);
        }
        return true;
    }

    public async Task ReconcileSnapshotsAsync(CancellationToken cancellationToken)
    {
        var query = db.EnvironmentSnapshots.AsNoTracking()
            .Where(x => x.Status == EnvironmentSnapshotStatuses.Superseded ||
                x.Status == EnvironmentSnapshotStatuses.Deleting ||
                x.Status == EnvironmentSnapshotStatuses.Failed);
        var candidates = db.Database.IsNpgsql()
            ? await query.OrderBy(x => x.CreatedAt).Take(25).ToListAsync(cancellationToken)
            : (await query.ToListAsync(cancellationToken))
                .OrderBy(x => x.CreatedAt)
                .Take(25)
                .ToList();

        foreach (var snapshot in candidates)
        {
            try
            {
                await ReconcileSnapshotAsync(snapshot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                throw;
            }
            catch (Exception error)
            {
                logger?.LogWarning("Snapshot cleanup for {SnapshotId} will retry ({FailureType}).",
                    snapshot.Id, error.GetType().Name);
            }
        }
    }

    private async Task<ClaimedOperation?> ClaimNextAsync(CancellationToken ct)
    {
        ClaimedOperation? claimed = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            if (!await authority.CanClaimAsync(db, ct))
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var now = clock.GetUtcNow();
            EnvironmentPowerOperation? operation;
            if (db.Database.IsNpgsql())
            {
                operation = await db.EnvironmentPowerOperations.FromSqlInterpolated($"""
                    SELECT power.* FROM environment_power_operations power
                    WHERE power.kind = 'sleep'
                      AND (
                        power.status = 'queued'
                        OR (power.status IN ('running', 'cancelling')
                            AND (power.lease_expires_at IS NULL OR power.lease_expires_at <= {now}))
                      )
                      AND (
                        power.step NOT IN ('quiesce', 'rollback')
                        AND power.step IS NOT NULL
                        OR NOT EXISTS (
                        SELECT 1 FROM deployments deployment
                        WHERE deployment.app_id = power.environment_id
                          AND (deployment.ownership_token IS NOT NULL
                            OR lower(deployment.status::text) IN
                              ('queued', 'waitingforbuild', 'waiting_for_build', 'dispatching',
                               'provisioning', 'deploying', 'verifying'))
                      )
                      AND NOT EXISTS (
                        SELECT 1 FROM database_operations operation
                        WHERE operation.status IN ('queued', 'running', 'cancelling')
                          AND (
                            EXISTS (SELECT 1 FROM managed_database_servers server
                                    WHERE server.id = operation.server_id
                                      AND server.runtime_app_id = power.environment_id)
                            OR EXISTS (SELECT 1 FROM managed_databases database
                                       WHERE database.id = operation.database_id
                                         AND (database.app_id = power.environment_id
                                           OR database.recovery_source_app_id = power.environment_id))
                            OR EXISTS (SELECT 1 FROM database_backup_policies policy
                                       WHERE policy.id = operation.backup_policy_id
                                         AND policy.repository_app_id = power.environment_id)
                          ))
                      )
                    ORDER BY power.updated_at, power.created_at
                    FOR UPDATE OF power SKIP LOCKED
                    LIMIT 1
                    """).FirstOrDefaultAsync(ct);
            }
            else
            {
                var candidates = (await db.EnvironmentPowerOperations
                    .Where(x => x.Kind == "sleep" &&
                        (x.Status == EnvironmentPowerOperationStatuses.Queued ||
                         x.Status == EnvironmentPowerOperationStatuses.Running ||
                         x.Status == EnvironmentPowerOperationStatuses.Cancelling))
                    .ToListAsync(ct))
                    .Where(x => x.Status == EnvironmentPowerOperationStatuses.Queued ||
                        x.LeaseExpiresAt is null || x.LeaseExpiresAt <= now)
                    .OrderBy(x => x.UpdatedAt)
                    .ThenBy(x => x.CreatedAt);
                operation = null;
                foreach (var candidate in candidates)
                {
                    var candidatePreCommit = candidate.Step is null or Quiesce or Rollback;
                    if (!candidatePreCommit || !await HasConflictingWorkAsync(candidate.EnvironmentId, ct))
                    {
                        operation = candidate;
                        break;
                    }
                }
            }

            if (operation is null)
            {
                await transaction.RollbackAsync(ct);
                return;
            }

            var app = db.Database.IsNpgsql()
                ? await db.Apps.FromSqlInterpolated(
                    $"SELECT * FROM apps WHERE id = {operation.EnvironmentId} AND organization_id = {operation.OrganizationId} FOR UPDATE")
                    .SingleOrDefaultAsync(ct)
                : await db.Apps.SingleOrDefaultAsync(x =>
                    x.Id == operation.EnvironmentId && x.OrganizationId == operation.OrganizationId, ct);
            var settings = await db.EnvironmentSleepSettings.SingleOrDefaultAsync(
                x => x.EnvironmentId == operation.EnvironmentId, ct);
            var preCommit = operation.Step is null or Quiesce;
            var applicationDeleting = app?.ApplicationId is { } applicationId &&
                await db.Applications.AnyAsync(x => x.Id == applicationId && x.DeletionStatus != null, ct);
            var invalid = app is null || settings is null ||
                (preCommit && (app.DeletionStatus is not null || applicationDeleting ||
                    app.EnvironmentKind == EnvironmentKinds.Internal ||
                    settings.PowerState is not (EnvironmentPowerStates.Awake or EnvironmentPowerStates.Sleeping or EnvironmentPowerStates.SleepFailed) ||
                    !await IsEligibleAtClaimAsync(operation, settings, ct)));
            if (invalid && preCommit && app is not null && settings is not null)
            {
                var ineligibleAt = clock.GetUtcNow();
                var wakeCancelledQueuedSleep =
                    operation.Step is null &&
                    settings.PowerState == EnvironmentPowerStates.Awake &&
                    settings.KeepAwakeUntil is { } keepAwakeUntil &&
                    keepAwakeUntil > ineligibleAt;
                if (wakeCancelledQueuedSleep)
                {
                    operation.Status = EnvironmentPowerOperationStatuses.Cancelled;
                    operation.Step = "completed";
                    operation.ErrorCode = null;
                    operation.ErrorMessage = null;
                    operation.CompletedAt = ineligibleAt;
                    operation.OwnershipToken = null;
                    operation.LeaseExpiresAt = null;
                    operation.UpdatedAt = ineligibleAt;
                    settings.LastErrorCode = null;
                    settings.LastErrorMessage = null;
                    settings.StateChangedAt = ineligibleAt;
                    await authority.EnsureCommitAuthorityAsync(db, ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    return;
                }

                operation.Step = Rollback;
                operation.ErrorCode = operation.Status == EnvironmentPowerOperationStatuses.Cancelling
                    ? "environment_power_cancelled"
                    : "environment_sleep_cancelled";
                operation.ErrorMessage = "Sleep conditions changed before the provider power-off request.";
                operation.Status = operation.Status == EnvironmentPowerOperationStatuses.Cancelling
                    ? EnvironmentPowerOperationStatuses.Cancelling
                    : EnvironmentPowerOperationStatuses.Queued;
                operation.CompletedAt = null;
                operation.OwnershipToken = null;
                operation.LeaseExpiresAt = null;
                operation.UpdatedAt = ineligibleAt;
                settings.PowerState = EnvironmentPowerStates.SleepFailed;
                settings.LastErrorCode = operation.ErrorCode;
                settings.LastErrorMessage = operation.ErrorMessage;
                settings.StateChangedAt = ineligibleAt;
                await authority.EnsureCommitAuthorityAsync(db, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return;
            }
            if (invalid)
            {
                operation.Status = EnvironmentPowerOperationStatuses.Failed;
                operation.ErrorCode = "environment_sleep_ineligible";
                operation.ErrorMessage = "The environment is no longer eligible for sleep.";
                operation.CompletedAt = now;
                operation.OwnershipToken = null;
                operation.LeaseExpiresAt = null;
                operation.UpdatedAt = now;
                if (settings is not null)
                {
                    settings.PowerState = EnvironmentPowerStates.SleepFailed;
                    settings.LastErrorCode = operation.ErrorCode;
                    settings.LastErrorMessage = operation.ErrorMessage;
                    settings.StateChangedAt = now;
                }
                await authority.EnsureCommitAuthorityAsync(db, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return;
            }

            var claimedOperation = operation ??
                throw new InvalidOperationException("A sleep operation was required for the worker claim.");
            var claimedSettings = settings ??
                throw new InvalidOperationException("Sleep settings were required for the worker claim.");
            var ownerToken = Guid.NewGuid().ToString("N");
            claimedOperation.Status = claimedOperation.Status == EnvironmentPowerOperationStatuses.Cancelling
                ? EnvironmentPowerOperationStatuses.Cancelling
                : EnvironmentPowerOperationStatuses.Running;
            claimedOperation.OwnershipToken = ownerToken;
            claimedOperation.LeaseExpiresAt = now + options.LeaseDuration;
            claimedOperation.AttemptCount++;
            claimedOperation.Step ??= Quiesce;
            claimedOperation.UpdatedAt = now;
            claimedSettings.PowerState = EnvironmentPowerStates.Sleeping;
            claimedSettings.StateChangedAt = now;
            if (claimedOperation.Step != Rollback)
            {
                claimedSettings.LastErrorCode = null;
                claimedSettings.LastErrorMessage = null;
            }
            await authority.EnsureCommitAuthorityAsync(db, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            claimed = new ClaimedOperation(
                claimedOperation.Id, claimedOperation.OrganizationId, claimedOperation.EnvironmentId, ownerToken);
        });
        return claimed;
    }

    private async Task<bool> IsEligibleAtClaimAsync(
        EnvironmentPowerOperation operation, EnvironmentSleepSettings settings, CancellationToken ct)
    {
        if (settings.AddressMode is not (EnvironmentAddressModes.Dynamic or EnvironmentAddressModes.Retained))
            return false;
        if (settings.AddressMode == EnvironmentAddressModes.Retained &&
            !await db.EnvironmentRetainedAddresses.AnyAsync(x =>
                x.EnvironmentId == operation.EnvironmentId &&
                x.OrganizationId == operation.OrganizationId &&
                x.State == EnvironmentRetainedAddressStates.Assigned, ct))
            return false;

        var domains = await db.Domains.AsNoTracking().Where(x =>
            x.AppId == operation.EnvironmentId &&
            x.OrganizationId == operation.OrganizationId &&
            x.Status != DomainBindingService.StatusDisabled).ToListAsync(ct);
        if (settings.AddressMode == EnvironmentAddressModes.Dynamic &&
            (!managedDomains.Value.ManageDnsRecords ||
             domains.Any(x => x.Kind != DomainBindingService.KindSelfrunSubdomain ||
                 !managedDomains.Value.Contains(x.Hostname))))
            return false;

        if (!await HasConflictingWorkAsync(operation.EnvironmentId, ct))
        {
            var preCommit = operation.Step is null or Quiesce or PowerOff;
            return !preCommit ||
                (settings.KeepAwakeUntil is not { } keepAwake || keepAwake <= clock.GetUtcNow()) &&
                (operation.Trigger != "idle" || IsIdleDue(operation, settings));
        }
        return false;
    }

    private bool IsIdleDue(EnvironmentPowerOperation operation, EnvironmentSleepSettings settings)
    {
        if (settings.SleepAfterMinutes is not { } minutes)
            return false;
        var activityAt = settings.LastActivityAt ?? settings.StateChangedAt;
        if (settings.HttpTrafficIsActivity && settings.LastHttpRequestAt is { } httpAt && httpAt > activityAt)
            activityAt = httpAt;
        var dueAt = activityAt + TimeSpan.FromMinutes(minutes);
        if (settings.KeepAwakeUntil is { } keepAwake && keepAwake > dueAt)
            dueAt = keepAwake;
        return clock.GetUtcNow() >= dueAt;
    }

    private async Task<bool> HasConflictingWorkAsync(string appId, CancellationToken ct)
    {
        if (await db.Deployments.AnyAsync(x => x.AppId == appId &&
            (x.OwnershipToken != null ||
             x.Status == DeploymentStatus.Queued ||
             x.Status == DeploymentStatus.WaitingForBuild ||
             x.Status == DeploymentStatus.Dispatching ||
             x.Status == DeploymentStatus.Provisioning ||
             x.Status == DeploymentStatus.Deploying ||
             x.Status == DeploymentStatus.Verifying), ct))
            return true;

        return await db.DatabaseOperations.AnyAsync(operation =>
            (operation.Status == "queued" || operation.Status == "running" || operation.Status == "cancelling") &&
            ((operation.ServerId != null && db.ManagedDatabaseServers.Any(server =>
                server.Id == operation.ServerId && server.RuntimeAppId == appId)) ||
             (operation.DatabaseId != null && db.ManagedDatabases.Any(database =>
                database.Id == operation.DatabaseId &&
                (database.AppId == appId || database.RecoverySourceAppId == appId))) ||
             (operation.BackupPolicyId != null && db.DatabaseBackupPolicies.Any(policy =>
                policy.Id == operation.BackupPolicyId && policy.RepositoryAppId == appId))), ct);
    }

    private async Task ExecuteAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var state = await ReadOperationAsync(operation, ct);
        switch (state.Step)
        {
            case Quiesce:
                await QuiesceAsync(operation, ct);
                await SaveStepAsync(operation, PowerOff, ct);
                state = await ReadOperationAsync(operation, ct);
                if (state.Status == EnvironmentPowerOperationStatuses.Cancelling)
                {
                    await BeginRollbackAsync(operation, "environment_power_cancelled",
                        "Sleep was cancelled before the provider power-off request.", cancelled: true, ct);
                    await RollbackAsync(operation, ct);
                    return;
                }
                await CheckCommitEligibilityAsync(operation, ct);
                await SaveStepAsync(operation, PowerOff, ct);
                await PowerOffServerAsync(operation, ct);
                await SaveStepAsync(operation, Snapshot, ct);
                goto case Snapshot;

            case PowerOff:
                await PowerOffServerAsync(operation, ct);
                await SaveStepAsync(operation, Snapshot, ct);
                goto case Snapshot;

            case Snapshot:
                await CreateAndVerifySnapshotAsync(operation, ct);
                await SaveStepAsync(operation, PurgeSnapshot, ct);
                goto case PurgeSnapshot;

            case PurgeSnapshot:
                await PromoteAndPurgeSnapshotAsync(operation, ct);
                await SaveStepAsync(operation, RemoveDns, ct);
                goto case RemoveDns;

            case RemoveDns:
                await RemoveDnsOrPreserveAddressAsync(operation, ct);
                await SaveStepAsync(operation, DeleteServer, ct);
                goto case DeleteServer;

            case DeleteServer:
                await DeleteOwnedServerAsync(operation, ct);
                await SaveStepAsync(operation, Finish, ct);
                goto case Finish;

            case Finish:
                await FinishSleepAsync(operation, ct);
                return;

            case Rollback:
                await RollbackAsync(operation, ct);
                return;

            default:
                throw new InvalidOperationException("The sleep operation contains an unknown worker step.");
        }
    }

    private async Task QuiesceAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var context = await LoadEnvironmentAsync(operation, ct);
        await EnsureOwnedAsync(operation, ct);
        var result = await ssh.ExecuteCommandAsync(
            new SshTarget(context.Server.Id, Required(context.Server.IpAddress, "Server address is missing."),
                context.Server.HostKeyFingerprint),
            sshOptions.Value.Username,
            GetPrivateKey(),
            QuiesceCommand(context.App.Slug),
            (_, _) => Task.CompletedTask,
            ct);
        if (result != 0)
            throw new RemoteQuiescenceException("The environment services could not be stopped safely.");
    }

    private async Task CheckCommitEligibilityAsync(ClaimedOperation operation, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        db.ChangeTracker.Clear();
        var current = await db.EnvironmentPowerOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == operation.Id, ct);
        if (current is null || current.OwnershipToken != operation.OwnerToken ||
            current.Status == EnvironmentPowerOperationStatuses.Cancelling)
        {
            await BeginRollbackAsync(operation, "environment_power_cancelled",
                "Sleep was cancelled before the provider power-off request.", cancelled: true, ct);
            await RollbackAsync(operation, ct);
            throw new OperationCanceledException("Sleep was cancelled before provider power-off.", ct);
        }
        var settings = await db.EnvironmentSleepSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EnvironmentId == operation.EnvironmentId, ct);
        if (settings is null || settings.PowerState != EnvironmentPowerStates.Sleeping ||
            (current.Trigger == "idle" && !IsIdleDue(current, settings)))
        {
            await BeginRollbackAsync(operation, "environment_sleep_cancelled",
                "Sleep conditions changed before the provider power-off request.", cancelled: true, ct);
            await RollbackAsync(operation, ct);
            throw new OperationCanceledException("Sleep conditions changed before provider power-off.", ct);
        }
    }

    private async Task PowerOffServerAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var context = await LoadEnvironmentAsync(operation, ct);
        var client = context.Provider;
        await EnsureProviderServerOwnedAsync(context, ct);
        var status = await client.GetServerStatusAsync(Required(context.Server.ProviderServerId, "Server provider ID is missing."), ct);
        if (status.IsRunning)
            await context.Snapshots.PowerOffAsync(context.Server.ProviderServerId!, ct);

        status = await client.GetServerStatusAsync(context.Server.ProviderServerId!, ct);
        if (status.IsRunning)
            throw new InvalidOperationException("Provider did not confirm that the server is powered off.");
    }

    private async Task CreateAndVerifySnapshotAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var context = await LoadEnvironmentAsync(operation, ct);
        await EnsureProviderServerOwnedAsync(context, ct);
        var serverId = Required(context.Server.ProviderServerId, "Server provider ID is missing.");
        var serverStatus = await context.Provider.GetServerStatusAsync(serverId, ct);
        if (serverStatus.IsRunning)
            throw new InvalidOperationException("The server must be powered off before snapshot creation.");

        var labels = SnapshotLabels(operation);
        var row = await db.EnvironmentSnapshots.SingleOrDefaultAsync(x => x.Id == operation.Id, ct);
        if (row is null)
        {
            var placement = await BuildPlacementAsync(context, ct);
            row = new EnvironmentSnapshot
            {
                Id = operation.Id,
                EnvironmentId = operation.EnvironmentId,
                OrganizationId = operation.OrganizationId,
                Provider = context.Provider.Provider.ToString().ToLowerInvariant(),
                ProviderAccountId = context.Server.ProviderAccountId!,
                Region = context.Server.Region,
                Datacenter = context.Address?.Datacenter,
                Sku = context.Server.Sku ?? "unknown",
                ServerName = context.ServerName,
                AddressMode = context.Settings.AddressMode!,
                Status = EnvironmentSnapshotStatuses.Creating,
                PlacementJson = JsonSerializer.Serialize(placement, JsonOptions),
                CreatedAt = clock.GetUtcNow()
            };
            await AddSnapshotRowAsync(operation, row, ct);
        }
        else if (row.EnvironmentId != operation.EnvironmentId ||
            row.OrganizationId != operation.OrganizationId ||
            row.ProviderAccountId != context.Server.ProviderAccountId ||
            !string.Equals(row.Provider, context.Provider.Provider.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Snapshot ownership does not match the sleep operation.");
        }

        ProviderSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(row.ProviderImageId))
        {
            snapshot = await context.Snapshots.GetSnapshotAsync(row.ProviderImageId, ct);
            if (snapshot is null)
                throw new InvalidOperationException("The pending snapshot disappeared before verification.");
            VerifySnapshotOwnership(snapshot, labels);
        }
        else
        {
            var discovered = await context.Snapshots.ListSnapshotsAsync(labels, ct);
            if (discovered.Count > 1)
                throw new InvalidOperationException("Multiple provider snapshots match this sleep operation.");
            if (discovered.Count == 1)
            {
                snapshot = discovered[0];
                VerifySnapshotOwnership(snapshot, labels);
                await SaveSnapshotIdentityAsync(operation, snapshot, ct);
            }
            else
            {
                await EnsureOwnedAsync(operation, ct);
                snapshot = await context.Snapshots.CreateSnapshotAsync(serverId,
                    $"Selfrun {context.Organization.Slug}/{context.App.Name} ({context.App.Slug})", labels, ct);
                VerifySnapshotOwnership(snapshot, labels);
                await SaveSnapshotIdentityAsync(operation, snapshot, ct);
            }
        }

        var verified = await WaitForAvailableSnapshotAsync(context.Snapshots, snapshot!.Id, labels, ct);
        await EnsureOwnedAsync(operation, ct);
        db.ChangeTracker.Clear();
        row = await db.EnvironmentSnapshots.SingleAsync(x => x.Id == operation.Id, ct);
        row.ProviderImageId = verified.Id;
        row.SizeGb = verified.SizeGb is { } size ? (int)Math.Ceiling(size) : null;
        row.CreatedAt = verified.CreatedAt;
        row.Status = EnvironmentSnapshotStatuses.Pending;
        await SaveOwnedAsync(operation, ct);
    }

    private async Task<object> BuildPlacementAsync(EnvironmentContext context, CancellationToken ct)
    {
        var volumes = await db.AppStorageVolumes.AsNoTracking()
            .Where(x => x.AppId == context.App.Id)
            .Select(x => new { x.Id, x.Provider, x.ProviderAccountId, x.ProviderVolumeId })
            .ToListAsync(ct);
        if (volumes.Any(x => x.ProviderVolumeId is not null &&
            (x.ProviderAccountId != context.Server.ProviderAccountId ||
             !string.Equals(x.Provider, context.Server.Provider, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Durable volume ownership does not match the environment server.");

        var deployments = db.Deployments.AsNoTracking()
            .Where(x => x.AppId == context.App.Id && x.Status == DeploymentStatus.Succeeded);
        var sshReferences = db.Database.IsNpgsql()
            ? await deployments.OrderByDescending(x => x.CreatedAt)
                .Select(x => x.SshProviderReferencesJson)
                .FirstOrDefaultAsync(ct)
            : (await deployments.Select(x => new { x.CreatedAt, x.SshProviderReferencesJson })
                .ToListAsync(ct))
                .OrderByDescending(x => x.CreatedAt)
                .Select(x => x.SshProviderReferencesJson)
                .FirstOrDefault();
        return new
        {
            volumeIds = volumes.Where(x => x.ProviderVolumeId is not null).Select(x => x.ProviderVolumeId).ToArray(),
            sshKeyReferences = ParseSshReferences(sshReferences)
        };
    }

    private static IReadOnlyList<string> ParseSshReferences(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Saved deployment SSH key references are malformed.", error);
        }
    }

    private async Task AddSnapshotRowAsync(ClaimedOperation operation, EnvironmentSnapshot row, CancellationToken ct)
    {
        db.EnvironmentSnapshots.Add(row);
        await SaveOwnedAsync(operation, ct);
    }

    private async Task SaveSnapshotIdentityAsync(
        ClaimedOperation operation, ProviderSnapshot snapshot, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        db.ChangeTracker.Clear();
        var row = await db.EnvironmentSnapshots.SingleAsync(x => x.Id == operation.Id, ct);
        row.ProviderImageId = snapshot.Id;
        row.SizeGb = snapshot.SizeGb is { } size ? (int)Math.Ceiling(size) : null;
        row.CreatedAt = snapshot.CreatedAt;
        row.Status = EnvironmentSnapshotStatuses.Pending;
        await SaveOwnedAsync(operation, ct);
    }

    private async Task<ProviderSnapshot> WaitForAvailableSnapshotAsync(
        IVpsProviderSnapshotClient snapshots,
        string snapshotId,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken ct)
    {
        var deadline = clock.GetUtcNow() + options.SnapshotTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await snapshots.GetSnapshotAsync(snapshotId, ct)
                ?? throw new InvalidOperationException("Provider snapshot was not found during verification.");
            VerifySnapshotOwnership(snapshot, labels);
            if (snapshot.IsAvailable)
                return snapshot;
            if (clock.GetUtcNow() >= deadline)
                throw new TimeoutException("Provider snapshot did not become available before the worker deadline.");
            await Task.Delay(options.PollInterval, clock, ct);
        }
    }

    private static void VerifySnapshotOwnership(
        ProviderSnapshot snapshot, IReadOnlyDictionary<string, string> labels)
    {
        foreach (var (key, value) in labels)
        {
            if (!snapshot.Labels.TryGetValue(key, out var actual) ||
                !string.Equals(actual, value, StringComparison.Ordinal))
                throw new InvalidOperationException("Provider snapshot ownership could not be verified.");
        }
    }

    private static IReadOnlyDictionary<string, string> SnapshotLabels(ClaimedOperation operation) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["managed-by"] = "selfrun",
            ["organization-id"] = operation.OrganizationId,
            ["environment-id"] = operation.EnvironmentId,
            ["operation-id"] = operation.Id
        };

    private async Task PromoteAndPurgeSnapshotAsync(ClaimedOperation operation, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        db.ChangeTracker.Clear();
        var current = await db.EnvironmentSnapshots.SingleOrDefaultAsync(x => x.Id == operation.Id, ct)
            ?? throw new InvalidOperationException("Verified snapshot record is missing.");
        if (current.Status is not (EnvironmentSnapshotStatuses.Current or EnvironmentSnapshotStatuses.Pending) ||
            string.IsNullOrWhiteSpace(current.ProviderImageId))
            throw new InvalidOperationException("A verified snapshot is required before replacing the current image.");

        var previous = await db.EnvironmentSnapshots.Where(x =>
            x.EnvironmentId == operation.EnvironmentId &&
            x.Status == EnvironmentSnapshotStatuses.Current &&
            x.Id != operation.Id).ToListAsync(ct);
        foreach (var old in previous)
            old.Status = EnvironmentSnapshotStatuses.Superseded;
        current.Status = EnvironmentSnapshotStatuses.Current;
        await SaveOwnedAsync(operation, ct);

        var stale = await db.EnvironmentSnapshots.AsNoTracking().Where(x =>
                x.EnvironmentId == operation.EnvironmentId &&
                x.Id != operation.Id &&
                (x.Status == EnvironmentSnapshotStatuses.Superseded ||
                 x.Status == EnvironmentSnapshotStatuses.Deleting))
            .Select(x => x.Id).ToListAsync(ct);
        foreach (var oldId in stale)
            await DeleteSupersededSnapshotAsync(operation, oldId, ct);
    }

    private async Task DeleteSupersededSnapshotAsync(
        ClaimedOperation operation, string snapshotRowId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var row = await db.EnvironmentSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == snapshotRowId, ct);
        if (row is null || row.Status == EnvironmentSnapshotStatuses.Deleted)
            return;
        if (string.IsNullOrWhiteSpace(row.ProviderImageId))
        {
            await MarkSnapshotDeletedAsync(operation, row.Id, ct);
            return;
        }
        if (row.Status != EnvironmentSnapshotStatuses.Deleting)
        {
            await EnsureOwnedAsync(operation, ct);
            db.ChangeTracker.Clear();
            var tracked = await db.EnvironmentSnapshots.SingleAsync(x => x.Id == snapshotRowId, ct);
            tracked.Status = EnvironmentSnapshotStatuses.Deleting;
            await SaveOwnedAsync(operation, ct);
            row = tracked;
        }

        var context = await LoadEnvironmentAsync(operation, ct);
        for (var attempt = 1; attempt <= options.SnapshotDeleteAttempts; attempt++)
        {
            try
            {
                await EnsureOwnedAsync(operation, ct);
                var oldClient = row.ProviderAccountId == context.Server.ProviderAccountId
                    ? context.Snapshots
                    : await GetSnapshotClientAsync(row.OrganizationId, row.ProviderAccountId, row.Provider, ct);
                var snapshot = await oldClient.GetSnapshotAsync(
                    Required(row.ProviderImageId, "Superseded snapshot has no provider image ID."), ct);
                if (snapshot is not null)
                {
                    VerifySnapshotOwnership(snapshot, SnapshotLabels(
                        new ClaimedOperation(row.Id, row.OrganizationId, row.EnvironmentId, operation.OwnerToken)));
                    await oldClient.DeleteSnapshotAsync(snapshot.Id, ct);
                }
                await MarkSnapshotDeletedAsync(operation, row.Id, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception error)
            {
                if (attempt == options.SnapshotDeleteAttempts)
                {
                    logger?.LogWarning("Prior snapshot {SnapshotId} remains queued for cleanup ({FailureType}).",
                        row.Id, error.GetType().Name);
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), clock, ct);
            }
        }
    }

    private async Task RemoveDnsOrPreserveAddressAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var context = await LoadEnvironmentAsync(operation, ct);
        if (context.Settings.AddressMode == EnvironmentAddressModes.Retained)
        {
            var address = context.Address
                ?? throw new InvalidOperationException("The retained address is missing.");
            var addressClient = context.AddressProvider
                ?? throw new InvalidOperationException("The provider does not support retained addresses.");
            var current = await addressClient.GetRetainedAddressAsync(address.ProviderAddressId, ct);
            if (current is null || current.Id != address.ProviderAddressId ||
                current.Ipv4Address != address.Ipv4)
                throw new InvalidOperationException("The retained address could not be verified before server deletion.");
            await EnsureOwnedAsync(operation, ct);
            await addressClient.EnsureSurvivesServerDeletionAsync(address.ProviderAddressId, ct);
            return;
        }

        if (context.Settings.AddressMode != EnvironmentAddressModes.Dynamic ||
            !managedDomains.Value.ManageDnsRecords)
            throw new InvalidOperationException("Managed DNS removal is unavailable for this address mode.");

        var domains = await db.Domains.AsNoTracking().Where(x =>
            x.AppId == operation.EnvironmentId &&
            x.OrganizationId == operation.OrganizationId &&
            x.Status != DomainBindingService.StatusDisabled).ToListAsync(ct);
        foreach (var domain in domains)
        {
            if (domain.Kind != DomainBindingService.KindSelfrunSubdomain ||
                !managedDomains.Value.Contains(domain.Hostname))
                throw new InvalidOperationException("Dynamic address mode contains a hostname not managed by Selfrun.");
            await EnsureOwnedAsync(operation, ct);
            await dns.DeleteDnsRecordsAsync(domain.Hostname, ct);
        }
    }

    private async Task DeleteOwnedServerAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var context = await LoadEnvironmentAsync(operation, ct);
        var serverId = Required(context.Server.ProviderServerId, "Server provider ID is missing.");
        await EnsureNoSharedServerRegistrationAsync(context.Server, ct);
        var discovered = await context.Provider.FindServerAsync(context.ServerName, ct);
        if (discovered is not null)
        {
            VerifyServerOwnership(context, discovered);
            if (discovered.Id != serverId)
                throw new InvalidOperationException("A different provider server now owns the environment name.");
            await EnsureOwnedAsync(operation, ct);
            await context.Provider.DeleteServerAsync(serverId, ct);
        }

        var deadline = clock.GetUtcNow() + TimeSpan.FromMinutes(5);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var found = await context.Provider.FindServerAsync(context.ServerName, ct);
            if (found is null)
                break;
            VerifyServerOwnership(context, found);
            if (found.Id != serverId)
                throw new InvalidOperationException("A different provider server now owns the environment name.");
            if (clock.GetUtcNow() >= deadline)
                throw new TimeoutException("Provider server deletion was not confirmed.");
            await Task.Delay(options.PollInterval, clock, ct);
        }

        await EnsureOwnedAsync(operation, ct);
        if (context.Address is not null)
        {
            var address = await context.AddressProvider!.GetRetainedAddressAsync(
                context.Address.ProviderAddressId, ct);
            if (address is null || address.Id != context.Address.ProviderAddressId ||
                address.Ipv4Address != context.Address.Ipv4)
                throw new InvalidOperationException("The retained address did not survive server deletion.");
            db.ChangeTracker.Clear();
            var retained = await db.EnvironmentRetainedAddresses.SingleAsync(x =>
                x.Id == context.Address.Id && x.EnvironmentId == operation.EnvironmentId, ct);
            retained.State = EnvironmentRetainedAddressStates.Unassigned;
            await SaveOwnedAsync(operation, ct);
        }
    }

    private async Task FinishSleepAsync(ClaimedOperation operation, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var settings = await db.EnvironmentSleepSettings.SingleAsync(
            x => x.EnvironmentId == operation.EnvironmentId, ct);
        var server = await db.ServerInstances.SingleAsync(x =>
            x.AppId == operation.EnvironmentId && x.OrganizationId == operation.OrganizationId &&
            x.Status != ServerInstance.RetiredStatus, ct);
        var runtime = await db.RuntimeServers.SingleOrDefaultAsync(x =>
            x.AppId == operation.EnvironmentId && x.OrganizationId == operation.OrganizationId, ct);
        var current = await db.EnvironmentSnapshots.SingleOrDefaultAsync(x =>
            x.EnvironmentId == operation.EnvironmentId && x.Status == EnvironmentSnapshotStatuses.Current, ct);
        if (current is null || string.IsNullOrWhiteSpace(current.ProviderImageId))
            throw new InvalidOperationException("Sleep cannot finish without a current verified snapshot.");

        var now = clock.GetUtcNow();
        server.Status = ServerInstance.RetiredStatus;
        server.UpdatedAt = now;
        if (runtime is not null)
        {
            runtime.ProviderServerId = null;
            runtime.UpdatedAt = now;
        }
        settings.PowerState = EnvironmentPowerStates.Asleep;
        settings.StateChangedAt = now;
        settings.LastErrorCode = null;
        settings.LastErrorMessage = null;

        var operationRow = await GetOwnedOperationForUpdateAsync(operation, ct);
        operationRow.Status = EnvironmentPowerOperationStatuses.Completed;
        operationRow.Step = "completed";
        operationRow.ErrorCode = null;
        operationRow.ErrorMessage = null;
        operationRow.CompletedAt = now;
        operationRow.OwnershipToken = null;
        operationRow.LeaseExpiresAt = null;
        operationRow.UpdatedAt = now;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            OrganizationId = operation.OrganizationId,
            AppId = operation.EnvironmentId,
            Actor = "worker",
            Action = "environment.slept",
            Details = operation.Id,
            CreatedAt = now
        });
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task RollbackAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var context = await LoadEnvironmentAsync(operation, ct);
        var serverId = Required(context.Server.ProviderServerId, "Server provider ID is missing.");
        await EnsureProviderServerOwnedAsync(context, ct);
        var status = await context.Provider.GetServerStatusAsync(serverId, ct);
        if (!status.IsRunning)
        {
            await EnsureOwnedAsync(operation, ct);
            await context.Snapshots.PowerOnAsync(serverId, ct);
            status = await context.Provider.GetServerStatusAsync(serverId, ct);
            if (!status.IsRunning)
                throw new InvalidOperationException("Provider did not confirm that the server powered on during rollback.");
        }

        await EnsureOwnedAsync(operation, ct);
        var result = await ssh.ExecuteCommandAsync(
            new SshTarget(context.Server.Id, Required(context.Server.IpAddress, "Server address is missing."),
                context.Server.HostKeyFingerprint),
            sshOptions.Value.Username,
            GetPrivateKey(),
            StartServicesCommand(context.App.Slug),
            (_, _) => Task.CompletedTask,
            ct);
        if (result != 0)
            throw new RemoteQuiescenceException("The environment services could not be restarted during rollback.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var settings = await db.EnvironmentSleepSettings.SingleAsync(
            x => x.EnvironmentId == operation.EnvironmentId, ct);
        var operationRow = await GetOwnedOperationForUpdateAsync(operation, ct);
        var wasCancelled = operationRow.Status == EnvironmentPowerOperationStatuses.Cancelling ||
            operationRow.ErrorCode == "environment_power_cancelled" ||
            operationRow.ErrorCode == "environment_sleep_cancelled";
        var now = clock.GetUtcNow();
        settings.PowerState = EnvironmentPowerStates.Awake;
        settings.StateChangedAt = now;
        settings.LastErrorCode = wasCancelled ? null : operationRow.ErrorCode;
        settings.LastErrorMessage = wasCancelled ? null : operationRow.ErrorMessage;
        operationRow.Status = wasCancelled
            ? EnvironmentPowerOperationStatuses.Cancelled
            : EnvironmentPowerOperationStatuses.Failed;
        operationRow.Step = "completed";
        operationRow.CompletedAt = now;
        operationRow.OwnershipToken = null;
        operationRow.LeaseExpiresAt = null;
        operationRow.UpdatedAt = now;
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task BeginRollbackAsync(
        ClaimedOperation operation, string code, string message, bool cancelled, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var operationRow = await GetOwnedOperationForUpdateAsync(operation, ct);
        var settings = await db.EnvironmentSleepSettings.SingleAsync(
            x => x.EnvironmentId == operation.EnvironmentId, ct);
        operationRow.Step = Rollback;
        operationRow.ErrorCode = code;
        operationRow.ErrorMessage = message;
        if (cancelled)
            operationRow.Status = EnvironmentPowerOperationStatuses.Cancelling;
        settings.PowerState = EnvironmentPowerStates.SleepFailed;
        settings.StateChangedAt = clock.GetUtcNow();
        settings.LastErrorCode = code;
        settings.LastErrorMessage = message;
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task HandleFailureAsync(ClaimedOperation operation, Exception error, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var current = await db.EnvironmentPowerOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == operation.Id, ct);
        if (current is null || current.OwnershipToken != operation.OwnerToken ||
            current.LeaseExpiresAt is null || current.LeaseExpiresAt <= clock.GetUtcNow() ||
            current.Status is not (EnvironmentPowerOperationStatuses.Running or EnvironmentPowerOperationStatuses.Cancelling))
            return;

        var code = FailureCode(current.Step, error);
        var message = FailureMessage(current.Step, code);
        logger?.LogWarning("Sleep operation {OperationId} failed at {Step} ({FailureType}, {ErrorCode}).",
            operation.Id, current.Step, error.GetType().Name, code);

        if (current.Step is null or Quiesce)
        {
            await BeginRollbackAsync(operation, code, message, cancelled: false, ct);
            try
            {
                await RollbackAsync(operation, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception rollbackError)
            {
                await QueueRetryAsync(operation, code, message, rollbackError, ct, keepRollbackStep: true);
            }
            return;
        }

        if (current.Step == Snapshot)
        {
            await MarkFailedSnapshotAsync(operation, ct);
            await BeginRollbackAsync(operation, "snapshot_failed",
                "The replacement snapshot could not be verified; the previous snapshot and server were retained.", false, ct);
            try
            {
                await RollbackAsync(operation, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception rollbackError)
            {
                await QueueRetryAsync(operation, "snapshot_failed",
                    "Snapshot creation failed and the environment rollback is still retrying.", rollbackError,
                    ct, keepRollbackStep: true);
            }
            return;
        }

        await QueueRetryAsync(operation, code, message, error, ct, keepRollbackStep: false);
    }

    private async Task MarkFailedSnapshotAsync(ClaimedOperation operation, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var row = await db.EnvironmentSnapshots.SingleOrDefaultAsync(x => x.Id == operation.Id, ct);
        if (row is null || row.Status == EnvironmentSnapshotStatuses.Current)
            return;
        row.Status = EnvironmentSnapshotStatuses.Failed;
        await SaveOwnedAsync(operation, ct);
    }

    private async Task QueueRetryAsync(
        ClaimedOperation operation, string code, string message, Exception error,
        CancellationToken ct, bool keepRollbackStep)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var operationRow = await GetOwnedOperationForUpdateAsync(operation, ct);
        var settings = await db.EnvironmentSleepSettings.SingleAsync(
            x => x.EnvironmentId == operation.EnvironmentId, ct);
        var now = clock.GetUtcNow();
        operationRow.Status = EnvironmentPowerOperationStatuses.Queued;
        if (keepRollbackStep)
            operationRow.Step = Rollback;
        operationRow.ErrorCode = code;
        operationRow.ErrorMessage = message;
        operationRow.CompletedAt = null;
        operationRow.OwnershipToken = null;
        operationRow.LeaseExpiresAt = null;
        operationRow.UpdatedAt = now;
        settings.PowerState = EnvironmentPowerStates.SleepFailed;
        settings.StateChangedAt = now;
        settings.LastErrorCode = code;
        settings.LastErrorMessage = message;
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        logger?.LogWarning("Sleep operation {OperationId} remains queued at {Step} after {FailureType}.",
            operation.Id, operationRow.Step, error.GetType().Name);
    }

    private async Task MarkSnapshotDeletedAsync(ClaimedOperation operation, string rowId, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        db.ChangeTracker.Clear();
        var row = await db.EnvironmentSnapshots.SingleOrDefaultAsync(x => x.Id == rowId, ct);
        if (row is null || row.Status == EnvironmentSnapshotStatuses.Deleted)
            return;
        row.Status = EnvironmentSnapshotStatuses.Deleted;
        row.DeletedAt = clock.GetUtcNow();
        await SaveOwnedAsync(operation, ct);
    }

    private async Task ReconcileSnapshotAsync(EnvironmentSnapshot snapshot, CancellationToken ct)
    {
        var client = await GetSnapshotClientAsync(snapshot.OrganizationId, snapshot.ProviderAccountId, snapshot.Provider, ct);
        var labels = SnapshotLabels(new ClaimedOperation(
            snapshot.Id, snapshot.OrganizationId, snapshot.EnvironmentId, string.Empty));
        if (string.IsNullOrWhiteSpace(snapshot.ProviderImageId))
        {
            var matches = await client.ListSnapshotsAsync(labels, ct);
            foreach (var image in matches)
            {
                VerifySnapshotOwnership(image, labels);
                await client.DeleteSnapshotAsync(image.Id, ct);
            }
        }
        else
        {
            var image = await client.GetSnapshotAsync(snapshot.ProviderImageId, ct);
            if (image is not null)
            {
                VerifySnapshotOwnership(image, labels);
                await client.DeleteSnapshotAsync(image.Id, ct);
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var current = await db.EnvironmentSnapshots.SingleOrDefaultAsync(x => x.Id == snapshot.Id, ct);
        if (current is null || current.Status is not (EnvironmentSnapshotStatuses.Deleting or
            EnvironmentSnapshotStatuses.Superseded or EnvironmentSnapshotStatuses.Failed))
            return;
        current.Status = EnvironmentSnapshotStatuses.Deleted;
        current.DeletedAt = clock.GetUtcNow();
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<EnvironmentContext> LoadEnvironmentAsync(ClaimedOperation operation, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var app = await db.Apps.AsNoTracking().SingleAsync(x =>
            x.Id == operation.EnvironmentId && x.OrganizationId == operation.OrganizationId, ct);
        var organization = await db.Organizations.AsNoTracking().SingleAsync(
            x => x.Id == operation.OrganizationId, ct);
        var settings = await db.EnvironmentSleepSettings.AsNoTracking().SingleAsync(
            x => x.EnvironmentId == operation.EnvironmentId, ct);
        var servers = await db.ServerInstances.AsNoTracking().Where(x =>
            x.AppId == operation.EnvironmentId &&
            x.OrganizationId == operation.OrganizationId &&
            x.Status != ServerInstance.RetiredStatus).ToListAsync(ct);
        if (servers.Count != 1)
            throw new InvalidOperationException("The environment does not have exactly one active server registration.");
        var server = servers[0];
        if (string.IsNullOrWhiteSpace(server.ProviderAccountId) ||
            !Enum.TryParse<CloudProvider>(server.Provider, true, out var providerType) ||
            !Enum.IsDefined(providerType))
            throw new InvalidOperationException("The server provider account or type is invalid.");

        var runtimeRows = await db.RuntimeServers.AsNoTracking().Where(x =>
            x.AppId == operation.EnvironmentId && x.OrganizationId == operation.OrganizationId).ToListAsync(ct);
        if (runtimeRows.Count != 1 || runtimeRows[0].ProviderServerId != server.ProviderServerId ||
            runtimeRows[0].ProviderAccountId != server.ProviderAccountId ||
            !string.Equals(runtimeRows[0].Provider, server.Provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Runtime and provider server ownership do not match.");
        await EnsureNoSharedServerRegistrationAsync(server, ct);

        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == server.ProviderAccountId && x.OrganizationId == operation.OrganizationId, ct)
            ?? throw new InvalidOperationException("The provider account does not belong to the environment organization.");
        if (account.ProviderType != providerType)
            throw new InvalidOperationException("The provider account type does not match the server registration.");
        var client = await providerClients.CreateAsync(operation.OrganizationId, account.Id, ct);
        if (client.Provider != providerType ||
            client is not IVpsProviderSnapshotClient snapshotClient)
            throw new InvalidOperationException("The registered provider does not expose snapshot operations.");
        var address = settings.AddressMode == EnvironmentAddressModes.Retained
            ? await db.EnvironmentRetainedAddresses.AsNoTracking().SingleOrDefaultAsync(x =>
                x.EnvironmentId == operation.EnvironmentId &&
                x.OrganizationId == operation.OrganizationId &&
                x.State != EnvironmentRetainedAddressStates.Released, ct)
            : null;
        if (settings.AddressMode == EnvironmentAddressModes.Retained &&
            (address is null || address.ProviderAccountId != account.Id ||
             !string.Equals(address.Provider, server.Provider, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The retained address is not owned by the environment provider account.");
        var addressClient = client as IVpsProviderAddressClient;
        if (settings.AddressMode == EnvironmentAddressModes.Retained && addressClient is null)
            throw new InvalidOperationException("The registered provider does not expose retained-address operations.");

        return new EnvironmentContext(
            app, organization, settings, server, client, snapshotClient, addressClient,
            address, account, ManagedServerName.ForApp(organization.Slug, app.Slug));
    }

    private async Task<IVpsProviderSnapshotClient> GetSnapshotClientAsync(
        string organizationId, string accountId, string provider, CancellationToken ct)
    {
        if (!Enum.TryParse<CloudProvider>(provider, true, out var expectedProvider) ||
            !Enum.IsDefined(expectedProvider))
            throw new InvalidOperationException("Snapshot provider identity is invalid.");
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId, ct)
            ?? throw new InvalidOperationException("Snapshot provider account does not belong to its organization.");
        if (account.ProviderType != expectedProvider)
            throw new InvalidOperationException("Snapshot provider account type does not match its record.");
        var client = await providerClients.CreateAsync(organizationId, accountId, ct);
        if (client.Provider != expectedProvider || client is not IVpsProviderSnapshotClient snapshotClient)
            throw new InvalidOperationException("Snapshot provider adapter is unavailable.");
        return snapshotClient;
    }

    private async Task EnsureProviderServerOwnedAsync(EnvironmentContext context, CancellationToken ct)
    {
        await EnsureNoSharedServerRegistrationAsync(context.Server, ct);
        var found = await context.Provider.FindServerAsync(context.ServerName, ct);
        if (found is null)
            throw new InvalidOperationException("The registered provider server could not be found.");
        VerifyServerOwnership(context, found);
        if (found.Id != context.Server.ProviderServerId)
            throw new InvalidOperationException("The provider server ID differs from the environment registration.");
    }

    private static void VerifyServerOwnership(EnvironmentContext context, ProviderServerInfo discovered)
    {
        var ownership = new ProviderOwnershipEvidence(
            context.Organization.Slug,
            context.App.Slug,
            "production",
            context.Organization.Id,
            context.Server.ProviderAccountId,
            context.App.Id);
        var descriptor = BuiltInProviderDescriptors.ForBuiltIn(context.Provider.Provider);
        if (!ProviderOwnershipVerifier.VerifyDiscoveredServer(
                descriptor, discovered, context.ServerName, ownership))
            throw new InvalidOperationException("Provider server ownership could not be verified.");
    }

    private async Task EnsureNoSharedServerRegistrationAsync(ServerInstance server, CancellationToken ct)
    {
        var providerId = Required(server.ProviderServerId, "Server provider ID is missing.");
        var shared = await db.ServerInstances.AsNoTracking().AnyAsync(x =>
            x.Id != server.Id &&
            x.ProviderAccountId == server.ProviderAccountId &&
            x.Provider.ToLower() == server.Provider.ToLower() &&
            x.ProviderServerId == providerId, ct) ||
            await db.RuntimeServers.AsNoTracking().AnyAsync(x =>
                (x.AppId != server.AppId || x.OrganizationId != server.OrganizationId) &&
                x.ProviderAccountId == server.ProviderAccountId &&
                x.Provider.ToLower() == server.Provider.ToLower() &&
                x.ProviderServerId == providerId, ct);
        if (shared)
            throw new InvalidOperationException("The provider server is registered to another environment.");
    }

    private async Task SaveStepAsync(ClaimedOperation operation, string step, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.ChangeTracker.Clear();
        var row = await GetOwnedOperationForUpdateAsync(operation, ct);
        row.Step = step;
        row.UpdatedAt = clock.GetUtcNow();
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<OperationState> ReadOperationAsync(ClaimedOperation operation, CancellationToken ct)
    {
        await EnsureOwnedAsync(operation, ct);
        var row = await db.EnvironmentPowerOperations.AsNoTracking()
            .SingleAsync(x => x.Id == operation.Id, ct);
        return new OperationState(row.Step, row.Status, row.Trigger, row.ErrorCode);
    }

    private async Task EnsureOwnedAsync(ClaimedOperation operation, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var current = await db.EnvironmentPowerOperations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == operation.Id &&
            x.OrganizationId == operation.OrganizationId &&
            x.EnvironmentId == operation.EnvironmentId &&
            x.OwnershipToken == operation.OwnerToken &&
            (x.Status == EnvironmentPowerOperationStatuses.Running ||
             x.Status == EnvironmentPowerOperationStatuses.Cancelling), ct);
        if (current?.LeaseExpiresAt is not { } leaseExpiresAt || leaseExpiresAt <= now)
            throw new UnauthorizedAccessException("Sleep operation ownership changed.");
        await authority.EnsureCommitAuthorityAsync(db, ct);
    }

    private async Task<EnvironmentPowerOperation> GetOwnedOperationForUpdateAsync(
        ClaimedOperation operation, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var row = db.Database.IsNpgsql()
            ? await db.EnvironmentPowerOperations.FromSqlInterpolated(
                $"SELECT * FROM environment_power_operations WHERE id = {operation.Id} FOR UPDATE").SingleOrDefaultAsync(ct)
            : await db.EnvironmentPowerOperations.SingleOrDefaultAsync(x => x.Id == operation.Id, ct);
        if (row is null || row.OrganizationId != operation.OrganizationId ||
            row.EnvironmentId != operation.EnvironmentId || row.OwnershipToken != operation.OwnerToken ||
            row.LeaseExpiresAt is null || row.LeaseExpiresAt <= now ||
            row.Status is not (EnvironmentPowerOperationStatuses.Running or EnvironmentPowerOperationStatuses.Cancelling))
            throw new UnauthorizedAccessException("Sleep operation ownership changed before persistence.");
        return row;
    }

    private async Task SaveOwnedAsync(ClaimedOperation operation, CancellationToken ct)
    {
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        _ = await GetOwnedOperationForUpdateAsync(operation, ct);
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    private async Task SaveOwnedAsync(CancellationToken ct)
    {
        await authority.EnsureCommitAuthorityAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    private static string FailureCode(string? step, Exception error)
    {
        if (step == RemoveDns)
            return "dns_removal_failed";
        if (step == PowerOff)
            return "provider_power_off_failed";
        if (step == DeleteServer)
            return "provider_server_delete_failed";
        if (error is SshHostKeyTrustException trust)
            return trust.FailureCode;
        if (error is ProviderCatalogException catalog)
            return catalog.Code;
        if (error is ProviderApiException { StatusCode: HttpStatusCode.NotFound })
            return "provider_resource_not_found";
        return "environment_sleep_failed";
    }

    private static string FailureMessage(string? step, string code) => step switch
    {
        RemoveDns => "Managed DNS records could not be removed; the powered-off server was retained.",
        Snapshot => "The replacement snapshot could not be verified; the previous snapshot and server were retained.",
        DeleteServer => "The powered-off server could not be deleted; retrying the same sleep step.",
        Rollback => "The environment rollback is still retrying.",
        _ => $"Sleep could not complete at step '{step ?? "claim"}' ({code}); retrying safely."
    };

    private string GetPrivateKey()
    {
        var value = sshOptions.Value.PrivateKeyContent;
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        if (!string.IsNullOrWhiteSpace(sshOptions.Value.PrivateKeyPath))
            return File.ReadAllText(sshOptions.Value.PrivateKeyPath);
        throw new InvalidOperationException("Worker SSH credentials are unavailable.");
    }

    private static string QuiesceCommand(string appSlug)
    {
        var project = $"selfrun-{appSlug}";
        var compose = $"/opt/selfrun/apps/{appSlug}/app.compose.json";
        return $"docker compose -p {Quote(project)} -f {Quote(compose)} stop && " +
            "sudo systemctl stop selfrun-agent.service && sync";
    }

    private static string StartServicesCommand(string appSlug)
    {
        var project = $"selfrun-{appSlug}";
        var compose = $"/opt/selfrun/apps/{appSlug}/app.compose.json";
        return $"docker compose -p {Quote(project)} -f {Quote(compose)} up -d && " +
            "sudo systemctl start selfrun-agent.service";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Required(string? value, string message) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException(message);

    private sealed record ClaimedOperation(
        string Id, string OrganizationId, string EnvironmentId, string OwnerToken);

    private sealed record OperationState(string? Step, string Status, string Trigger, string? ErrorCode);

    private sealed record EnvironmentContext(
        SelfrunApp App,
        Organization Organization,
        EnvironmentSleepSettings Settings,
        ServerInstance Server,
        IVpsProviderClient Provider,
        IVpsProviderSnapshotClient Snapshots,
        IVpsProviderAddressClient? AddressProvider,
        EnvironmentRetainedAddress? Address,
        ProviderAccount Account,
        string ServerName);
}
