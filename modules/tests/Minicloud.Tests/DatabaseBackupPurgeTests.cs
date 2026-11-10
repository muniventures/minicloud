using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DatabaseBackupPurgeTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MinicloudDbContext db = default!;
    private DatabaseBackupPurgeService service = default!;
    private readonly ActorIdentity actor = new("api_key", null, "key", "org_test", new HashSet<string> { ApiKeyScopes.DatabasesManage });
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org_test", Name = "Test", Slug = "test" });
        db.ManagedDatabases.Add(new ManagedDatabase { Id = "database_test", OrganizationId = "org_test", Name = "orders", Engine = "postgres", Status = "deleted" });
        db.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy { Id = "policy_test", DatabaseId = "database_test", OrganizationId = "org_test", Enabled = false, KeepLastCopies = false, EncryptedKeyMaterialJson = "retained-custody" });
        await db.SaveChangesAsync();
        var ids = new IdGenerator();
        service = new(db, new DatabaseManagementService(db, new(db), new(db, ids, TimeProvider.System), ids, TimeProvider.System));
    }
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task QueuesExplicitPurgeWithoutRemovingArtifactsOrCustodyBeforeWorkerConfirmation()
    {
        var operation = await service.QueueAsync("database_test", new("orders"), actor, default);
        Assert.Equal("purge-backups", operation.Kind);
        Assert.Equal("queued", operation.Status);
        using var request = JsonDocument.Parse(operation.RequestJson);
        Assert.Equal("orders", request.RootElement.GetProperty("confirmation").GetString());
        Assert.Equal("retained-custody", (await db.DatabaseBackupPolicies.SingleAsync()).EncryptedKeyMaterialJson);
    }

    [Theory]
    [InlineData("Orders")]
    [InlineData("orders ")]
    [InlineData("")]
    public async Task ConfirmationMustMatchExactly(string confirmation)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => service.QueueAsync("database_test", new(confirmation), actor, default));
        Assert.Empty(await db.DatabaseOperations.ToListAsync());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EnabledCaptureOrIndefiniteRetentionBlocksPurge(bool enabled, bool keep)
    {
        var policy = await db.DatabaseBackupPolicies.SingleAsync(); policy.Enabled = enabled; policy.KeepLastCopies = keep;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueueAsync("database_test", new("orders"), actor, default));
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("cancelling")]
    public async Task ActiveOperationBlocksPurge(string status)
    {
        db.DatabaseOperations.Add(new DatabaseOperation { Id = "operation_active", OrganizationId = "org_test", DatabaseId = "database_test", Kind = "restore", Status = status });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.QueueAsync("database_test", new("orders"), actor, default));
    }

    [Fact]
    public async Task OtherOrganizationAndReadOnlyAuthorityCannotPurge()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QueueAsync("database_test", new("orders"), actor with { OrganizationId = "other" }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QueueAsync("database_test", new("orders"), actor with { Scopes = new HashSet<string> { ApiKeyScopes.DatabasesRead } }, default));
        Assert.Empty(await db.DatabaseOperations.ToListAsync());
    }
}
