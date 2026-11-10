using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Minicloud.Api.Controllers;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ApplicationListPerformanceTests
{
    [Fact]
    public async Task List_UsesDetachedSummaries_AndConstantCommandsAsHistoryGrows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var reads = new ReadCounter();
        await using var db = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>()
            .UseSqlite(connection).AddInterceptors(reads).Options);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var org = new Organization { Id = "org_list_perf", Name = "Test", Slug = "list-perf" };
        var foreignOrg = new Organization { Id = "org_list_foreign", Name = "Foreign", Slug = "list-foreign" };
        db.Organizations.AddRange(org, foreignOrg);
        var application = new MinicloudApplication
        {
            Id = "appl_perf", OrganizationId = org.Id, Name = "Shop", Slug = "shop", NormalizedSlug = "shop",
            CreatedAt = now, UpdatedAt = now
        };
        var empty = new MinicloudApplication
        {
            Id = "appl_empty", OrganizationId = org.Id, Name = "Empty", Slug = "empty", NormalizedSlug = "empty"
        };
        db.Applications.AddRange(application, empty, new MinicloudApplication
        {
            Id = "appl_foreign", OrganizationId = foreignOrg.Id, Name = "Foreign", Slug = "foreign", NormalizedSlug = "foreign"
        });
        var production = Environment("env_prod", "production", EnvironmentKinds.Persistent);
        var preview = Environment("env_preview", "preview", EnvironmentKinds.Preview);
        preview.BranchName = "feature/test";
        preview.SourceBranch = "feature/test";
        preview.SourceEnvironmentId = production.Id;
        db.Apps.AddRange(production, preview, Environment("env_internal", "internal", EnvironmentKinds.Internal));
        await db.SaveChangesAsync();
        application.DefaultEnvironmentId = production.Id;
        application.LegacyRootAppId = production.Id;
        db.Deployments.Add(Deployment("deployment_latest", production.Id, now, DeploymentStatus.Failed));
        db.RuntimeServers.Add(new RuntimeServer
        {
            Id = "runtime_prod", OrganizationId = org.Id, AppId = production.Id, Provider = "fixture",
            CreatedAt = now, UpdatedAt = now, LastSeenAt = now
        });
        db.RuntimeStatusSnapshots.Add(Snapshot("snapshot_latest", now, "healthy"));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var controller = new ApplicationsController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim("auth_kind", "api_key"), new Claim("organization_id", org.Id),
                        new Claim("api_key_id", "key_fixture"), new Claim("scope", ApiKeyScopes.AppsRead)
                    ], "fixture"))
                }
            }
        };

        reads.Count = 0;
        var first = await ReadAsync();
        var firstReads = reads.Count;
        var serialized = JsonSerializer.Serialize(first);
        Assert.Equal(firstReads, reads.Count);
        Assert.InRange(firstReads, 1, 9);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal(new[] { empty.Id, application.Id }, first.Select(x => x.Id));
        var result = first.Single(x => x.Id == application.Id);
        Assert.Equal(production.Id, result.DefaultEnvironmentId);
        Assert.Equal(2, result.EnvironmentCount);
        Assert.Equal(1, result.PersistentEnvironmentCount);
        Assert.Equal(1, result.PreviewEnvironmentCount);
        Assert.Equal("deployment_latest", result.Environments.Single(x => x.Id == production.Id).LatestDeployment!.Id);
        Assert.Equal("healthy", result.Environments.Single(x => x.Id == production.Id).RuntimeObservation.State);
        Assert.Equal("feature/test", result.Environments.Single(x => x.Id == preview.Id).SourceBranch);

        db.Deployments.AddRange(Enumerable.Range(0, 500).Select(i =>
            Deployment($"old_{i:D4}", production.Id, now.AddMinutes(-i - 1), DeploymentStatus.Failed)));
        db.RuntimeStatusSnapshots.AddRange(Enumerable.Range(0, 500).Select(i =>
            Snapshot($"snapshot_old_{i:D4}", now.AddMinutes(-i - 1), "unhealthy")));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        reads.Count = 0;

        var second = await ReadAsync();

        Assert.Equal(firstReads, reads.Count);
        Assert.Equal(serialized, JsonSerializer.Serialize(second));
        Assert.Empty(db.ChangeTracker.Entries());

        MinicloudApp Environment(string id, string name, string kind) => new()
        {
            Id = id, OrganizationId = org.Id, ApplicationId = application.Id, Name = name, Slug = id,
            EnvironmentName = name, NormalizedEnvironmentName = name, EnvironmentKind = kind, CreatedAt = now, UpdatedAt = now
        };
        Deployment Deployment(string id, string envId, DateTimeOffset createdAt, DeploymentStatus status) => new()
        {
            Id = id, OrganizationId = org.Id, AppId = envId, CreatedAt = createdAt, UpdatedAt = createdAt, Status = status
        };
        RuntimeStatusSnapshot Snapshot(string id, DateTimeOffset observedAt, string health) => new()
        {
            Id = id, OrganizationId = org.Id, AppId = production.Id, RuntimeServerId = "runtime_prod",
            ObservedAt = observedAt, ReceivedAt = observedAt,
            StatusJson = JsonSerializer.Serialize(new
            {
                observedAt,
                services = new[] { new { name = "web", state = "running", health } }
            })
        };
        async Task<IReadOnlyList<ApplicationResponse>> ReadAsync()
        {
            var response = await controller.ListApplications(org.Id, db, new ControlPlaneAuthorization(db), TimeProvider.System, default);
            return Assert.IsAssignableFrom<IReadOnlyList<ApplicationResponse>>(((IValueHttpResult)response).Value);
        }
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
