using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Queue;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Queue;

public sealed class PostgresDeploymentQueueConsumerTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public PostgresDeploymentQueueConsumerTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedCancellationStopsPipelineAndWaitsForCleanup(bool remoteUnknown)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("cancel" + remoteUnknown);
        var deployment = CreateDeployment("dep-cancel-" + remoteUnknown, org.Id, app.Id, DeploymentStatus.Queued, DateTimeOffset.UtcNow);
        await using (var db = _harness.CreateDbContext()) { db.Add(deployment); await db.SaveChangesAsync(); }
        var executor = new CancelablePipeline(remoteUnknown);
        var consumer = CreateConsumer("worker-cancel", executor: executor);
        var claimed = (await consumer.TryClaimNextDeploymentAsync(default))!;
        var processing = consumer.ProcessDeploymentAsync(claimed, default);
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var db = _harness.CreateDbContext())
        {
            var stored = await db.Deployments.FindAsync(claimed.Id);
            stored!.CancellationRequestedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await executor.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using (var db = _harness.CreateDbContext())
        {
            var stored = await db.Deployments.FindAsync(claimed.Id);
            Assert.NotNull(stored!.OwnershipToken);
            Assert.Null(stored.CompletedAt);
            Assert.Equal(DeploymentStatus.Provisioning, stored.Status);
        }
        executor.CleanupFinished.SetResult();
        if (remoteUnknown) { await Assert.ThrowsAsync<RemoteQuiescenceException>(() => processing); }
        else { await processing.WaitAsync(TimeSpan.FromSeconds(5)); }
        await using var verify = _harness.CreateDbContext();
        var final = await verify.Deployments.FindAsync(claimed.Id);
        Assert.Equal(remoteUnknown ? DeploymentStatus.Failed : DeploymentStatus.Canceled, final!.Status);
        Assert.NotNull(final.CompletedAt);
        if (remoteUnknown) { Assert.NotNull(final.OwnershipToken); Assert.Equal("remote_quiescence_unconfirmed", final.FailureCode); }
        else { Assert.Null(final.OwnershipToken); Assert.Null(final.FailureCode); }
    }

    private sealed class CancelablePipeline(bool remoteUnknown) : IDeploymentPipelineExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(Deployment deployment, CancellationToken ct)
        {
            Started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                Stopping.SetResult();
                await CleanupFinished.Task;
                if (remoteUnknown) { throw new RemoteQuiescenceException("stop could not be confirmed"); }
                throw;
            }
        }
    }

    [Fact]
    public async Task QueueWaitStartsAtAdmissionAfterLocalBuild()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("build_queue_timing");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            var deployment = CreateDeployment("dep-build-queue-timing", org.Id, app.Id, DeploymentStatus.Queued, now.AddHours(-1));
            deployment.UpdatedAt = now;
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }
        var claimed = (await CreateConsumer().TryClaimNextDeploymentAsync(default))!;
        await using var check = _harness.CreateDbContext();
        var claimEvent = await check.DeploymentEvents.SingleAsync(x => x.DeploymentId == claimed.Id);
        using var metadata = System.Text.Json.JsonDocument.Parse(claimEvent.MetadataJson!);
        Assert.InRange(metadata.RootElement.GetProperty("duration_ms").GetDouble(), 0, 60000);
    }

    [Fact]
    public async Task LocalBuildWaiting_IsNotClaimedAndExcludesSameAppUntilExpired()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("local_waiting");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.AddRange(
                CreateDeployment("dep-local-wait", org.Id, app.Id, DeploymentStatus.WaitingForBuild, now),
                CreateDeployment("dep-local-queued", org.Id, app.Id, DeploymentStatus.Queued, now));
            await db.SaveChangesAsync();
        }
        var consumer = CreateConsumer();
        Assert.Null(await consumer.TryClaimNextDeploymentAsync(default));
        Assert.Equal(0, await consumer.FailStaleDeploymentsAsync(default));
        await using (var db = _harness.CreateDbContext())
        {
            (await db.Deployments.FindAsync("dep-local-wait"))!.UpdatedAt = now.AddMinutes(-6);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await consumer.FailStaleDeploymentsAsync(default));
        await using (var db = _harness.CreateDbContext())
        {
            var expired = (await db.Deployments.FindAsync("dep-local-wait"))!;
            Assert.Equal(DeploymentStatus.Failed, expired.Status);
            Assert.Equal("local_build_abandoned", expired.FailureCode);
            Assert.NotNull(expired.CompletedAt);
        }
        Assert.Equal("dep-local-queued", (await consumer.TryClaimNextDeploymentAsync(default))!.Id);
        Assert.Equal(0, await consumer.FailStaleDeploymentsAsync(default));
    }

    [Fact]
    public async Task NeverClaimsDeploymentForDeletingApp()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("deleting");
        await using (var db = _harness.CreateDbContext())
        {
            var stored = await db.Apps.FindAsync(app.Id);
            stored!.DeletionStatus = "queued";
            db.Deployments.Add(CreateDeployment("dep-deleting", org.Id, app.Id, DeploymentStatus.Queued, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        Assert.Null(await CreateConsumer().TryClaimNextDeploymentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClaimsQueuedDeploymentsInFifoOrder()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("fifo");
        var baseTime = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.AddRange(
                CreateDeployment("dep-fifo-2", org.Id, app.Id, DeploymentStatus.Queued, baseTime.AddMinutes(2)),
                CreateDeployment("dep-fifo-1", org.Id, app.Id, DeploymentStatus.Queued, baseTime.AddMinutes(1)),
                CreateDeployment("dep-fifo-3", org.Id, app.Id, DeploymentStatus.Queued, baseTime.AddMinutes(3))
            );
            await db.SaveChangesAsync();
        }

        var consumer = CreateConsumer();

        // 1st claim must be dep-fifo-1 (oldest)
        var claim1 = await consumer.TryClaimNextDeploymentAsync(CancellationToken.None);
        Assert.NotNull(claim1);
        Assert.Equal("dep-fifo-1", claim1.Id);
        Assert.Equal(DeploymentStatus.Provisioning, claim1.Status);
        Assert.Equal("test-worker", claim1.WorkerId);
        Assert.False(string.IsNullOrWhiteSpace(claim1.OwnershipToken));
        Assert.NotNull(claim1.OwnershipAcquiredAt);

        // Verify in DB that status is Provisioning
        await using (var db = _harness.CreateDbContext())
        {
            var inDb = await db.Deployments.FindAsync(claim1.Id);
            Assert.NotNull(inDb);
            Assert.Equal(DeploymentStatus.Provisioning, inDb.Status);
        }

        // Same-app work remains excluded until the active deployment is terminal.
        Assert.Null(await consumer.TryClaimNextDeploymentAsync(CancellationToken.None));
        await MarkSucceededAsync(claim1.Id);

        // 2nd claim must be dep-fifo-2 after dep-fifo-1 completes.
        var claim2 = await consumer.TryClaimNextDeploymentAsync(CancellationToken.None);
        Assert.NotNull(claim2);
        Assert.Equal("dep-fifo-2", claim2.Id);
        await MarkSucceededAsync(claim2.Id);

        // 3rd claim must be dep-fifo-3
        var claim3 = await consumer.TryClaimNextDeploymentAsync(CancellationToken.None);
        Assert.NotNull(claim3);
        Assert.Equal("dep-fifo-3", claim3.Id);
        await MarkSucceededAsync(claim3.Id);

        // 4th claim: queue is empty
        var claim4 = await consumer.TryClaimNextDeploymentAsync(CancellationToken.None);
        Assert.Null(claim4);
    }

    private async Task MarkSucceededAsync(string deploymentId)
    {
        await using var db = _harness.CreateDbContext();
        var deployment = await db.Deployments.FindAsync(deploymentId);
        deployment!.Status = DeploymentStatus.Succeeded;
        deployment.WorkerId = null;
        deployment.OwnershipToken = null;
        deployment.OwnershipAcquiredAt = null;
        deployment.CompletedAt = DateTimeOffset.UtcNow;
        deployment.UpdatedAt = deployment.CompletedAt.Value;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task IgnoresDeploymentsNotInQueuedStatus()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("ignore_status");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.AddRange(
                CreateDeployment("dep-prov", org.Id, app.Id, DeploymentStatus.Provisioning, now),
                CreateDeployment("dep-succ", org.Id, app.Id, DeploymentStatus.Succeeded, now),
                CreateDeployment("dep-fail", org.Id, app.Id, DeploymentStatus.Failed, now),
                CreateDeployment("dep-canc", org.Id, app.Id, DeploymentStatus.Canceled, now)
            );
            await db.SaveChangesAsync();
        }

        var consumer = CreateConsumer();
        var claim = await consumer.TryClaimNextDeploymentAsync(CancellationToken.None);
        Assert.Null(claim);
    }

    [Fact]
    public async Task ConcurrentWorkersDoNotClaimTheSameDeployment()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("concurrency");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(CreateDeployment("dep-single", org.Id, app.Id, DeploymentStatus.Queued, now));
            await db.SaveChangesAsync();
        }

        var consumerA = CreateConsumer("worker-A");
        var consumerB = CreateConsumer("worker-B");

        // Attempt concurrent claims
        var taskA = consumerA.TryClaimNextDeploymentAsync(CancellationToken.None);
        var taskB = consumerB.TryClaimNextDeploymentAsync(CancellationToken.None);

        var results = await Task.WhenAll(taskA, taskB);

        // Exactly one worker must claim the deployment, the other must get null
        var claimedCount = results.Count(r => r is not null);
        Assert.Equal(1, claimedCount);

        var winner = results.Single(r => r is not null);
        Assert.Equal("dep-single", winner!.Id);
        Assert.Equal(DeploymentStatus.Provisioning, winner.Status);
    }

    [Fact]
    public async Task ProcessDeployment_ExecutesPipelineAndRecordsCompletion()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("pipeline_success");
        var now = DateTimeOffset.UtcNow;
        var deployment = CreateDeployment("dep-exec", org.Id, app.Id, DeploymentStatus.Provisioning, now);
        Own(deployment, "worker-exec");

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        var testExecutor = new RecordingPipelineExecutor();
        var consumer = CreateConsumer("worker-exec", executor: testExecutor);

        await consumer.ProcessDeploymentAsync(deployment, CancellationToken.None);

        Assert.Single(testExecutor.ExecutedDeployments);
        Assert.Equal("dep-exec", testExecutor.ExecutedDeployments[0].Id);
    }

    [Fact]
    public async Task ProcessDeployment_OnPipelineFailure_MarksDeploymentFailed()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("pipeline_failure");
        var now = DateTimeOffset.UtcNow;
        var deployment = CreateDeployment("dep-failing", org.Id, app.Id, DeploymentStatus.Provisioning, now);
        Own(deployment, "worker-failing");

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        var failingExecutor = new RecordingPipelineExecutor(throwException: new InvalidOperationException("VPS SSH handshake failed"));
        var consumer = CreateConsumer("worker-failing", executor: failingExecutor);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.ProcessDeploymentAsync(deployment, CancellationToken.None));

        await using (var db = _harness.CreateDbContext())
        {
            var updated = await db.Deployments
                .Include(d => d.Events)
                .SingleAsync(d => d.Id == "dep-failing");

            Assert.Equal(DeploymentStatus.Failed, updated.Status);
            Assert.Equal("worker_pipeline_error", updated.FailureCode);
            Assert.Contains("VPS SSH handshake failed", updated.FailureMessage);
            Assert.NotNull(updated.CompletedAt);
            Assert.Null(updated.OwnershipToken);
            Assert.Contains(updated.Events, e => e.Level == EventLevel.Error && e.Message.Contains("VPS SSH handshake failed"));
        }
    }

    [Fact]
    public async Task ProcessDeployment_OnTimeout_MarksDeploymentFailedWithTimeout()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("pipeline_timeout");
        var now = DateTimeOffset.UtcNow;
        var deployment = CreateDeployment("dep-timed-out", org.Id, app.Id, DeploymentStatus.Provisioning, now);
        Own(deployment, "worker-timeout");

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        var hangingExecutor = new RecordingPipelineExecutor(delay: TimeSpan.FromSeconds(5));
        var options = new WorkerOptions
        {
            WorkerId = "worker-timeout",
            DeploymentTimeout = TimeSpan.FromMilliseconds(100),
            HeartbeatInterval = TimeSpan.FromMilliseconds(50)
        };
        var consumer = CreateConsumer("worker-timeout", options: options, executor: hangingExecutor);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            consumer.ProcessDeploymentAsync(deployment, CancellationToken.None));

        await using (var db = _harness.CreateDbContext())
        {
            var updated = await db.Deployments.SingleAsync(d => d.Id == "dep-timed-out");
            Assert.Equal(DeploymentStatus.Failed, updated.Status);
            Assert.Equal("worker_timeout", updated.FailureCode);
            Assert.NotNull(updated.CompletedAt);
            Assert.Equal("worker-timeout", updated.WorkerId);
            Assert.NotNull(updated.OwnershipToken);
        }
    }

    [Fact]
    public async Task ProcessDeployment_WhenRemoteQuiescenceIsUnknown_RetainsOwnership()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("remote_quiescence");
        var deployment = CreateDeployment("dep-quiescence", org.Id, app.Id, DeploymentStatus.Provisioning, DateTimeOffset.UtcNow);
        Own(deployment, "worker-quiescence");
        var token = deployment.OwnershipToken;
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        var executor = new RecordingPipelineExecutor(
            throwException: new RemoteQuiescenceException("remote process group is still active"));
        var consumer = CreateConsumer("worker-quiescence", executor: executor);

        await Assert.ThrowsAsync<RemoteQuiescenceException>(() =>
            consumer.ProcessDeploymentAsync(deployment, CancellationToken.None));

        await using var verify = _harness.CreateDbContext();
        var updated = await verify.Deployments.SingleAsync(item => item.Id == deployment.Id);
        Assert.Equal(DeploymentStatus.Failed, updated.Status);
        Assert.Equal("remote_quiescence_unconfirmed", updated.FailureCode);
        Assert.Equal(token, updated.OwnershipToken);
        Assert.Equal("worker-quiescence", updated.WorkerId);
    }

    [Fact]
    public async Task FailStaleDeployment_ReleasesFailedRemoteQuiescenceOwnership_AndUnblocksSameAppClaim()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("quiescence_reclaim");
        var failed = CreateDeployment("dep-quiescence-reclaim", org.Id, app.Id,
            DeploymentStatus.Failed, DateTimeOffset.UtcNow.AddMinutes(-5));
        Own(failed, "dead-worker");
        failed.FailureCode = "remote_quiescence_unconfirmed";
        failed.FailureMessage = "Remote process termination was not confirmed.";
        failed.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        failed.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var queued = CreateDeployment("dep-after-quiescence", org.Id, app.Id,
            DeploymentStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(-1));
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.AddRange(failed, queued);
            await db.SaveChangesAsync();
        }

        var consumer = CreateConsumer(options: new WorkerOptions
        {
            WorkerId = "reconciler",
            PollingInterval = TimeSpan.FromMilliseconds(10),
            HeartbeatInterval = TimeSpan.FromMilliseconds(10),
            DeploymentTimeout = TimeSpan.FromMinutes(15),
            MaxConcurrentDeployments = 1
        });
        Assert.Equal(1, await consumer.FailStaleDeploymentsAsync(CancellationToken.None));
        var claimed = await consumer.TryClaimNextDeploymentAsync(CancellationToken.None);
        Assert.Equal(queued.Id, claimed?.Id);

        await using var verify = _harness.CreateDbContext();
        var released = await verify.Deployments.FindAsync(failed.Id);
        Assert.NotNull(released);
        Assert.Equal(DeploymentStatus.Failed, released.Status);
        Assert.Equal("remote_quiescence_unconfirmed", released.FailureCode);
        Assert.Null(released.WorkerId);
        Assert.Null(released.OwnershipToken);
        Assert.Null(released.OwnershipAcquiredAt);
    }

    [Fact]
    public async Task ProcessDeployment_RejectsStaleOwnershipBeforePipelineExecution()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("stale_owner");
        var deployment = CreateDeployment("dep-stale-owner", org.Id, app.Id, DeploymentStatus.Provisioning, DateTimeOffset.UtcNow);
        Own(deployment, "worker-old");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
            deployment.WorkerId = "worker-new";
            deployment.OwnershipToken = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync();
        }

        var executor = new RecordingPipelineExecutor();
        var consumer = CreateConsumer("worker-old", executor: executor);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.ProcessDeploymentAsync(deployment, CancellationToken.None));
        Assert.Empty(executor.ExecutedDeployments);
    }

    [Fact]
    public async Task FailStaleDeployment_ReleasesOwnershipAfterRemoteLeaseGrace_AndUnblocksSameAppClaim()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("stale_reclaim");
        var stale = CreateDeployment("dep-stale-reclaim", org.Id, app.Id,
            DeploymentStatus.Deploying, DateTimeOffset.UtcNow.AddHours(-2));
        Own(stale, "dead-worker");
        stale.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var queued = CreateDeployment("dep-after-stale", org.Id, app.Id,
            DeploymentStatus.Queued, DateTimeOffset.UtcNow.AddMinutes(-1));
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.AddRange(stale, queued);
            await db.SaveChangesAsync();
        }

        var consumer = CreateConsumer(options: new WorkerOptions
        {
            WorkerId = "reconciler",
            PollingInterval = TimeSpan.FromMilliseconds(10),
            HeartbeatInterval = TimeSpan.FromMilliseconds(10),
            DeploymentTimeout = TimeSpan.FromSeconds(1),
            MaxConcurrentDeployments = 1
        });
        Assert.Equal(1, await consumer.FailStaleDeploymentsAsync(CancellationToken.None));
        var claimed = await consumer.TryClaimNextDeploymentAsync(CancellationToken.None);
        Assert.Equal(queued.Id, claimed?.Id);

        await using var verify = _harness.CreateDbContext();
        var failed = await verify.Deployments.FindAsync(stale.Id);
        Assert.NotNull(failed);
        Assert.Equal(DeploymentStatus.Failed, failed.Status);
        Assert.Equal("worker_lost", failed.FailureCode);
        Assert.Null(failed.WorkerId);
        Assert.Null(failed.OwnershipToken);
        Assert.Null(failed.OwnershipAcquiredAt);
    }

    private PostgresDeploymentQueueConsumer CreateConsumer(
        string workerId = "test-worker",
        WorkerOptions? options = null,
        IDeploymentPipelineExecutor? executor = null)
    {
        var pipelineExecutor = executor ?? new RecordingPipelineExecutor();
        var scopeFactory = _harness.CreateScopeFactory(services =>
        {
            services.AddScoped<IDeploymentPipelineExecutor>(_ => pipelineExecutor);
        });
        var workerOptions = Options.Create(options ?? new WorkerOptions
        {
            WorkerId = workerId,
            PollingInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
            DeploymentTimeout = TimeSpan.FromSeconds(30)
        });
        return new PostgresDeploymentQueueConsumer(
            scopeFactory,
            workerOptions,
            TimeProvider.System,
            NullLogger<PostgresDeploymentQueueConsumer>.Instance);
    }

    private static Deployment CreateDeployment(string id, string orgId, string appId, DeploymentStatus status, DateTimeOffset createdAt) =>
        new()
        {
            Id = id,
            OrganizationId = orgId,
            AppId = appId,
            Status = status,
            Database = DatabaseMode.None,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static void Own(Deployment deployment, string workerId)
    {
        deployment.WorkerId = workerId;
        deployment.OwnershipToken = Guid.NewGuid().ToString("N");
        deployment.OwnershipAcquiredAt = DateTimeOffset.UtcNow;
    }

    private sealed class RecordingPipelineExecutor(
        Exception? throwException = null,
        TimeSpan? delay = null) : IDeploymentPipelineExecutor
    {
        public List<Deployment> ExecutedDeployments { get; } = [];

        public async Task ExecuteAsync(Deployment deployment, CancellationToken cancellationToken)
        {
            ExecutedDeployments.Add(deployment);
            if (delay is not null && delay.Value > TimeSpan.Zero)
            {
                await Task.Delay(delay.Value, cancellationToken);
            }
            if (throwException is not null)
            {
                throw throwException;
            }
        }
    }
}
