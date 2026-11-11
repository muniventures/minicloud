using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Databases;
using Minicloud.Worker.Deletion;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Queue;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Queue;

public sealed class MaintenanceClaimTests : IAsyncLifetime
{
    private const string Installation = "inst-worker-maintenance";
    private readonly WorkerSqliteTestHarness _harness = new();

    public async Task InitializeAsync()
    {
        await _harness.InitializeAsync();
        await using var db = _harness.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.BootstrapRecords.Add(new BootstrapRecord { InstallationId = Installation, OperationId = "op-b", State = "ready", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _harness.DisposeAsync();

    private async Task<T> Operator<T>(string? host, Func<MaintenanceAuthorityService, Task<T>> action)
    {
        await using var db = _harness.CreateDbContext();
        return await action(new MaintenanceAuthorityService(db, TimeProvider.System, host));
    }

    private (PostgresDeploymentQueueConsumer Consumer, Executor Executor, MaintenanceParticipant Participant) Consumer(string? host)
    {
        var executor = new Executor();
        var scopes = _harness.CreateScopeFactory(services => services.AddScoped<IDeploymentPipelineExecutor>(_ => executor));
        var options = Options.Create(new WorkerOptions
        {
            WorkerId = "worker-" + (host ?? "unbound"), PollingInterval = TimeSpan.FromMilliseconds(50),
            HeartbeatInterval = TimeSpan.FromMilliseconds(100), DeploymentTimeout = TimeSpan.FromSeconds(30)
        });
        var participant = new MaintenanceParticipant(scopes, "worker", host, TimeProvider.System);
        return (new PostgresDeploymentQueueConsumer(scopes, options, TimeProvider.System,
            NullLogger<PostgresDeploymentQueueConsumer>.Instance, participant), executor, participant);
    }

    private async Task AddDeploymentAsync(string id, string appId)
    {
        await using var db = _harness.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.Deployments.Add(new Deployment { Id = id, OrganizationId = "org_" + appId[4..], AppId = appId, Status = DeploymentStatus.Queued,
            Database = DatabaseMode.None, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task FencedWorkerFinishesAcceptedWorkAndDrainCompletes()
    {
        var (_, app) = await _harness.SeedOrgAndAppAsync("fenced");
        await AddDeploymentAsync("dep_accepted", app.Id);
        var (consumer, executor, participant) = Consumer("host-a");
        await Operator("host-a", s => s.FenceAsync(Installation, "op-upgrade"));
        Assert.False((await Operator("host-a", s => s.DrainStatusAsync(Installation))).Drained);

        // Accepted work queued before the fence is still claimed and run to completion while fenced.
        var claimed = await consumer.TryClaimNextDeploymentAsync(default);
        Assert.Equal("dep_accepted", claimed?.Id);
        await consumer.ProcessDeploymentAsync(claimed!, default);
        Assert.Single(executor.Executed);
        await using (var db = _harness.CreateDbContext())
        {
            // The fake executor does not finish the deployment; record a successful outcome as the real pipeline would.
            await db.Deployments.ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DeploymentStatus.Succeeded)
                .SetProperty(d => d.OwnershipToken, (string?)null).SetProperty(d => d.WorkerId, (string?)null));
        }
        Assert.True((await participant.RegisterAsync(default)).IsAdmitted);
        var drained = await Operator("host-a", s => s.DrainStatusAsync(Installation));
        Assert.True(drained.Drained);
        Assert.Contains(drained.Participants, x => x.Role == "worker" && x.Current);
    }

    [Fact]
    public async Task StaleEpochAndForeignHostWorkersCannotClaimNewWork()
    {
        var (_, app) = await _harness.SeedOrgAndAppAsync("stale");
        var (oldWorker, oldExecutor, _) = Consumer(null);
        var (oldBoundWorker, _, _) = Consumer("host-a");
        await AddDeploymentAsync("dep_first", app.Id);
        Assert.Equal("dep_first", (await oldWorker.TryClaimNextDeploymentAsync(default))?.Id);
        await using (var db = _harness.CreateDbContext())
            await db.Deployments.ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DeploymentStatus.Succeeded).SetProperty(d => d.OwnershipToken, (string?)null));
        await Operator("host-b", s => s.TransferAsync(Installation, "op-transfer", null));
        await Operator("host-b", s => s.ReleaseAsync(Installation, "op-transfer"));
        await AddDeploymentAsync("dep_second", app.Id);

        Assert.Null(await oldWorker.TryClaimNextDeploymentAsync(default));
        Assert.Null(await oldBoundWorker.TryClaimNextDeploymentAsync(default));
        var (restartedOldHost, _, _) = Consumer("host-a");
        Assert.Null(await restartedOldHost.TryClaimNextDeploymentAsync(default));
        await using (var db = _harness.CreateDbContext())
            Assert.Equal(DeploymentStatus.Queued, (await db.Deployments.SingleAsync(x => x.Id == "dep_second")).Status);
        Assert.Empty(oldExecutor.Executed);

        var (newHost, _, _) = Consumer("host-b");
        Assert.Equal("dep_second", (await newHost.TryClaimNextDeploymentAsync(default))?.Id);
    }

    [Fact]
    public async Task RevokedWorkerDoesNotClaimDatabaseOperationsOrDeletions()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("revoked");
        var now = DateTimeOffset.UtcNow;
        await using var db = _harness.CreateDbContext();
        var participant = new MaintenanceParticipant(_harness.CreateScopeFactory(), "worker", "host-a", TimeProvider.System);
        Assert.True(await participant.CanClaimAsync(db, default));
        db.DatabaseOperations.Add(new DatabaseOperation { Id = "dbop_queued", OrganizationId = org.Id, DatabaseId = "db_x", Kind = "migrate",
            Status = "queued", CreatedAt = now, UpdatedAt = now });
        (await db.Apps.SingleAsync(x => x.Id == app.Id)).DeletionStatus = "queued";
        await db.SaveChangesAsync();
        await Operator("host-b", s => s.TransferAsync(Installation, "op-transfer", null));
        db.ChangeTracker.Clear();

        var operations = new DatabaseOperationProcessor(db, new ControlPlaneAuthorization(db), null!, null!, null!, null!,
            new IdGenerator(), TimeProvider.System, null!, Options.Create(new SshDeploymentOptions()),
            Options.Create(new DatabasePlatformOptions()), null!, maintenance: participant);
        Assert.False(await operations.ProcessNextAsync(default));
        var deletions = new AppDeletionProcessor(db, _ => throw new InvalidOperationException("must not run"), null!,
            NullLogger<AppDeletionProcessor>.Instance, maintenance: participant);
        Assert.False(await deletions.ProcessNextAsync(default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => participant.EnsureCommitAuthorityAsync(db, default));

        db.ChangeTracker.Clear();
        Assert.Equal("queued", (await db.DatabaseOperations.SingleAsync()).Status);
        Assert.Equal("queued", (await db.Apps.SingleAsync(x => x.Id == app.Id)).DeletionStatus);
    }

    private sealed class Executor : IDeploymentPipelineExecutor
    {
        public List<string> Executed { get; } = [];
        public Task ExecuteAsync(Deployment deployment, CancellationToken cancellationToken)
        {
            Executed.Add(deployment.Id);
            return Task.CompletedTask;
        }
    }
}
