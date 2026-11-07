using System.Net;
using System.Text.Json;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;

namespace Minicloud.Worker.Tests.Providers;

public sealed class HetznerStorageTests
{
    private const string Volume = """{"id":901,"name":"mc-test","size":20,"location":{"name":"nbg1"},"server":null,"labels":{"app-id":"app_test"}}""";
    private static HetznerClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), new HetznerOptions { ApiToken = "secret", BaseUrl = "https://api.hetzner.cloud/v1/" });

    [Fact]
    public async Task CreatesFormattedDiskWithoutAutomountAndWaitsForNextActions()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"volume\":" + Volume + ",\"action\":null,\"next_actions\":[{\"id\":2,\"status\":\"success\"}]}");
        var volume = await Client(handler).CreateVolumeAsync("mc-test", 20, "nbg1", new Dictionary<string, string> { ["app-id"] = "app_test" }, default);
        Assert.Equal("901", volume.Id);
        using var body = JsonDocument.Parse(Assert.Single(handler.RecordedRequests).Body!);
        Assert.Equal("ext4", body.RootElement.GetProperty("format").GetString());
        Assert.False(body.RootElement.GetProperty("automount").GetBoolean());
        Assert.Equal("nbg1", body.RootElement.GetProperty("location").GetString());
        Assert.Equal(20, body.RootElement.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task AttachAndResizeUseProviderActions()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"action":{"id":1,"status":"success"}}""");
        handler.QueueJsonResponse("""{"action":{"id":2,"status":"success"}}""");
        var client = Client(handler);
        await client.AttachVolumeAsync("901", "123", default);
        await client.ResizeVolumeAsync("901", 30, default);
        Assert.EndsWith("volumes/901/actions/attach", handler.RecordedRequests[0].Uri);
        Assert.Contains("\"server\":123", handler.RecordedRequests[0].Body);
        Assert.Contains("\"automount\":false", handler.RecordedRequests[0].Body);
        Assert.EndsWith("volumes/901/actions/resize", handler.RecordedRequests[1].Uri);
        Assert.Contains("\"size\":30", handler.RecordedRequests[1].Body);
    }

    [Fact]
    public async Task CreateFailureIsNotBlindlyRetriedBySharedRetryHandler()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"error":{"message":"temporary"}}""", HttpStatusCode.ServiceUnavailable);
        var retry = new ResilientHttpRetryHandler(handler, baseDelay: TimeSpan.Zero);
        await Assert.ThrowsAsync<ProviderApiException>(() => Client(retry).CreateVolumeAsync("mc-test", 20, "nbg1", new Dictionary<string, string>(), default));
        Assert.Single(handler.RecordedRequests);
    }

    [Fact]
    public async Task MissingVolumeReturnsNullAndFailedActionFailsDeployment()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        handler.QueueJsonResponse("""{"action":{"id":1,"status":"error","error":{"message":"cannot attach"}}}""");
        var client = Client(handler);
        Assert.Null(await client.GetVolumeAsync("901", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.AttachVolumeAsync("901", "123", default));
    }
}
