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
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DatabaseNetworkControllerTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MinicloudDbContext db = default!;
    private DatabaseNetworksController controller = default!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org_fixture", Name = "Fixture", Slug = "fixture" });
        db.ProviderAccounts.Add(new ProviderAccount
        {
            Id = "provider_fixture", OrganizationId = "org_fixture", Name = "Fixture", ProviderType = CloudProvider.Hetzner,
            Revision = 1, Enabled = true, CredentialPath = "fixture", CredentialGeneration = "fixture"
        });
        db.ManagedDatabaseNetworks.Add(new ManagedDatabaseNetwork
        {
            Id = "dbnetwork_fixture", OrganizationId = "org_fixture", ProviderAccountId = "provider_fixture", ProviderAccountRevision = 1,
            Name = "fixture", Region = "nbg1", NetworkZone = "eu-central", Status = "creating", CreationAttempted = false
        });
        await db.SaveChangesAsync();
        var ids = new IdGenerator();
        var management = new DatabaseManagementService(db, new(db), new(db, ids, TimeProvider.System), ids, TimeProvider.System);
        controller = new(db, management, null!, TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("auth_kind", "api_key"), new Claim("organization_id", "org_fixture"),
                        new Claim("api_key_id", "key_fixture"), new Claim("scope", ApiKeyScopes.DatabasesManage)
                    ], "fixture"))
                }
            }
        };
    }

    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private void AddOperation(string kind, string status) => db.DatabaseOperations.Add(new DatabaseOperation
    {
        Id = "operation_fixture", OrganizationId = "org_fixture", Kind = kind, Status = status,
        RequestJson = JsonSerializer.Serialize(new { networkId = "dbnetwork_fixture" })
    });

    [Fact]
    public async Task FailedUnallocatedCreationCanQueueDeletionWithoutClaimingProviderDeletion()
    {
        AddOperation("create-network", "failed");
        await db.SaveChangesAsync();

        var result = await controller.Delete("dbnetwork_fixture", default);

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var network = await db.ManagedDatabaseNetworks.SingleAsync();
        Assert.Equal("deleting", network.Status);
        Assert.False(network.CreationAttempted);
        Assert.Null(network.ProviderNetworkId);
        var deletion = await db.DatabaseOperations.SingleAsync(x => x.Kind == "delete-network");
        Assert.Equal("queued", deletion.Status);
        using var request = JsonDocument.Parse(deletion.RequestJson);
        Assert.Equal(network.Id, request.RootElement.GetProperty("networkId").GetString());
    }

    [Theory]
    [InlineData("create-network", "queued")]
    [InlineData("create-network", "running")]
    [InlineData("create-network", "cancelling")]
    [InlineData("delete-network", "queued")]
    [InlineData("delete-network", "running")]
    [InlineData("delete-network", "cancelling")]
    public async Task ActiveNetworkOperationStillRejectsDeletion(string kind, string status)
    {
        AddOperation(kind, status);
        await db.SaveChangesAsync();

        var result = await controller.Delete("dbnetwork_fixture", default);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("creating", (await db.ManagedDatabaseNetworks.SingleAsync()).Status);
        Assert.Equal(1, await db.DatabaseOperations.CountAsync());
    }

    [Fact]
    public async Task DatabaseReferenceStillBlocksDeletionAfterCreationFailure()
    {
        AddOperation("create-network", "failed");
        db.ManagedDatabases.Add(new ManagedDatabase
        {
            Id = "database_fixture", OrganizationId = "org_fixture", Name = "fixture", NetworkId = "dbnetwork_fixture", Status = "ready"
        });
        await db.SaveChangesAsync();

        var result = await controller.Delete("dbnetwork_fixture", default);

        Assert.Equal(StatusCodes.Status409Conflict, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("creating", (await db.ManagedDatabaseNetworks.SingleAsync()).Status);
        Assert.DoesNotContain(await db.DatabaseOperations.ToListAsync(), operation => operation.Kind == "delete-network");
    }
}
