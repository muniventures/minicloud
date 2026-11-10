using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Minicloud.Api.Auth;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppDeletionServiceTests
{
    [Theory]
    [InlineData("other", "demo", false, 404)]
    [InlineData("org", "wrong", false, 400)]
    [InlineData("org", "demo", true, 409)]
    [InlineData("org", "demo", false, 0)]
    public async Task EnforcesScopeConfirmationAndActiveDeployment(string organizationId, string confirmation, bool active, int expected)
    {
        await using var harness = new SqliteTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        var app = new MinicloudApp { Id = "app", OrganizationId = "org", Name = "Demo", Slug = "demo" };
        var application = new MinicloudApplication { Id = "application", OrganizationId = "org", Name = "Demo", Slug = "demo", NormalizedSlug = "demo", DefaultEnvironmentId = "app" };
        app.ApplicationId = application.Id;
        app.EnvironmentName = "production";
        db.Applications.Add(application);
        db.Apps.Add(app);
        db.Apps.Add(new MinicloudApp { Id = "branch", ApplicationId = application.Id, EnvironmentName = "staging", OrganizationId = "org", Name = "Branch", Slug = "branch", ParentAppId = "app" });
        db.Deployments.Add(new Deployment { Id = "dep", AppId = "app", OrganizationId = "org", Status = active ? DeploymentStatus.Deploying : DeploymentStatus.Queued });
        await db.SaveChangesAsync();
        var ids = new IdGenerator();
        var auth = new ControlPlaneAuthorization(db);
        var audit = new AuditEventService(db, ids, TimeProvider.System);
        var store = new Store();
        var secrets = new AppServiceSecretService(db, store, ids, TimeProvider.System);
        var telemetry = new TelemetryService(db, ids, TimeProvider.System, new TelemetryTokenService(), new DeploymentServiceSetService(), auth, audit, new Hosting());
        var service = new AppDeletionService(db, auth, secrets, telemetry, audit, new DeploymentArtifactStorageService(Options.Create(new DeploymentArtifactStorageOptions()), TimeProvider.System));
        var principal = new ControlPlanePrincipal("api_key", null, "key", organizationId, new HashSet<string> { ApiKeyScopes.AppsWrite, ApiKeyScopes.DeploymentsCreate });
        var result = await service.RequestApplicationDeletionAsync(principal, application.Id, confirmation, default);
        Assert.Equal(expected, result?.Status ?? 0);
        if (expected == 0)
        {
            Assert.All(await db.Apps.ToListAsync(), x => Assert.Equal("queued", x.DeletionStatus));
            Assert.Equal(DeploymentStatus.Canceled, (await db.Deployments.SingleAsync()).Status);
            Assert.False(await auth.CanAccessAppAsync(principal.ToActorIdentity(), "app", OrganizationAction.CreateDeployments, default));
            Assert.Single(await db.AuditEvents.ToListAsync());
            Assert.Null(await service.RequestApplicationDeletionAsync(principal, application.Id, confirmation, default));
            Assert.Single(await db.AuditEvents.ToListAsync());
        }
        else
        {
            Assert.All(await db.Apps.ToListAsync(), x => Assert.Null(x.DeletionStatus));
            Assert.Empty(await db.AuditEvents.ToListAsync());
        }
    }

    private sealed class Store : ISecretStore
    {
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Hosting : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
