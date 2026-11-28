using System.Net;
using System.Text.Json;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.DigitalOcean;
using Selfrun.Common.Providers.Hetzner;
using Selfrun.Common.Providers.Models;
using Selfrun.Common.Providers.Vultr;

namespace Selfrun.Worker.Tests.Providers;

public class SnapshotProviderTests
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["managed-by"] = "selfrun",
        ["environment"] = "production",
        ["operation"] = "a=1,2"
    };

    private static readonly Dictionary<string, string> Managed = new() { ["managed-by"] = "selfrun" };
    private const string CreatedAt = "2026-01-02T03:04:05Z";
    private readonly TestHttpMessageHandler _handler = new();

    [Fact]
    public async Task HetznerCreatePollsActionAndReadsAvailableImage()
    {
        _handler.QueueJsonResponse("""{"image":{"id":21},"action":{"id":7,"status":"running"}}""", HttpStatusCode.Created);
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"success"}}""");
        _handler.QueueJsonResponse($$"""{"image":{{HetznerImage(21, "selfrun")}}}""");

        var nativeLabels = new Dictionary<string, string>(Labels) { ["operation"] = "op-412" };
        var image = await CreateClient(CloudProvider.Hetzner).CreateSnapshotAsync("14", "app production", nativeLabels, default);

        Assert.Equal("21", image.Id);
        Assert.True(image.IsAvailable);
        Assert.Equal(2.5m, image.SizeGb);
        Assert.Equal(DateTimeOffset.Parse(CreatedAt), image.CreatedAt);
        Assert.Equal("selfrun", image.Labels["managed-by"]);
        AssertRequests(
            ("POST", "/v1/servers/14/actions/create_image"),
            ("GET", "/v1/servers/actions/7"),
            ("GET", "/v1/servers/actions/7"),
            ("GET", "/v1/images/21"));
        using var payload = ReadBody(0);
        Assert.Equal("snapshot", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("app production", payload.RootElement.GetProperty("description").GetString());
        Assert.Equal("op-412", payload.RootElement.GetProperty("labels").GetProperty("operation").GetString());
    }

    [Fact]
    public async Task DigitalOceanCreateResolvesNewImageBySourceAndNameAfterActionCompletes()
    {
        const string encodedName = "app production--selfrun-v1--eyJlbnZpcm9ubWVudCI6InByb2R1Y3Rpb24iLCJtYW5hZ2VkLWJ5Ijoic2VsZnJ1biIsIm9wZXJhdGlvbiI6ImE9MSwyIn0";
        var existing = DigitalOceanSnapshot("20", "14", encodedName);
        _handler.QueueJsonResponse($$"""{"snapshots":[{{existing}}]}""");
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"in-progress","resource_id":14}}""", HttpStatusCode.Created);
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"completed","resource_id":14}}""");
        _handler.QueueJsonResponse($$"""{"snapshots":[{{existing}},{{DigitalOceanSnapshot("22", "17", encodedName)}},{{DigitalOceanSnapshot("21", "14", encodedName)}}]}""");
        _handler.QueueJsonResponse($$"""{"snapshot":{{DigitalOceanSnapshot("21", "14", encodedName)}}}""");

        var client = CreateClient(CloudProvider.DigitalOcean);
        var snapshot = await client.CreateSnapshotAsync("14", "app production", Labels, default);
        var read = await client.GetSnapshotAsync(snapshot.Id, default);

        Assert.Equal("21", snapshot.Id);
        Assert.True(snapshot.IsAvailable);
        Assert.Equal(["nyc3"], snapshot.Regions);
        Assert.Equal("app production", snapshot.Description);
        Assert.Equal(Labels.OrderBy(x => x.Key), snapshot.Labels.OrderBy(x => x.Key));
        Assert.Equal(snapshot.Id, read?.Id);
        AssertRequests(
            ("GET", "/v2/snapshots?resource_type=droplet&per_page=200&page=1"),
            ("POST", "/v2/droplets/14/actions"),
            ("GET", "/v2/droplets/14/actions/7"),
            ("GET", "/v2/snapshots?resource_type=droplet&per_page=200&page=1"),
            ("GET", "/v2/snapshots/21"));
        using var payload = ReadBody(1);
        Assert.Equal("snapshot", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal(encodedName, payload.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task VultrCreatePollsPendingSnapshotAndPreservesOwnership()
    {
        const string description = "app production--selfrun-v1--eyJlbnZpcm9ubWVudCI6InByb2R1Y3Rpb24iLCJtYW5hZ2VkLWJ5Ijoic2VsZnJ1biIsIm9wZXJhdGlvbiI6ImE9MSwyIn0";
        _handler.QueueJsonResponse("""{"snapshot":{"id":"snap-21"}}""", HttpStatusCode.Created);
        _handler.QueueJsonResponse($$"""{"snapshot":{{VultrSnapshot("snap-21", description, "pending")}}}""");
        _handler.QueueJsonResponse($$"""{"snapshot":{{VultrSnapshot("snap-21", description, "complete")}}}""");

        var snapshot = await CreateClient(CloudProvider.Vultr).CreateSnapshotAsync("instance-14", "app production", Labels, default);

        Assert.True(snapshot.IsAvailable);
        Assert.Equal("snap-21", snapshot.Id);
        Assert.Equal("app production", snapshot.Description);
        Assert.Equal(Labels.OrderBy(x => x.Key), snapshot.Labels.OrderBy(x => x.Key));
        Assert.Equal(2.5m, snapshot.SizeGb);
        AssertRequests(("POST", "/v2/snapshots"), ("GET", "/v2/snapshots/snap-21"), ("GET", "/v2/snapshots/snap-21"));
        using var payload = ReadBody(0);
        Assert.Equal("instance-14", payload.RootElement.GetProperty("instance_id").GetString());
        Assert.Equal(description, payload.RootElement.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData(CloudProvider.Hetzner, "/v1/images?type=snapshot&per_page=50&page=2")]
    [InlineData(CloudProvider.DigitalOcean, "/v2/snapshots?resource_type=droplet&per_page=200&page=2")]
    [InlineData(CloudProvider.Vultr, "/v2/snapshots?per_page=500&cursor=next%2Fpage")]
    public async Task SnapshotListFollowsPaginationAndFiltersByOwnership(CloudProvider provider, string secondPage)
    {
        const string owned = "backup--selfrun-v1--eyJtYW5hZ2VkLWJ5Ijoic2VsZnJ1biJ9";
        const string unrelated = "unrelated--selfrun-v1--not-metadata";
        var (page1, page2) = provider switch
        {
            CloudProvider.Hetzner => (
                $$"""{"images":[{{HetznerImage(20, "someone-else")}}],"meta":{"pagination":{"next_page":2} } }""",
                $$"""{"images":[{{HetznerImage(21, "selfrun")}}],"meta":{"pagination":{"next_page":null} } }"""),
            CloudProvider.DigitalOcean => (
                $$"""{"snapshots":[{{DigitalOceanSnapshot("20", "14", unrelated)}}],"links":{"pages":{"next":"https://api.digitalocean.com/v2/snapshots?page=2"} } }""",
                $$"""{"snapshots":[{{DigitalOceanSnapshot("21", "14", owned)}}]}"""),
            _ => (
                $$"""{"snapshots":[{{VultrSnapshot("20", unrelated, "complete")}}],"meta":{"links":{"next":"next/page"} } }""",
                $$"""{"snapshots":[{{VultrSnapshot("21", owned, "complete")}}],"meta":{"links":{"next":""} } }""")
        };
        _handler.QueueJsonResponse(page1);
        _handler.QueueJsonResponse(page2);

        var snapshot = Assert.Single(await CreateClient(provider).ListSnapshotsAsync(Managed, default));

        Assert.Equal("21", snapshot.Id);
        Assert.Equal(secondPage, new Uri(_handler.RecordedRequests[1].Uri).PathAndQuery);
        Assert.Equal(2, _handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task HetznerPowerOffVerifiesShutdownAndPowerOnWaitsForRunning()
    {
        _handler.QueueJsonResponse("""{"server":{"id":14,"status":"running"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"running"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"success"}}""");
        _handler.QueueJsonResponse("""{"server":{"id":14,"status":"off"}}""");
        _handler.QueueJsonResponse("""{"server":{"id":14,"status":"off"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":8,"status":"success"}}""");
        _handler.QueueJsonResponse("""{"server":{"id":14,"status":"running"}}""");
        var client = CreateClient(CloudProvider.Hetzner);

        await client.PowerOffAsync("14", default);
        await client.PowerOnAsync("14", default);

        AssertRequests(("GET", "/v1/servers/14"), ("POST", "/v1/servers/14/actions/shutdown"), ("GET", "/v1/servers/actions/7"),
            ("GET", "/v1/servers/14"), ("GET", "/v1/servers/14"), ("POST", "/v1/servers/14/actions/poweron"), ("GET", "/v1/servers/14"));
    }

    [Fact]
    public async Task DigitalOceanPowerOffFallsBackAfterFailedGracefulAction()
    {
        _handler.QueueJsonResponse("""{"droplet":{"id":14,"status":"active"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"in-progress"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"errored"}}""");
        _handler.QueueJsonResponse("""{"droplet":{"id":14,"status":"active"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":8,"status":"completed"}}""");
        _handler.QueueJsonResponse("""{"droplet":{"id":14,"status":"off"}}""");
        _handler.QueueJsonResponse("""{"droplet":{"id":14,"status":"off"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":9,"status":"completed"}}""");
        _handler.QueueJsonResponse("""{"droplet":{"id":14,"status":"active"}}""");
        var client = CreateClient(CloudProvider.DigitalOcean);

        await client.PowerOffAsync("14", default);
        await client.PowerOnAsync("14", default);

        AssertRequests(("GET", "/v2/droplets/14"), ("POST", "/v2/droplets/14/actions"), ("GET", "/v2/droplets/14/actions/7"),
            ("GET", "/v2/droplets/14"), ("POST", "/v2/droplets/14/actions"), ("GET", "/v2/droplets/14"),
            ("GET", "/v2/droplets/14"), ("POST", "/v2/droplets/14/actions"), ("GET", "/v2/droplets/14"));
        using var graceful = ReadBody(1);
        using var forced = ReadBody(4);
        using var rollback = ReadBody(7);
        Assert.Equal("shutdown", graceful.RootElement.GetProperty("type").GetString());
        Assert.Equal("power_off", forced.RootElement.GetProperty("type").GetString());
        Assert.Equal("power_on", rollback.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task VultrPowerUsesHaltAndStartAndChecksObservedState()
    {
        _handler.QueueJsonResponse("""{"instance":{"status":"active","power_status":"running"}}""");
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        _handler.QueueJsonResponse("""{"instance":{"status":"active","power_status":"stopped"}}""");
        _handler.QueueJsonResponse("""{"instance":{"status":"active","power_status":"stopped"}}""");
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        _handler.QueueJsonResponse("""{"instance":{"status":"active","power_status":"running"}}""");
        var client = CreateClient(CloudProvider.Vultr);

        await client.PowerOffAsync("14", default);
        await client.PowerOnAsync("14", default);

        AssertRequests(("GET", "/v2/instances/14"), ("POST", "/v2/instances/14/halt"), ("GET", "/v2/instances/14"),
            ("GET", "/v2/instances/14"), ("POST", "/v2/instances/14/start"), ("GET", "/v2/instances/14"));
    }

    [Theory]
    [InlineData(CloudProvider.Hetzner, "/v1/servers", "image")]
    [InlineData(CloudProvider.DigitalOcean, "/v2/droplets", "image")]
    [InlineData(CloudProvider.Vultr, "/v2/instances", "snapshot_id")]
    public async Task RestoreUsesSnapshotImageAndPreservesProvisioningOptions(CloudProvider provider, string path, string imageProperty)
    {
        var body = provider switch
        {
            CloudProvider.Hetzner => """{"server":{"id":14}}""",
            CloudProvider.DigitalOcean => """{"droplet":{"id":14}}""",
            _ => """{"instance":{"id":"14"}}"""
        };
        _handler.QueueJsonResponse(body, HttpStatusCode.Created);
        var request = new CreateServerRequest("production", "plan", "region", ["9"], "cloud-init", Image: "not-the-snapshot");

        var server = await CreateClient(provider).CreateServerFromSnapshotAsync(request, "21", null, default);

        Assert.Equal("14", server.Id);
        AssertRequests(("POST", path));
        using var payload = ReadBody(0);
        var image = payload.RootElement.GetProperty(imageProperty);
        if (provider == CloudProvider.DigitalOcean)
            Assert.Equal(21, image.GetInt64());
        else
            Assert.Equal("21", image.GetString());
        Assert.False(payload.RootElement.TryGetProperty("os_id", out _));
        if (provider == CloudProvider.Vultr)
        {
            Assert.Equal("Y2xvdWQtaW5pdA==", payload.RootElement.GetProperty("user_data").GetString());
            Assert.Equal("9", payload.RootElement.GetProperty("sshkey_id")[0].GetString());
        }
        else
        {
            Assert.Equal("cloud-init", payload.RootElement.GetProperty("user_data").GetString());
            Assert.Equal(9, payload.RootElement.GetProperty("ssh_keys")[0].GetInt64());
        }
    }

    [Theory]
    [InlineData(CloudProvider.Hetzner, "/v1/images/21")]
    [InlineData(CloudProvider.DigitalOcean, "/v2/snapshots/21")]
    [InlineData(CloudProvider.Vultr, "/v2/snapshots/21")]
    public async Task SnapshotMissingIsNotAnErrorButOtherDeletionFailuresSurface(CloudProvider provider, string path)
    {
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        _handler.QueueJsonResponse("""{"message":"forbidden"}""", HttpStatusCode.Forbidden);
        var client = CreateClient(provider);

        Assert.Null(await client.GetSnapshotAsync("21", default));
        await client.DeleteSnapshotAsync("21", default);
        var error = await Assert.ThrowsAsync<ProviderApiException>(() => client.DeleteSnapshotAsync("21", default));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Equal(provider, error.Provider);
        AssertRequests(("GET", path), ("DELETE", path), ("DELETE", path));
    }

    [Theory]
    [InlineData(CloudProvider.Hetzner)]
    [InlineData(CloudProvider.DigitalOcean)]
    [InlineData(CloudProvider.Vultr)]
    public async Task TransientSnapshotPostFailureDoesNotReplayMutation(CloudProvider provider)
    {
        if (provider == CloudProvider.DigitalOcean)
            _handler.QueueJsonResponse("""{"snapshots":[]}""");
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var error = await Assert.ThrowsAsync<ProviderApiException>(() =>
            CreateClient(provider).CreateSnapshotAsync("14", "production", Labels, default));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Single(_handler.RecordedRequests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task HetznerSnapshotActionFailureDoesNotWaitForAnImage()
    {
        _handler.QueueJsonResponse("""{"image":{"id":21},"action":{"id":7,"status":"running"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"error","error":{"code":"snapshot_failed"}}}""");

        var error = await Assert.ThrowsAsync<ProviderApiException>(() =>
            CreateClient(CloudProvider.Hetzner).CreateSnapshotAsync("14", "production", Labels, default));

        Assert.Equal("snapshot_failed", error.ProviderErrorCode);
        AssertRequests(("POST", "/v1/servers/14/actions/create_image"), ("GET", "/v1/servers/actions/7"));
    }

    [Fact]
    public async Task CancellingGracefulShutdownDoesNotForcePowerOff()
    {
        using var cancelled = new CancellationTokenSource();
        _handler.QueueJsonResponse("""{"server":{"id":14,"status":"running"}}""");
        _handler.QueueJsonResponse("""{"action":{"id":7,"status":"success"}}""");
        _handler.QueueResponse(_ =>
        {
            cancelled.Cancel();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"server":{"id":14,"status":"running"}}""")
            };
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(CloudProvider.Hetzner).PowerOffAsync("14", cancelled.Token));

        AssertRequests(("GET", "/v1/servers/14"), ("POST", "/v1/servers/14/actions/shutdown"), ("GET", "/v1/servers/14"));
    }

    [Fact]
    public async Task VultrCreateDoesNotReturnAnImageThatLostItsOwnershipMetadata()
    {
        _handler.QueueJsonResponse("""{"snapshot":{"id":"snap-21"}}""", HttpStatusCode.Created);
        _handler.QueueJsonResponse($$"""{"snapshot":{{VultrSnapshot("snap-21", "production", "complete")}}}""");

        await Assert.ThrowsAsync<ProviderApiException>(() =>
            CreateClient(CloudProvider.Vultr).CreateSnapshotAsync("14", "production", Labels, default));
    }

    private IVpsProviderSnapshotClient CreateClient(CloudProvider provider)
    {
        var http = new HttpClient(new ResilientHttpRetryHandler(_handler, maxRetries: 1, baseDelay: TimeSpan.Zero));
        return provider switch
        {
            CloudProvider.Hetzner => new HetznerClient(http, new HetznerOptions()),
            CloudProvider.DigitalOcean => new DigitalOceanClient(http, new DigitalOceanOptions()),
            CloudProvider.Vultr => new VultrClient(http, new VultrOptions()),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
    }

    private void AssertRequests(params (string Method, string Path)[] expected) =>
        Assert.Equal(expected, _handler.RecordedRequests.Select(request => (request.Method.Method, new Uri(request.Uri).PathAndQuery)));

    private JsonDocument ReadBody(int index) => JsonDocument.Parse(_handler.RecordedRequests[index].Body!);

    private static string HetznerImage(int id, string owner) =>
        $$$"""{"id":{{{id}}},"type":"snapshot","status":"available","created":"{{{CreatedAt}}}","image_size":2.5,"labels":{"managed-by":"{{{owner}}}","environment":"production","operation":"op-412"}}""";

    private static string DigitalOceanSnapshot(string id, string serverId, string name) =>
        $$"""{"id":"{{id}}","resource_id":"{{serverId}}","resource_type":"droplet","name":"{{name}}","created_at":"{{CreatedAt}}","size_gigabytes":2.5,"regions":["nyc3"]}""";

    private static string VultrSnapshot(string id, string description, string status) =>
        $$"""{"id":"{{id}}","description":"{{description}}","status":"{{status}}","date_created":"{{CreatedAt}}","size":2684354560}""";
}
