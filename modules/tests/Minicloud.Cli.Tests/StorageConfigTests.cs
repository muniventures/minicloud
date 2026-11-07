using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;
using Minicloud.Common.Validation;

namespace Minicloud.Tests;

public sealed class StorageConfigTests
{
    [Fact]
    public void StorageRoundTripsAndChangesDeploymentChecksum()
    {
        var service = new MinicloudServiceConfig(null, null, "example:1", 8080, true, "/", "/health",
            Storage: new Dictionary<string, StorageMountRequest> { ["uploads"] = new(20, "/app/uploads", 1000, 1000) });
        var config = new MinicloudConfig("demo", "none", null, new Dictionary<string, MinicloudServiceConfig> { ["web"] = service }) { AppId = "app_demo" };
        var parsed = MinicloudConfigLoader.Parse(MinicloudConfigWriter.Write(config).Split('\n'));
        Assert.True(parsed.IsValid, string.Join(';', parsed.Diagnostics));
        Assert.Equal(service.Storage!["uploads"], parsed.Config!.Services["web"].Storage!["uploads"]);
        var initial = ServiceChecksumCalculator.Compute("web", service);
        foreach (var mount in new[] { new StorageMountRequest(30, "/app/uploads", 1000, 1000), new(20, "/app/files", 1000, 1000), new(20, "/app/uploads", 1001, 1000) })
        { Assert.NotEqual(initial, ServiceChecksumCalculator.Compute("web", service with { Storage = new Dictionary<string, StorageMountRequest> { ["uploads"] = mount } })); }
        Assert.NotEqual(initial, ServiceChecksumCalculator.Compute("web", service with { Storage = null }));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app/../data")]
    [InlineData("/proc/data")]
    [InlineData("/etc")]
    [InlineData("/var/run/docker.sock")]
    [InlineData("relative")]
    [InlineData("/app/data/")]
    [InlineData("/app/$HOME")]
    public void RejectsUnsafeMountPaths(string path) => Assert.False(StorageValidation.IsMountPath(path));

    [Fact]
    public void DataMountIsSupportedButCannotShadowBuiltInSqlite()
    {
        var volumes = new Dictionary<string, StorageMountRequest> { ["data"] = new(20, "/data") };
        Assert.True(StorageValidation.Validate([("web", volumes, Array.Empty<string>())]).IsValid);
        Assert.False(StorageValidation.Validate([("web", volumes, Array.Empty<string>())], sqlite: true).IsValid);
    }

    [Fact]
    public void RejectsConflictingSharedVolumesAndMountOverlap()
    {
        var a = new Dictionary<string, StorageMountRequest> { ["uploads"] = new(20, "/app/data") };
        var b = new Dictionary<string, StorageMountRequest> { ["uploads"] = new(30, "/app/files") };
        Assert.False(StorageValidation.Validate([("web", a, Array.Empty<string>()), ("worker", b, Array.Empty<string>())]).IsValid);
        Assert.False(StorageValidation.Validate([("web", a, new[] { "/app/data/assets" })]).IsValid);
        Assert.True(StorageValidation.Validate([("web", a, Array.Empty<string>()), ("worker", a, Array.Empty<string>())]).IsValid);
    }

    [Theory]
    [InlineData("sizeGb: wrong")]
    [InlineData("sizeGb: 9")]
    [InlineData("sizeGb: 10001")]
    [InlineData("sizeGb: 20\n        uid: -1")]
    [InlineData("sizeGb: 20\n        size: 30")]
    public void RejectsInvalidStorageConfiguration(string fields)
    {
        var yaml = "app: demo\nappId: app_demo\nservices:\n  web:\n    image: example:1\n    port: 8080\n    public: true\n    path: /\n    healthPath: /health\n    storage:\n      uploads:\n        mountPath: /app/data\n        " + fields;
        Assert.False(MinicloudConfigLoader.Parse(yaml.Split('\n')).IsValid);
    }
}
