using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DeploymentArtifactStorageServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"minicloud-artifacts-{Guid.NewGuid():N}");

    [Fact]
    public async Task StoreAsync_WritesReadyArtifactWithSha256AndSize()
    {
        var now = new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(now);
        var payload = Encoding.UTF8.GetBytes("artifact payload");
        var artifact = CreateArtifact(service, payload);

        var result = await service.StoreAsync(artifact, new MemoryStream(payload), payload.Length, Sha256(payload), CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Equal(payload.Length, result.SizeBytes);
        Assert.Equal(Sha256(payload), result.Sha256);
        Assert.Equal(now, result.UploadedAt);
        Assert.True(File.Exists(service.AbsolutePath(artifact)));
    }

    [Fact]
    public async Task StoreAsync_RejectsSecondWriteToExistingArtifact()
    {
        var service = CreateService(new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetBytes("artifact payload");
        var artifact = CreateArtifact(service, payload);
        await service.StoreAsync(artifact, new MemoryStream(payload), payload.Length, Sha256(payload), CancellationToken.None);

        var result = await service.StoreAsync(artifact, new MemoryStream(payload), payload.Length, Sha256(payload), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("artifact_already_exists", result.FailureCode);
    }

    [Fact]
    public async Task StoreAsync_RejectsIntegrityMismatch()
    {
        var service = CreateService(new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero));
        var payload = Encoding.UTF8.GetBytes("artifact payload");
        var artifact = CreateArtifact(service, payload);

        var result = await service.StoreAsync(artifact, new MemoryStream(payload), payload.Length + 1, Sha256(payload), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("artifact_integrity_mismatch", result.FailureCode);
        Assert.False(File.Exists(service.AbsolutePath(artifact)));
    }

    [Fact]
    public async Task StoreAsync_RejectsOverLimitPayload()
    {
        var service = CreateService(
            new DateTimeOffset(2026, 6, 7, 12, 0, 0, TimeSpan.Zero),
            maxArtifactBytes: 4);
        var payload = Encoding.UTF8.GetBytes("too large");
        var artifact = CreateArtifact(service, payload);

        var result = await service.StoreAsync(artifact, new MemoryStream(payload), payload.Length, Sha256(payload), CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Equal("artifact_size_invalid", result.FailureCode);
    }

    [Fact]
    public void Options_DefaultRetentionIsOneDay()
    {
        var options = new DeploymentArtifactStorageOptions();

        Assert.Equal(TimeSpan.FromDays(1), options.Retention);
        Assert.Equal(250L * 1024L * 1024L, options.MaxArtifactBytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private DeploymentArtifactStorageService CreateService(DateTimeOffset now, long maxArtifactBytes = 1024) =>
        new(
            Options.Create(new DeploymentArtifactStorageOptions
            {
                RootPath = _root,
                MaxArtifactBytes = maxArtifactBytes,
                Retention = TimeSpan.FromDays(1)
            }),
            new TestTimeProvider(now));

    private static DeploymentArtifact CreateArtifact(DeploymentArtifactStorageService service, byte[] payload)
    {
        var sha = Sha256(payload);
        var artifact = new DeploymentArtifact
        {
            Id = "art_test",
            OrganizationId = "org_test",
            AppId = "app_test",
            ServiceName = "web",
            FileName = "service.zip",
            ContentType = "application/zip",
            ExpectedSizeBytes = payload.Length,
            ExpectedSha256 = sha,
            ManifestJson = "{}",
            Status = DeploymentArtifactStatus.Created,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };
        artifact.StoragePath = service.CreateStoragePath(artifact.OrganizationId, artifact.AppId, artifact.Id);
        return artifact;
    }

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
