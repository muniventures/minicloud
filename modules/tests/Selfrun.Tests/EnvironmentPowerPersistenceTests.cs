using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Data;
using Selfrun.Common.Models;
using Selfrun.Common.Providers;

namespace Selfrun.Tests;

public sealed class EnvironmentPowerPersistenceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=True");
    private static readonly DateTimeOffset UpdatedAt = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private SelfrunDbContext Context() => new(new DbContextOptionsBuilder<SelfrunDbContext>().UseSqlite(_connection).Options);

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = Context();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org", Slug = "org", Name = "Organization" });
        db.Apps.AddRange(
            new SelfrunApp { Id = "env", OrganizationId = "org", Slug = "env", Name = "Environment", UpdatedAt = UpdatedAt },
            new SelfrunApp { Id = "other", OrganizationId = "org", Slug = "other", Name = "Other", UpdatedAt = UpdatedAt });
        db.ProviderAccounts.AddRange(Account("account"), Account("second-account"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SqliteUpgradeBackfillsExistingEnvironmentsAndPreservesExplicitTrafficOptOutOnRetry()
    {
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE environment_power_operations;
            DROP TABLE environment_retained_addresses;
            DROP TABLE environment_sleep_settings;
            DROP TABLE environment_snapshots;
            """);

        await SqliteDatabaseInitializer.InitializeAsync(db);

        var settings = await db.EnvironmentSleepSettings.ToListAsync();
        Assert.Equal(2, settings.Count);
        Assert.All(settings, row =>
        {
            Assert.Equal(EnvironmentPowerStates.Awake, row.PowerState);
            Assert.Null(row.SleepAfterMinutes);
            Assert.True(row.HttpTrafficIsActivity);
            Assert.Equal(UpdatedAt, row.StateChangedAt);
        });
        settings.Single(x => x.EnvironmentId == "env").HttpTrafficIsActivity = false;
        await db.SaveChangesAsync();
        await SqliteDatabaseInitializer.InitializeAsync(db);
        Assert.False(await db.EnvironmentSleepSettings.Where(x => x.EnvironmentId == "env").Select(x => x.HttpTrafficIsActivity).SingleAsync());
    }

    [Fact]
    public async Task OperationsEnforceSingleNonTerminalRowAndFenceStaleTrackedUpdates()
    {
        await using var db = Context();
        var original = Operation("original", EnvironmentPowerOperationStatuses.Running);
        original.OwnershipToken = "first";
        original.LeaseExpiresAt = UpdatedAt;
        db.EnvironmentPowerOperations.Add(original);
        await db.SaveChangesAsync();

        await RejectAsync(db, Operation("duplicate", EnvironmentPowerOperationStatuses.Queued));
        db.EnvironmentPowerOperations.Add(Operation("historical", EnvironmentPowerOperationStatuses.Failed));
        await db.SaveChangesAsync();

        await using var reclaimer = Context();
        var reclaimed = await reclaimer.EnvironmentPowerOperations.SingleAsync(x => x.Id == original.Id);
        reclaimed.OwnershipToken = "replacement";
        await reclaimer.SaveChangesAsync();
        original.Step = "delete_server";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task SnapshotsAllowPendingAndHistoricalRowsButOnlyOneCurrentImage()
    {
        await using var db = Context();
        db.EnvironmentSnapshots.AddRange(
            Snapshot("current", EnvironmentSnapshotStatuses.Current),
            Snapshot("pending", EnvironmentSnapshotStatuses.Pending),
            Snapshot("superseded", EnvironmentSnapshotStatuses.Superseded));
        await db.SaveChangesAsync();

        await RejectAsync(db, Snapshot("duplicate", EnvironmentSnapshotStatuses.Current));
        var missingImage = Snapshot("missing-image", EnvironmentSnapshotStatuses.Current);
        missingImage.EnvironmentId = "other";
        missingImage.ProviderImageId = null;
        await RejectAsync(db, missingImage, SqliteConstraintCheck);
    }

    [Fact]
    public async Task RetainedAddressesEnforceEnvironmentAndAccountUniquenessAndAllowReleasedIpv4ToBeReused()
    {
        await using var db = Context();
        var original = Address("first", "env", "first-provider-id", "192.0.2.1");
        db.EnvironmentRetainedAddresses.Add(original);
        await db.SaveChangesAsync();

        await RejectAsync(db, Address("duplicate-env", "env", "second-provider-id", "192.0.2.2"));
        await RejectAsync(db, Address("duplicate-ip", "other", "second-provider-id", "192.0.2.1"));
        original.State = EnvironmentRetainedAddressStates.Released;
        await db.SaveChangesAsync();
        await RejectAsync(db, Address("duplicate-id", "other", original.ProviderAddressId, "192.0.2.2"));

        db.EnvironmentRetainedAddresses.AddRange(
            Address("reused-ip", "other", "second-provider-id", original.Ipv4),
            Address("different-account", "env", original.ProviderAddressId, original.Ipv4, "second-account"));
        await db.SaveChangesAsync();
        Assert.Equal(3, await db.EnvironmentRetainedAddresses.CountAsync());
    }

    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintCheck = 275;

    private static async Task RejectAsync(SelfrunDbContext db, object row, int constraint = SqliteConstraintUnique)
    {
        var entry = db.Add(row);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(constraint, Assert.IsType<SqliteException>(failure.InnerException).SqliteExtendedErrorCode);
        entry.State = EntityState.Detached;
    }

    private static ProviderAccount Account(string id) => new()
    {
        Id = id, OrganizationId = "org", Name = id, ProviderType = CloudProvider.Hetzner,
        CredentialPath = "synthetic/provider", CredentialGeneration = "test"
    };

    private static EnvironmentPowerOperation Operation(string id, string status) => new()
    {
        Id = id, EnvironmentId = "env", OrganizationId = "org", Kind = "sleep", Trigger = "idle", Status = status
    };

    private static EnvironmentSnapshot Snapshot(string id, string status) => new()
    {
        Id = id, EnvironmentId = "env", OrganizationId = "org", Provider = "hetzner", ProviderAccountId = "account",
        ProviderImageId = $"image-{id}", Region = "nbg1", Sku = "cx23", ServerName = "synthetic-server",
        AddressMode = EnvironmentAddressModes.Dynamic, Status = status
    };

    private static EnvironmentRetainedAddress Address(string id, string environment, string providerId, string ipv4,
        string account = "account") => new()
    {
        Id = id, EnvironmentId = environment, OrganizationId = "org", Provider = "hetzner", ProviderAccountId = account,
        ProviderAddressId = providerId, Ipv4 = ipv4, Region = "nbg1", State = EnvironmentRetainedAddressStates.Unassigned
    };

    public async Task DisposeAsync() => await _connection.DisposeAsync();
}
