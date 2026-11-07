using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppServiceSecretServiceTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public AppServiceSecretServiceTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task SetAsync_StoresMetadataWithoutValue()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_1", "org_service_secret_1");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var store = new FakeSecretStore();
        var service = CreateService(db, store);

        var result = await service.SetAsync(app, new SetAppServiceSecretRequest("api", "MAPBOX_ACCESS_TOKEN", "secret-value"), CancellationToken.None);

        Assert.True(result.Succeeded);
        var row = Assert.Single(db.AppServiceSecrets.Where(x => x.AppId == app.Id));
        Assert.Equal("api", row.ServiceName);
        Assert.Equal("MAPBOX_ACCESS_TOKEN", row.Name);
        Assert.DoesNotContain("secret-value", row.StorePath, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", result.Secret!.ToString(), StringComparison.Ordinal);
        Assert.Equal("secret-value", store.Values[row.StorePath]);
        var serviceDefinition = Assert.Single(db.AppServiceSecretServices.Where(x => x.AppId == app.Id));
        Assert.Equal("api", serviceDefinition.ServiceName);
    }

    [Fact]
    public async Task SetAsync_StoresAppScopedSecretWhenServiceIsOmitted()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_app_scope_1", "org_service_secret_app_scope_1");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var store = new FakeSecretStore();
        var service = CreateService(db, store);

        var result = await service.SetAsync(app, new SetAppServiceSecretRequest(null, "MAPBOX_ACCESS_TOKEN", "secret-value"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("", result.Secret!.ServiceName);
        var row = Assert.Single(db.AppServiceSecrets.Where(x => x.AppId == app.Id));
        Assert.Equal("__app", row.ServiceName);
        Assert.Empty(db.AppServiceSecretServices.Where(x => x.AppId == app.Id));
        Assert.Equal("secret-value", store.Values[row.StorePath]);
    }

    [Fact]
    public async Task CreateServiceAsync_AddsServiceBeforeDeployment()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_4", "org_service_secret_4");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeSecretStore());

        var result = await service.CreateServiceAsync(app, new CreateAppServiceSecretServiceRequest("web"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("web", result.Service!.ServiceName);
        Assert.Equal(0, result.Service.SecretCount);
        Assert.Equal("web", Assert.Single(db.AppServiceSecretServices.Where(x => x.AppId == app.Id)).ServiceName);
    }

    [Fact]
    public async Task RenameServiceAsync_MovesSecretMetadataToNewServiceName()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_5", "org_service_secret_5");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeSecretStore());
        await service.SetAsync(app, new SetAppServiceSecretRequest("web", "API_TOKEN", "secret-value"), CancellationToken.None);
        var serviceDefinition = Assert.Single(db.AppServiceSecretServices.Where(x => x.AppId == app.Id));

        var result = await service.RenameServiceAsync(serviceDefinition, new UpdateAppServiceSecretServiceRequest("api"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("api", result.Service!.ServiceName);
        Assert.Equal(1, result.Service.SecretCount);
        Assert.Equal("api", Assert.Single(db.AppServiceSecrets.Where(x => x.AppId == app.Id)).ServiceName);
    }

    [Fact]
    public async Task ResolveForDeploymentAsync_ReturnsValuesForReferences()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_2", "org_service_secret_2");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var store = new FakeSecretStore();
        var service = CreateService(db, store);
        await service.SetAsync(app, new SetAppServiceSecretRequest("api", "MAPBOX_ACCESS_TOKEN", "secret-value"), CancellationToken.None);

        var resolved = await service.ResolveForDeploymentAsync(
            app.Id,
            [
                new DeploymentServiceRequest(
                    "api",
                    "ghcr.io/example/api:latest",
                    8080,
                    true,
                    "/",
                    "/health",
                    SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MAPBOX_TOKEN"] = "MAPBOX_ACCESS_TOKEN"
                    })
            ],
            CancellationToken.None);

        Assert.Equal("secret-value", resolved["api"]["MAPBOX_TOKEN"]);
    }

    [Fact]
    public async Task ResolveForDeploymentAsync_ReturnsAppScopedValuesForAnyServiceReference()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_app_scope_2", "org_service_secret_app_scope_2");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var store = new FakeSecretStore();
        var service = CreateService(db, store);
        await service.SetAsync(app, new SetAppServiceSecretRequest(null, "MAPBOX_ACCESS_TOKEN", "secret-value"), CancellationToken.None);

        var resolved = await service.ResolveForDeploymentAsync(
            app.Id,
            [
                new DeploymentServiceRequest(
                    "api",
                    "ghcr.io/example/api:latest",
                    8080,
                    true,
                    "/",
                    "/health",
                    SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MAPBOX_TOKEN"] = "MAPBOX_ACCESS_TOKEN"
                    }),
                new DeploymentServiceRequest(
                    "worker",
                    "ghcr.io/example/worker:latest",
                    8080,
                    false,
                    "/",
                    "/health",
                    SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MAPBOX_TOKEN"] = "MAPBOX_ACCESS_TOKEN"
                    })
            ],
            CancellationToken.None);

        Assert.Equal("secret-value", resolved["api"]["MAPBOX_TOKEN"]);
        Assert.Equal("secret-value", resolved["worker"]["MAPBOX_TOKEN"]);
    }

    [Fact]
    public async Task ResolveForDeploymentAsync_ReturnsStoredServiceSecretsWhenNoExplicitReferences()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_6", "org_service_secret_6");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var store = new FakeSecretStore();
        var service = CreateService(db, store);
        await service.SetAsync(app, new SetAppServiceSecretRequest("api", "MAPBOX_ACCESS_TOKEN", "secret-value"), CancellationToken.None);

        var resolved = await service.ResolveForDeploymentAsync(
            app.Id,
            [
                new DeploymentServiceRequest(
                    "api",
                    "ghcr.io/example/api:latest",
                    8080,
                    true,
                    "/",
                    "/health")
            ],
            CancellationToken.None);

        Assert.Equal("secret-value", resolved["api"]["MAPBOX_ACCESS_TOKEN"]);
    }

    [Fact]
    public async Task ResolveForDeploymentAsync_IgnoresStoredSecretsForNonMatchingServices()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_7", "org_service_secret_7");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeSecretStore());
        await service.SetAsync(app, new SetAppServiceSecretRequest("worker", "API_TOKEN", "secret-value"), CancellationToken.None);

        var resolved = await service.ResolveForDeploymentAsync(
            app.Id,
            [
                new DeploymentServiceRequest(
                    "api",
                    "ghcr.io/example/api:latest",
                    8080,
                    true,
                    "/",
                    "/health")
            ],
            CancellationToken.None);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task ValidateReferencesAsync_UsesScopedMetadataWithoutReadingValues()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_secret_validation", "org_secret_validation");
        var foreign = App("app_secret_validation_foreign", "org_secret_validation_foreign");
        db.Apps.AddRange(app, foreign);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeSecretStore());
        await service.SetAsync(app, new SetAppServiceSecretRequest(null, "SHARED", "sentinel-secret"), default);
        await service.SetAsync(app, new SetAppServiceSecretRequest("api", "SCOPED", "sentinel-secret"), default);
        await service.SetAsync(foreign, new SetAppServiceSecretRequest(null, "FOREIGN", "sentinel-secret"), default);
        DeploymentSecretServiceRequest Request(string name, string secret) => new(name,
            new Dictionary<string, string> { ["TOKEN"] = secret });
        await service.ValidateReferencesAsync(app.Id, [Request("api", "SHARED"), Request("api", "SCOPED"),
            Request("api", AppServiceSecretService.ReservedTelemetryHeadersName)], default);
        foreach (var request in new[] { Request("worker", "SCOPED"), Request("api", "FOREIGN"), Request("api", "MISSING") })
        {
            var exception = await Assert.ThrowsAsync<MissingAppServiceSecretException>(() => service.ValidateReferencesAsync(app.Id, [request], default));
            Assert.DoesNotContain("sentinel-secret", exception.Message);
        }
        var scoped = db.AppServiceSecrets.Single(x => x.AppId == app.Id && x.Name == "SCOPED");
        scoped.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<MissingAppServiceSecretException>(() => service.ValidateReferencesAsync(app.Id, [Request("api", "SCOPED")], default));
    }

    [Fact]
    public async Task ResolveForDeploymentAsync_FailsForMissingReference()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_3", "org_service_secret_3");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeSecretStore());

        await Assert.ThrowsAsync<MissingAppServiceSecretException>(() => service.ResolveForDeploymentAsync(
            app.Id,
            [
                new DeploymentServiceRequest(
                    "api",
                    "ghcr.io/example/api:latest",
                    8080,
                    true,
                    "/",
                    "/health",
                    SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MAPBOX_TOKEN"] = "MAPBOX_ACCESS_TOKEN"
                    })
            ],
            CancellationToken.None));
    }

    [Fact]
    public async Task ResolveForDeploymentAsync_ServiceScopedSecretOverridesAppScopedSecret()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_override", "org_service_secret_override");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var store = new FakeSecretStore();
        var service = CreateService(db, store);
        await service.SetAsync(app, new SetAppServiceSecretRequest(null, "MAPBOX_ACCESS_TOKEN", "app-level-secret"), CancellationToken.None);
        await service.SetAsync(app, new SetAppServiceSecretRequest("api", "MAPBOX_ACCESS_TOKEN", "service-level-secret"), CancellationToken.None);

        var resolved = await service.ResolveForDeploymentAsync(
            app.Id,
            [
                new DeploymentServiceRequest(
                    "api",
                    "ghcr.io/example/api:latest",
                    8080,
                    true,
                    "/",
                    "/health",
                    SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["MAPBOX_TOKEN"] = "MAPBOX_ACCESS_TOKEN"
                    })
            ],
            CancellationToken.None);

        Assert.Equal("service-level-secret", resolved["api"]["MAPBOX_TOKEN"]);
    }

    [Fact]
    public async Task ResolveForDeploymentAsync_IgnoresMissingReservedTelemetryHeaders()
    {
        await using var db = _harness.CreateDbContext();
        var app = App("app_service_secret_telemetry_optional", "org_service_secret_telemetry_optional");
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var store = new FakeSecretStore();
        var service = CreateService(db, store);

        var resolved = await service.ResolveForDeploymentAsync(
            app.Id,
            [
                new DeploymentServiceRequest(
                    "api",
                    "ghcr.io/example/api:latest",
                    8080,
                    true,
                    "/",
                    "/health",
                    SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = TelemetryLimits.ReservedHeadersSecretName
                    })
            ],
            CancellationToken.None);

        Assert.False(resolved.TryGetValue("api", out var apiSecrets) && apiSecrets.ContainsKey("OTEL_EXPORTER_OTLP_LOGS_HEADERS"));
    }

    private static AppServiceSecretService CreateService(MinicloudDbContext db, ISecretStore store) =>
        new(db, store, new TestIdGenerator(), new TestTimeProvider());

    private static MinicloudApp App(string appId, string organizationId) =>
        new()
        {
            Id = appId,
            OrganizationId = organizationId,
            Organization = new Organization { Id = organizationId, Name = organizationId, Slug = organizationId },
            Name = appId,
            Slug = appId,
            Plan = AppPlan.P0,
            Database = DatabaseMode.Postgres,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private sealed class FakeSecretStore : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
        {
            var path = $"{request.OrganizationId}/{request.AppId}/{request.ServiceName}/{request.Name}";
            Values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference(OpenBaoSecretStore.ProviderName, path, "1"));
        }

        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(Values[reference.Path]);

        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            Values.Remove(reference.Path);
            return Task.CompletedTask;
        }
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 5, 23, 12, 0, 0, TimeSpan.Zero);
    }
}
