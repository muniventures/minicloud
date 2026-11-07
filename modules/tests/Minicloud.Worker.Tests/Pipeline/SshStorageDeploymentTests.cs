using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Worker.Ssh;
using Minicloud.Worker.Storage;
using Minicloud.Worker.Tests.Providers;
using static Minicloud.Common.Providers.ProviderServiceCollectionExtensions;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class SshStorageDeploymentTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;
    public SshStorageDeploymentTests(WorkerSqliteTestHarness harness) => _harness = harness;

    [Fact]
    public async Task WorkerProvisionsSharedVolumeOnceAndTransfersResolvedMountsAndHelper()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("storage-ssh");
        var now = DateTimeOffset.UtcNow;
        const string deploymentId = "dep_storage_ssh", serverId = "srv_storage_ssh";
        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance { Id = serverId, AppId = app.Id, OrganizationId = org.Id, Provider = "Hetzner", ProviderServerId = "123", Region = "nbg1", Status = "running", BootstrapStatus = "ready", CreatedAt = now, UpdatedAt = now });
            db.Deployments.Add(new Deployment { Id = deploymentId, AppId = app.Id, OrganizationId = org.Id,
                Database = DatabaseMode.None, Status = DeploymentStatus.Deploying, OwnershipToken = "owner", CreatedAt = now, UpdatedAt = now,
                Services = new[] { "web", "worker" }.Select(name => new DeploymentService { Id = "svc_storage_" + name,
                    Name = name, Image = "example/" + name + ":1", Port = 8080, Public = name == "web", Path = "/", HealthPath = "/health",
                    StorageJson = JsonSerializer.Serialize(new Dictionary<string, StorageMountRequest> { ["uploads"] = new(20, "/app/" + name, 1000, 1000) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }).ToList() });
            await db.SaveChangesAsync();
        }
        var name = AppStorageService.VolumeName(app.Id, "uploads");
        var labels = new Dictionary<string, string> { ["managed-by"] = "minicloud", ["app-id"] = app.Id, ["storage-name"] = "uploads", ["purpose"] = "customer-storage" };
        string Volume(long? server) => JsonSerializer.Serialize(new { id = 900123, name, size = 20, location = new { name = "nbg1" }, server, labels });
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"volumes\":[]}");
        handler.QueueJsonResponse("{\"volume\":" + Volume(null) + ",\"action\":null}");
        handler.QueueJsonResponse("""{"action":{"id":1,"status":"success"}}""");
        for (var i = 0; i < 3; i++) { handler.QueueJsonResponse("{\"volume\":" + Volume(123) + "}"); }
        var provider = new HetznerClient(new HttpClient(handler), new HetznerOptions());
        var ssh = new CaptureSsh();
        var step = new SshDeployStep(_harness.CreateScopeFactory(services => services.AddSingleton<VpsProviderClientResolver>(sp => requested => provider)),
            new NoArtifacts(), ssh, new SshDeploymentOptions { PrivateKeyContent = "fixture-key" }, new DeploymentPipelineOptions(), new Environment(), TimeProvider.System);
        await step.ExecuteAsync(new PipelineStepContext { DeploymentId = deploymentId, OwnershipToken = "owner", AppId = app.Id,
            OrganizationId = org.Id, AppSlug = app.Slug, OrganizationSlug = org.Slug, ServerInstanceId = serverId, ServerIp = "192.0.2.1", Provider = CloudProvider.Hetzner }, default);
        Assert.NotNull(ssh.Request);
        Assert.Contains(ssh.Request!.Artifacts, x => x.RemoteFileName == "storage-volume.py");
        using var payload = JsonDocument.Parse(ssh.Request.PayloadJson);
        foreach (var service in payload.RootElement.GetProperty("services").EnumerateArray())
        {
            var mount = Assert.Single(service.GetProperty("storage_mounts").EnumerateArray());
            Assert.Equal("900123", mount.GetProperty("volume_id").GetString());
            Assert.Equal("/app/" + service.GetProperty("name").GetString(), mount.GetProperty("mount_path").GetString());
        }
        Assert.Single(handler.RecordedRequests, x => x.Method == HttpMethod.Post && x.Uri.EndsWith("/volumes"));
    }

    private sealed class CaptureSsh : ISshDeploymentClient
    {
        public SshDeploymentRequest? Request;
        public Task<SshDeploymentResult> ExecuteDeploymentAsync(SshDeploymentRequest request, Func<string, string, Task> onLogLine, CancellationToken cancellationToken)
        { Request = request; return Task.FromResult(new SshDeploymentResult(0, true, "fixture", [])); }
        public Task<int> ExecuteCommandAsync(string host, string username, string privateKeyContent, string command, Func<string, string, Task> onLogLine, CancellationToken cancellationToken) => Task.FromResult(0);
    }
    private sealed class NoArtifacts : IArtifactRetriever
    { public Task<Stream> OpenArtifactStreamAsync(string artifactId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No source artifact expected"); }
    private sealed class Environment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
