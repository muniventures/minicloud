using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Common.Providers;
using Minicloud.Worker.Ssh;
using System.Diagnostics;
using System.Text.Json;

namespace Minicloud.Worker.Pipeline;

/// <summary>
/// Tunable timeouts and defaults for the worker deployment pipeline.
/// Bound from the <c>Worker:Pipeline</c> configuration section.
/// </summary>
public sealed class DeploymentPipelineOptions : ProviderPlanOptions
{
    public const string SectionName = "Worker:Pipeline";

    /// <summary>
    /// Public SSH key (or key id) injected into newly provisioned servers.
    /// </summary>
    public string SshPublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Absolute path to remote-deploy.sh on the worker host.
    /// </summary>
    public string RemoteDeployScriptPath { get; set; } = string.Empty;

    public TimeSpan ProvisionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan SshReadyTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan DnsPropagationTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan SshCommandTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(2);

    public long BuildCacheMaxBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public int BuildCacheMaxAgeHours { get; set; } = 168;

    public long BuildCacheMinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public int BuildCacheMinFreePercent { get; set; } = 10;

    public bool HasValidBuildCachePolicy =>
        BuildCacheMaxBytes is >= 268435456 and <= 17179869184 &&
        BuildCacheMinFreeBytes is >= 268435456 and <= 17179869184 &&
        BuildCacheMaxAgeHours is >= 1 and <= 720 &&
        BuildCacheMinFreePercent is >= 1 and <= 50;

    public int LogBatchSize { get; set; } = 50;

    public int MaxParallelBuilds { get; set; } = 1;

    public int MaxParallelNetworkOperations { get; set; } = 1;
}

/// <summary>
/// End-to-end deployment pipeline: resolve/provision -> DNS -> SSH deploy ->
/// health verify -> finalize. Owns <c>provisioning -> deploying -> verifying
/// -> succeeded/failed</c> transitions, batched log writes, events, and
/// best-effort rollback on step 3/4 failure.
/// </summary>
public sealed class DeploymentPipelineExecutor : IDeploymentPipelineExecutor
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ResolveProvisionServerStep _provisionStep;
    private readonly DnsSetupStep _dnsStep;
    private readonly SshDeployStep _sshStep;
    private readonly HealthCheckFinalizeStep _healthStep;
    private readonly ISshDeploymentClient _sshClient;
    private readonly SshDeploymentOptions _sshOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DeploymentPipelineExecutor>? _logger;
    private readonly SemaphoreSlim _timingEventGate = new(1, 1);

    public DeploymentPipelineExecutor(
        IServiceScopeFactory scopeFactory,
        ResolveProvisionServerStep provisionStep,
        DnsSetupStep dnsStep,
        SshDeployStep sshStep,
        HealthCheckFinalizeStep healthStep,
        ISshDeploymentClient sshClient,
        IOptions<SshDeploymentOptions> sshOptions,
        TimeProvider timeProvider,
        ILogger<DeploymentPipelineExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(provisionStep);
        ArgumentNullException.ThrowIfNull(dnsStep);
        ArgumentNullException.ThrowIfNull(sshStep);
        ArgumentNullException.ThrowIfNull(healthStep);
        ArgumentNullException.ThrowIfNull(sshClient);
        ArgumentNullException.ThrowIfNull(sshOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _provisionStep = provisionStep;
        _dnsStep = dnsStep;
        _sshStep = sshStep;
        _healthStep = healthStep;
        _sshClient = sshClient;
        _sshOptions = sshOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(Deployment deployment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        if (string.IsNullOrWhiteSpace(deployment.OwnershipToken))
        {
            throw new InvalidOperationException($"Deployment {deployment.Id} has no durable ownership token.");
        }

        var context = await BuildContextAsync(deployment.Id, deployment.OwnershipToken, cancellationToken);

        try
        {
            if (deployment.AssetReleaseId is null) { await RefreshDesiredServicesAsync(context, cancellationToken); }
            await RunStepAsync(_provisionStep, context, cancellationToken);
            if (deployment.AssetReleaseId is not null)
            {
                await TransitionAsync(context.DeploymentId, context.OwnershipToken, DeploymentStatus.Deploying, "Building asset release on customer server.", cancellationToken);
                await RunStepAsync(_sshStep, context, cancellationToken);
                await TransitionAsync(context.DeploymentId, context.OwnershipToken, DeploymentStatus.Verifying, "Asset outputs validated and published.", cancellationToken);
                await TransitionAsync(context.DeploymentId, context.OwnershipToken, DeploymentStatus.Succeeded, "Asset release ready; consumer bindings unchanged.", cancellationToken);
                return;
            }
            await _dnsStep.ResolveAndAssignHostnamesAsync(context, cancellationToken);

            await TransitionAsync(
                context.DeploymentId,
                context.OwnershipToken,
                DeploymentStatus.Deploying,
                "Server ready; DNS and artifact/build preparation starting concurrently.",
                cancellationToken);

            context.RequireRolloutGate = true;
            context.RemoteDirectory = SshDeployStep.RemoteDirectoryFor(context.DeploymentId);
            using (var preparationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var dnsTask = RunStepAsync(_dnsStep, context, preparationCts.Token);
                var sshTask = RunStepAsync(_sshStep, context, preparationCts.Token);
                Task gateTask = Task.CompletedTask;
                try
                {
                    var first = await Task.WhenAny(dnsTask, sshTask);
                    if (first == sshTask)
                    {
                        await sshTask;
                    }

                    await dnsTask;
                    gateTask = _sshStep.ReleaseRolloutGateAsync(context, preparationCts.Token);
                    if (await Task.WhenAny(gateTask, sshTask) == sshTask)
                    {
                        await sshTask;
                    }
                    await gateTask;
                    await sshTask;
                }
                catch (Exception preparationFailure)
                {
                    await preparationCts.CancelAsync();
                    var dnsFailure = await ObserveCancellationAsync(dnsTask);
                    var sshFailure = await ObserveCancellationAsync(sshTask);
                    var gateFailure = await ObserveCancellationAsync(gateTask);
                    var quiescenceFailure = preparationFailure as RemoteQuiescenceException
                        ?? dnsFailure as RemoteQuiescenceException
                        ?? sshFailure as RemoteQuiescenceException
                        ?? gateFailure as RemoteQuiescenceException;
                    if (quiescenceFailure is not null)
                    {
                        throw quiescenceFailure;
                    }
                    if (ReferenceEquals(preparationFailure, gateFailure) && sshFailure is not null && sshFailure is not OperationCanceledException)
                    {
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sshFailure).Throw();
                    }

                    throw;
                }
            }

            await TransitionAsync(
                context.DeploymentId,
                context.OwnershipToken,
                DeploymentStatus.Verifying,
                "Build done, containers started; running health checks.",
                cancellationToken);

            await RunStepAsync(_healthStep, context, cancellationToken);

            _logger?.LogInformation("Deployment {DeploymentId} pipeline succeeded", context.DeploymentId);
        }
        catch (PipelineStepException ex)
        {
            _logger?.LogError(ex, "Deployment {DeploymentId} pipeline failed at {Code}", context.DeploymentId, ex.FailureCode);
            var needsRollback = deployment.AssetReleaseId is null && _sshStep is not null && (ex.FailureCode == SshDeployStep.FailureCode || ex.FailureCode == HealthCheckFinalizeStep.FailureCode);
            if (needsRollback)
            {
                await TryRollbackAsync(context, ex.FailureCode, cancellationToken);
            }

            await MarkFailedAsync(context.DeploymentId, context.OwnershipToken, ex.FailureCode, ex.Message, cancellationToken);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteQuiescenceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Deployment {DeploymentId} pipeline failed unexpectedly", context.DeploymentId);
            await MarkFailedAsync(context.DeploymentId, context.OwnershipToken, "internal_error", ex.Message, cancellationToken);
            throw;
        }
    }

    internal Task RefreshDesiredServicesAsync(PipelineStepContext context, CancellationToken ct) => Steps.PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, token) =>
    {
        if (db.Database.IsNpgsql()) { await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({Minicloud.Common.Registry.ImagePublicationService.LockKey})", token); }
        var stored = await db.Deployments.Include(x => x.Services).Include(x => x.App)
            .SingleAsync(x => x.Id == context.DeploymentId && x.OwnershipToken == context.OwnershipToken, token);
        if (stored.RequestedServiceNamesJson is null) { return; }
        var names = JsonSerializer.Deserialize<HashSet<string>>(stored.RequestedServiceNamesJson)!;
        var requested = stored.Services.Where(x => names.Contains(x.Name)).ToArray();
        var desired = await new DeploymentServiceSetService().BuildMergedServicesAsync(db, stored.AppId, requested, token);
        var managedInfrastructureOnly = desired.Count == 0 && stored.Database == DatabaseMode.Postgres &&
            await db.ManagedDatabaseServers.AnyAsync(server => server.OrganizationId == stored.OrganizationId &&
                server.RuntimeAppId == stored.AppId && server.RuntimeDeploymentId == stored.Id && server.Status != "deleted", token);
        var validation = Minicloud.Common.Validation.ControlPlaneValidation.ValidateMergedDeploymentServices(desired, stored.Database == DatabaseMode.Sqlite);
        if (!managedInfrastructureOnly && !validation.IsValid) { throw new PipelineStepException("invalid_service_set", "Active service set changed incompatibly while deployment was queued."); }
        var retained = stored.Services.Where(x => !names.Contains(x.Name)).ToArray();
        db.DeploymentServices.RemoveRange(retained);
        foreach (var service in retained) { stored.Services.Remove(service); }
        new DeploymentLifecycleService(new IdGenerator(), _timeProvider).AddRetainedServices(stored, desired);
        if (stored.Services.Any(x => x.ImagePublicationId != null))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            // Validate against current metadata before provisioning; runtime verifies the actual host platform again.
            var images = scope.ServiceProvider.GetRequiredService<Minicloud.Common.Registry.ImagePublicationService>();
            await images.FinalizeDeploymentAsync(stored.App, stored, token, db);
        }
        await db.SaveChangesAsync(token);
    }, ct);

    private static async Task<Exception?> ObserveCancellationAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task<PipelineStepContext> BuildContextAsync(
        string deploymentId, string ownershipToken, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var deployment = await db.Deployments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == deploymentId && d.OwnershipToken == ownershipToken, cancellationToken);
        if (deployment is null)
        {
            throw new InvalidOperationException($"Deployment {deploymentId} not found.");
        }

        if (deployment.Status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled)
        {
            throw new InvalidOperationException($"Deployment {deploymentId} is already terminal ({deployment.Status}).");
        }

        return new PipelineStepContext
        {
            DeploymentId = deployment.Id,
            OrganizationId = deployment.OrganizationId,
            AppId = deployment.AppId,
            OwnershipToken = ownershipToken,
            CommitSha = deployment.CommitSha ?? string.Empty,
        };
    }

    private async Task RunStepAsync(IPipelineStep step, PipelineStepContext context, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stopwatch = Stopwatch.StartNew();
        _logger?.LogInformation("Pipeline step {Step} starting for deployment {DeploymentId}", step.Name, context.DeploymentId);
        try
        {
            await step.ExecuteAsync(context, cancellationToken);
            stopwatch.Stop();
            await RecordPhaseTimingAsync(context.DeploymentId, context.OwnershipToken, step.Name, startedAt, stopwatch.Elapsed, "succeeded", CancellationToken.None);
            _logger?.LogInformation("Pipeline step {Step} completed for deployment {DeploymentId}", step.Name, context.DeploymentId);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            await RecordPhaseTimingAsync(context.DeploymentId, context.OwnershipToken, step.Name, startedAt, stopwatch.Elapsed, "canceled", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Pipeline step {Step} failed for deployment {DeploymentId}", step.Name, context.DeploymentId);
            stopwatch.Stop();
            await RecordPhaseTimingAsync(context.DeploymentId, context.OwnershipToken, step.Name, startedAt, stopwatch.Elapsed, "failed", CancellationToken.None);
            throw;
        }
    }

    private Task RecordPhaseTimingAsync(
        string deploymentId, string ownershipToken, string phase, DateTimeOffset startedAt, TimeSpan elapsed, string outcome,
        CancellationToken cancellationToken)
    {
        var completedAt = _timeProvider.GetUtcNow();
        var metadata = JsonSerializer.Serialize(new
        {
            phase,
            started_at_utc = startedAt,
            completed_at_utc = completedAt,
            duration_ms = Math.Round(elapsed.TotalMilliseconds, 3),
            outcome,
        });
        return RecordPhaseTimingCoreAsync();

        async Task RecordPhaseTimingCoreAsync()
        {
            await _timingEventGate.WaitAsync(cancellationToken);
            try
            {
                await Steps.PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
                {
                    if (!await db.Deployments.AnyAsync(
                            d => d.Id == deploymentId && d.OwnershipToken == ownershipToken, ct))
                    {
                        return;
                    }
                    db.DeploymentEvents.Add(new DeploymentEvent
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DeploymentId = deploymentId,
                        Level = outcome == "failed" ? EventLevel.Error : EventLevel.Info,
                        Message = $"Deployment phase '{phase}' {outcome} in {elapsed.TotalMilliseconds:F0} ms.",
                        MetadataJson = metadata,
                        CreatedAt = completedAt,
                    });
                    await Task.CompletedTask;
                }, cancellationToken);
            }
            finally
            {
                _timingEventGate.Release();
            }
        }
    }

    private Task TransitionAsync(
        string deploymentId,
        string ownershipToken,
        DeploymentStatus nextStatus,
        string message,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return Steps.PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            var deployment = await DeploymentMutation.LockAsync(db, deploymentId, ct);
            if (deployment is not null && deployment.OwnershipToken != ownershipToken) { deployment = null; }
            if (deployment is null)
            {
                throw new InvalidOperationException($"Deployment {deploymentId} ownership is no longer current.");
            }

            if (deployment.CancellationRequestedAt is not null) { throw new OperationCanceledException("Deployment cancellation requested."); }
            if (!IsAllowed(deployment.Status, nextStatus))
            {
                throw new InvalidOperationException(
                    $"Invalid deployment transition {deployment.Status} -> {nextStatus}.");
            }

            deployment.Status = nextStatus;
            if (nextStatus == DeploymentStatus.Succeeded)
            {
                deployment.CompletedAt = now;
                deployment.WorkerId = null;
                deployment.OwnershipToken = null;
                deployment.OwnershipAcquiredAt = null;
            }
            Steps.PipelineDb.Touch(deployment, now);
            Steps.PipelineDb.AddEvent(db, deployment.Id, EventLevel.Info, message, now);
            await Task.CompletedTask;
        }, cancellationToken);
    }

    private Task MarkFailedAsync(
        string deploymentId, string ownershipToken, string failureCode, string failureMessage, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return Steps.PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            var deployment = await DeploymentMutation.LockAsync(db, deploymentId, ct);
            if (deployment is not null && deployment.OwnershipToken != ownershipToken) { deployment = null; }
            if (deployment is null)
            {
                return;
            }

            if (deployment.Status is DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled)
            {
                return;
            }

            if (deployment.CancellationRequestedAt is not null) { throw new OperationCanceledException("Deployment cancellation requested."); }
            deployment.Status = DeploymentStatus.Failed;
            deployment.FailureCode = failureCode;
            deployment.FailureMessage = failureMessage;
            deployment.CompletedAt = now;
            deployment.UpdatedAt = now;
            deployment.WorkerId = null;
            deployment.OwnershipToken = null;
            deployment.OwnershipAcquiredAt = null;
            Steps.PipelineDb.AddEvent(db, deployment.Id, EventLevel.Error,
                $"Deployment pipeline failed: {failureMessage}", now,
                $"{{\"failure_code\":\"{failureCode}\"}}");
            await Task.CompletedTask;
        }, cancellationToken);
    }

    private async Task TryRollbackAsync(
        PipelineStepContext context, string failureCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.ServerIp))
        {
            return;
        }

        var privateKey = _sshOptions.PrivateKeyContent;
        if (!string.IsNullOrWhiteSpace(_sshOptions.PrivateKeyPath) && File.Exists(_sshOptions.PrivateKeyPath))
        {
            try
            {
                privateKey = await File.ReadAllTextAsync(_sshOptions.PrivateKeyPath, cancellationToken);
            }
            catch
            {
                privateKey = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            await RecordEventAsync(context.DeploymentId, EventLevel.Warning,
                $"Rollback skipped for {failureCode}: no SSH private key available.", cancellationToken);
            return;
        }

        // Best-effort: restore the previous Caddy config if the deploy staged one,
        // then re-list containers. Never fails the pipeline.
        var appSlug = string.IsNullOrWhiteSpace(context.AppSlug) ? "*" : context.AppSlug;
        var rollbackCommand = string.Join(" && ",
            $"test -f /opt/minicloud/apps/{appSlug}/shared/*.caddy.previous && install -m 0644 /opt/minicloud/apps/{appSlug}/shared/*.caddy.previous /opt/minicloud/caddy/apps/{appSlug}.caddy || true",
            "sudo -u caddy caddy validate --config /etc/caddy/Caddyfile >/dev/null 2>&1 && sudo systemctl reload caddy || true",
            "echo rollback-attempted");

        try
        {
            var lines = new List<string>();
            var exitCode = await _sshClient.ExecuteCommandAsync(
                context.ServerIp,
                string.IsNullOrWhiteSpace(context.SshUsername) ? _sshOptions.Username : context.SshUsername,
                privateKey,
                rollbackCommand,
                (source, line) =>
                {
                    lines.Add(line);
                    return Task.CompletedTask;
                },
                cancellationToken);

            await RecordEventAsync(context.DeploymentId,
                EventLevel.Warning,
                $"Rollback command attempted after {failureCode} (exit {exitCode}); previous release health was not verified.",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordEventAsync(context.DeploymentId, EventLevel.Warning,
                $"Rollback attempt after {failureCode} failed: {ex.Message}", cancellationToken);
        }
    }

    private Task RecordEventAsync(
        string deploymentId, EventLevel level, string message, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        return Steps.PipelineDb.ExecuteInTransactionAsync(_scopeFactory, async (db, ct) =>
        {
            Steps.PipelineDb.AddEvent(db, deploymentId, level, message, now);
            var deployment = await db.Deployments
                .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
            if (deployment is not null)
            {
                Steps.PipelineDb.Touch(deployment, now);
            }

            await Task.CompletedTask;
        }, cancellationToken);
    }

    private static bool IsAllowed(DeploymentStatus current, DeploymentStatus next)
    {
        if (next == DeploymentStatus.Failed || next == DeploymentStatus.Canceled)
        {
            return current is not (DeploymentStatus.Succeeded or DeploymentStatus.Failed or DeploymentStatus.Canceled);
        }

        return (current, next) switch
        {
            (DeploymentStatus.Queued, DeploymentStatus.Provisioning) => true,
            (DeploymentStatus.Queued, DeploymentStatus.Dispatching) => true,
            (DeploymentStatus.Dispatching, DeploymentStatus.Provisioning) => true,
            (DeploymentStatus.Dispatching, DeploymentStatus.Deploying) => true,
            (DeploymentStatus.Provisioning, DeploymentStatus.Deploying) => true,
            (DeploymentStatus.Deploying, DeploymentStatus.Verifying) => true,
            (DeploymentStatus.Verifying, DeploymentStatus.Succeeded) => true,
            _ => false,
        };
    }
}
