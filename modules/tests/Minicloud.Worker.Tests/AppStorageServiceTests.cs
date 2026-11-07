using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Providers.Common;
using System.Net;
using Minicloud.Common.Contracts;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Tests;

public sealed class AppStorageServiceTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;
    public AppStorageServiceTests(WorkerSqliteTestHarness harness) => _harness = harness;

    [Fact]
    public async Task CreatesOnceReusesAcrossContextsAndGrowsWithoutChangingIdentity()
    {
        var provider = new FakeProvider();
        var app = "app_" + Guid.NewGuid().ToString("N");
        await using (var db = _harness.CreateDbContext())
        {
            var mount = await new AppStorageService(db, provider).EnsureAsync(app, "uploads", new(20, "/app/data", 1000, 1000), "123", "nbg1", default);
            Assert.Equal(provider.Volume!.Id, mount.VolumeId);
        }
        await using (var db = _harness.CreateDbContext())
        {
            var service = new AppStorageService(db, provider);
            await service.EnsureAsync(app, "uploads", new(20, "/worker/data", 1000, 1000), "123", "nbg1", default);
            await service.EnsureAsync(app, "uploads", new(30, "/worker/data", 1000, 1000), "123", "nbg1", default);
            // Old configurations can still mount the enlarged disk during recovery.
            await service.EnsureAsync(app, "uploads", new(20, "/app/data", 1000, 1000), "123", "nbg1", default);
            Assert.Equal(30, (await db.AppStorageVolumes.SingleAsync(x => x.AppId == app)).SizeGb);
        }
        Assert.Equal(1, provider.Creates);
        Assert.Equal(1, provider.Attaches);
        Assert.Equal(1, provider.Resizes);
    }

    [Fact]
    public async Task MissingKnownVolumeNeverCreatesEmptyReplacement()
    {
        var provider = new FakeProvider();
        var app = "app_" + Guid.NewGuid().ToString("N");
        await using var db = _harness.CreateDbContext();
        var service = new AppStorageService(db, provider);
        await service.EnsureAsync(app, "uploads", new(20, "/app/data"), "123", "nbg1", default);
        provider.Volume = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(app, "uploads", new(20, "/app/data"), "123", "nbg1", default));
        Assert.Equal(1, provider.Creates);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguousCreateRecoversByIdentityAndDoesNotRepeatPost(bool providerCreated)
    {
        var provider = new FakeProvider { FailCreate = true, CreateBeforeFailure = providerCreated };
        var app = "app_" + Guid.NewGuid().ToString("N");
        await using var db = _harness.CreateDbContext();
        var service = new AppStorageService(db, provider);
        await Assert.ThrowsAsync<HttpRequestException>(() => service.EnsureAsync(app, "uploads", new(20, "/data/files"), "123", "nbg1", default));
        if (providerCreated) { await service.EnsureAsync(app, "uploads", new(20, "/data/files"), "123", "nbg1", default); }
        else { await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(app, "uploads", new(20, "/data/files"), "123", "nbg1", default)); }
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public async Task RejectsForeignServerLabelsRegionAndOwnership()
    {
        var provider = new FakeProvider();
        var app = "app_" + Guid.NewGuid().ToString("N");
        await using var db = _harness.CreateDbContext();
        var service = new AppStorageService(db, provider);
        await service.EnsureAsync(app, "uploads", new(20, "/app/data"), "123", "nbg1", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(app, "uploads", new(20, "/app/data"), "456", "nbg1", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(app, "uploads", new(20, "/app/data"), "123", "fsn1", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(app, "uploads", new(20, "/app/data", 1000), "123", "nbg1", default));
        provider.Volume = provider.Volume! with { Labels = new Dictionary<string, string>() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureAsync(app, "uploads", new(20, "/app/data"), "123", "nbg1", default));
        Assert.Equal(1, provider.Creates);
    }

    [Fact]
    public async Task DefiniteCreateRejectionCanBeRetriedAfterQuotaOrPermissionsAreFixed()
    {
        var provider = new FakeProvider { RejectCreate = true };
        var app = "app_" + Guid.NewGuid().ToString("N");
        await using var db = _harness.CreateDbContext();
        var service = new AppStorageService(db, provider);
        await Assert.ThrowsAsync<ProviderApiException>(() => service.EnsureAsync(app, "uploads", new(20, "/app/data"), "123", "nbg1", default));
        provider.RejectCreate = false;
        await service.EnsureAsync(app, "uploads", new(20, "/app/data"), "123", "nbg1", default);
        Assert.Equal(2, provider.Creates);
    }

    [Fact]
    public void BranchAppsUseDifferentDiskIdentities() => Assert.NotEqual(AppStorageService.VolumeName("app_main", "uploads"), AppStorageService.VolumeName("app_branch", "uploads"));

    private sealed class FakeProvider : IStorageVolumeProvider
    {
        private static long _nextId = 10000;
        private readonly string _id = Interlocked.Increment(ref _nextId).ToString();
        public ProviderStorageVolume? Volume;
        public int Creates, Attaches, Resizes;
        public bool FailCreate, CreateBeforeFailure, RejectCreate;
        public Task<ProviderStorageVolume?> FindVolumeAsync(string name, CancellationToken ct) => Task.FromResult(Volume);
        public Task<ProviderStorageVolume?> GetVolumeAsync(string id, CancellationToken ct) => Task.FromResult(Volume);
        public Task<ProviderStorageVolume> CreateVolumeAsync(string name, int sizeGb, string region, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
        {
            Creates++;
            if (RejectCreate) { throw new ProviderApiException("quota exceeded", HttpStatusCode.BadRequest); }
            if (!FailCreate || CreateBeforeFailure) { Volume = new(_id, name, sizeGb, region, null, labels); }
            if (FailCreate) { throw new HttpRequestException("connection lost"); }
            return Task.FromResult(Volume!);
        }
        public Task AttachVolumeAsync(string id, string serverId, CancellationToken ct) { Attaches++; Volume = Volume! with { ServerId = serverId }; return Task.CompletedTask; }
        public Task ResizeVolumeAsync(string id, int sizeGb, CancellationToken ct) { Resizes++; Volume = Volume! with { SizeGb = sizeGb }; return Task.CompletedTask; }
    }
}
