using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class MaintenanceAuthorityOptions
{
    public const string SectionName = "Maintenance";

    /// <summary>Host-local identity of this installation host. It must not be part of backups.</summary>
    public string? HostId { get; set; }
    public string? HostIdFile { get; set; }

    private static readonly Regex HostIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant);

    public static bool IsValidId(string? value) => value is not null && HostIdPattern.IsMatch(value);

    public static string? ResolveHostId(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var value = section["HostId"];
        var file = section["HostIdFile"];
        if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(file))
        {
            value = File.ReadAllText(file).Trim();
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!IsValidId(value))
        {
            throw new InvalidOperationException("Maintenance:HostId must be 1-128 characters of letters, digits, '.', '_', ':' or '-'.");
        }
        return value;
    }
}

public enum MaintenanceAdmissionOutcome
{
    Admitted,
    Fenced,
    Revoked,
    HostMismatch,
    Unavailable
}

public sealed record MaintenanceAdmissionDecision(MaintenanceAdmissionOutcome Outcome, long Epoch, string Code, string Message)
{
    public bool IsAdmitted => Outcome == MaintenanceAdmissionOutcome.Admitted;

    /// <summary>True when this process no longer holds installation authority (as opposed to a temporary fence).</summary>
    public bool IsRevoked => Outcome is MaintenanceAdmissionOutcome.Revoked or MaintenanceAdmissionOutcome.HostMismatch;

    internal static MaintenanceAdmissionDecision Admit(long epoch) => new(MaintenanceAdmissionOutcome.Admitted, epoch, "admitted", "Admitted.");
}

/// <summary>
/// The maintenance identity of one API or worker process. It registers the authority epoch it
/// observed at startup and refuses new admissions or claims once that epoch is superseded by a
/// transfer, or when the authority is bound to a different host.
/// </summary>
public sealed class MaintenanceParticipant
{
    public static readonly TimeSpan ActivityLeaseDuration = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ActivityRenewalInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory? scopes;
    private readonly TimeProvider clock;
    private readonly ILogger? logger;
    private readonly object gate = new();
    private long? registeredEpoch;

    public MaintenanceParticipant(IServiceScopeFactory? scopes, string role, string? hostId, TimeProvider clock, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (hostId is not null && !MaintenanceAuthorityOptions.IsValidId(hostId))
        {
            throw new ArgumentException("Invalid maintenance host id.", nameof(hostId));
        }
        this.scopes = scopes;
        this.clock = clock;
        this.logger = logger;
        Role = role;
        HostId = hostId;
    }

    /// <summary>
    /// A participant without durable registration or activity support, used when a claimer is constructed
    /// outside the host container. It still enforces epoch and host checks inside claim transactions.
    /// </summary>
    public static MaintenanceParticipant CreateLocal(string role, TimeProvider clock) => new(null, role, null, clock);

    public string Id { get; } = "mp_" + Guid.NewGuid().ToString("N");
    public string Role { get; }
    public string? HostId { get; }

    public long? RegisteredEpoch
    {
        get { lock (gate) { return registeredEpoch; } }
    }

    /// <summary>
    /// Validates this process against the authority row inside the caller's transaction. On
    /// PostgreSQL the row is read FOR SHARE, so the decision is ordered against fence/transfer.
    /// Accepted work may be claimed while fenced; only a revoked or foreign authority refuses it.
    /// </summary>
    public async Task<MaintenanceAdmissionDecision> CheckAsync(MinicloudDbContext db, bool requireOpen, CancellationToken ct, bool lockRow = true)
    {
        var record = await MaintenanceAuthorityStore.ReadAsync(db, lockRow ? MaintenanceAuthorityLock.Share : MaintenanceAuthorityLock.None, ct);
        return Evaluate(record, requireOpen);
    }

    /// <summary>
    /// Claim gate. Use <paramref name="lockRow"/> = false only for claim transactions that stay open across
    /// remote calls, where holding a share lock would block fence/transfer for the call's duration.
    /// </summary>
    public async Task<bool> CanClaimAsync(MinicloudDbContext db, CancellationToken ct, bool lockRow = true)
    {
        var decision = await CheckAsync(db, requireOpen: false, ct, lockRow);
        if (!decision.IsAdmitted)
        {
            logger?.LogWarning("Maintenance authority refused a {Role} claim: {Code}.", Role, decision.Code);
        }
        return decision.IsAdmitted;
    }

    /// <summary>Throws when this process may not commit owned work (authority revoked or foreign).</summary>
    public async Task EnsureCommitAuthorityAsync(MinicloudDbContext db, CancellationToken ct)
    {
        var decision = await CheckAsync(db, requireOpen: false, ct);
        if (!decision.IsAdmitted)
        {
            throw new UnauthorizedAccessException("Maintenance authority for this process is no longer current.");
        }
    }

    public MaintenanceAdmissionDecision Evaluate(MaintenanceAuthorityRecord? record, bool requireOpen)
    {
        if (record is null)
        {
            return new(MaintenanceAdmissionOutcome.Unavailable, 0, "maintenance_authority_unavailable",
                "The maintenance authority record is missing; run database migrations.");
        }
        if (record.HostId is not null && !string.Equals(record.HostId, HostId, StringComparison.Ordinal))
        {
            return new(MaintenanceAdmissionOutcome.HostMismatch, record.Epoch, "maintenance_authority_revoked",
                "Installation authority is bound to a different host.");
        }
        lock (gate)
        {
            if (registeredEpoch is null)
            {
                registeredEpoch = record.Epoch;
            }
            else if (registeredEpoch != record.Epoch)
            {
                // A transfer to this very host re-binds its running processes; any other change revokes them.
                if (record.HostId is not null && record.Epoch > registeredEpoch)
                {
                    registeredEpoch = record.Epoch;
                }
                else
                {
                    return new(MaintenanceAdmissionOutcome.Revoked, record.Epoch, "maintenance_authority_revoked",
                        "Installation authority was transferred; this process holds a superseded epoch.");
                }
            }
        }
        if (requireOpen && record.State != MaintenanceAuthorityStates.Open)
        {
            return new(MaintenanceAdmissionOutcome.Fenced, record.Epoch, "maintenance_in_progress",
                "Management writes are paused for installation maintenance. Retry later.");
        }
        return MaintenanceAdmissionDecision.Admit(record.Epoch);
    }

    /// <summary>
    /// Durably admits one unit of new activity (an API management write or a scheduler pass).
    /// The activity row is committed in the same transaction as the authority check, so a fence
    /// either observes it in drain status or the admission observes the fence.
    /// </summary>
    public async Task<(MaintenanceAdmissionDecision Decision, MaintenanceActivityLease? Lease)> TryBeginActivityAsync(
        string kind, string? detail, bool requireOpen, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var factory = scopes ?? throw new InvalidOperationException("This maintenance participant cannot record activities.");
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var strategy = db.Database.CreateExecutionStrategy();
        var activity = new MaintenanceActivity
        {
            Id = "ma_" + Guid.NewGuid().ToString("N"),
            Kind = kind.Length > 64 ? kind[..64] : kind,
            ParticipantId = Id,
            Detail = detail is { Length: > 256 } ? detail[..256] : detail
        };
        var decision = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await CheckAsync(db, requireOpen, ct);
            if (!result.IsAdmitted)
            {
                await transaction.RollbackAsync(ct);
                return result;
            }
            var now = clock.GetUtcNow();
            activity.Epoch = result.Epoch;
            activity.StartedAt = now;
            activity.ExpiresAt = now + ActivityLeaseDuration;
            db.MaintenanceActivities.Add(activity);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        });
        return decision.IsAdmitted
            ? (decision, new MaintenanceActivityLease(factory, clock, activity.Id, logger))
            : (decision, null);
    }

    /// <summary>
    /// Runs one scheduler pass as a durable activity. Returns false without running when not admitted
    /// (fenced for <paramref name="requireOpen"/> passes, or revoked authority).
    /// </summary>
    public async Task<bool> RunActivityAsync(string kind, bool requireOpen, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        var (decision, lease) = await TryBeginActivityAsync(kind, null, requireOpen, ct);
        if (!decision.IsAdmitted) return false;
        await using (lease)
        {
            await work(ct);
        }
        return true;
    }

    /// <summary>Registers this process at startup and refreshes its participant heartbeat until stopped.</summary>
    public async Task RunRegistrationLoopAsync(CancellationToken stoppingToken)
    {
        MaintenanceAdmissionOutcome? last = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var decision = await RegisterAsync(stoppingToken);
                if (decision.Outcome != last)
                {
                    if (decision.IsRevoked || decision.Outcome == MaintenanceAdmissionOutcome.Unavailable)
                        logger?.LogError("Maintenance authority refuses this {Role} process: {Code}. No new work will be admitted or claimed.", Role, decision.Code);
                    else
                        logger?.LogInformation("{Role} process registered with maintenance authority epoch {Epoch}.", Role, RegisteredEpoch);
                    last = decision.Outcome;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger?.LogWarning("Maintenance participant registration failed ({FailureType}); retrying.", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Registers (or refreshes) this process in maintenance_participants.</summary>
    public async Task<MaintenanceAdmissionDecision> RegisterAsync(CancellationToken ct)
    {
        var factory = scopes ?? throw new InvalidOperationException("This maintenance participant cannot register.");
        await using var scope = factory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var decision = Evaluate(await MaintenanceAuthorityStore.ReadAsync(db, MaintenanceAuthorityLock.None, ct), requireOpen: false);
        var now = clock.GetUtcNow();
        var row = await db.MaintenanceParticipants.SingleOrDefaultAsync(x => x.Id == Id, ct);
        if (row is null)
        {
            row = new MaintenanceParticipantRecord { Id = Id, Role = Role, HostId = HostId, RegisteredAt = now };
            db.MaintenanceParticipants.Add(row);
        }
        row.Epoch = RegisteredEpoch ?? 0;
        row.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return decision;
    }
}

/// <summary>A renewed durable activity row; only recorded completion removes its drain blocker.</summary>
public sealed class MaintenanceActivityLease : IAsyncDisposable
{
    private readonly IServiceScopeFactory scopes;
    private readonly TimeProvider clock;
    private readonly ILogger? logger;
    private readonly CancellationTokenSource stop = new();
    private readonly Task renewal;
    private int disposed;

    internal MaintenanceActivityLease(IServiceScopeFactory scopes, TimeProvider clock, string id, ILogger? logger)
    {
        this.scopes = scopes;
        this.clock = clock;
        this.logger = logger;
        Id = id;
        renewal = RenewAsync();
    }

    public string Id { get; }

    private async Task RenewAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(MaintenanceParticipant.ActivityRenewalInterval, clock, stop.Token);
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
                    var row = await db.MaintenanceActivities.SingleOrDefaultAsync(x => x.Id == Id, stop.Token);
                    if (row is null) return;
                    row.ExpiresAt = clock.GetUtcNow() + MaintenanceParticipant.ActivityLeaseDuration;
                    await db.SaveChangesAsync(stop.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning("Maintenance activity {ActivityId} renewal failed ({FailureType}); accepted work continues and drain remains blocked until completion is recorded.", Id, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        await stop.CancelAsync();
        await renewal;
        stop.Dispose();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var row = await db.MaintenanceActivities.SingleOrDefaultAsync(x => x.Id == Id, timeout.Token);
            if (row is not null)
            {
                db.MaintenanceActivities.Remove(row);
                await db.SaveChangesAsync(timeout.Token);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Maintenance activity {ActivityId} completion was not recorded ({FailureType}); drain remains blocked and requires reconciliation.", Id, ex.GetType().Name);
        }
    }
}

internal enum MaintenanceAuthorityLock
{
    None,
    Share,
    Update
}

internal static class MaintenanceAuthorityStore
{
    public static async Task<MaintenanceAuthorityRecord?> ReadAsync(MinicloudDbContext db, MaintenanceAuthorityLock mode, CancellationToken ct)
    {
        if (db.Database.IsNpgsql() && mode != MaintenanceAuthorityLock.None)
        {
            var sql = mode == MaintenanceAuthorityLock.Update
                ? "SELECT * FROM maintenance_authority WHERE id = 1 FOR UPDATE"
                : "SELECT * FROM maintenance_authority WHERE id = 1 FOR SHARE";
            var query = db.MaintenanceAuthorities.FromSqlRaw(sql);
            return mode == MaintenanceAuthorityLock.Update
                ? await query.SingleOrDefaultAsync(ct)
                : await query.AsNoTracking().SingleOrDefaultAsync(ct);
        }
        var rows = db.MaintenanceAuthorities.Where(x => x.Id == MaintenanceAuthorityRecord.SingletonId);
        return mode == MaintenanceAuthorityLock.Update
            ? await rows.SingleOrDefaultAsync(ct)
            : await rows.AsNoTracking().SingleOrDefaultAsync(ct);
    }
}

public static class MaintenanceServiceCollectionExtensions
{
    /// <summary>Registers the process-wide participant. Hosts also add a hosted service that calls RunRegistrationLoopAsync.</summary>
    public static IServiceCollection AddMinicloudMaintenanceParticipant(this IServiceCollection services, IConfiguration configuration, string role)
    {
        var hostId = MaintenanceAuthorityOptions.ResolveHostId(configuration);
        services.AddSingleton(provider => new MaintenanceParticipant(
            provider.GetRequiredService<IServiceScopeFactory>(), role, hostId,
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<ILoggerFactory>()?.CreateLogger<MaintenanceParticipant>()));
        return services;
    }
}
