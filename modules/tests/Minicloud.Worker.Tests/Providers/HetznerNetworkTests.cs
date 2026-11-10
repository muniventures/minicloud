using System.Net;
using System.Text.Json;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Tests.Providers;

public sealed class HetznerNetworkTests
{
    private const string Network = """{"id":901,"name":"minicloud-network","ip_range":"10.72.0.0/16","subnets":[{"type":"cloud","ip_range":"10.72.1.0/24","network_zone":"eu-central"}],"labels":{"minicloud-org":"org_test"},"servers":[]}""";
    private static HetznerClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), new HetznerOptions { ApiToken = "fixture", BaseUrl = "https://api.hetzner.cloud/v1/" });

    [Fact]
    public async Task NetworkCreateUsesCloudSubnetAndOwnershipLabels()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"network\":" + Network + "}");
        var network = await Client(handler).CreatePrivateNetworkAsync("minicloud-network", "10.72.0.0/16", "10.72.1.0/24", "eu-central", new Dictionary<string, string> { ["minicloud-org"] = "org_test" }, default);
        Assert.Equal("901", network.Id);
        using var body = JsonDocument.Parse(Assert.Single(handler.RecordedRequests).Body!);
        Assert.Equal("cloud", body.RootElement.GetProperty("subnets")[0].GetProperty("type").GetString());
        Assert.Equal("eu-central", body.RootElement.GetProperty("subnets")[0].GetProperty("network_zone").GetString());
        Assert.Equal("org_test", body.RootElement.GetProperty("labels").GetProperty("minicloud-org").GetString());
    }

    [Fact]
    public async Task AttachmentReconcilesActualServerPrivateAddress()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"network\":" + Network + "}");
        handler.QueueJsonResponse("""{"server":{"id":123,"datacenter":{"location":{"network_zone":"eu-central"}},"private_net":[]}}""");
        handler.QueueJsonResponse("""{"action":{"id":9,"status":"success"}}""");
        handler.QueueJsonResponse("""{"server":{"id":123,"private_net":[{"network":901,"ip":"10.72.1.10"}]}}""");
        var attached = await Client(handler).AttachPrivateNetworkAsync("901", "123", default);
        Assert.Equal("10.72.1.10", attached.IpAddress);
        Assert.EndsWith("servers/123/actions/attach_to_network", handler.RecordedRequests[2].Uri);
        using var body = JsonDocument.Parse(handler.RecordedRequests[2].Body!);
        Assert.Equal(901, body.RootElement.GetProperty("network").GetInt64());
    }

    [Fact]
    public async Task ExistingAttachmentAvoidsMutationAndWrongZoneIsRejected()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"network\":" + Network + "}");
        handler.QueueJsonResponse("""{"server":{"id":123,"datacenter":{"location":{"network_zone":"eu-central"}},"private_net":[{"network":901,"ip":"10.72.1.10"}]}}""");
        Assert.Equal("10.72.1.10", (await Client(handler).AttachPrivateNetworkAsync("901", "123", default)).IpAddress);
        Assert.Equal(2, handler.RecordedRequests.Count);
        handler.QueueJsonResponse("{\"network\":" + Network + "}");
        handler.QueueJsonResponse("""{"server":{"id":124,"datacenter":{"location":{"network_zone":"us-east"}},"private_net":[]}}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).AttachPrivateNetworkAsync("901", "124", default));
        Assert.Equal(4, handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task AmbiguousCreateFailureIsNotBlindlyRetried()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"error":{"message":"temporary"}}""", HttpStatusCode.ServiceUnavailable);
        var retry = new ResilientHttpRetryHandler(handler, baseDelay: TimeSpan.Zero);
        await Assert.ThrowsAsync<ProviderApiException>(() => Client(retry).CreatePrivateNetworkAsync("minicloud-network", "10.72.0.0/16", "10.72.1.0/24", "eu-central", new Dictionary<string, string>(), default));
        Assert.Single(handler.RecordedRequests);
    }

    [Fact]
    public async Task DetachWaitsAndChecksObservedAbsence()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"server":{"id":123,"private_net":[{"network":901,"ip":"10.72.1.10"}]}}""");
        handler.QueueJsonResponse("""{"action":{"id":9,"status":"success"}}""");
        handler.QueueJsonResponse("""{"server":{"id":123,"private_net":[]}}""");
        await Client(handler).DetachPrivateNetworkAsync("901", "123", default);
        Assert.EndsWith("servers/123/actions/detach_from_network", handler.RecordedRequests[1].Uri);
        Assert.Equal(3, handler.RecordedRequests.Count);
    }

    [Fact]
    public async Task DeleteRequiresObservedEmptyNetworkAndChecksRemoval()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("{\"network\":" + Network.Replace("\"servers\":[]", "\"servers\":[123]") + "}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).DeletePrivateNetworkAsync("901", default));
        Assert.Single(handler.RecordedRequests);
        handler.QueueJsonResponse("{\"network\":" + Network + "}");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        await Client(handler).DeletePrivateNetworkAsync("901", default);
        Assert.Equal(HttpMethod.Delete, handler.RecordedRequests[2].Method);
    }

    [Theory]
    [InlineData("10.72.0.0/16", "10.72.1.0/24")]
    [InlineData("172.16.0.0/12", "172.20.0.0/24")]
    [InlineData("192.168.0.0/16", "192.168.1.0/24")]
    public void AcceptsContainedCanonicalPrivateRanges(string range, string subnet) => DatabaseNetworkService.ValidateRanges(range, subnet);

    [Theory]
    [InlineData("192.168.0.0/15", "192.168.1.0/24")]
    [InlineData("10.72.0.1/16", "10.72.1.0/24")]
    [InlineData("10.72.0.0/16", "10.73.1.0/24")]
    [InlineData("203.0.113.0/24", "203.0.113.0/28")]
    [InlineData("fd00::/64", "fd00::/80")]
    public void RejectsPublicNonCanonicalOrOutsideSubnets(string range, string subnet) => Assert.Throws<ArgumentException>(() => DatabaseNetworkService.ValidateRanges(range, subnet));
}
