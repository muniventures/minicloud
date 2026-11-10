using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppSummaryReaderTests
{
    [Fact]
    public async Task Reads_PreserveApplicationSiblingAndPersistedEnvironmentSummaries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reader = new AppSummaryReader(fixture.Db);

        var all = (await reader.ReadEnvironmentsAsync("org", fixture.Db.Apps.Where(x => x.EnvironmentKind != EnvironmentKinds.Internal), default)).Select(x => x.ToResponse()).ToArray();
        var roots = all.Where(x => x.ParentAppId == null).ToArray();
        Assert.Equal(new[] { "legacy", "root" }, roots.Select(x => x.Id));
        Assert.Equal(4, all.Length);
        Assert.DoesNotContain(all, x => x.Id is "internal" or "foreign");
        var root = Assert.Single(all, x => x.Id == "root");
        Assert.Equal("application", root.ApplicationId);
        Assert.Equal("named", root.DefaultEnvironmentId);
        Assert.Equal(new[] { "production", "staging" }, root.Environments!.Select(x => x.Name));
        Assert.Equal("preview", Assert.Single(root.Branches).Id);
        var child = await reader.GetAsync("org", "named", default);
        Assert.NotNull(child);
        Assert.Equal(new[] { "root", "named" }, child.Environments!.Select(x => x.Id));
        Assert.Equal("preview", Assert.Single(child.Branches).Id);
        Assert.Equal("failed", child.DeletionStatus);
        Assert.Equal("retry cleanup", child.DeletionError);
        var preview = await reader.GetAsync("org", "preview", default);
        Assert.NotNull(preview);
        Assert.Empty(preview.Environments!);
        Assert.Empty(preview.Branches);
        var legacy = Assert.Single(roots, x => x.Id == "legacy");
        Assert.Equal("production", legacy.EnvironmentName);
        Assert.Equal("legacy", Assert.Single(legacy.Environments!).Id);
        Assert.Null(legacy.ApplicationId);
        Assert.Null(legacy.LatestDeployment);
        Assert.Empty(legacy.Domains);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task LatestDeployment_ExcludesAssetsAndUsesCreationTimeThenIdRegardlessOfStatus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reader = new AppSummaryReader(fixture.Db);
        var root = await reader.GetAsync("org", "root", default);
        Assert.NotNull(root);
        Assert.NotNull(root.LatestDeployment);
        Assert.Equal("latest-z", root.LatestDeployment.Id);
        Assert.Equal("failed", root.LatestDeployment.Status);
        Assert.Null(root.LatestDeployment.WebsiteUrl);
        Assert.Null(root.LatestDeployment.CompletedAt);
        Assert.Equal("https://preview.example.test", Assert.Single(root.Branches).WebsiteUrl);
        var named = await reader.GetAsync("org", "named", default);
        Assert.NotNull(named);
        Assert.Null(named.LatestDeployment);
    }

    [Fact]
    public async Task LatestDeployment_OrdersByInstantIncludingOffsetsAndSubMillisecondPrecision()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Db.Deployments.AddRange(
            Fixture.Deployment("precision-a", "root", Fixture.Now.AddTicks(2).ToOffset(TimeSpan.FromHours(-5))),
            Fixture.Deployment("precision-z", "root", Fixture.Now.AddTicks(1).ToOffset(TimeSpan.FromHours(5))));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var root = await new AppSummaryReader(fixture.Db).GetAsync("org", "root", default);

        Assert.NotNull(root);
        Assert.Equal("precision-a", root.LatestDeployment?.Id);
    }

    [Fact]
    public async Task DomainsAndPlan_DisplayFieldsRemainScopedAndDisabledDomainsAreExcluded()
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = await new AppSummaryReader(fixture.Db).GetAsync("org", "root", default);
        Assert.NotNull(root);
        Assert.Equal("Display plan", root.Plan);
        Assert.Equal("plan", root.PlanId);
        Assert.Equal("postgres", root.Database);
        Assert.Equal(new[] { "a.example.test", "z.example.test" }, root.Domains.Select(x => x.Hostname));
        Assert.All(root.Domains, x => { Assert.Equal($"https://{x.Hostname}", x.Url); Assert.True(x.Reserved); });
        Assert.Equal("pending", root.Domains[0].SslStatus);
        Assert.Equal(new[] { "https://preview-domain.example.test" }, Assert.Single(root.Branches).Urls);
        Assert.Equal("p0", Assert.Single(root.Environments!, x => x.Id == "named").Plan);
    }

    [Fact]
    public async Task QueriesStayBoundedAndSerializationDoesNotAccessDatabaseWhenHistoryGrows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reader = new AppSummaryReader(fixture.Db);
        fixture.Commands.Reset();
        var before = (await reader.ReadEnvironmentsAsync("org", fixture.Db.Apps, default)).Select(x => x.ToResponse()).ToArray();
        var beforeJson = JsonSerializer.Serialize(before);
        var smallCount = fixture.Commands.Sql.Count;
        Assert.InRange(smallCount, 1, 4);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        for (var i = 0; i < 1000; i++)
            fixture.Db.Deployments.Add(Fixture.Deployment($"old-{i:D4}", "root", Fixture.Now.AddDays(-i - 1)));
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        fixture.Commands.Reset();

        var after = (await reader.ReadEnvironmentsAsync("org", fixture.Db.Apps, default)).Select(x => x.ToResponse()).ToArray();
        var commandsAfterRead = fixture.Commands.Sql.Count;
        Assert.Equal(beforeJson, JsonSerializer.Serialize(after));
        Assert.Equal(commandsAfterRead, fixture.Commands.Sql.Count);
        Assert.Equal(smallCount, commandsAfterRead);
        Assert.InRange(commandsAfterRead, 1, 4);
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
        Assert.All(fixture.Commands.Sql, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, fixture.Commands.NonQueryCount);
    }

    [Fact]
    public async Task EmptyForeignAndMissingReadsReturnNoData()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reader = new AppSummaryReader(fixture.Db);
        Assert.Empty(await reader.ReadEnvironmentsAsync("absent", fixture.Db.Apps, default));
        Assert.Null(await reader.GetAsync("org", "foreign", default));
        Assert.Null(await reader.GetAsync("org", "absent", default));
        Assert.Empty(fixture.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task CancellationIsPropagated()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new AppSummaryReader(fixture.Db).ReadEnvironmentsAsync("org", fixture.Db.Apps, cancellation.Token));
    }

    private sealed class Fixture(SqliteConnection connection, MinicloudDbContext db, Commands commands) : IAsyncDisposable
    {
        public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        public MinicloudDbContext Db { get; } = db;
        public Commands Commands { get; } = commands;

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new Commands();
            var db = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseSqlite(connection).AddInterceptors(commands).Options);
            await db.Database.EnsureCreatedAsync();
            db.Organizations.AddRange(new Organization { Id = "org", Name = "Main", Slug = "main", CreatedAt = Now, UpdatedAt = Now },
                new Organization { Id = "other", Name = "Other", Slug = "other", CreatedAt = Now, UpdatedAt = Now });
            db.ProviderAccounts.Add(new ProviderAccount { Id = "account", OrganizationId = "org", Name = "Account", CredentialPath = "synthetic", CredentialGeneration = "1", CreatedAt = Now, UpdatedAt = Now });
            db.ProviderPlans.Add(new ProviderPlan { Id = "plan", OrganizationId = "org", ProviderAccountId = "account", Name = "Display plan", NormalizedName = "display plan", Region = "test", Sku = "test", CreatedAt = Now, UpdatedAt = Now });
            db.Applications.Add(new MinicloudApplication { Id = "application", OrganizationId = "org", Name = "Project", Slug = "project", NormalizedSlug = "project", DefaultEnvironmentId = "named", LegacyRootAppId = "root", CreatedAt = Now, UpdatedAt = Now });
            var root = App("root", "Project", "production");
            root.PlanId = "plan";
            root.Database = DatabaseMode.Postgres;
            var named = App("named", "Staging", "staging", "root");
            named.DeletionStatus = "failed";
            named.DeletionError = "retry cleanup";
            var preview = App("preview", "Preview", "feature", "root");
            preview.BranchName = "feature";
            preview.EnvironmentKind = EnvironmentKinds.Preview;
            var internalApp = App("internal", "Internal", "internal", "root");
            internalApp.EnvironmentKind = EnvironmentKinds.Internal;
            var legacy = App("legacy", "Legacy", null);
            legacy.ApplicationId = null;
            var foreign = App("foreign", "Foreign", null);
            foreign.OrganizationId = "other";
            foreign.ApplicationId = null;
            db.Apps.AddRange(root, named, preview, internalApp, legacy, foreign);
            db.Domains.AddRange(Domain("z", "root", "z.example.test"), Domain("a", "root", "a.example.test"),
                Domain("disabled", "root", "disabled.example.test", DomainBindingService.StatusDisabled),
                Domain("preview-domain", "preview", "preview-domain.example.test"), Domain("named-domain", "named", "named.example.test"));
            var artifact = new DeploymentArtifact { Id = "artifact", OrganizationId = "org", AppId = "root", ServiceName = "assets", FileName = "assets.tar", ContentType = "application/octet-stream", ManifestJson = "{}", StoragePath = "synthetic", CreatedAt = Now, UpdatedAt = Now, ExpiresAt = Now.AddDays(1) };
            db.DeploymentArtifacts.Add(artifact);
            db.AssetReleases.Add(new AssetRelease { Id = "asset", OrganizationId = "org", AppId = "root", Name = "assets", ArtifactId = artifact.Id, InputSha256 = new string('a', 64), OutputPath = "/assets", CreatedAt = Now });
            db.Deployments.AddRange(Deployment("latest-a", "root", Now), Deployment("latest-z", "root", Now),
                Deployment("older", "root", Now.AddDays(-1)), Deployment("preview-latest", "preview", Now));
            db.Deployments.Add(Deployment("newer-asset", "root", Now.AddDays(1), "asset"));
            db.Deployments.Add(Deployment("named-asset", "named", Now.AddDays(1), "asset"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            commands.Reset();
            return new Fixture(connection, db, commands);
        }

        private static MinicloudApp App(string id, string name, string? environmentName, string? parent = null) => new()
        {
            Id = id, OrganizationId = "org", ApplicationId = "application", ParentAppId = parent,
            Name = name, Slug = id, EnvironmentName = environmentName, NormalizedEnvironmentName = environmentName,
            Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = Now, UpdatedAt = Now
        };

        public static Deployment Deployment(string id, string appId, DateTimeOffset created, string? asset = null) => new()
        {
            Id = id, OrganizationId = "org", AppId = appId, AssetReleaseId = asset, CreatedAt = created,
            UpdatedAt = created, Status = id == "latest-z" ? DeploymentStatus.Failed : DeploymentStatus.Queued,
            WebsiteUrl = appId == "preview" ? "https://preview.example.test" : null
        };

        private static Domain Domain(string id, string appId, string hostname, string status = "active") => new()
        {
            Id = id, OrganizationId = "org", AppId = appId, Hostname = hostname, ServiceName = "web",
            Kind = "generated", DnsStatus = "pending", ApplyStatus = "pending", SslStatus = "pending",
            Status = status, CreatedAt = Now, UpdatedAt = Now
        };

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class Commands : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];
        public int NonQueryCount { get; private set; }
        public void Reset() { Sql.Clear(); NonQueryCount = 0; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Sql.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            NonQueryCount++;
            return ValueTask.FromResult(result);
        }
    }
}
