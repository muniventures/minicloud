using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DatabaseBindingTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MinicloudDbContext db = default!;
    private DatabaseBindingService service = default!;
    private readonly ActorIdentity actor = new("api_key", null, "key", "org_test", new HashSet<string> { ApiKeyScopes.DatabasesManage, ApiKeyScopes.AppsWrite, ApiKeyScopes.SecretsWrite, ApiKeyScopes.DeploymentsCreate });
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org_test", Name = "Test", Slug = "test" });
        db.Apps.Add(new MinicloudApp { Id = "app_test", OrganizationId = "org_test", Name = "App", Slug = "app", Database = DatabaseMode.Postgres });
        db.ManagedDatabases.Add(new ManagedDatabase { Id = "database_test", OrganizationId = "org_test", Name = "orders", Engine = "postgres", Placement = "shared", Status = "ready" });
        var release = new Deployment { Id = "dep_test", OrganizationId = "org_test", AppId = "app_test", Status = DeploymentStatus.Succeeded, CreatedAt = DateTimeOffset.UtcNow };
        release.Services.Add(new DeploymentService { Id = "svc_test", Name = "backend", Image = "example/backend", Path = "/", HealthPath = "/health" });
        db.Deployments.Add(release);
        await db.SaveChangesAsync();
        service = new(db, new(db), new IdGenerator(), TimeProvider.System);
    }
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task BindingQueuesNetworkWorkWithoutClaimingApplied()
    {
        var result = await service.BindAsync("database_test", new("app_test", ["backend"]), actor, default);
        Assert.Equal("pending", Assert.Single(result).Status);
        Assert.Null(result[0].Host);
        Assert.Equal("bind", (await db.DatabaseOperations.SingleAsync()).Kind);
        Assert.Equal(DatabaseMode.Postgres, (await db.Apps.SingleAsync()).Database);
    }

    [Fact]
    public async Task UnknownServiceAndMissingSecretAuthorityAreRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => service.BindAsync("database_test", new("app_test", ["unselected"]), actor, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BindAsync("database_test", new("app_test", ["backend"]), actor with { Scopes = new HashSet<string> { ApiKeyScopes.DatabasesManage, ApiKeyScopes.AppsWrite } }, default));
        Assert.Empty(await db.ManagedDatabaseBindings.ToListAsync());
    }

    [Fact]
    public async Task AppliedIdenticalBindingIsIdempotentAndPendingBindingCannotBeOverwritten()
    {
        await service.BindAsync("database_test", new("app_test", ["backend"]), actor, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync("database_test", new("app_test", ["backend"]), actor, default));
        var binding = await db.ManagedDatabaseBindings.SingleAsync(); binding.Status = "applied";
        (await db.DatabaseOperations.SingleAsync()).Status = "completed";
        await db.SaveChangesAsync();
        Assert.Single(await service.BindAsync("database_test", new("app_test", ["backend"]), actor, default));
        Assert.Single(await db.DatabaseOperations.ToListAsync());
    }
    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("cancelling")]
    public async Task BindAndUnbindCannotOverlapAnActiveDatabaseMutation(string status)
    {
        var database = await db.ManagedDatabases.SingleAsync();
        var initialRevision = database.Revision;
        db.DatabaseOperations.Add(new DatabaseOperation { Id = "active_op", OrganizationId = "org_test", DatabaseId = database.Id, Kind = "rotate-credentials", Status = status });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync(database.Id, new("app_test", ["backend"]), actor, default));
        db.ManagedDatabaseBindings.Add(new ManagedDatabaseBinding { Id = "binding_applied", OrganizationId = "org_test", DatabaseId = database.Id, AppId = "app_test", ServiceName = "backend", Status = "applied" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UnbindAsync(database.Id, "binding_applied", actor, default));
        Assert.Equal("applied", (await db.ManagedDatabaseBindings.SingleAsync()).Status);
        Assert.Equal(initialRevision, database.Revision);
        Assert.Single(await db.DatabaseOperations.ToListAsync());
    }

    [Fact]
    public async Task UnbindRequiresDeploymentAuthorityBeforeChangingBindingStatus()
    {
        db.ManagedDatabaseBindings.Add(new ManagedDatabaseBinding { Id = "binding_applied", OrganizationId = "org_test", DatabaseId = "database_test", AppId = "app_test", ServiceName = "backend", Status = "applied" });
        await db.SaveChangesAsync();
        var withoutDeploy = actor with { Scopes = new HashSet<string> { ApiKeyScopes.DatabasesManage, ApiKeyScopes.AppsWrite, ApiKeyScopes.SecretsWrite } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UnbindAsync("database_test", "binding_applied", withoutDeploy, default));
        Assert.Equal("applied", (await db.ManagedDatabaseBindings.SingleAsync()).Status);
        Assert.Empty(await db.DatabaseOperations.ToListAsync());
    }

    [Fact]
    public async Task BindingMutationsAdvanceSourceRevisionAndRevokedUnbindRemainsIdempotent()
    {
        var database = await db.ManagedDatabases.SingleAsync();
        var initialRevision = database.Revision;
        var binding = Assert.Single(await service.BindAsync(database.Id, new("app_test", ["backend"]), actor, default));
        Assert.Equal(initialRevision + 1, database.Revision);
        (await db.DatabaseOperations.SingleAsync()).Status = "completed";
        binding.Status = "applied";
        await db.SaveChangesAsync();
        await service.UnbindAsync(database.Id, binding.Id, actor, default);
        Assert.Equal(initialRevision + 2, database.Revision);
        Assert.Equal("revoking", binding.Status);
        binding.Status = "revoked";
        await db.SaveChangesAsync();
        await service.UnbindAsync(database.Id, binding.Id, actor, default);
        Assert.Equal(initialRevision + 2, database.Revision);
        Assert.Equal(2, await db.DatabaseOperations.CountAsync());
    }
    [Fact]
    public async Task StaleDatabaseRevisionRollsBackBindingAndQueuedOperationAtomically()
    {
        await using var concurrent = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        var current = await concurrent.ManagedDatabases.SingleAsync();
        current.Revision++;
        await concurrent.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.BindAsync("database_test", new("app_test", ["backend"]), actor, default));
        Assert.Empty(await concurrent.ManagedDatabaseBindings.AsNoTracking().ToListAsync());
        Assert.Empty(await concurrent.DatabaseOperations.AsNoTracking().ToListAsync());
    }
}
