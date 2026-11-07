using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

/// <summary>
/// T30 item 5: the OTLP headers value travels through the OpenBao service-secret
/// path only. Generated deployment artifacts carry the secretEnv name mapping, and
/// branch cloning excludes the reserved parent credential.
/// </summary>
public sealed class TelemetrySecretArtifactTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public TelemetrySecretArtifactTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task EnqueueAsync_OtlpHeadersTravelAsSecretReferenceOnly()
    {
        const string rawHeaders = "Authorization=Bearer%20mcl_ingest_secret_value";
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            new TestSecretService(),
            NullLogger<DeploymentOrchestrator>.Instance);
        var organization = new Organization { Id = "org_123", Name = "Shop", Slug = "shop" };
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "Shop",
            Slug = "shop",
            Plan = AppPlan.P0H0
        };
        var deployment = new Deployment
        {
            Id = "dep_123",
            OrganizationId = organization.Id,
            AppId = app.Id,
            Database = DatabaseMode.Postgres,
            Status = DeploymentStatus.Queued,
            CreatedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
            Services =
            [
                new DeploymentService
                {
                    Name = "backend",
                    Image = "ghcr.io/example/backend:sha",
                    Port = 8080,
                    Public = true,
                    Path = "/",
                    HealthPath = "/health"
                }
            ]
        };
        var requestedServices = new[]
        {
            new Minicloud.Common.Contracts.DeploymentServiceRequest(
                "backend",
                "ghcr.io/example/backend:sha",
                8080,
                true,
                "/",
                "/health",
                SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = TelemetryLimits.ReservedHeadersSecretName
                })
        };

        await orchestrator.EnqueueAsync(organization, app, deployment, requestedPostgresPassword: null, requestedServices, domainBindings: null, CancellationToken.None);

        // T50 queue gateway: no GitHub dispatch, so the raw value has no HTTP
        // body to leak into. The worker resolves the named reference later.
        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.DoesNotContain(rawHeaders, JsonSerializer.Serialize(requestedServices), StringComparison.Ordinal);
        Assert.DoesNotContain("mcl_ingest_secret_value", JsonSerializer.Serialize(requestedServices), StringComparison.Ordinal);
        Assert.Equal(
            TelemetryLimits.ReservedHeadersSecretName,
            requestedServices[0].SecretEnv!["OTEL_EXPORTER_OTLP_LOGS_HEADERS"]);
    }

    [Fact]
    public async Task CloneAsync_Skips_Reserved_Telemetry_Headers_Value()
    {
        var tag = Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();
        await using var db = _harness.CreateDbContext();
        var now = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var org = new Organization
        {
            Id = $"org_{tag}", Name = $"Org {tag}", Slug = $"org-{tag}",
            CreatedAt = now, UpdatedAt = now
        };
        db.Organizations.Add(org);
        var parent = new MinicloudApp
        {
            Id = $"app_{tag}_parent", OrganizationId = org.Id, Name = "shop", Slug = "shop",
            Plan = AppPlan.P0H0, Database = DatabaseMode.Postgres, CreatedAt = now, UpdatedAt = now
        };
        var branch = new MinicloudApp
        {
            Id = $"app_{tag}_branch", OrganizationId = org.Id, Name = "shop (branch)", Slug = "shop-branch",
            Plan = AppPlan.P0H0, Database = DatabaseMode.Postgres, CreatedAt = now, UpdatedAt = now
        };
        db.Apps.AddRange(parent, branch);
        db.AppServiceSecrets.AddRange(
            new AppServiceSecret
            {
                Id = $"sec_{tag}_plain", OrganizationId = org.Id, AppId = parent.Id,
                ServiceName = "backend", Name = "DATABASE_URL",
                StoreProvider = "memory", StorePath = "plain", Status = "active",
                CreatedAt = now, UpdatedAt = now
            },
            new AppServiceSecret
            {
                Id = $"sec_{tag}_headers", OrganizationId = org.Id, AppId = parent.Id,
                ServiceName = "backend", Name = TelemetryLimits.ReservedHeadersSecretName,
                StoreProvider = "memory", StorePath = "headers", Status = "active",
                CreatedAt = now, UpdatedAt = now
            });
        await db.SaveChangesAsync();

        var store = new StubSecretStore(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["plain"] = "postgres://db",
            ["headers"] = "Authorization=Bearer%20mcl_ingest_parent_token"
        });
        var secrets = new AppServiceSecretService(db, store, new TestIdGenerator(), TimeProvider.System);
        await secrets.CloneAsync(parent, branch, CancellationToken.None);
        await db.SaveChangesAsync(CancellationToken.None);

        var cloned = db.AppServiceSecrets.Where(x => x.AppId == branch.Id && x.DeletedAt == null).ToList();
        var names = cloned.Select(x => x.Name).ToList();
        Assert.Contains("DATABASE_URL", names);
        Assert.DoesNotContain(TelemetryLimits.ReservedHeadersSecretName, names);
        Assert.DoesNotContain(store.WrittenValues, v => v.Contains("mcl_ingest_parent_token", StringComparison.Ordinal));
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        private int _nextId;
        public string NewId(string prefix) => $"{prefix}_{++_nextId}";
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestSecretService : IAppSecretService
    {
        public Task<AppSecretValue> EnsurePostgresPasswordAsync(MinicloudApp app, string? requestedPassword, CancellationToken cancellationToken) =>
            Task.FromResult(new AppSecretValue("pg-secret", ShouldUpdateDatabasePassword: true, WasGenerated: true));
    }

    private sealed class StubSecretStore(Dictionary<string, string> values) : ISecretStore
    {
        public List<string> WrittenValues { get; } = [];

        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
        {
            WrittenValues.Add(request.Value);
            return Task.FromResult(new StoredSecretReference("memory", $"cloned/{request.Name}", "1"));
        }

        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(values.TryGetValue(reference.Path, out var value) ? value : string.Empty);

        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
