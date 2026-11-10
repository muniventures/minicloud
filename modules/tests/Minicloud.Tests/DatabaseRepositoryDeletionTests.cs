using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Controllers;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DatabaseRepositoryDeletionTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MinicloudDbContext db = default!;
    private DatabaseRepositoriesController controller = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org_fixture", Name = "Fixture", Slug = "fixture" });
        db.Apps.Add(new MinicloudApp { Id = "app_repository", OrganizationId = "org_fixture", Name = "Daily backups", Slug = "dbrepo-fixture", EnvironmentKind = EnvironmentKinds.Internal, Database = DatabaseMode.None });
        db.ManagedDatabases.Add(new ManagedDatabase { Id = "database_fixture", OrganizationId = "org_fixture", Name = "Source", Status = "ready" });
        db.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy { Id = "policy_fixture", OrganizationId = "org_fixture", DatabaseId = "database_fixture", RepositoryAppId = "app_repository", Enabled = false });
        await db.SaveChangesAsync();
        controller = Controller("org_fixture", ApiKeyScopes.DatabasesManage);
    }

    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private DatabaseRepositoriesController Controller(string organization, string scope)
    {
        var ids = new IdGenerator();
        var catalog = new ProviderCatalogService(db, ids, TimeProvider.System);
        return new(db, new(db, new(db), catalog, ids, TimeProvider.System), catalog, ids, TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("auth_kind", "api_key"), new Claim("organization_id", organization),
                        new Claim("api_key_id", "key_fixture"), new Claim("scope", scope), new Claim("scope", ApiKeyScopes.DatabasesRead)
                    ], "fixture"))
                }
            }
        };
    }

    private async Task<int?> DeleteAsync(string confirmation = "Daily backups") =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(await controller.Delete("app_repository", new(confirmation), default)).StatusCode;

    [Fact]
    public async Task UnusedRepositoryQueuesExistingAppDeletionAndAuditsOnlyIdentity()
    {
        Assert.Equal(202, await DeleteAsync());
        Assert.Equal("queued", (await db.Apps.SingleAsync()).DeletionStatus);
        Assert.Empty(await db.Applications.ToListAsync());
        var audit = Assert.Single(await db.AuditEvents.ToListAsync());
        Assert.Equal("database.repository.delete_requested", audit.Action);
        Assert.Contains("app_repository", audit.Details);
        Assert.Empty(await db.DatabaseOperations.ToListAsync());
        Assert.Equal(202, await DeleteAsync());
        Assert.Single(await db.AuditEvents.ToListAsync());
    }

    [Theory]
    [InlineData("daily backups")]
    [InlineData("Daily backups ")]
    public async Task ConfirmationIsExact(string confirmation)
    {
        Assert.Equal(400, await DeleteAsync(confirmation));
        Assert.Null((await db.Apps.SingleAsync()).DeletionStatus);
    }

    [Theory]
    [InlineData("org_other", ApiKeyScopes.DatabasesManage)]
    [InlineData("org_fixture", ApiKeyScopes.DatabasesRead)]
    public async Task RequiresOwnedDatabaseManagementAuthority(string organization, string scope)
    {
        controller = Controller(organization, scope);
        Assert.Equal(403, await DeleteAsync());
        Assert.Null((await db.Apps.SingleAsync()).DeletionStatus);
    }

    [Theory]
    [InlineData("customer", EnvironmentKinds.Persistent)]
    [InlineData("dbrepo-customer", EnvironmentKinds.Persistent)]
    [InlineData("customer", EnvironmentKinds.Internal)]
    public async Task CannotDeleteOrdinaryAppsOrArbitraryInternalRuntimes(string slug, string kind)
    {
        var app = await db.Apps.SingleAsync(); app.Slug = slug; app.EnvironmentKind = kind;
        await db.SaveChangesAsync();
        Assert.Equal(404, await DeleteAsync());
        Assert.Null(app.DeletionStatus);
    }

    [Theory]
    [InlineData("enabled-policy")]
    [InlineData("complete-artifact")]
    [InlineData("binding")]
    [InlineData("policy-operation")]
    [InlineData("deployment")]
    public async Task ProtectedDataAndActiveWorkBlockDeletion(string blocker)
    {
        if (blocker == "enabled-policy") { (await db.DatabaseBackupPolicies.SingleAsync()).Enabled = true; }
        if (blocker == "complete-artifact") { db.DatabaseBackupArtifacts.Add(new DatabaseBackupArtifact { Id = "artifact", OrganizationId = "org_fixture", DatabaseId = "database_fixture", RepositoryAppId = "app_repository", Status = "complete" }); }
        if (blocker == "binding") { db.ManagedDatabaseBindings.Add(new ManagedDatabaseBinding { Id = "binding", OrganizationId = "org_fixture", DatabaseId = "database_fixture", AppId = "app_repository", ServiceName = "probe", Status = "applied" }); }
        if (blocker == "policy-operation") { db.DatabaseOperations.Add(new DatabaseOperation { Id = "operation", OrganizationId = "org_fixture", DatabaseId = "database_fixture", Kind = "purge-backups", Status = "running" }); }
        if (blocker == "deployment") { db.Deployments.Add(new Deployment { Id = "deployment", OrganizationId = "org_fixture", AppId = "app_repository", Status = DeploymentStatus.Queued }); }
        await db.SaveChangesAsync();
        Assert.Equal(409, await DeleteAsync());
        Assert.Null((await db.Apps.SingleAsync()).DeletionStatus);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("cancelling")]
    public async Task ExactActiveRepositoryCreationBlocksDeletion(string status)
    {
        db.DatabaseOperations.Add(new DatabaseOperation { Id = "operation", OrganizationId = "org_fixture", Kind = "create-repository", Status = status, RequestJson = JsonSerializer.Serialize(new { appId = "app_repository" }) });
        await db.SaveChangesAsync();
        Assert.Equal(409, await DeleteAsync());
    }

    [Fact]
    public async Task OtherRepositoryCreationAndPurgedArtifactsDoNotPermanentlyBlockDeletion()
    {
        db.DatabaseOperations.Add(new DatabaseOperation { Id = "operation", OrganizationId = "org_fixture", Kind = "create-repository", Status = "running", RequestJson = JsonSerializer.Serialize(new { appId = "app_repository_other" }) });
        db.DatabaseBackupArtifacts.Add(new DatabaseBackupArtifact { Id = "artifact", OrganizationId = "org_fixture", DatabaseId = "database_fixture", RepositoryAppId = "app_repository", Status = "purged" });
        await db.SaveChangesAsync();
        Assert.Equal(202, await DeleteAsync());
    }

    [Fact]
    public async Task ADeletingRepositoryCannotAcquireAnotherEnabledBackupPolicy()
    {
        Assert.Equal(202, await DeleteAsync());
        var ids = new IdGenerator();
        var management = new DatabaseManagementService(db, new(db), new(db, ids, TimeProvider.System), ids, TimeProvider.System);
        await Assert.ThrowsAsync<ArgumentException>(() => management.SetPolicyAsync("database_fixture", new(true, "app_repository"),
            new("api_key", null, "key_fixture", "org_fixture", new HashSet<string> { ApiKeyScopes.DatabasesManage }), default));
        Assert.False((await db.DatabaseBackupPolicies.SingleAsync()).Enabled);
    }

    [Fact]
    public async Task FailedRepositoryDeletionRemainsListedAndCanBeRetried()
    {
        var app = await db.Apps.SingleAsync(); app.DeletionStatus = "failed"; app.DeletionError = "Synthetic provider outage";
        await db.SaveChangesAsync();
        var listed = await controller.List("org_fixture", default);
        Assert.Contains("app_repository", JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(listed).Value));
        Assert.Equal(202, await DeleteAsync());
        Assert.Equal("queued", app.DeletionStatus);
        Assert.Null(app.DeletionError);
    }

    [Fact]
    public async Task QueuedRepositoryRemainsVisibleUntilTheWorkerConfirmsRemoval()
    {
        Assert.Equal(202, await DeleteAsync());
        var listed = await controller.List("org_fixture", default);
        Assert.Contains("queued", JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(listed).Value));
        var response = await controller.Get("app_repository", default);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(response).Value));
        Assert.Equal("app_repository", json.RootElement.GetProperty("Id").GetString());
        Assert.Equal("org_fixture", json.RootElement.GetProperty("OrganizationId").GetString());
        Assert.Equal("queued", json.RootElement.GetProperty("DeletionStatus").GetString());
    }

    [Fact]
    public async Task MissingAndForeignRepositoryStatusCannotBeRead()
    {
        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await controller.Get("missing", default)).StatusCode);
        controller = Controller("org_other", ApiKeyScopes.DatabasesRead);
        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await controller.Get("app_repository", default)).StatusCode);
    }
}
