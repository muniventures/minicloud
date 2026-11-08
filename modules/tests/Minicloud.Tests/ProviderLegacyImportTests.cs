using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Services;
using Minicloud.Common.Validation;

namespace Minicloud.Tests;

public sealed class ProviderLegacyImportTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private MinicloudDbContext _db = default!;
    private readonly ReadOnlyProvider _provider = new();
    private readonly Organization _org = new() { Id = "legacy-org", Name = "Legacy", Slug = "legacy" };
    private readonly ProviderAccount _account = new() { Id = "legacy-account", OrganizationId = "legacy-org",
        Name = "Original Hetzner", ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/path", CredentialGeneration = "test" };

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.Organizations.Add(_org);
        _db.ProviderAccounts.Add(_account);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task ImportsVerifiedResourcesWithoutAllocationAndIsIdempotent()
    {
        var app = await SeedAppAsync("first");
        var volume = Volume(app.Id, "uploads", "volume-123");
        _db.AppStorageVolumes.Add(volume);
        _provider.Volumes[volume.ProviderVolumeId!] = Remote(volume, "server-first");
        _db.RuntimeServers.Add(new RuntimeServer { Id = "runtime-first", AppId = app.Id, OrganizationId = _org.Id,
            Provider = "hetzner", ProviderServerId = "server-first" });
        await _db.SaveChangesAsync();
        var request = Request([new(app.Id, BuildPlatform: "linux/amd64")]);
        var result = await Service().ImportAsync(request, default);
        Assert.Equal(new ProviderLegacyImportResult(1, 1, 1, 1, 1), result);
        var savedApp = await _db.Apps.SingleAsync(x => x.Id == app.Id);
        Assert.NotNull(savedApp.PlanId);
        Assert.Equal(_account.Id, (await _db.ServerInstances.SingleAsync()).ProviderAccountId);
        Assert.Null((await _db.ServerInstances.SingleAsync()).Sku);
        Assert.Null((await _db.ServerInstances.SingleAsync()).Image);
        Assert.Equal(_account.Id, (await _db.RuntimeServers.SingleAsync()).ProviderAccountId);
        var retained = await _db.AppStorageVolumes.SingleAsync();
        Assert.Equal(_org.Id, retained.OrganizationId);
        Assert.Equal(_account.Id, retained.ProviderAccountId);
        var plan = await _db.ProviderPlans.SingleAsync();
        Assert.Equal(ControlPlaneValidation.ToWireValue(AppPlan.P0H0), plan.Name);
        Assert.NotNull(plan.ProvisioningLockedAt);
        Assert.Equal(new ProviderLegacyImportResult(0, 0, 0, 0, 0), await Service().ImportAsync(request, default));
        Assert.Single(await _db.ProviderPlans.ToListAsync());
        Assert.Equal(0, _provider.Mutations);
    }

    [Fact]
    public async Task WrongCredentialScopeCannotBindAnExistingServer()
    {
        var app = await SeedAppAsync("missing");
        _provider.Servers.Clear();
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => Service().ImportAsync(
            Request([new(app.Id, BuildPlatform: "linux/amd64")]), default));
        Assert.Equal("legacy_import_resource_unverified", error.Code);
        Assert.Null((await _db.Apps.SingleAsync()).PlanId);
        Assert.Null((await _db.ServerInstances.SingleAsync()).ProviderAccountId);
        Assert.Empty(await _db.ProviderPlans.ToListAsync());
        Assert.Equal(0, _provider.Mutations);
    }

    [Fact]
    public async Task AFailureLaterInManifestRollsBackEarlierBindingsAndPlans()
    {
        var first = await SeedAppAsync("first");
        var second = await SeedAppAsync("second");
        _provider.Servers.Remove(Name(second));
        await Assert.ThrowsAsync<ProviderCatalogException>(() => Service().ImportAsync(
            Request([new(first.Id, "primary", "linux/amd64"), new(second.Id, "secondary", "linux/amd64")]), default));
        Assert.All(await _db.Apps.ToListAsync(), x => Assert.Null(x.PlanId));
        Assert.All(await _db.ServerInstances.ToListAsync(), x => Assert.Null(x.ProviderAccountId));
        Assert.Empty(await _db.ProviderPlans.ToListAsync());
        Assert.Empty(await _db.ProviderPlanNames.ToListAsync());
    }

    [Fact]
    public async Task RetainedOrphanVolumeRequiresExplicitIdAndRemoteOwnershipEvidence()
    {
        var volume = Volume("deleted-app", "uploads", "retained-id");
        _db.AppStorageVolumes.Add(volume);
        await _db.SaveChangesAsync();
        _provider.Volumes[volume.ProviderVolumeId!] = Remote(volume, null);
        var result = await Service().ImportAsync(Request([], [volume.Id]), default);
        Assert.Equal(1, result.BoundVolumes);
        Assert.Empty(await _db.Apps.ToListAsync());
        Assert.Equal(_account.Id, (await _db.AppStorageVolumes.SingleAsync()).ProviderAccountId);
        Assert.Equal(0, _provider.Mutations);
    }

    [Fact]
    public async Task CurrentPlanRegionMismatchIsReportedWithoutChangingOriginalIdentity()
    {
        var app = await SeedAppAsync("region");
        (await _db.ServerInstances.SingleAsync()).Region = "fsn1";
        await _db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => Service().ImportAsync(
            Request([new(app.Id, BuildPlatform: "linux/amd64")]), default));
        Assert.Equal("legacy_import_mapping_conflict", error.Code);
        Assert.Null((await _db.ServerInstances.SingleAsync()).ProviderAccountId);
        Assert.Empty(await _db.ProviderPlans.ToListAsync());
    }

    [Fact]
    public async Task UnknownArchitectureRequiresManifestRatherThanGuessing()
    {
        var app = await SeedAppAsync("architecture");
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => Service().ImportAsync(Request([new(app.Id)]), default));
        Assert.Equal("legacy_import_platform_unknown", error.Code);
        Assert.Empty(await _db.ProviderPlans.ToListAsync());
    }

    [Fact]
    public async Task ActiveDeploymentBlocksImportBeforeResourceDiscovery()
    {
        var app = await SeedAppAsync("active");
        _db.Deployments.Add(new Deployment { Id = "active-deployment", AppId = app.Id, OrganizationId = _org.Id,
            Status = DeploymentStatus.Queued });
        await _db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => Service().ImportAsync(
            Request([new(app.Id, BuildPlatform: "linux/amd64")]), default));
        Assert.Equal("legacy_import_active_work", error.Code);
        Assert.Equal(0, _provider.ServerReads);
    }

    [Fact]
    public async Task RuntimeWithoutRegisteredRegionCannotImportGuessedPlacement()
    {
        var app = new MinicloudApp { Id = "runtime-only", OrganizationId = _org.Id, Name = "Runtime", Slug = "runtime", Plan = AppPlan.P0H0 };
        _db.Apps.Add(app);
        _db.RuntimeServers.Add(new RuntimeServer { Id = "runtime-only-registration", AppId = app.Id, OrganizationId = _org.Id,
            Provider = "hetzner", ProviderServerId = "runtime-provider-id" });
        await _db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => Service().ImportAsync(Request([new(app.Id, BuildPlatform: "linux/amd64")]), default));
        Assert.Equal("legacy_import_resource_unverified", error.Code);
        Assert.Null((await _db.RuntimeServers.SingleAsync()).ProviderAccountId);
        Assert.Empty(await _db.ProviderPlans.ToListAsync());
    }

    private ProviderLegacyImportService Service() => new(_db, new ProviderCatalogService(_db, new IdGenerator(), TimeProvider.System),
        new Factory(_provider), Options.Create(new ProviderPlanOptions()), TimeProvider.System);

    private ProviderLegacyImportRequest Request(IReadOnlyList<ProviderLegacyAppBinding> apps, IReadOnlyList<string>? volumes = null) =>
        new(_org.Id, _account.Id, apps, volumes ?? []);

    private async Task<MinicloudApp> SeedAppAsync(string id)
    {
        var app = new MinicloudApp { Id = id, OrganizationId = _org.Id, Name = id, Slug = id, Plan = AppPlan.P0H0 };
        _db.Apps.Add(app);
        _db.ServerInstances.Add(new ServerInstance { Id = "registered-" + id, AppId = id, OrganizationId = _org.Id,
            Provider = "hetzner", ProviderServerId = "server-" + id, Region = "nbg1", Status = "ready", BootstrapStatus = "ready" });
        _provider.Servers[Name(app)] = new ProviderServerInfo("server-" + id, Name(app), "192.0.2.40", "running",
            Labels: new Dictionary<string, string> { ["managed-by"] = "minicloud", ["customer"] = _org.Slug,
                ["app"] = app.Slug, ["env"] = "production" });
        await _db.SaveChangesAsync();
        return app;
    }

    private static string Name(MinicloudApp app) => $"minicloud-legacy-production-{app.Slug}";
    private static AppStorageVolume Volume(string appId, string name, string providerId) => new()
    {
        Id = AppStorageService.VolumeName(appId, name), AppId = appId, Name = name, Region = "nbg1",
        ProviderVolumeId = providerId, SizeGb = 20
    };
    private static ProviderStorageVolume Remote(AppStorageVolume volume, string? server) => new(volume.ProviderVolumeId!, volume.Id,
        volume.SizeGb, volume.Region, server, new Dictionary<string, string> { ["managed-by"] = "minicloud",
            ["app-id"] = volume.AppId, ["storage-name"] = volume.Name, ["purpose"] = "customer-storage" });

    private sealed class Factory(IVpsProviderClient client) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!)
    {
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true) => Task.FromResult(client);
    }

    private sealed class ReadOnlyProvider : IVpsProviderClient, IStorageVolumeProvider
    {
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Dictionary<string, ProviderServerInfo> Servers { get; } = [];
        public Dictionary<string, ProviderStorageVolume> Volumes { get; } = [];
        public int Mutations;
        public int ServerReads;
        public Task<ProviderServerInfo?> FindServerAsync(string serverName, CancellationToken ct = default)
        { ServerReads++; return Task.FromResult(Servers.GetValueOrDefault(serverName)); }
        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct) => Task.FromResult(Volumes.GetValueOrDefault(id));
        public Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct) => Task.FromResult(Volumes.Values.SingleOrDefault(x => x.Name == name));
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken ct = default) { Mutations++; throw new NotSupportedException(); }
        public Task DeleteServerAsync(string serverId, CancellationToken ct = default) { Mutations++; throw new NotSupportedException(); }
        public Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct) { Mutations++; throw new NotSupportedException(); }
        public Task AttachVolumeAsync(string id, string serverId, CancellationToken ct) { Mutations++; throw new NotSupportedException(); }
        public Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct) { Mutations++; throw new NotSupportedException(); }
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
