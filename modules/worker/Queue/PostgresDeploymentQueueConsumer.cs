using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Queue;

public sealed class PostgresDeploymentQueueConsumer : BackgroundService, IDeploymentQueueConsumer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly WorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PostgresDeploymentQueueConsumer> _logger;

    public PostgresDeploymentQueueConsumer(
        IServiceScopeFactory scopeFactory,
        IOptions<WorkerOptions> options,
        TimeProvider timeProvider,
        ILogger<PostgresDeploymentQueueConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _options = options.Value;
        if (_options.MaxConcurrentDeployments is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), "Worker:MaxConcurrentDeployments must be between 1 and 16.");
        }
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<Deployment?> TryClaimNextDeploymentAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

            Deployment? deployment;

            var isPostgres = dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
            if (isPostgres)
            {
                // PostgreSQL atomic claim query using FOR UPDATE OF d, a SKIP LOCKED
                // Allows multiple concurrent workers to poll without locking contention or duplicate claims
                deployment = await dbContext.Deployments
                    .FromSqlInterpolated($@"
                    SELECT d.* FROM deployments d JOIN apps a ON a.id = d.app_id
                    WHERE (d.status = 'queued' OR d.status = 'Queued')
                      AND a.deletion_status IS NULL
                      AND NOT EXISTS (
                        SELECT 1 FROM deployments active
                        WHERE active.app_id = d.app_id
                          AND active.id <> d.id
                          AND (active.ownership_token IS NOT NULL
                               OR lower(active.status) IN ('waitingforbuild', 'waiting_for_build', 'dispatching', 'provisioning', 'deploying', 'verifying')))
                    ORDER BY d.created_at ASC, d.id ASC
                    FOR UPDATE OF d, a SKIP LOCKED
                    LIMIT 1")
                    .Include(d => d.App)
                        .ThenInclude(a => a.Organization)
                    .Include(d => d.Services)
                        .ThenInclude(s => s.Artifact)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            else
            {
                // Non-Postgres fallback (e.g. SQLite test harness in unit tests)
                var queuedDeployments = await dbContext.Deployments
                    .Include(d => d.App)
                        .ThenInclude(a => a.Organization)
                    .Include(d => d.Services)
                        .ThenInclude(s => s.Artifact)
                    .Where(d => d.Status == DeploymentStatus.Queued && d.App.DeletionStatus == null)
                    .ToListAsync(cancellationToken);

                deployment = queuedDeployments
                    .Where(candidate => !dbContext.Deployments.Any(active =>
                        active.AppId == candidate.AppId && active.Id != candidate.Id &&
                        (active.OwnershipToken != null ||
                         active.Status == DeploymentStatus.WaitingForBuild ||
                         active.Status == DeploymentStatus.Dispatching ||
                         active.Status == DeploymentStatus.Provisioning ||
                         active.Status == DeploymentStatus.Deploying ||
                         active.Status == DeploymentStatus.Verifying)))
                    .OrderBy(d => d.CreatedAt)
                    .ThenBy(d => d.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
            }

            if (deployment is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            var queuedAt = deployment.UpdatedAt;
            deployment.Status = DeploymentStatus.Provisioning;
            deployment.WorkerId = _options.WorkerId;
            deployment.OwnershipToken = Guid.NewGuid().ToString("N");
            deployment.OwnershipAcquiredAt = now;
            deployment.UpdatedAt = now;
            if (deployment.StartedAt is null)
            {
                deployment.StartedAt = now;
            }

            dbContext.DeploymentEvents.Add(new DeploymentEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                DeploymentId = deployment.Id,
                Level = EventLevel.Info,
                Message = $"Deployment claimed by worker '{_options.WorkerId}'. Queue wait {(now - queuedAt).TotalMilliseconds:F0} ms; status transitioned to provisioning.",
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    phase = "queue_wait",
                    duration_ms = Math.Round((now - queuedAt).TotalMilliseconds, 3),
                    outcome = "succeeded",
                }),
                CreatedAt = now
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Worker '{WorkerId}' claimed deployment {DeploymentId} for app {AppId}",
                _options.WorkerId,
                deployment.Id,
                deployment.AppId);

            return deployment;
        });
    }

    public async Task ProcessDeploymentAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (string.IsNullOrWhiteSpace(deployment.OwnershipToken) ||
            !string.Equals(deployment.WorkerId, _options.WorkerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Deployment {deployment.Id} is not owned by worker '{_options.WorkerId}'.");
        }

        await using (var ownershipScope = _scopeFactory.CreateAsyncScope())
        {
            var ownershipDb = ownershipScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            if (!await ownershipDb.Deployments.AsNoTracking().AnyAsync(
                    d => d.Id == deployment.Id &&
                         d.WorkerId == _options.WorkerId &&
                         d.OwnershipToken == deployment.OwnershipToken,
                    cancellationToken))
            {
                throw new InvalidOperationException($"Deployment {deployment.Id} ownership is no longer current.");
            }
        }

        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.DeploymentTimeout > TimeSpan.Zero)
        {
            pipelineCts.CancelAfter(_options.DeploymentTimeout);
        }

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var ownershipLostCts = new CancellationTokenSource();
        using var userCancellationCts = new CancellationTokenSource();
        var cancellationTask = WatchCancellationAsync(deployment.Id, deployment.OwnershipToken, pipelineCts, userCancellationCts, heartbeatCts.Token);
        var heartbeatTask = RunHeartbeatLoopAsync(
            deployment.Id, deployment.OwnershipToken, pipelineCts, ownershipLostCts, heartbeatCts.Token);

        try
        {
            _logger.LogInformation("Beginning pipeline execution for deployment {DeploymentId}", deployment.Id);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var pipelineExecutor = scope.ServiceProvider.GetRequiredService<IDeploymentPipelineExecutor>();
            await pipelineExecutor.ExecuteAsync(deployment, pipelineCts.Token);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await MarkCanceledAsync(deployment.Id, deployment.OwnershipToken, cleanup.Token);
            _logger.LogInformation("Pipeline execution finished successfully for deployment {DeploymentId}", deployment.Id);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested && !ownershipLostCts.IsCancellationRequested &&
                (userCancellationCts.IsCancellationRequested || await IsCancellationRequestedAsync(deployment.Id, deployment.OwnershipToken)))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await MarkCanceledAsync(deployment.Id, deployment.OwnershipToken, cleanup.Token);
            }
            else
            {
                if (!cancellationToken.IsCancellationRequested && !ownershipLostCts.IsCancellationRequested && pipelineCts.IsCancellationRequested)
                {
                    await MarkDeploymentFailedAsync(deployment.Id, deployment.OwnershipToken, "worker_timeout",
                        $"Deployment timed out after {_options.DeploymentTimeout.TotalMinutes:F1} minutes.",
                        releaseOwnership: false, cancellationToken: CancellationToken.None);
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deployment {DeploymentId} failed during pipeline execution", deployment.Id);
            await MarkDeploymentFailedAsync(
                deployment.Id,
                deployment.OwnershipToken,
                ex is RemoteQuiescenceException ? "remote_quiescence_unconfirmed" : "worker_pipeline_error",
                ex.Message,
                releaseOwnership: ex is not RemoteQuiescenceException,
                cancellationToken: CancellationToken.None);
            throw;
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try
            {
                await Task.WhenAll(heartbeatTask, cancellationTask);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling heartbeat loop
            }
        }
    }

    private async Task<bool> IsCancellationRequestedAsync(string id, string owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().Deployments.AsNoTracking()
            .AnyAsync(d => d.Id == id && d.OwnershipToken == owner && d.CancellationRequestedAt != null, timeout.Token);
    }

    private async Task WatchCancellationAsync(string id, string owner, CancellationTokenSource pipeline,
        CancellationTokenSource requested, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
                if (await db.Deployments.AsNoTracking().AnyAsync(d => d.Id == id && d.OwnershipToken == owner &&
                    d.CancellationRequestedAt != null, ct))
                {
                    await requested.CancelAsync();
                    await pipeline.CancelAsync();
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to read cancellation request for {DeploymentId}", id);
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            }
        }
    }

    private async Task MarkCanceledAsync(string id, string owner, CancellationToken ct)
    {
        await Pipeline.Steps.PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, token) =>
        {
            var deployment = await DeploymentMutation.LockAsync(db, id, token);
            if (deployment is null || deployment.OwnershipToken != owner || deployment.CancellationRequestedAt is null ||
                DeploymentMutation.IsTerminal(deployment.Status)) { return; }
            var now = _timeProvider.GetUtcNow();
            deployment.Status = DeploymentStatus.Canceled;
            deployment.CompletedAt = now;
            deployment.UpdatedAt = now;
            deployment.WorkerId = null;
            deployment.OwnershipToken = null;
            deployment.OwnershipAcquiredAt = null;
            Pipeline.Steps.PipelineDb.AddEvent(db, id, EventLevel.Info,
                "Deployment canceled; execution stopped. Changes already applied were not restored.", now);
        }, ct);
    }

    private async Task RunHeartbeatLoopAsync(
        string deploymentId,
        string ownershipToken,
        CancellationTokenSource pipelineCts,
        CancellationTokenSource ownershipLostCts,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
                var now = _timeProvider.GetUtcNow();

                int rowsAffected = 0;
                try
                {
                    rowsAffected = await dbContext.Deployments
                        .Where(d => d.Id == deploymentId && d.OwnershipToken == ownershipToken && d.WorkerId == _options.WorkerId &&
                            (d.Status == DeploymentStatus.Provisioning || d.Status == DeploymentStatus.Deploying || d.Status == DeploymentStatus.Verifying))
                        .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, now), cancellationToken);
                }
                catch (Exception)
                {
                    // Fallback for providers that do not support ExecuteUpdateAsync
                    var dep = await dbContext.Deployments.FindAsync([deploymentId], cancellationToken);
                    if (dep is not null && dep.OwnershipToken == ownershipToken && dep.WorkerId == _options.WorkerId &&
                        dep.Status is DeploymentStatus.Provisioning or DeploymentStatus.Deploying or DeploymentStatus.Verifying)
                    {
                        dep.UpdatedAt = now;
                        rowsAffected = await dbContext.SaveChangesAsync(cancellationToken);
                    }
                }

                if (rowsAffected > 0)
                {
                    _logger.LogDebug("Heartbeat emitted for deployment {DeploymentId} at {Timestamp}", deploymentId, now);
                }
                else
                {
                    _logger.LogWarning(
                        "Deployment {DeploymentId} ownership is no longer active; canceling pipeline", deploymentId);
                    await ownershipLostCts.CancelAsync();
                    await pipelineCts.CancelAsync();
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send heartbeat for deployment {DeploymentId}", deploymentId);
            }
        }
    }

    private async Task MarkDeploymentFailedAsync(
        string deploymentId,
        string ownershipToken,
        string failureCode,
        string failureMessage,
        bool releaseOwnership,
        CancellationToken cancellationToken)
    {
        try
        {
            using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cleanup.CancelAfter(TimeSpan.FromSeconds(20));
            await Pipeline.Steps.PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
            {
                var deployment = await DeploymentMutation.LockAsync(db, deploymentId, ct);

                if (deployment is not null && deployment.OwnershipToken == ownershipToken && deployment.WorkerId == _options.WorkerId &&
                    deployment.Status is not (DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled))
                {
                    var now = _timeProvider.GetUtcNow();
                    deployment.Status = DeploymentStatus.Failed;
                    deployment.FailureCode = failureCode;
                    deployment.FailureMessage = failureMessage;
                    deployment.CompletedAt = now;
                    deployment.UpdatedAt = now;
                    if (releaseOwnership)
                    {
                        deployment.WorkerId = null;
                        deployment.OwnershipToken = null;
                        deployment.OwnershipAcquiredAt = null;
                    }

                    db.DeploymentEvents.Add(new DeploymentEvent
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DeploymentId = deployment.Id,
                        Level = EventLevel.Error,
                        Message = $"Deployment pipeline failed: {failureMessage}",
                        CreatedAt = now
                    });

                }
            }, cleanup.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record deployment failure for {DeploymentId}", deploymentId);
        }
    }

    public async Task<int> FailStaleDeploymentsAsync(CancellationToken cancellationToken)
    {
        var abandoned = await FailAbandonedLocalBuildsAsync(cancellationToken);
        if (_options.DeploymentTimeout <= TimeSpan.Zero)
        {
            return abandoned;
        }
        var heartbeatGrace = TimeSpan.FromTicks(Math.Max(
            _options.HeartbeatInterval.Ticks * 3,
            TimeSpan.FromMinutes(1).Ticks));
        var now = _timeProvider.GetUtcNow();
        var activeCutoff = now - _options.DeploymentTimeout - heartbeatGrace;
        var quiescenceCutoff = now - heartbeatGrace;
        var activeStatuses = new[]
        {
            DeploymentStatus.Provisioning,
            DeploymentStatus.Deploying,
            DeploymentStatus.Verifying
        };

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var isPostgres = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        var staleCandidates = isPostgres
            ? await db.Deployments.AsNoTracking()
                .Where(d => d.OwnershipToken != null && d.UpdatedAt < quiescenceCutoff)
                .OrderBy(d => d.UpdatedAt)
                .Take(100)
                .ToListAsync(cancellationToken)
            : (await db.Deployments.AsNoTracking().ToListAsync(cancellationToken))
                .Where(d => d.OwnershipToken is not null && d.UpdatedAt < quiescenceCutoff)
                .OrderBy(d => d.UpdatedAt)
                .Take(100)
                .ToList();
        var staleIds = staleCandidates
            .Where(d =>
                (activeStatuses.Contains(d.Status) && d.UpdatedAt < activeCutoff) ||
                (d.Status == DeploymentStatus.Failed &&
                 d.FailureCode == "remote_quiescence_unconfirmed" &&
                 d.UpdatedAt < quiescenceCutoff))
            .Select(d => d.Id)
            .Take(20)
            .ToList();

        var failed = 0;
        foreach (var id in staleIds)
        {
            var strategy = db.Database.CreateExecutionStrategy();
            failed += await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var stale = isPostgres
                    ? await db.Deployments.FromSqlInterpolated($"SELECT * FROM deployments WHERE id = {id} FOR UPDATE")
                        .FirstOrDefaultAsync(cancellationToken)
                    : await db.Deployments.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
                var activeDeploymentExpired = stale is not null && stale.UpdatedAt < activeCutoff &&
                    activeStatuses.Contains(stale.Status);
                var quiescenceGraceExpired = stale is not null && stale.UpdatedAt < quiescenceCutoff &&
                    stale.Status == DeploymentStatus.Failed &&
                    stale.FailureCode == "remote_quiescence_unconfirmed";
                var changed = stale is not null && stale.OwnershipToken is not null &&
                    (activeDeploymentExpired || quiescenceGraceExpired) ? 1 : 0;
                if (changed == 1)
                {
                    var ownedStale = stale!;
                    if (activeDeploymentExpired)
                    {
                        ownedStale.Status = DeploymentStatus.Failed;
                        ownedStale.FailureCode = "worker_lost";
                        ownedStale.FailureMessage = "Deployment stopped updating after the worker deadline; the remote lease grace elapsed and ownership was released.";
                        ownedStale.CompletedAt = now;
                    }
                    ownedStale.WorkerId = null;
                    ownedStale.OwnershipToken = null;
                    ownedStale.OwnershipAcquiredAt = null;
                    ownedStale.UpdatedAt = now;
                    db.DeploymentEvents.Add(new DeploymentEvent
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DeploymentId = id,
                        Level = EventLevel.Error,
                        Message = activeDeploymentExpired
                            ? "Worker heartbeat expired after the remote-quiescence grace; deployment marked failed and ownership released."
                            : "Remote-quiescence grace expired; retained deployment ownership released.",
                        CreatedAt = now
                    });
                    await db.SaveChangesAsync(cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                return changed;
            });
        }
        return failed + abandoned;
    }

    private async Task<int> FailAbandonedLocalBuildsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var now = _timeProvider.GetUtcNow();
        var cutoff = now - Minicloud.Common.Services.DeploymentPreparationService.HeartbeatTimeout;
        // Conditional updates serialize with heartbeats/admission without touching worker-owned rows.
        var candidates = db.Deployments.AsNoTracking().Where(d => d.Status == DeploymentStatus.WaitingForBuild);
        if (db.Database.IsNpgsql()) { candidates = candidates.Where(d => d.UpdatedAt < cutoff).OrderBy(d => d.UpdatedAt).Take(100); }
        var ids = (await candidates.ToListAsync(ct)).Where(d => d.UpdatedAt < cutoff).Select(d => d.Id).ToArray();
        var expired = 0;
        foreach (var id in ids)
        {
            int count;
            if (db.Database.IsNpgsql())
            {
                count = await db.Deployments.Where(d => d.Id == id && d.Status == DeploymentStatus.WaitingForBuild && d.UpdatedAt < cutoff)
                    .ExecuteUpdateAsync(set => set.SetProperty(d => d.Status, DeploymentStatus.Failed)
                        .SetProperty(d => d.FailureCode, "local_build_abandoned")
                        .SetProperty(d => d.FailureMessage, "The CLI stopped reporting local build progress. Run deploy again.")
                        .SetProperty(d => d.CompletedAt, now).SetProperty(d => d.UpdatedAt, now), ct);
            }
            else
            {
                var deployment = await db.Deployments.SingleAsync(d => d.Id == id, ct);
                count = deployment.Status == DeploymentStatus.WaitingForBuild && deployment.UpdatedAt < cutoff ? 1 : 0;
                if (count > 0)
                {
                    deployment.Status = DeploymentStatus.Failed;
                    deployment.FailureCode = "local_build_abandoned";
                    deployment.FailureMessage = "The CLI stopped reporting local build progress. Run deploy again.";
                    deployment.CompletedAt = now;
                    deployment.UpdatedAt = now;
                    await db.SaveChangesAsync(ct);
                }
            }
            expired += count;
            if (count > 0)
            {
                db.DeploymentEvents.Add(new DeploymentEvent { Id = Guid.NewGuid().ToString("N"), DeploymentId = id,
                    Level = EventLevel.Error, Message = "Local build heartbeat expired; deployment marked failed.", CreatedAt = now });
                await db.SaveChangesAsync(ct);
            }
        }
        return expired;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting PostgresDeploymentQueueConsumer [WorkerId: {WorkerId}, PollingInterval: {PollingInterval}]",
            _options.WorkerId,
            _options.PollingInterval);

        var active = new HashSet<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                active.RemoveWhere(task => task.IsCompleted);
                await FailStaleDeploymentsAsync(stoppingToken);
                while (active.Count < _options.MaxConcurrentDeployments)
                {
                    var deployment = await TryClaimNextDeploymentAsync(stoppingToken);
                    if (deployment is null)
                    {
                        break;
                    }

                    active.Add(ProcessDeploymentSafelyAsync(deployment, stoppingToken));
                }

                if (active.Count > 0)
                {
                    await Task.WhenAny(active).WaitAsync(_options.PollingInterval, _timeProvider, stoppingToken);
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in queue consumer polling loop");
            }

            try
            {
                await Task.Delay(_options.PollingInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await Task.WhenAll(active);

        _logger.LogInformation("PostgresDeploymentQueueConsumer stopped");
    }

    private async Task ProcessDeploymentSafelyAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessDeploymentAsync(deployment, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deployment {DeploymentId} task ended with an error", deployment.Id);
        }
    }
}
