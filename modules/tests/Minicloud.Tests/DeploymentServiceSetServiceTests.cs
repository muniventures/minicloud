using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DeploymentServiceSetServiceTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public DeploymentServiceSetServiceTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task BuildMergedServicesAsync_ReplaysSuccessfulDeploymentsAndReplacesRequestedService()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_set_1", "org_service_set_1", "teamcore");
        db.Apps.Add(app);
        db.Deployments.Add(SucceededDeployment("dep_frontend_1", app, new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            Service("frontend", "ghcr.io/teamcore/frontend:a", 3000, true, "/", "/", """{"NODE_ENV":"production"}""")));
        db.Deployments.Add(SucceededDeployment("dep_backend_1", app, new DateTimeOffset(2026, 5, 1, 11, 0, 0, TimeSpan.Zero),
            Service("backend", "ghcr.io/teamcore/backend:a", 8080, true, "/", "/health")));
        var persisted = db.ChangeTracker.Entries<Deployment>().SelectMany(x => x.Entity.Services).FirstOrDefault(x => x.Name == "frontend");
        if (persisted is not null) { persisted.StorageJson = """{"uploads":{"sizeGb":20,"mountPath":"/app/uploads","uid":1000,"gid":1000}}"""; }
        await db.SaveChangesAsync();

        var service = new DeploymentServiceSetService();
        var merged = await service.BuildMergedServicesAsync(
            db,
            app.Id,
            [
                new DeploymentServiceRequest("backend", "ghcr.io/teamcore/backend:b", 8080, true, "/", "/health")
            ],
            CancellationToken.None);

        Assert.Equal(2, merged.Count);
        var frontend = Assert.Single(merged, x => x.Name == "frontend");
        var backend = Assert.Single(merged, x => x.Name == "backend");
        Assert.Equal("ghcr.io/teamcore/frontend:a", frontend.Image);
        Assert.Equal("production", frontend.Env?["NODE_ENV"]);
        Assert.Equal(20, frontend.Storage?["uploads"].SizeGb);
        Assert.Equal("ghcr.io/teamcore/backend:b", backend.Image);
    }

    [Fact]
    public async Task BuildMergedServicesAsync_IgnoresFailedDeployments()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_set_2", "org_service_set_2", "teamcore-2");
        db.Apps.Add(app);
        db.Deployments.Add(SucceededDeployment("dep_backend_2a", app, new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            Service("backend", "ghcr.io/teamcore/backend:a", 8080, true, "/", "/health")));
        db.Deployments.Add(Deployment("dep_backend_2b", app, DeploymentStatus.Failed, new DateTimeOffset(2026, 5, 1, 11, 0, 0, TimeSpan.Zero),
            Service("backend", "ghcr.io/teamcore/backend:bad", 8080, true, "/", "/health")));
        await db.SaveChangesAsync();

        var service = new DeploymentServiceSetService();
        var merged = await service.BuildMergedServicesAsync(db, app.Id, Array.Empty<DeploymentServiceRequest>(), CancellationToken.None);

        var backend = Assert.Single(merged);
        Assert.Equal("ghcr.io/teamcore/backend:a", backend.Image);
    }

    [Fact]
    public async Task BuildActiveServicesAsync_NormalizesLegacyArtifactImageToLocalTag()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_set_local", "org_service_set_local", "teamcore-local");
        var createdAt = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);
        db.Apps.Add(app);
        db.DeploymentArtifacts.Add(new DeploymentArtifact
        {
            Id = "art_local_1",
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            ServiceName = "web",
            FileName = "web.zip",
            ContentType = "application/zip",
            SizeBytes = 3,
            Sha256 = "abc123",
            ManifestJson = "{}",
            StoragePath = "artifacts/art_local_1.zip",
            Status = DeploymentArtifactStatus.Ready,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ExpiresAt = createdAt.AddDays(1),
            UploadedAt = createdAt
        });
        db.Deployments.Add(SucceededDeployment(
            "dep_local_1",
            app,
            createdAt,
            Service(
                "web",
                "ghcr.io/muniventures/teamcore-local/web:legacy",
                3000,
                true,
                "/",
                "/health",
                artifactId: "art_local_1")));
        await db.SaveChangesAsync();

        var active = await new DeploymentServiceSetService().BuildActiveServicesAsync(
            db,
            app.Id,
            CancellationToken.None);

        var web = Assert.Single(active);
        Assert.Equal("minicloud/teamcore-local-web:dep_local_1", web.Image);
    }

    private static MinicloudApp App(string appId, string organizationId, string slug) =>
        new()
        {
            Id = appId,
            OrganizationId = organizationId,
            Organization = new Organization { Id = organizationId, Name = slug, Slug = slug },
            Name = slug,
            Slug = slug,
            Plan = AppPlan.P0,
            Database = DatabaseMode.Postgres,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static Deployment SucceededDeployment(string id, MinicloudApp app, DateTimeOffset createdAt, params DeploymentService[] services) =>
        Deployment(id, app, DeploymentStatus.Succeeded, createdAt, services);

    private static Deployment Deployment(string id, MinicloudApp app, DeploymentStatus status, DateTimeOffset createdAt, params DeploymentService[] services) =>
        new()
        {
            Id = id,
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            App = app,
            Database = DatabaseMode.Postgres,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Services = services.ToList()
        };

    [Fact]
    public async Task BuildMergedServicesAsync_PreservesAndReplacesSha256()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_set_3", "org_service_set_3", "teamcore-3");
        db.Apps.Add(app);
        db.Deployments.Add(SucceededDeployment("dep_frontend_3", app, new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            Service("frontend", "ghcr.io/teamcore/frontend:a", 3000, true, "/", "/", sha256: "hash_frontend_a")));
        db.Deployments.Add(SucceededDeployment("dep_backend_3", app, new DateTimeOffset(2026, 5, 1, 11, 0, 0, TimeSpan.Zero),
            Service("backend", "ghcr.io/teamcore/backend:a", 8080, true, "/", "/health", sha256: "hash_backend_a")));
        await db.SaveChangesAsync();

        var service = new DeploymentServiceSetService();
        var merged = await service.BuildMergedServicesAsync(
            db,
            app.Id,
            [
                new DeploymentServiceRequest("backend", "ghcr.io/teamcore/backend:b", 8080, true, "/", "/health", Sha256: "hash_backend_b")
            ],
            CancellationToken.None);

        Assert.Equal(2, merged.Count);
        var frontend = Assert.Single(merged, x => x.Name == "frontend");
        var backend = Assert.Single(merged, x => x.Name == "backend");
        Assert.Equal("hash_frontend_a", frontend.Sha256);
        Assert.Equal("hash_backend_b", backend.Sha256);
    }

    [Fact]
    public async Task BuildMergedServicesAsync_ReplaysSuccessivePartialDeploymentsAndPreservesMixedServiceShape()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_set_4", "org_service_set_4", "teamcore-4");
        db.Apps.Add(app);
        db.Deployments.Add(SucceededDeployment("dep_initial_4", app, new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero),
            Service("web", "minicloud/teamcore-4-web:dep-initial", 3000, true, "/", "/ready", "{\"MODE\":\"production\"}"),
            Service("cache", "redis:7-alpine", 6379, false, "/", "/health")));
        db.Deployments.Add(SucceededDeployment("dep_api_4", app, new DateTimeOffset(2026, 5, 1, 11, 0, 0, TimeSpan.Zero),
            Service("api", "minicloud/teamcore-4-api:dep-api", 8080, false, "/api", "/health", "{\"FEATURE\":\"on\"}")));
        db.Deployments.Add(SucceededDeployment("dep_web_4", app, new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            Service("web", "minicloud/teamcore-4-web:dep-web", 3001, true, "/", "/ready", "{\"MODE\":\"updated\"}")));
        await db.SaveChangesAsync();

        var merged = await new DeploymentServiceSetService().BuildMergedServicesAsync(
            db, app.Id,
            [new DeploymentServiceRequest("api", null, 8081, false, "/api", "/ready", ArtifactId: "art_api_2")],
            CancellationToken.None);

        Assert.Equal(3, merged.Count);
        var web = Assert.Single(merged, x => x.Name == "web");
        Assert.Equal("minicloud/teamcore-4-web:dep-web", web.Image);
        Assert.Equal(3001, web.Port);
        Assert.Equal("updated", web.Env?["MODE"]);
        var cache = Assert.Single(merged, x => x.Name == "cache");
        Assert.Equal("redis:7-alpine", cache.Image);
        Assert.False(cache.Public);
        var api = Assert.Single(merged, x => x.Name == "api");
        Assert.Equal("art_api_2", api.ArtifactId);
        Assert.Equal(8081, api.Port);
        Assert.False(api.Public);
    }

    private static DeploymentService Service(
        string name,
        string image,
        int port,
        bool isPublic,
        string path,
        string healthPath,
        string? envJson = null,
        string? sha256 = null,
        string? artifactId = null) =>
        new()
        {
            Id = $"svc_{Guid.NewGuid():N}",
            Name = name,
            Image = image,
            Port = port,
            Public = isPublic,
            Path = path,
            HealthPath = healthPath,
            EnvJson = envJson,
            Sha256 = sha256,
            ArtifactId = artifactId
        };
}
