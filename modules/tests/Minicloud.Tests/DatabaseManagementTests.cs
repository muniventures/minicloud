using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DatabaseManagementTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MinicloudDbContext db = default!;
    private DatabaseManagementService service = default!;
    private readonly ActorIdentity actor = new("api_key", null, "key", "org_test", new HashSet<string> { ApiKeyScopes.DatabasesRead, ApiKeyScopes.DatabasesManage });
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org_test", Name = "Test", Slug = "test" });
        db.Apps.Add(new MinicloudApp { Id = "app_test", OrganizationId = "org_test", Name = "App", Slug = "app", Database = DatabaseMode.Postgres });
        db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org_test", Name = "Account", CredentialPath = "test", CredentialGeneration = "test" });
        db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org_test", ProviderAccountId = "account", Name = "small", NormalizedName = "SMALL", Region = "test", Sku = "test" });
        await db.SaveChangesAsync();
        service = new(db, new(db), new(db, new IdGenerator(), TimeProvider.System), new IdGenerator(), TimeProvider.System);
    }
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task EnrollmentPreservesSourceAndQueuesRealInspection()
    {
        var result = await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        Assert.Equal("enrolling", result.Status);
        Assert.Equal("application", result.Placement);
        Assert.False((await db.DatabaseBackupPolicies.SingleAsync()).Enabled);
        Assert.Equal("enroll", (await db.DatabaseOperations.SingleAsync()).Kind);
        Assert.Equal(DatabaseMode.Postgres, (await db.Apps.SingleAsync()).Database);
    }

    [Fact]
    public async Task OtherOrganizationCannotEnrollOrRead()
    {
        var result = await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        var other = actor with { OrganizationId = "org_other" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(result.Id, other, false, default));
    }

    [Fact]
    public async Task DedicatedServerReservesOnlyOneUserDatabase()
    {
        db.ManagedDatabaseServers.Add(new ManagedDatabaseServer { Id = "server", OrganizationId = "org_test", Name = "dedicated", PlanId = "plan", Mode = "dedicated", Status = "ready" });
        await db.SaveChangesAsync();
        await service.CreateDatabaseAsync(new("org_test", "first", ServerId: "server"), actor, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDatabaseAsync(new("org_test", "second", ServerId: "server"), actor, default));
    }

    [Fact]
    public async Task BackupNeedsOwnedIndependentDestination()
    {
        var result = await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        (await db.DatabaseOperations.SingleAsync()).Status = "completed";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetPolicyAsync(result.Id, new(true, "app_missing"), actor, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetPolicyAsync(result.Id, new(false, FrequencyHours: 0), actor, default));
    }

    [Fact]
    public async Task DisabledPolicyAloneDoesNotBlockRepositoryDeletion()
    {
        var database = await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        var policy = await db.DatabaseBackupPolicies.SingleAsync();
        policy.RepositoryAppId = "repository";
        policy.Enabled = false;
        await db.SaveChangesAsync();
        Assert.Null(await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, "repository", default));
    }

    [Fact]
    public async Task OnlyCompleteArtifactsAndEnabledPoliciesProtectRepository()
    {
        db.DatabaseBackupArtifacts.Add(new DatabaseBackupArtifact { Id = "artifact", OrganizationId = "org_test", DatabaseId = "db_fixture", RepositoryAppId = "repository", Status = "complete" });
        await db.SaveChangesAsync();
        Assert.NotNull(await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, "repository", default));
        (await db.DatabaseBackupArtifacts.SingleAsync()).Status = "purged";
        await db.SaveChangesAsync();
        Assert.Null(await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, "repository", default));
    }

    [Fact]
    public async Task RevokedBindingDoesNotBlockApplicationDeletion()
    {
        var database = await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        (await db.DatabaseOperations.SingleAsync()).Status = "completed";
        db.ManagedDatabaseBindings.Add(new ManagedDatabaseBinding { Id = "binding", OrganizationId = "org_test", DatabaseId = database.Id, AppId = "app_test", ServiceName = "api", Status = "applied" });
        await db.SaveChangesAsync();
        Assert.NotNull(await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, "app_test", default));
        (await db.ManagedDatabaseBindings.SingleAsync()).Status = "revoked";
        await db.SaveChangesAsync();
        Assert.Null(await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, "app_test", default));
    }

    [Fact]
    public async Task ActiveSourceOperationBlocksApplicationDeletion()
    {
        await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        Assert.NotNull(await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, "app_test", default));
        (await db.DatabaseOperations.SingleAsync()).Status = "complete";
        await db.SaveChangesAsync();
        Assert.Null(await DatabaseLifecycleGuards.AppDeletionBlockerAsync(db, "app_test", default));
    }

    [Fact]
    public async Task DisablingBackupRetainsDestinationAndAdvancesPolicyAuthority()
    {
        var database = await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        (await db.DatabaseOperations.SingleAsync()).Status = "completed";
        var policy = await db.DatabaseBackupPolicies.SingleAsync();
        policy.RepositoryAppId = "retained-repository";
        await db.SaveChangesAsync();
        var result = await service.SetPolicyAsync(database.Id, new(false, KeepLastCopies: true), actor, default);
        Assert.Equal("retained-repository", result.RepositoryAppId);
        Assert.True(result.KeepLastCopies);
        Assert.Equal(2, result.Revision);
        Assert.Equal(2, database.Revision);
    }

    [Fact]
    public async Task CancellingUserOperationBlocksPolicyChanges()
    {
        var database = await service.CreateDatabaseAsync(new("org_test", "existing", AppId: "app_test"), actor, default);
        (await db.DatabaseOperations.SingleAsync()).Status = "cancelling";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPolicyAsync(database.Id, new(false), actor, default));
    }

    [Fact]
    public void CustodyNeverSerializesToPortal()
    {
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(new ManagedDatabase { EncryptedCredentialsJson = "secret" }));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(new DatabaseBackupPolicy { EncryptedKeyMaterialJson = "secret" }));
    }
    [Fact]
    public async Task DeletedSourceCanRetireIndefiniteRetentionWithoutMovingRepositoryOrRequiringSourceHost()
    {
        var database = await service.CreateDatabaseAsync(new("org_test", "retained", AppId: "app_test"), actor, default);
        (await db.DatabaseOperations.SingleAsync()).Status = "completed";
        database.Status = "deleted";
        var policy = await db.DatabaseBackupPolicies.SingleAsync();
        policy.Enabled = false; policy.KeepLastCopies = true; policy.RepositoryAppId = "retained-repository";
        policy.EncryptedKeyMaterialJson = "retained-key-custody";
        await db.SaveChangesAsync();
        var changed = await service.SetPolicyAsync(database.Id, new(false, KeepLastCopies: false), actor, default);
        Assert.False(changed.Enabled);
        Assert.False(changed.KeepLastCopies);
        Assert.Equal("retained-repository", changed.RepositoryAppId);
        Assert.Equal("retained-key-custody", changed.EncryptedKeyMaterialJson);
        var configure = await db.DatabaseOperations.SingleAsync(x => x.Kind == "configure-backup");
        Assert.Equal(database.Id, configure.DatabaseId);
        Assert.Equal("queued", configure.Status);
        var purge = new DatabaseBackupPurgeService(db, service);
        await Assert.ThrowsAsync<InvalidOperationException>(() => purge.QueueAsync(database.Id, new("retained"), actor, default));
        configure.Status = "completed";
        await db.SaveChangesAsync();
        Assert.Equal("purge-backups", (await purge.QueueAsync(database.Id, new("retained"), actor, default)).Kind);
    }

    [Fact]
    public async Task DeletedSourceCannotReenableBackupsOrReplaceRetainedDestination()
    {
        var database = await service.CreateDatabaseAsync(new("org_test", "retained", AppId: "app_test"), actor, default);
        (await db.DatabaseOperations.SingleAsync()).Status = "completed";
        database.Status = "deleted";
        var policy = await db.DatabaseBackupPolicies.SingleAsync();
        policy.Enabled = false; policy.KeepLastCopies = true; policy.RepositoryAppId = "retained-repository";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPolicyAsync(database.Id, new(true, "retained-repository"), actor, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPolicyAsync(database.Id, new(false, "different-repository"), actor, default));
        Assert.Equal("retained-repository", policy.RepositoryAppId);
        Assert.True(policy.KeepLastCopies);
        Assert.Single(await db.DatabaseOperations.ToListAsync());
    }
}
