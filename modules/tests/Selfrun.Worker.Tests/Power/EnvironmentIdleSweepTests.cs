using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Models;
using Selfrun.Common.Services;
using Selfrun.Worker.Power;

namespace Selfrun.Worker.Tests.Power;

public sealed class EnvironmentIdleSweepTests : IAsyncLifetime
{
    private readonly WorkerSqliteTestHarness _harness = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => _harness.InitializeAsync();

    public Task DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task Sweep_QueuesOneIdleSleepWhenTimeoutHasElapsed()
    {
        var app = await SeedEnvironmentAsync(lastActivity: _clock.GetUtcNow().AddMinutes(-31));
        await using var db = _harness.CreateDbContext();
        var processor = CreateProcessor(db);

        Assert.Equal(1, await processor.SweepDueAsync(default));
        Assert.Equal(0, await processor.SweepDueAsync(default));

        var operation = await db.EnvironmentPowerOperations.SingleAsync();
        Assert.Equal(app.Id, operation.EnvironmentId);
        Assert.Equal("sleep", operation.Kind);
        Assert.Equal("idle", operation.Trigger);
        Assert.Equal(EnvironmentPowerOperationStatuses.Queued, operation.Status);
    }

    [Fact]
    public async Task Sweep_DoesNotQueueBeforeTimeoutOrDuringKeepAwakeWindow()
    {
        var app = await SeedEnvironmentAsync(
            lastActivity: _clock.GetUtcNow().AddMinutes(-29),
            keepAwakeUntil: _clock.GetUtcNow().AddMinutes(1));
        await using var db = _harness.CreateDbContext();
        var processor = CreateProcessor(db);

        Assert.Equal(0, await processor.SweepDueAsync(default));

        var settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.LastActivityAt = _clock.GetUtcNow().AddMinutes(-40);
        await db.SaveChangesAsync();

        Assert.Equal(0, await processor.SweepDueAsync(default));
        Assert.Empty(await db.EnvironmentPowerOperations.ToListAsync());
    }

    [Fact]
    public async Task Sweep_UsesHttpActivityOnlyWhenPolicyEnablesIt()
    {
        var app = await SeedEnvironmentAsync(lastActivity: _clock.GetUtcNow().AddMinutes(-31));
        await using var db = _harness.CreateDbContext();
        var settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.LastHttpRequestAt = _clock.GetUtcNow().AddMinutes(-1);
        await db.SaveChangesAsync();

        Assert.Equal(0, await CreateProcessor(db).SweepDueAsync(default));

        settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.HttpTrafficIsActivity = false;
        await db.SaveChangesAsync();

        Assert.Equal(1, await CreateProcessor(db).SweepDueAsync(default));
    }

    [Fact]
    public async Task Admission_RechecksActivityAndConflictingDeploymentBeforeQueueing()
    {
        var app = await SeedEnvironmentAsync(lastActivity: _clock.GetUtcNow().AddMinutes(-31));
        await using var db = _harness.CreateDbContext();
        var settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.LastActivityAt = _clock.GetUtcNow().AddMinutes(-2);
        await db.SaveChangesAsync();

        Assert.False(await CreateProcessor(db).TryQueueSleepOperationAsync(app.Id, default));
        Assert.Empty(await db.EnvironmentPowerOperations.ToListAsync());

        settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.LastActivityAt = _clock.GetUtcNow().AddMinutes(-31);
        db.Deployments.Add(new Deployment
        {
            Id = "deploy_active",
            Number = "20261128.1",
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            Database = DatabaseMode.None,
            Status = DeploymentStatus.Queued,
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow()
        });
        await db.SaveChangesAsync();

        Assert.False(await CreateProcessor(db).TryQueueSleepOperationAsync(app.Id, default));
        Assert.Empty(await db.EnvironmentPowerOperations.ToListAsync());

        db.Deployments.Remove(await db.Deployments.SingleAsync(x => x.Id == "deploy_active"));
        db.ManagedDatabases.Add(new ManagedDatabase
        {
            Id = "database_active",
            OrganizationId = app.OrganizationId,
            Name = "active",
            AppId = app.Id,
            Status = "ready",
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow()
        });
        db.DatabaseOperations.Add(new DatabaseOperation
        {
            Id = "database_operation_active",
            OrganizationId = app.OrganizationId,
            DatabaseId = "database_active",
            Kind = "backup",
            Status = "queued",
            CreatedAt = _clock.GetUtcNow(),
            UpdatedAt = _clock.GetUtcNow()
        });
        await db.SaveChangesAsync();

        Assert.False(await CreateProcessor(db).TryQueueSleepOperationAsync(app.Id, default));
        Assert.Empty(await db.EnvironmentPowerOperations.ToListAsync());
    }

    [Fact]
    public async Task Admission_RechecksPowerStateAndPolicyBeforeQueueing()
    {
        var app = await SeedEnvironmentAsync(lastActivity: _clock.GetUtcNow().AddMinutes(-31));
        await using var db = _harness.CreateDbContext();
        var settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.PowerState = EnvironmentPowerStates.Asleep;
        await db.SaveChangesAsync();

        Assert.False(await CreateProcessor(db).TryQueueSleepOperationAsync(app.Id, default));

        settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.PowerState = EnvironmentPowerStates.Awake;
        settings.SleepAfterMinutes = null;
        await db.SaveChangesAsync();

        Assert.False(await CreateProcessor(db).TryQueueSleepOperationAsync(app.Id, default));
        Assert.Empty(await db.EnvironmentPowerOperations.ToListAsync());
    }

    [Fact]
    public async Task Admission_RechecksChangedTimeoutAndKeepAwakeDeadline()
    {
        var app = await SeedEnvironmentAsync(lastActivity: _clock.GetUtcNow().AddMinutes(-31));
        await using var db = _harness.CreateDbContext();
        var settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.SleepAfterMinutes = 120;
        await db.SaveChangesAsync();

        Assert.False(await CreateProcessor(db).TryQueueSleepOperationAsync(app.Id, default));

        settings = await db.EnvironmentSleepSettings.SingleAsync(x => x.EnvironmentId == app.Id);
        settings.SleepAfterMinutes = 30;
        settings.KeepAwakeUntil = _clock.GetUtcNow().AddMinutes(1);
        await db.SaveChangesAsync();

        Assert.False(await CreateProcessor(db).TryQueueSleepOperationAsync(app.Id, default));
        Assert.Empty(await db.EnvironmentPowerOperations.ToListAsync());
    }

    [Fact]
    public async Task ConcurrentSweeps_CreateExactlyOneOperation()
    {
        var app = await SeedEnvironmentAsync(lastActivity: _clock.GetUtcNow().AddMinutes(-31));

        var first = Task.Run(async () =>
        {
            await using var db = _harness.CreateDbContext();
            return await CreateProcessor(db).SweepDueAsync(default);
        });
        var second = Task.Run(async () =>
        {
            await using var db = _harness.CreateDbContext();
            return await CreateProcessor(db).SweepDueAsync(default);
        });

        await Task.WhenAll(first, second);

        await using var verify = _harness.CreateDbContext();
        Assert.Single(await verify.EnvironmentPowerOperations.Where(x => x.EnvironmentId == app.Id).ToListAsync());
    }

    [Fact]
    public async Task Sweep_ObservesCancellation()
    {
        await SeedEnvironmentAsync(lastActivity: _clock.GetUtcNow().AddMinutes(-31));
        await using var db = _harness.CreateDbContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProcessor(db).SweepDueAsync(cancellation.Token));
        Assert.Empty(await db.EnvironmentPowerOperations.ToListAsync());
    }

    private async Task<SelfrunApp> SeedEnvironmentAsync(DateTimeOffset lastActivity, DateTimeOffset? keepAwakeUntil = null)
    {
        var (_, app) = await _harness.SeedOrgAndAppAsync(Guid.NewGuid().ToString("N"));
        await using var db = _harness.CreateDbContext();
        var now = _clock.GetUtcNow();
        db.EnvironmentSleepSettings.Add(new EnvironmentSleepSettings
        {
            EnvironmentId = app.Id,
            SleepAfterMinutes = 30,
            PowerState = EnvironmentPowerStates.Awake,
            LastActivityAt = lastActivity,
            KeepAwakeUntil = keepAwakeUntil,
            StateChangedAt = now
        });
        await db.SaveChangesAsync();
        return app;
    }

    private EnvironmentIdleSweepProcessor CreateProcessor(Selfrun.Common.Data.SelfrunDbContext db) =>
        new(db, new IdGenerator(), _clock, new MaintenanceParticipant(null, "worker", null, _clock));

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
