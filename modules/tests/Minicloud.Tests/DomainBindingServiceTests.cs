using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;
using Microsoft.Extensions.Options;

namespace Minicloud.Tests;

public sealed class DomainBindingServiceTests : IAsyncLifetime
{
    private readonly SqliteTestHarness _harness = new();

    [Theory]
    [InlineData("app.muni.dev")]
    [InlineData("apps.example.test")]
    public async Task EnsureDefaultBindingsAsync_CreatesOneSubdomainPerPublicService(string suffix)
    {
        var now = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        await using var db = _harness.CreateDbContext();
        var organization = new Organization { Id = "org_domain", Name = "Domain", Slug = "domain", CreatedAt = now, UpdatedAt = now };
        var app = new MinicloudApp { Id = "app_domain", OrganizationId = organization.Id, Organization = organization, Name = "App", Slug = "app", Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(organization);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = new DomainBindingService(db, new TestIdGenerator(), new TestTimeProvider(now),
            Options.Create(new ManagedDomainOptions { Suffix = suffix }));

        var bindings = await service.EnsureDefaultBindingsAsync(organization, app, [
            new DeploymentServiceRequest("api", "ghcr.io/example/api", 8080, true, "/", "/health"),
            new DeploymentServiceRequest("worker", "ghcr.io/example/worker", 9000, false, "/", "/health")
        ], CancellationToken.None);
        await db.SaveChangesAsync();

        var binding = Assert.Single(bindings);
        Assert.Equal("api", binding.ServiceName);
        Assert.Equal($"domain-app-api.{suffix}", binding.Hostname);
        Assert.Equal(DomainBindingService.StatusPendingApply, binding.Status);
    }

    [Fact]
    public async Task Making_service_private_disables_all_its_domains_and_preserves_other_services()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _harness.CreateDbContext();
        var organization = new Organization { Id = "org_visibility", Name = "Visibility", Slug = "visibility", CreatedAt = now, UpdatedAt = now };
        var app = new MinicloudApp { Id = "app_visibility", OrganizationId = organization.Id, Organization = organization, Name = "App", Slug = "app", Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(organization);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = new DomainBindingService(db, new TestIdGenerator(), new TestTimeProvider(now));
        var worker = new DeploymentServiceRequest("worker", "ghcr.io/example/worker", 8080, true, "/", "/health");
        var api = new DeploymentServiceRequest("api", "ghcr.io/example/api", 8080, true, "/", "/health");
        var original = await service.EnsureDefaultBindingsAsync(organization, app, [worker, api], CancellationToken.None);
        await db.SaveChangesAsync();
        var extra = await service.CreateSubdomainBindingAsync(app, "worker", "visibility-extra", [worker], CancellationToken.None);
        await db.SaveChangesAsync();
        var result = await service.EnsureDefaultBindingsAsync(organization, app, [worker with { Public = false }], CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Empty(result);
        Assert.Equal(DomainBindingService.StatusDisabled, original.Single(x => x.ServiceName == "worker").Status);
        Assert.Equal(DomainBindingService.StatusDisabled, extra.Binding!.Status);
        Assert.Equal(DomainBindingService.ApplyPending, extra.Binding.ApplyStatus);
        Assert.NotEqual(DomainBindingService.StatusDisabled, original.Single(x => x.ServiceName == "api").Status);
    }

    [Fact]
    public async Task EnsureDefaultBindingsAsync_Appends_branch_to_main_service_subdomain()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        await using var db = _harness.CreateDbContext();
        var organization = new Organization { Id = "org_branch_domain", Name = "Domain", Slug = "acme", CreatedAt = now, UpdatedAt = now };
        var parent = new MinicloudApp { Id = "app_branch_domain_parent", OrganizationId = organization.Id, Organization = organization, Name = "Store", Slug = "store", Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now };
        var branch = new MinicloudApp { Id = "app_branch_domain_child", OrganizationId = organization.Id, Organization = organization, ParentAppId = parent.Id, ParentApp = parent, BranchName = "feature-cart", Name = "Store branch", Slug = "store-feature-cart", Plan = AppPlan.P0V0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(organization);
        db.Apps.AddRange(parent, branch);
        await db.SaveChangesAsync();
        var service = new DomainBindingService(db, new TestIdGenerator(), new TestTimeProvider(now));

        var bindings = await service.EnsureDefaultBindingsAsync(organization, branch, [
            new DeploymentServiceRequest("api", "ghcr.io/example/api", 8080, true, "/", "/health")
        ], CancellationToken.None);

        Assert.Equal("acme-store-api-feature-cart.app.muni.dev", Assert.Single(bindings).Hostname);
    }

    [Fact]
    public async Task PreviewRoutingRemainsStableAfterSourceEnvironmentIsDeleted()
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = _harness.CreateDbContext();
        var organization = new Organization { Id = "org_detached_preview", Name = "Detached", Slug = "detached" };
        var preview = new MinicloudApp { Id = "app_detached_preview", OrganizationId = organization.Id,
            Organization = organization, Name = "Preview", Slug = "store-preview", BranchName = "feature-cart",
            RuntimeParentSlug = "store", EnvironmentKind = EnvironmentKinds.Preview };
        db.Apps.Add(preview);
        await db.SaveChangesAsync();
        var service = new DomainBindingService(db, new TestIdGenerator(), new TestTimeProvider(now));
        var binding = Assert.Single(await service.EnsureDefaultBindingsAsync(organization, preview,
            [new DeploymentServiceRequest("new-api", "example/api", 8080, true, "/", "/health")], default));
        Assert.Equal("detached-store-new-api-feature-cart.app.muni.dev", binding.Hostname);
    }

    [Fact]
    public async Task CreateSubdomainBindingAsync_RejectsPrivateServices()
    {
        var now = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        await using var db = _harness.CreateDbContext();
        var organization = new Organization { Id = "org_private_domain", Name = "Private", Slug = "private", CreatedAt = now, UpdatedAt = now };
        var app = new MinicloudApp { Id = "app_private_domain", OrganizationId = organization.Id, Organization = organization, Name = "App", Slug = "app", Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(organization);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = new DomainBindingService(db, new TestIdGenerator(), new TestTimeProvider(now));

        var result = await service.CreateSubdomainBindingAsync(app, "worker", "private-app-worker", [
            new DeploymentServiceRequest("worker", "ghcr.io/example/worker", 9000, false, "/", "/health")
        ], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("serviceName", result.ErrorField);
    }

    [Fact]
    public async Task CreateSubdomainBindingAsync_AllowsMultipleBindingsForService()
    {
        var now = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        await using var db = _harness.CreateDbContext();
        var organization = new Organization { Id = "org_single_domain", Name = "Single", Slug = "single", CreatedAt = now, UpdatedAt = now };
        var app = new MinicloudApp { Id = "app_single_domain", OrganizationId = organization.Id, Organization = organization, Name = "App", Slug = "app", Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(organization);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        var service = new DomainBindingService(db, new TestIdGenerator(), new TestTimeProvider(now));
        var activeServices = new[]
        {
            new DeploymentServiceRequest("api", "ghcr.io/example/api", 8080, true, "/", "/health")
        };
        await service.CreateSubdomainBindingAsync(app, "api", "single-app-api", activeServices, CancellationToken.None);
        await db.SaveChangesAsync();

        var result = await service.CreateSubdomainBindingAsync(app, "api", "single-app-api-alt", activeServices, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("single-app-api-alt.app.muni.dev", result.Binding!.Hostname);
        Assert.Equal(2, db.Domains.Count(x => x.AppId == app.Id && x.ServiceName == "api"));
    }

    [Fact]
    public async Task CheckSubdomainAvailabilityAsync_ReturnsAvailability()
    {
        var now = new DateTimeOffset(2026, 5, 17, 12, 0, 0, TimeSpan.Zero);
        await using var db = _harness.CreateDbContext();
        var organization = new Organization { Id = "org_taken", Name = "Taken", Slug = "taken", CreatedAt = now, UpdatedAt = now };
        var app = new MinicloudApp { Id = "app_taken", OrganizationId = organization.Id, Organization = organization, Name = "App", Slug = "app", Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(organization);
        db.Apps.Add(app);
        db.Domains.Add(new Domain
        {
            Id = "dom_taken",
            OrganizationId = organization.Id,
            AppId = app.Id,
            ServiceName = "api",
            Hostname = "taken.app.muni.dev",
            Kind = DomainBindingService.KindMinicloudSubdomain,
            PathPrefix = "/",
            DnsStatus = DomainBindingService.DnsVerified,
            ApplyStatus = DomainBindingService.ApplyPending,
            SslStatus = DomainBindingService.SslPending,
            Status = DomainBindingService.StatusPendingApply,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var service = new DomainBindingService(db, new TestIdGenerator(), new TestTimeProvider(now));

        var available = await service.CheckSubdomainAvailabilityAsync("Fresh", CancellationToken.None);
        var taken = await service.CheckSubdomainAvailabilityAsync("taken", CancellationToken.None);
        var invalid = await service.CheckSubdomainAvailabilityAsync("-bad", CancellationToken.None);

        Assert.True(available.Available);
        Assert.Equal("fresh", available.Label);
        Assert.Equal("fresh.app.muni.dev", available.Hostname);
        Assert.False(taken.Available);
        Assert.True(taken.IsValid);
        Assert.False(invalid.Available);
        Assert.False(invalid.IsValid);
    }

    public Task InitializeAsync() => _harness.InitializeAsync();

    public Task DisposeAsync() => _harness.DisposeAsync();

    private sealed class TestIdGenerator : IIdGenerator
    {
        private int _next;

        public string NewId(string prefix) => $"{prefix}_{++_next}";
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
