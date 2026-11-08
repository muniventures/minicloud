using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Registry;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Tests;

public sealed class ImageBuildTargetTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private MinicloudDbContext _db = default!;
    private readonly MinicloudApp _app = new() { Id = "app_target", OrganizationId = "org_target", Slug = "target", Name = "Target" };
    private ImageBuildTargetService Targets(RegistryOptions? registry = null) => new(_db, Options.Create(registry ?? new RegistryOptions()), Options.Create(new ProviderPlanOptions()));

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.Organizations.Add(new Organization { Id = _app.OrganizationId, Slug = "target", Name = "Target" });
        _db.Apps.Add(_app);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task New_app_requires_explicit_provider_sku_platform_and_does_not_provision()
    {
        await Assert.ThrowsAsync<RegistryImageValidationException>(() => Targets().ResolveAsync(_app, default));
        var target = await Targets(new RegistryOptions { PlannedPlatforms = { ["Hetzner_cx23"] = "linux/amd64" } }).ResolveAsync(_app, default);
        Assert.Equal("linux/amd64", target.Platform);
        Assert.Equal(64, target.Version.Length);
        Assert.Empty(await _db.ServerInstances.ToListAsync());
    }

    [Fact]
    public async Task Existing_server_requires_observation_and_target_changes_with_provider_identity()
    {
        var server = Server();
        _db.ServerInstances.Add(server);
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<RegistryImageValidationException>(() => Targets().ResolveAsync(_app, default));
        server.BuildPlatformObservedAt = DateTimeOffset.UtcNow;
        server.BuildPlatform = "linux/arm64";
        await _db.SaveChangesAsync();
        var first = await Targets().ResolveAsync(_app, default);
        Assert.Equal("linux/arm64", first.Platform);
        server.ProviderServerId = "replacement";
        await _db.SaveChangesAsync();
        Assert.NotEqual(first.Version, (await Targets().ResolveAsync(_app, default)).Version);
    }

    [Fact]
    public async Task Foreign_server_registration_cannot_supply_a_build_platform()
    {
        _db.Organizations.Add(new Organization { Id = "org_foreign", Slug = "foreign", Name = "Foreign" });
        var server = Server();
        server.OrganizationId = "org_foreign";
        server.BuildPlatform = "linux/amd64";
        server.BuildPlatformObservedAt = DateTimeOffset.UtcNow;
        _db.ServerInstances.Add(server);
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<RegistryImageValidationException>(() => Targets().ResolveAsync(_app, default));
    }

    [Fact]
    public async Task Named_plan_build_target_uses_account_scope_and_platform_without_global_mapping()
    {
        var account = new ProviderAccount { Id = "account-a", OrganizationId = _app.OrganizationId,
            Name = "Hetzner secondary", ProviderType = Minicloud.Common.Providers.CloudProvider.Hetzner,
            CredentialPath = "synthetic/path", CredentialGeneration = "test" };
        var plan = new ProviderPlan { Id = "named-plan", OrganizationId = _app.OrganizationId,
            ProviderAccount = account, ProviderAccountId = account.Id, Name = "Meu plano", NormalizedName = "MEU PLANO",
            Region = "nbg1", Sku = "cax11", BuildPlatform = "linux/arm64" };
        _db.ProviderPlans.Add(plan);
        _app.PlanId = plan.Id;
        await _db.SaveChangesAsync();
        var first = await Targets().ResolveAsync(_app, default);
        Assert.Equal("linux/arm64", first.Platform);
        var deployment = new Deployment { Id = "target-reservation", AppId = _app.Id, OrganizationId = _app.OrganizationId };
        var catalog = new ProviderCatalogService(_db, new IdGenerator(), TimeProvider.System);
        await catalog.ReserveAsync(_app, deployment, default);
        plan.Name = "Renamed plan";
        plan.NormalizedName = "RENAMED PLAN";
        plan.Revision++;
        await _db.SaveChangesAsync();
        Assert.Equal(first.Version, (await Targets().ResolveAsync(_app, default)).Version);
        var secondAccount = new ProviderAccount { Id = "account-b", OrganizationId = _app.OrganizationId,
            Name = "Hetzner third", ProviderType = Minicloud.Common.Providers.CloudProvider.Hetzner,
            CredentialPath = "synthetic/other", CredentialGeneration = "test" };
        _db.ProviderAccounts.Add(secondAccount);
        plan.ProviderAccount = secondAccount;
        plan.ProviderAccountId = secondAccount.Id;
        await _db.SaveChangesAsync();
        Assert.NotEqual(first.Version, (await Targets().ResolveAsync(_app, default)).Version);
        Assert.Empty(await _db.ServerInstances.ToListAsync());
    }

    [Fact]
    public async Task Same_external_id_in_a_different_runtime_account_cannot_supply_build_identity()
    {
        var account = new ProviderAccount { Id = "target-account-a", OrganizationId = _app.OrganizationId,
            Name = "A", ProviderType = Minicloud.Common.Providers.CloudProvider.Hetzner,
            CredentialPath = "synthetic/path", CredentialGeneration = "test" };
        var other = new ProviderAccount { Id = "target-account-b", OrganizationId = _app.OrganizationId,
            Name = "B", ProviderType = Minicloud.Common.Providers.CloudProvider.Hetzner,
            CredentialPath = "synthetic/other", CredentialGeneration = "test" };
        _db.ProviderAccounts.AddRange(account, other);
        var server = Server();
        server.ProviderAccountId = account.Id;
        server.BuildPlatform = "linux/amd64";
        server.BuildPlatformObservedAt = DateTimeOffset.UtcNow;
        _db.ServerInstances.Add(server);
        _db.RuntimeServers.Add(new RuntimeServer { Id = "runtime-account-mismatch", OrganizationId = _app.OrganizationId,
            AppId = _app.Id, Provider = server.Provider, ProviderAccountId = other.Id, ProviderServerId = server.ProviderServerId });
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<RegistryImageValidationException>(() => Targets().ResolveAsync(_app, default));
    }

    private ServerInstance Server() => new()
    {
        Id = "server_target",
        AppId = _app.Id,
        OrganizationId = _app.OrganizationId,
        Provider = "Hetzner",
        ProviderServerId = "original",
        Region = "nbg1",
        Status = "running",
        BootstrapStatus = "ready"
    };

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
