using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Databases;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Databases;

public sealed class DatabaseOwnershipTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private DbContextOptions<MinicloudDbContext> options = default!;
    private MinicloudDbContext db = default!;
    private DatabaseOperationProcessor processor = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        options = new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options;
        db = new(options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        db.DatabaseOperations.Add(new DatabaseOperation { Id = "operation", OrganizationId = "org", Kind = "create-server", Status = "running", OwnershipToken = "owner-one", LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30) });
        await db.SaveChangesAsync();
        processor = new(db, null!, null!, null!, null!, null!, new IdGenerator(), TimeProvider.System, null!, Options.Create(new SshDeploymentOptions()), Options.Create(new DatabasePlatformOptions()), null!);
    }

    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task ReassignedOwnerCannotSavePaidAppOrFinalizeOperation()
    {
        var old = await db.DatabaseOperations.SingleAsync();
        await using (var changed = new MinicloudDbContext(options))
        {
            (await changed.DatabaseOperations.SingleAsync()).OwnershipToken = "owner-two";
            await changed.SaveChangesAsync();
        }
        db.Apps.Add(new MinicloudApp { Id = "paid-app", OrganizationId = "org", Name = "Paid", Slug = "paid" });
        old.Status = "completed";
        old.OwnershipToken = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => processor.SaveOwnedDatabaseChangesAsync(old, default, "owner-one"));
        await using var observed = new MinicloudDbContext(options);
        Assert.Empty(await observed.Apps.ToListAsync());
        var operation = await observed.DatabaseOperations.SingleAsync();
        Assert.Equal("running", operation.Status);
        Assert.Equal("owner-two", operation.OwnershipToken);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("cancelling")]
    public async Task ExpiredWorkIsTerminalWithoutAutomaticReplay(string status)
    {
        var operation = await db.DatabaseOperations.SingleAsync();
        operation.Status = status;
        operation.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.True(await processor.ProcessNextAsync(default));
        var retired = await db.DatabaseOperations.AsNoTracking().SingleAsync();
        Assert.Equal("failed", retired.Status);
        Assert.Equal("database_operation_interrupted_reconciliation_required", retired.ErrorCode);
        Assert.Null(retired.OwnershipToken);
        Assert.Null(retired.LeaseExpiresAt);
        Assert.Equal(0, retired.AttemptCount);
        Assert.Empty(await db.Apps.ToListAsync());
        Assert.False(await processor.ProcessNextAsync(default));
    }

    [Fact]
    public async Task InterruptedMigrationRequiresRetainedFenceReconciliation()
    {
        var operation = await db.DatabaseOperations.SingleAsync();
        operation.Kind = "migrate";
        operation.LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.SaveChangesAsync();
        Assert.True(await processor.ProcessNextAsync(default));
        var retired = await db.DatabaseOperations.AsNoTracking().SingleAsync();
        Assert.Equal("database_migration_reconciliation_required", retired.ErrorCode);
        Assert.Equal("failed", retired.Status);
        Assert.Equal(0, retired.AttemptCount);
    }

    [Fact]
    public async Task CancellationCannotBeOverwrittenByStaleSuccessfulCompletion()
    {
        var operation = await db.DatabaseOperations.SingleAsync();
        await using (var cancellation = new MinicloudDbContext(options))
        {
            (await cancellation.DatabaseOperations.SingleAsync()).Status = "cancelling";
            await cancellation.SaveChangesAsync();
        }
        operation.Status = "completed";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => processor.SaveOwnedDatabaseChangesAsync(operation, default));
        await using var observed = new MinicloudDbContext(options);
        Assert.Equal("cancelling", (await observed.DatabaseOperations.SingleAsync()).Status);
    }

    [Fact]
    public async Task OwnedQuiescentCancellationCanCommitItsTerminalStatus()
    {
        var operation = await db.DatabaseOperations.SingleAsync();
        operation.Status = "cancelling";
        await db.SaveChangesAsync();
        operation.Status = "cancelled";
        operation.OwnershipToken = null;
        operation.LeaseExpiresAt = null;
        await processor.SaveOwnedDatabaseChangesAsync(operation, default, "owner-one");
        await using var observed = new MinicloudDbContext(options);
        Assert.Equal("cancelled", (await observed.DatabaseOperations.SingleAsync()).Status);
    }
}
