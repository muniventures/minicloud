using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Selfrun.Common.Auth;
using Selfrun.Common.Models;
using Selfrun.Common.Services;
using Selfrun.Worker.Databases;
using Selfrun.Worker.Ssh;

namespace Selfrun.Worker.Tests.Databases;

public sealed class DatabaseBackupPowerGateTests : IAsyncLifetime
{
    private readonly WorkerSqliteTestHarness _harness = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => _harness.InitializeAsync();

    public Task DisposeAsync() => _harness.DisposeAsync();

    [Theory]
    [InlineData("source")]
    [InlineData("repository")]
    public async Task ScheduledBackup_RemainsDueWhileEitherEnvironmentSleeps_ThenQueuesAfterWake(string sleepingSide)
    {
        var (organization, sourceApp, repositoryApp, databaseId) = await SeedBackupAsync(sleepingSide);
        await using var db = _harness.CreateDbContext();
        var processor = CreateProcessor(db);

        await processor.ScheduleBackupsAsync(default);

        var deferredPolicy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == databaseId);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(-1), deferredPolicy.NextDueAt);
        Assert.Empty(await db.DatabaseOperations.ToListAsync());

        var sleepingAppId = sleepingSide == "source" ? sourceApp.Id : repositoryApp.Id;
        await db.EnvironmentSleepSettings
            .Where(x => x.EnvironmentId == sleepingAppId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PowerState, EnvironmentPowerStates.Awake));

        await processor.ScheduleBackupsAsync(default);

        var scheduled = await db.DatabaseOperations.SingleAsync();
        Assert.Equal(databaseId, scheduled.DatabaseId);
        Assert.Equal("backup", scheduled.Kind);
        Assert.Equal("queued", scheduled.Status);
        Assert.Equal(organization.Id, scheduled.OrganizationId);
    }

    private async Task<(Organization Organization, SelfrunApp Source, SelfrunApp Repository, string DatabaseId)> SeedBackupAsync(string sleepingSide)
    {
        var (organization, sourceApp) = await _harness.SeedOrgAndAppAsync(Guid.NewGuid().ToString("N"));
        var now = _clock.GetUtcNow();
        var repositoryApp = new SelfrunApp
        {
            Id = "app_backup_repository",
            OrganizationId = organization.Id,
            Name = "Backup repository",
            Slug = "backup-repository",
            Plan = AppPlan.P0V0,
            Database = DatabaseMode.None,
            CreatedAt = now,
            UpdatedAt = now
        };
        var databaseId = "database_source";
        await using var db = _harness.CreateDbContext();
        db.Apps.Add(repositoryApp);
        db.ManagedDatabases.Add(new ManagedDatabase
        {
            Id = databaseId,
            OrganizationId = organization.Id,
            Name = "source",
            Engine = "postgres",
            Status = "ready",
            AppId = sourceApp.Id,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy
        {
            Id = "backup_policy",
            OrganizationId = organization.Id,
            DatabaseId = databaseId,
            Enabled = true,
            RepositoryAppId = repositoryApp.Id,
            NextDueAt = now.AddMinutes(-1),
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        });

        var sleepingAppId = sleepingSide == "source" ? sourceApp.Id : repositoryApp.Id;
        db.EnvironmentSleepSettings.Add(new EnvironmentSleepSettings
        {
            EnvironmentId = sleepingAppId,
            PowerState = EnvironmentPowerStates.Asleep,
            StateChangedAt = now
        });
        await db.SaveChangesAsync();
        return (organization, sourceApp, repositoryApp, databaseId);
    }

    private DatabaseOperationProcessor CreateProcessor(Selfrun.Common.Data.SelfrunDbContext db) =>
        new(db, new ControlPlaneAuthorization(db), null!, null!, null!, null!, new IdGenerator(), _clock, null!,
            Options.Create(new SshDeploymentOptions()), Options.Create(new DatabasePlatformOptions()), null!,
            maintenance: new MaintenanceParticipant(null, "worker", null, _clock));

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
