using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Tests;

public class ApiKeyTokenServiceTests
{
    [Fact]
    public void Hash_VerifiesGeneratedToken_AndRejectsDifferentToken()
    {
        var service = new ApiKeyTokenService();

        var token = service.Generate();
        var hash = service.Hash(token.Token);

        Assert.StartsWith("mc_live_", token.Token);
        Assert.Equal(4, token.Token.Split('_').Length);
        Assert.Equal(3, token.Prefix.Split('_').Length);
        Assert.True(service.Verify(token.Token, hash));
        Assert.False(service.Verify(token.Token + "x", hash));
        Assert.DoesNotContain(token.Token, hash);
    }

    [Fact]
    public void RuntimeToken_HasUnambiguousPrefixSeparator()
    {
        var service = new RuntimeTokenService(null!, TimeProvider.System);

        var token = service.Generate();

        Assert.StartsWith("mrt_", token.Token);
        Assert.Equal(3, token.Token.Split('_').Length);
        Assert.Equal(2, token.Prefix.Split('_').Length);
        Assert.Equal(token.Prefix, service.ExtractPrefix(token.Token));
    }

    [Fact]
    public void CliLoginDefaultScopes_AllowDeployToBootstrapApps()
    {
        Assert.Contains(ApiKeyScopes.AppsRead, ApiKeyScopes.CliLoginDefault);
        Assert.Contains(ApiKeyScopes.AppsWrite, ApiKeyScopes.CliLoginDefault);
        Assert.Contains(ApiKeyScopes.DeploymentsCreate, ApiKeyScopes.CliLoginDefault);
        Assert.Contains(ApiKeyScopes.DeploymentsRead, ApiKeyScopes.CliLoginDefault);
    }

    [Fact]
    public void SecretEncryption_RoundTripsWithoutStoringPlaintext()
    {
        var service = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions
        {
            EncryptionKey = "test-secret-key"
        }));

        var encrypted = service.Encrypt("postgres-password");

        Assert.DoesNotContain("postgres-password", encrypted, StringComparison.Ordinal);
        Assert.Equal("postgres-password", service.Decrypt(encrypted));
    }
}

public class ProviderPlanMappingTests
{
    [Theory]
    [InlineData(AppPlan.P0D0, "s-1vcpu-512mb-10gb")]
    [InlineData(AppPlan.P0D1, "s-1vcpu-1gb")]
    [InlineData(AppPlan.P0D2, "s-1vcpu-2gb")]
    public void DigitalOcean_plans_default_to_nyc3_and_respect_region_overrides(AppPlan plan, string sku)
    {
        var options = new ProviderPlanOptions();
        var mapping = ProviderPlanMapping.FromAppPlan(plan, options);

        Assert.Equal(Minicloud.Common.Providers.CloudProvider.DigitalOcean, mapping.Provider);
        Assert.Equal("nyc3", mapping.Region);
        Assert.Equal(sku, mapping.Sku);

        options.DigitalOceanRegion = "nyc2";
        Assert.Equal("nyc2", ProviderPlanMapping.FromAppPlan(plan, options).Region);
    }
}

public class ControlPlaneValidationTests
{
    [Theory]
    [InlineData("p0-h-0", AppPlan.P0H0)]
    [InlineData("P0-H-0", AppPlan.P0H0)]
    [InlineData("p0-v-0", AppPlan.P0V0)]
    [InlineData("p0-v-1", AppPlan.P0V1)]
    [InlineData("p0-v-2", AppPlan.P0V2)]
    [InlineData("p0-d-0", AppPlan.P0D0)]
    [InlineData("p0-d-1", AppPlan.P0D1)]
    [InlineData("p0-d-2", AppPlan.P0D2)]
    [InlineData("p0", AppPlan.P0)]
    public void TryParseEnum_ParsesPlanCodes(string value, AppPlan expected)
    {
        Assert.True(ControlPlaneValidation.TryParseEnum<AppPlan>(value, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(AppPlan.P0H0, "p0-h-0")]
    [InlineData(AppPlan.P0V0, "p0-v-0")]
    [InlineData(AppPlan.P0V1, "p0-v-1")]
    [InlineData(AppPlan.P0V2, "p0-v-2")]
    [InlineData(AppPlan.P0D0, "p0-d-0")]
    [InlineData(AppPlan.P0D1, "p0-d-1")]
    [InlineData(AppPlan.P0D2, "p0-d-2")]
    [InlineData(AppPlan.P0, "p0")]
    public void ToWireValue_FormatsPlanCodes(AppPlan plan, string expected)
    {
        Assert.Equal(expected, ControlPlaneValidation.ToWireValue(plan));
    }

    [Fact]
    public void DatabaseMode_SupportsNoneWireValue()
    {
        Assert.True(ControlPlaneValidation.TryParseEnum<DatabaseMode>("none", out var parsed));
        Assert.Equal(DatabaseMode.None, parsed);
        Assert.Equal("none", ControlPlaneValidation.ToWireValue(DatabaseMode.None));
    }
}

public class DeploymentValidationTests
{
    [Fact]
    public void Deployment_AllowsServiceListToDefineShape()
    {
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            [
                new DeploymentServiceRequest("frontend", "ghcr.io/customer/app/frontend:abc123", 3000, true, "/", "/"),
                new DeploymentServiceRequest("worker", "ghcr.io/customer/app/worker:abc123", 8080, false, "/", "/health")
            ]);

        var result = ControlPlaneValidation.ValidateDeployment(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidBackendFrontendDeployment_Passes()
    {
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            [
                new DeploymentServiceRequest("frontend", "ghcr.io/customer/app/frontend:abc123", 3000, true, "/", "/"),
                new DeploymentServiceRequest("backend", "ghcr.io/customer/app/backend:abc123", 8080, true, "/", "/health")
            ]);

        var result = ControlPlaneValidation.ValidateDeployment(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void CustomDeployment_AllowsArbitraryServices()
    {
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            [
                new DeploymentServiceRequest("api", "ghcr.io/customer/app/api:abc123", 8080, true, "/", "/health"),
                new DeploymentServiceRequest("dashboard", "ghcr.io/customer/app/dashboard:abc123", 3000, true, "/", "/"),
                new DeploymentServiceRequest("registry", "ghcr.io/customer/app/registry:abc123", 5000, false, "/", "/health")
            ]);

        var result = ControlPlaneValidation.ValidateDeployment(request);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(6, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void Deployment_EnforcesTenServiceLimit(int serviceCount, bool expectedValid)
    {
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            Enumerable.Range(1, serviceCount)
                .Select(index => new DeploymentServiceRequest($"service-{index}", $"ghcr.io/customer/app/service-{index}:abc123", 8080, index == 1, "/", "/health"))
                .ToArray());

        var result = ControlPlaneValidation.ValidateDeployment(request);

        Assert.Equal(expectedValid, result.IsValid);
        var mergedResult = ControlPlaneValidation.ValidateMergedDeploymentServices(request.Services);
        Assert.Equal(expectedValid, mergedResult.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(result.Errors, x => x.Field == "services" && x.Message.Contains("At most 10", StringComparison.Ordinal));
            Assert.Contains(mergedResult.Errors, x => x.Field == "services" && x.Message.Contains("At most 10", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Deployment_RejectsMissingServices()
    {
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            null!);

        var result = ControlPlaneValidation.ValidateDeployment(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Field == "services" && x.Message.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Deployment_RejectsInvalidServiceEnvironmentVariableName()
    {
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            [
                new DeploymentServiceRequest(
                    "backend",
                    "ghcr.io/customer/app/backend:abc123",
                    8080,
                    true,
                    "/",
                    "/health",
                    new Dictionary<string, string>
                    {
                        ["BAD-NAME"] = "value"
                    })
            ]);

        var result = ControlPlaneValidation.ValidateDeployment(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Field == "services[0].env.BAD-NAME");
    }

    [Fact]
    public void Deployment_RequestValidationAllowsPrivateServiceForExistingPublicApp()
    {
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            [
                new DeploymentServiceRequest("worker", "ghcr.io/customer/app/worker:abc123", 8080, false, "/", "/health")
            ]);

        var result = ControlPlaneValidation.ValidateDeployment(request);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void MergedDeploymentServices_RejectsNoPublicActiveService()
    {
        var result = ControlPlaneValidation.ValidateMergedDeploymentServices(
        [
            new DeploymentServiceRequest("worker", "ghcr.io/customer/app/worker:abc123", 8080, false, "/", "/health")
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Field == "services" && x.Message.Contains("public service", StringComparison.Ordinal));
    }

    [Fact]
    public void MergedDeploymentServices_RejectsNonRootPaths()
    {
        var result = ControlPlaneValidation.ValidateMergedDeploymentServices(
        [
            new DeploymentServiceRequest("frontend", "ghcr.io/customer/app/frontend:abc123", 3000, true, "/", "/"),
            new DeploymentServiceRequest("backend", "ghcr.io/customer/app/backend:abc123", 8080, true, "/api", "/health")
        ]);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Field == "services[1].path");
    }
}

public class DeploymentDtoMappingTests
{
    [Fact]
    public void ToResponse_IncludesPublicServiceUrls()
    {
        var now = new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);
        var app = new MinicloudApp
        {
            Id = "app_test",
            OrganizationId = "org_test",
            Name = "Test",
            Slug = "test",
            Domains =
            [
                Domain("frontend", "org-test-frontend.app.muni.dev", now),
                Domain("backend", "org-test-backend.app.muni.dev", now)
            ]
        };
        var deployment = new Deployment
        {
            Id = "dep_test",
            Number = "20260521.3",
            AppId = app.Id,
            App = app,
            OrganizationId = app.OrganizationId,
            Database = DatabaseMode.Postgres,
            Status = DeploymentStatus.Succeeded,
            CreatedAt = now,
            Services =
            [
                Service("frontend"),
                Service("backend")
            ]
        };

        var response = deployment.ToResponse("https://cloud.muni.dev");

        Assert.Equal(("dep_test", "20260521.3"), (response.Id, response.Number));
        Assert.Equal("https://cloud.muni.dev/deployments/20260521.3?organizationId=org_test", response.ConsoleUrl);

        Assert.Collection(
            response.Services.OrderBy(x => x.Name),
            service =>
            {
                Assert.Equal("backend", service.Name);
                Assert.Equal(["https://org-test-backend.app.muni.dev"], service.Urls);
            },
            service =>
            {
                Assert.Equal("frontend", service.Name);
                Assert.Equal(["https://org-test-frontend.app.muni.dev"], service.Urls);
            });
    }

    [Fact]
    public void AppResponse_includes_all_branch_urls_and_excludes_disabled_domains()
    {
        var now = DateTimeOffset.UtcNow;
        var disabled = Domain("worker", "disabled.example", now);
        disabled.Status = DomainBindingService.StatusDisabled;
        var domains = new List<Domain>
        {
            Domain("admin", "admin.example", now),
            Domain("web", "web.example", now),
            Domain("web", "alias.example", now),
            disabled
        };
        var app = new MinicloudApp
        {
            Domains = domains,
            Branches = [new MinicloudApp { BranchName = "feature", Domains = domains }]
        };

        var response = app.ToResponse();

        Assert.Equal(new[] { "https://admin.example", "https://alias.example", "https://web.example" }, response.Domains.Select(x => x.Url));
        Assert.Equal(new[] { "https://admin.example", "https://alias.example", "https://web.example" }, Assert.Single(response.Branches).Urls);
    }

    private static DeploymentService Service(string name) =>
        new()
        {
            Id = $"dsvc_{name}",
            Name = name,
            Image = $"ghcr.io/customer/app/{name}:sha",
            Port = 8080,
            Public = true,
            Path = "/",
            HealthPath = "/health"
        };

    private static Domain Domain(string serviceName, string hostname, DateTimeOffset now) =>
        new()
        {
            Id = $"dom_{serviceName}",
            OrganizationId = "org_test",
            AppId = "app_test",
            ServiceName = serviceName,
            Hostname = hostname,
            Kind = DomainBindingService.KindMinicloudSubdomain,
            DnsStatus = DomainBindingService.DnsVerified,
            ApplyStatus = DomainBindingService.ApplyApplied,
            SslStatus = DomainBindingService.SslReady,
            Status = DomainBindingService.StatusActive,
            CreatedAt = now,
            UpdatedAt = now
        };
}

public sealed class DeploymentLifecycleServiceTests
{
    [Fact]
    public void CreateQueued_CreatesDeploymentWithQueuedEvent()
    {
        var service = CreateService(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = "org_123"
        };
        var request = ValidDeploymentRequest();

        var deployment = service.CreateQueued(app, request);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Null(deployment.StartedAt);
        Assert.Null(deployment.CompletedAt);
        Assert.Equal("org_123", deployment.OrganizationId);
        Assert.Equal("app_123", deployment.AppId);
        Assert.Null(deployment.Services[0].EnvJson);
        Assert.Single(deployment.Events);
        Assert.Equal("Deployment queued.", deployment.Events[0].Message);
    }

    [Fact]
    public void CreateQueued_StoresServiceEnvironmentJson()
    {
        var service = CreateService(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
        var app = new MinicloudApp
        {
            Id = "app_123",
            OrganizationId = "org_123"
        };
        var request = new CreateDeploymentRequest(
            "app_123",
            "sqlite",
            "abc123",
            [
                new DeploymentServiceRequest(
                    "backend",
                    "ghcr.io/customer/app/backend:abc123",
                    8080,
                    true,
                    "/",
                    "/health",
                    new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" })
            ]);

        var deployment = service.CreateQueued(app, request);

        Assert.Contains("ASPNETCORE_ENVIRONMENT", deployment.Services[0].EnvJson, StringComparison.Ordinal);
        Assert.Contains("Production", deployment.Services[0].EnvJson, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTransition_AppliesAllowedTransitionAndSetsStartedAt()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(now);
        var deployment = QueuedDeployment();

        var result = service.TryTransition(deployment, DeploymentStatus.Dispatching, "Internal workflow dispatching.");

        Assert.True(result.Succeeded);
        Assert.Equal(DeploymentStatus.Dispatching, deployment.Status);
        Assert.Equal(now, deployment.StartedAt);
        Assert.Null(deployment.CompletedAt);
        Assert.Contains(deployment.Events, x => x.Message == "Internal workflow dispatching.");
    }

    [Fact]
    public void TryTransition_RejectsInvalidTransition()
    {
        var service = CreateService(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
        var deployment = QueuedDeployment();

        var result = service.TryTransition(deployment, DeploymentStatus.Succeeded, "Deployment succeeded.");

        Assert.False(result.Succeeded);
        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Null(deployment.StartedAt);
        Assert.Null(deployment.CompletedAt);
        Assert.Empty(deployment.Events);
    }

    [Fact]
    public void TryTransition_FailedTransitionSetsFailureAndCompletedAt()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(now);
        var deployment = QueuedDeployment();

        var result = service.TryTransition(
            deployment,
            DeploymentStatus.Failed,
            "Workflow dispatch failed.",
            "workflow_dispatch_failed",
            "GitHub rejected the workflow dispatch.");

        Assert.True(result.Succeeded);
        Assert.Equal(DeploymentStatus.Failed, deployment.Status);
        Assert.Equal(now, deployment.StartedAt);
        Assert.Equal(now, deployment.CompletedAt);
        Assert.Equal("workflow_dispatch_failed", deployment.FailureCode);
        Assert.Equal("GitHub rejected the workflow dispatch.", deployment.FailureMessage);
        Assert.Contains(deployment.Events, x => x.Level == EventLevel.Error && x.Message == "Workflow dispatch failed.");
    }

    [Fact]
    public void TryTransition_AppliesSuccessfulLifecycleAndProtectsTerminalStatus()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(now);
        var deployment = QueuedDeployment();

        Assert.True(service.TryTransition(deployment, DeploymentStatus.Dispatching, "Dispatching.").Succeeded);
        Assert.True(service.TryTransition(deployment, DeploymentStatus.Provisioning, "Provisioning.").Succeeded);
        Assert.True(service.TryTransition(deployment, DeploymentStatus.Deploying, "Deploying.").Succeeded);
        Assert.True(service.TryTransition(deployment, DeploymentStatus.Verifying, "Verifying.").Succeeded);
        Assert.True(service.TryTransition(deployment, DeploymentStatus.Succeeded, "Deployment succeeded.").Succeeded);

        var rejected = service.TryTransition(deployment, DeploymentStatus.Deploying, "Deploying again.");

        Assert.False(rejected.Succeeded);
        Assert.Equal(DeploymentStatus.Succeeded, deployment.Status);
        Assert.Equal(now, deployment.StartedAt);
        Assert.Equal(now, deployment.CompletedAt);
        Assert.DoesNotContain(deployment.Events, x => x.Message == "Deploying again.");
    }

    [Fact]
    public void ResetForManualDispatch_ClearsRunAndFailureState()
    {
        var now = new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(now);
        var deployment = QueuedDeployment();
        deployment.Status = DeploymentStatus.Failed;
        deployment.GithubRunId = "123";
        deployment.GithubRunUrl = "https://github.com/muniventures/minicloud/actions/runs/123";
        deployment.FailureCode = "workflow_failed";
        deployment.FailureMessage = "Workflow failed.";
        deployment.StartedAt = now.AddMinutes(-10);
        deployment.CompletedAt = now.AddMinutes(-1);

        service.ResetForManualDispatch(deployment);

        Assert.Equal(DeploymentStatus.Queued, deployment.Status);
        Assert.Null(deployment.GithubRunId);
        Assert.Null(deployment.GithubRunUrl);
        Assert.Null(deployment.FailureCode);
        Assert.Null(deployment.FailureMessage);
        Assert.Null(deployment.StartedAt);
        Assert.Null(deployment.CompletedAt);
        Assert.Contains(deployment.Events, x => x.Message == "Deployment manually queued.");
    }

    private static DeploymentLifecycleService CreateService(DateTimeOffset now) =>
        new(new TestIdGenerator(), new TestTimeProvider(now));

    private static Deployment QueuedDeployment() =>
        new()
        {
            Id = "dep_123",
            OrganizationId = "org_123",
            AppId = "app_123",
            Database = DatabaseMode.Sqlite,
            Status = DeploymentStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static CreateDeploymentRequest ValidDeploymentRequest() =>
        new(
            "app_123",
            "sqlite",
            "abc123",
            [
                new DeploymentServiceRequest("frontend", "ghcr.io/customer/app/frontend:abc123", 3000, true, "/", "/"),
                new DeploymentServiceRequest("backend", "ghcr.io/customer/app/backend:abc123", 8080, true, "/", "/health")
            ]);

    private sealed class TestIdGenerator : IIdGenerator
    {
        private int _nextId;

        public string NewId(string prefix) => $"{prefix}_{++_nextId}";
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
