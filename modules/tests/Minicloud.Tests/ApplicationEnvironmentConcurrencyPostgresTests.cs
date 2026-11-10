using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed partial class ApplicationEnvironmentMigrationPostgresTests
{
    [Fact]
    public async Task DefaultEnvironmentPatchWaitsForDeletionLockAndRejectsStaleTarget()
    {
        await WithPostgres(async options =>
        {
            await using (var setup = new MinicloudDbContext(options))
                await setup.Database.MigrateAsync();

            var intercepted = new ApplicationLockInterceptor();
            await using var factory = new ConcurrentApiFactory(options, intercepted);
            using var client = factory.CreateClient();
            string token;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
                var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
                var key = tokens.Generate();
                token = key.Token;
                var now = DateTimeOffset.UtcNow;
                db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org", CreatedAt = now, UpdatedAt = now });
                db.ApiKeys.Add(new ApiKey
                {
                    Id = "key", OrganizationId = "org", Name = "test", KeyPrefix = key.Prefix,
                    KeyHash = tokens.Hash(key.Token), ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite }),
                    CreatedAt = now, UpdatedAt = now
                });
                db.Applications.Add(new MinicloudApplication
                {
                    Id = "appl", OrganizationId = "org", Name = "App", Slug = "app", NormalizedSlug = "app",
                    DefaultEnvironmentId = "prod", LegacyRootAppId = "prod", CreatedAt = now, UpdatedAt = now
                });
                db.Apps.AddRange(new[] { "prod", "staging" }.Select(id => new MinicloudApp
                {
                    Id = id, ApplicationId = "appl", OrganizationId = "org", Name = id, Slug = id,
                    EnvironmentName = id, NormalizedEnvironmentName = id,
                    CreatedAt = now, UpdatedAt = now
                }));
                db.RuntimeServers.Add(new RuntimeServer
                {
                    Id = "server", AppId = "prod", OrganizationId = "org", Provider = "Hetzner",
                    ProviderServerId = "synthetic", LastSeenAt = now, CreatedAt = now, UpdatedAt = now
                });
                for (var index = 0; index < 24; index++)
                {
                    var observed = now.AddSeconds(index - 23);
                    db.Deployments.Add(new Deployment
                    {
                        Id = $"dep{index:D3}", AppId = "prod", OrganizationId = "org", Status = DeploymentStatus.Succeeded,
                        CreatedAt = observed, UpdatedAt = observed, CompletedAt = observed
                    });
                    db.RuntimeStatusSnapshots.Add(new RuntimeStatusSnapshot
                    {
                        Id = $"status{index:D3}", AppId = "prod", OrganizationId = "org", RuntimeServerId = "server",
                        ObservedAt = observed, ReceivedAt = observed,
                        StatusJson = JsonSerializer.Serialize(new { observedAt = observed,
                            services = new[] { new { name = "web", state = "running", health = "healthy" } } })
                    });
                }
                await db.SaveChangesAsync();
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Canonical lists must materialize only the latest deployment and snapshot,
            // independently of retained history. PostgreSQL executes both selections.
            var listed = await client.GetFromJsonAsync<List<ApplicationResponse>>("/api/apps?organizationId=org");
            var production = Assert.Single(listed!).Environments.Single(x => x.Id == "prod");
            Assert.Equal("dep023", production.LatestDeployment!.Id);
            Assert.Equal(1, production.RuntimeObservation.HealthyServices);
            // Detached summary projections do not invoke EF's entity materializer.
            // Both that projection and the full entity path must remain bounded.
            Assert.Equal(0, intercepted.DeploymentsMaterialized);
            Assert.Contains(intercepted.ReadCommands, sql =>
                sql.Contains("FROM deployments", StringComparison.OrdinalIgnoreCase) &&
                sql.Contains("LIMIT 1", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, intercepted.SnapshotsMaterialized);

            await using var deleting = new MinicloudDbContext(options);
            await using var deletionTransaction = await deleting.Database.BeginTransactionAsync();
            var application = await deleting.Applications.FromSqlRaw("SELECT * FROM applications WHERE id = 'appl' FOR UPDATE").SingleAsync();

            var patch = client.PatchAsJsonAsync("/api/apps/appl", new UpdateApplicationRequest(DefaultEnvironmentId: "staging"));
            await intercepted.LockAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(patch.IsCompleted, "Default selection must wait for the application's deletion/admission lock.");
            var target = await deleting.Apps.SingleAsync(x => x.Id == "staging");
            target.DeletionStatus = "queued";
            application.DefaultEnvironmentId = "prod";
            await deleting.SaveChangesAsync();
            await deletionTransaction.CommitAsync();

            using var response = await patch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("environment_deleting", await response.Content.ReadAsStringAsync());
            await using var verify = new MinicloudDbContext(options);
            Assert.Equal("prod", (await verify.Applications.SingleAsync()).DefaultEnvironmentId);
            Assert.Equal("queued", (await verify.Apps.SingleAsync(x => x.Id == "staging")).DeletionStatus);
        });
    }

    private sealed class ApplicationLockInterceptor : DbCommandInterceptor, IMaterializationInterceptor
    {
        private int deployments;
        private int snapshots;
        public int DeploymentsMaterialized => deployments;
        public int SnapshotsMaterialized => snapshots;
        public List<string> ReadCommands { get; } = [];
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (entity is Deployment) Interlocked.Increment(ref deployments);
            if (entity is RuntimeStatusSnapshot) Interlocked.Increment(ref snapshots);
            return entity;
        }

        public TaskCompletionSource LockAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ReadCommands.Add(command.CommandText);
            if (command.CommandText.Contains("FROM applications", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                LockAttempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ConcurrentApiFactory(DbContextOptions<MinicloudDbContext> options, ApplicationLockInterceptor interceptor)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Postgres",
                ["Database:RunMigrationsOnStartup"] = "false",
                ["Providers:AllowLegacyConfiguration"] = "true",
                ["Firebase:ProjectId"] = "minicloud-test"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(builder =>
                    builder.UseNpgsql(options.Extensions.OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>().Single().ConnectionString)
                        .AddInterceptors(interceptor));
            });
        }
    }
}
