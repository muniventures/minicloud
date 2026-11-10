using Microsoft.Extensions.Logging.Abstractions;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

/// <summary>
/// T50 queue gateway: POST /api/deployments returns 201 {status:queued} and the
/// API makes zero GitHub calls. DeploymentOrchestrator.EnqueueAsync only sets
/// the deployment domain, ensures the postgres secret, and leaves the row
/// Queued for minicloud-worker (PostgreSQL SKIP LOCKED claim). DispatchAsync
/// is an [Obsolete] shim to EnqueueAsync; SyncRunAsync is a read-only no-op.
/// </summary>
public sealed class DeploymentOrchestratorTests
{
    [Fact]
    public async Task DispatchAsync_ShimStaysQueuedWithoutGitHubCalls()
    {
        var secrets = new TestSecretService("pg-secret");
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            secrets,
            NullLogger<DeploymentOrchestrator>.Instance);
        var organization = new Organization { Id = "org_123", Name = "TeamCore", Slug = "teamcore" };
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "TeamCore",
            Slug = "teamcore",
            Plan = AppPlan.P0H0
        };
        var deployment = Deployment();

#pragma warning disable CS0618 // Obsolete shim under test; must delegate to EnqueueAsync.
        var result = await orchestrator.DispatchAsync(organization, app, deployment, requestedPostgresPassword: null, requestedServices: null, runtimeApiKey: "mc_live_runtime", CancellationToken.None);
#pragma warning restore CS0618

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Equal("teamcore-teamcore-web.app.muni.dev", deployment.Domain);
        Assert.Null(deployment.GithubRunId);
        Assert.Null(deployment.GithubRunUrl);
        Assert.Equal("pg-secret", result.GeneratedPostgresPassword);
        Assert.Equal(1, secrets.CallCount);
        Assert.Null(secrets.RequestedPasswordReceived);
    }

    [Fact]
    public async Task EnqueueAsync_SkipsPostgresSecretForSqlite()
    {
        var secrets = new ThrowingSecretService();
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            secrets,
            NullLogger<DeploymentOrchestrator>.Instance);
        var organization = new Organization { Id = "org_123", Name = "TeamCore", Slug = "teamcore" };
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "TeamCore",
            Slug = "teamcore",
            Plan = AppPlan.P0H0
        };
        var deployment = Deployment();
        deployment.Database = DatabaseMode.Sqlite;

        var result = await orchestrator.EnqueueAsync(organization, app, deployment, requestedPostgresPassword: null, requestedServices: null, domainBindings: null, CancellationToken.None);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Equal("teamcore-teamcore-web.app.muni.dev", deployment.Domain);
        Assert.Null(result.GeneratedPostgresPassword);
        Assert.Equal(0, secrets.CallCount);
    }

    [Fact]
    public async Task EnqueueAsync_PrefersOrderedDomainBindingsForDeploymentDomain()
    {
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            new TestSecretService("pg-secret"),
            NullLogger<DeploymentOrchestrator>.Instance);
        var organization = new Organization { Id = "org_123", Name = "TeamCore", Slug = "teamcore" };
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "TeamCore",
            Slug = "teamcore",
            Plan = AppPlan.P0H0
        };
        var deployment = Deployment();
        var requestedServices = new[]
        {
            new Minicloud.Common.Contracts.DeploymentServiceRequest(
                "frontend",
                "ghcr.io/muniventures/teamcore-frontend:latest",
                3000,
                true,
                "/",
                "/health",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NODE_ENV"] = "production"
                }),
            new Minicloud.Common.Contracts.DeploymentServiceRequest(
                "backend",
                "ghcr.io/muniventures/teamcore-backend:latest",
                8080,
                true,
                "/",
                "/health")
        };
        var domainBindings = new[]
        {
            new Domain
            {
                Id = "dom_frontend",
                OrganizationId = organization.Id,
                AppId = app.Id,
                ServiceName = "frontend",
                Hostname = "teamcore-teamcore-frontend.app.muni.dev"
            },
            new Domain
            {
                Id = "dom_backend",
                OrganizationId = organization.Id,
                AppId = app.Id,
                ServiceName = "backend",
                Hostname = "teamcore-teamcore-backend.app.muni.dev"
            }
        };

        await orchestrator.EnqueueAsync(organization, app, deployment, requestedPostgresPassword: null, requestedServices, domainBindings, CancellationToken.None);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Equal("teamcore-teamcore-backend.app.muni.dev", deployment.Domain);
    }

    [Fact]
    public async Task EnqueueAsync_LeavesSecretReferencesUnresolvedWithoutGitHubCalls()
    {
        var secrets = new TestSecretService("pg-secret");
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            secrets,
            NullLogger<DeploymentOrchestrator>.Instance);
        var organization = new Organization { Id = "org_123", Name = "TeamCore", Slug = "teamcore" };
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "TeamCore",
            Slug = "teamcore",
            Plan = AppPlan.P0H0
        };
        var deployment = Deployment();
        var requestedServices = new[]
        {
            new Minicloud.Common.Contracts.DeploymentServiceRequest(
                "backend",
                "ghcr.io/muniventures/teamcore-backend:latest",
                8080,
                true,
                "/",
                "/health",
                SecretEnv: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MAPBOX_ACCESS_TOKEN"] = "MAPBOX_ACCESS_TOKEN"
                })
        };

        await orchestrator.EnqueueAsync(organization, app, deployment, requestedPostgresPassword: null, requestedServices, domainBindings: null, CancellationToken.None);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Equal("teamcore-teamcore-backend.app.muni.dev", deployment.Domain);
        Assert.Equal(1, secrets.CallCount);
        Assert.Equal(2, deployment.Services.Count);
        Assert.Equal("MAPBOX_ACCESS_TOKEN", requestedServices[0].SecretEnv!["MAPBOX_ACCESS_TOKEN"]);
    }

    [Fact]
    public async Task EnqueueAsync_UsesRequestedPostgresPasswordWithoutReturningGeneratedValue()
    {
        var secrets = new TestSecretService("generated-secret");
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            secrets,
            NullLogger<DeploymentOrchestrator>.Instance);
        var organization = new Organization { Id = "org_123", Name = "TeamCore", Slug = "teamcore" };
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "TeamCore",
            Slug = "teamcore",
            Plan = AppPlan.P0H0
        };
        var deployment = Deployment();

        var result = await orchestrator.EnqueueAsync(organization, app, deployment, "user-secret", requestedServices: null, domainBindings: null, CancellationToken.None);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Equal("teamcore-teamcore-web.app.muni.dev", deployment.Domain);
        Assert.Equal("user-secret", secrets.RequestedPasswordReceived);
        Assert.Null(result.GeneratedPostgresPassword);
    }

    [Fact]
    public async Task EnqueueAsync_ReturnsNullWhenPostgresPasswordIsReused()
    {
        var secrets = new TestSecretService("stored-secret", defaultShouldUpdateDatabasePassword: false, defaultWasGenerated: false);
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            secrets,
            NullLogger<DeploymentOrchestrator>.Instance);
        var organization = new Organization { Id = "org_123", Name = "TeamCore", Slug = "teamcore" };
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "TeamCore",
            Slug = "teamcore",
            Plan = AppPlan.P0H0
        };
        var deployment = Deployment();

        var result = await orchestrator.EnqueueAsync(organization, app, deployment, requestedPostgresPassword: null, requestedServices: null, domainBindings: null, CancellationToken.None);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Equal("teamcore-teamcore-web.app.muni.dev", deployment.Domain);
        Assert.Null(secrets.RequestedPasswordReceived);
        Assert.Null(result.GeneratedPostgresPassword);
    }

    [Fact]
    public async Task SyncRunAsync_IsReadOnlyAndLeavesDispatchingUntouched()
    {
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            new TestSecretService("pg-secret"),
            NullLogger<DeploymentOrchestrator>.Instance);
        var deployment = Deployment();
        deployment.Status = DeploymentStatus.Dispatching;
        deployment.Domain = "teamcore-teamcore.app.muni.dev";

#pragma warning disable CS0618 // Obsolete no-op under test; must stay read-only.
        await orchestrator.SyncRunAsync(deployment, CancellationToken.None);
#pragma warning restore CS0618

        Assert.Equal(DeploymentStatus.Dispatching, deployment.Status);
        Assert.Null(deployment.GithubRunId);
        Assert.Null(deployment.GithubRunUrl);
        Assert.Null(deployment.WebsiteUrl);
    }

    [Fact]
    public async Task SyncRunAsync_DoesNotTransitionDeployingToFailed()
    {
        var orchestrator = new DeploymentOrchestrator(
            new DeploymentLifecycleService(new TestIdGenerator(), new TestTimeProvider()),
            new TestSecretService("pg-secret"),
            NullLogger<DeploymentOrchestrator>.Instance);
        var deployment = Deployment();
        deployment.Status = DeploymentStatus.Deploying;
        deployment.GithubRunId = "12345";

#pragma warning disable CS0618 // Obsolete no-op under test; must stay read-only.
        await orchestrator.SyncRunAsync(deployment, CancellationToken.None);
#pragma warning restore CS0618

        Assert.Equal(DeploymentStatus.Deploying, deployment.Status);
        Assert.Equal("12345", deployment.GithubRunId);
        Assert.Null(deployment.FailureCode);
        Assert.Null(deployment.FailureMessage);
    }

    private static Deployment Deployment() =>
        new()
        {
            Id = "dep_123",
            OrganizationId = "org_123",
            AppId = "app_123",
            Database = DatabaseMode.Postgres,
            Status = DeploymentStatus.Queued,
            CreatedAt = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero),
            Services =
            [
                new DeploymentService
                {
                    Name = "frontend",
                    Image = "ghcr.io/muniventures/teamcore-frontend:latest",
                    Port = 3000,
                    Public = true,
                    Path = "/",
                    HealthPath = "/health"
                },
                new DeploymentService
                {
                    Name = "backend",
                    Image = "ghcr.io/muniventures/teamcore-backend:latest",
                    Port = 8080,
                    Public = true,
                    Path = "/",
                    HealthPath = "/health"
                }
            ]
        };

    private sealed class TestIdGenerator : IIdGenerator
    {
        private int _nextId;
        public string NewId(string prefix) => $"{prefix}_{++_nextId}";
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);
    }

    private sealed class TestSecretService(
        string postgresPassword,
        bool defaultShouldUpdateDatabasePassword = true,
        bool defaultWasGenerated = true) : IAppSecretService
    {
        public int CallCount { get; private set; }
        public string? RequestedPasswordReceived { get; private set; }

        public Task<AppSecretValue> EnsurePostgresPasswordAsync(MinicloudApp app, string? requestedPassword, CancellationToken cancellationToken)
        {
            CallCount++;
            RequestedPasswordReceived = requestedPassword;
            return Task.FromResult(requestedPassword is null
                ? new AppSecretValue(postgresPassword, defaultShouldUpdateDatabasePassword, defaultWasGenerated)
                : new AppSecretValue(requestedPassword, ShouldUpdateDatabasePassword: true, WasGenerated: false));
        }
    }

    private sealed class ThrowingSecretService : IAppSecretService
    {
        public int CallCount { get; private set; }

        public Task<AppSecretValue> EnsurePostgresPasswordAsync(MinicloudApp app, string? requestedPassword, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("Postgres secrets must not be touched for Sqlite deployments.");
        }
    }
}
