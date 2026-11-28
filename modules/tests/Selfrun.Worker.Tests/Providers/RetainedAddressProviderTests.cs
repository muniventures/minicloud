using System.Net;
using System.Text.Json;
using Selfrun.Common.Providers;
using Selfrun.Common.Providers.Common;
using Selfrun.Common.Providers.DigitalOcean;
using Selfrun.Common.Providers.Hetzner;
using Selfrun.Common.Providers.Models;
using Selfrun.Common.Providers.Vultr;

namespace Selfrun.Worker.Tests.Providers;

public class RetainedAddressProviderTests
{
    private const string HetznerBaseUrl = "https://api.hetzner.cloud/v1/";
    private const string VultrBaseUrl = "https://api.vultr.com/v2/";
    private const string DigitalOceanBaseUrl = "https://api.digitalocean.com/v2/";
    private readonly TestHttpMessageHandler _handler = new();

    [Fact]
    public async Task HetznerRetainServerAddressDisablesAutoDeleteAndKeepsDatacenter()
    {
        _handler.QueueJsonResponse("""{"server":{"public_net":{"ipv4":{"id":21,"ip":"192.0.2.21"}}}}""");
        _handler.QueueJsonResponse("""{"primary_ip":{"id":21,"ip":"192.0.2.21","datacenter":"fsn1-dc14","auto_delete":true}}""");
        _handler.QueueJsonResponse("""{"primary_ip":{"id":21,"ip":"192.0.2.21","datacenter":"fsn1-dc14","auto_delete":true}}""");
        _handler.QueueJsonResponse("""{"primary_ip":{"id":21,"ip":"192.0.2.21","datacenter":"fsn1-dc14","auto_delete":false}}""");

        var address = await CreateHetznerClient().RetainServerAddressAsync("14");

        Assert.Equal(new ProviderRetainedAddress("21", "192.0.2.21", Datacenter: "fsn1-dc14"), address);
        Assert.Equal(
            ["/v1/servers/14", "/v1/primary_ips/21", "/v1/primary_ips/21", "/v1/primary_ips/21"],
            _handler.RecordedRequests.Select(x => new Uri(x.Uri).PathAndQuery));
        Assert.Equal(HttpMethod.Put, _handler.RecordedRequests[3].Method);
        using var update = JsonDocument.Parse(_handler.RecordedRequests[3].Body!);
        Assert.False(update.RootElement.GetProperty("auto_delete").GetBoolean());
    }

    [Fact]
    public async Task HetznerEnsureAndReleaseAreIdempotentWhenAlreadySafeOrMissing()
    {
        _handler.QueueJsonResponse("""{"primary_ip":{"id":21,"ip":"192.0.2.21","datacenter":"fsn1-dc14","auto_delete":false}}""");
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateHetznerClient();

        await client.EnsureSurvivesServerDeletionAsync("21");
        await client.ReleaseRetainedAddressAsync("21");

        Assert.Equal(HttpMethod.Get, _handler.RecordedRequests[0].Method);
        Assert.Equal(HttpMethod.Delete, _handler.RecordedRequests[1].Method);
    }

    [Fact]
    public async Task HetznerCreateWithRetainedAddressUsesPrimaryIpAndItsDatacenter()
    {
        _handler.QueueJsonResponse("""{"primary_ip":{"id":21,"ip":"192.0.2.21","datacenter":"fsn1-dc14","auto_delete":false}}""");
        _handler.QueueJsonResponse("""{"server":{"id":22,"name":"sleeping","status":"initializing","public_net":{"ipv4":{"id":21,"ip":"192.0.2.21"}}}}""", HttpStatusCode.Created);

        await CreateHetznerClient().CreateServerAsync(
            new CreateServerRequest("sleeping", "cx23", "fsn1", [], "#cloud-config", RetainedAddressId: "21"));

        using var create = JsonDocument.Parse(_handler.RecordedRequests[1].Body!);
        var payload = create.RootElement;
        Assert.Equal("fsn1-dc14", payload.GetProperty("datacenter").GetString());
        Assert.Equal(21, payload.GetProperty("public_net").GetProperty("ipv4").GetInt64());
        Assert.False(payload.TryGetProperty("location", out _));
    }

    [Fact]
    public async Task HetznerAddressApiErrorsPreserveProviderAndStatus()
    {
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<ProviderApiException>(
            () => CreateHetznerClient().GetRetainedAddressAsync("21"));

        Assert.Equal(CloudProvider.Hetzner, error.Provider);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task VultrRetainConvertsCurrentIpv4AndReusesAlreadyReservedAddress()
    {
        _handler.QueueJsonResponse("""{"instance":{"id":"instance-14","main_ip":"192.0.2.14","status":"active"}}""");
        _handler.QueueJsonResponse("""{"reserved_ips":[]}""");
        _handler.QueueJsonResponse("""{"reserved_ip":{"id":"reserved-14","region":"ewr","ip_type":"v4","subnet":"192.0.2.14","instance_id":"instance-14"}}""", HttpStatusCode.Created);
        _handler.QueueJsonResponse("""{"instance":{"id":"instance-14","main_ip":"192.0.2.14","status":"active"}}""");
        _handler.QueueJsonResponse("""{"reserved_ips":[{"id":"reserved-14","region":"ewr","ip_type":"v4","subnet":"192.0.2.14","instance_id":"instance-14"}]}""");
        var client = CreateVultrClient();

        var address = await client.RetainServerAddressAsync("instance-14");
        var repeated = await client.RetainServerAddressAsync("instance-14");

        Assert.Equal(new ProviderRetainedAddress("reserved-14", "192.0.2.14", "ewr"), address);
        Assert.Equal(address, repeated);
        Assert.Equal(5, _handler.RecordedRequests.Count);
        Assert.Equal(HttpMethod.Post, _handler.RecordedRequests[2].Method);
        Assert.EndsWith("/reserved-ips/convert", _handler.RecordedRequests[2].Uri);
        using var body = JsonDocument.Parse(_handler.RecordedRequests[2].Body!);
        Assert.Equal("192.0.2.14", body.RootElement.GetProperty("ip_address").GetString());
    }

    [Fact]
    public async Task VultrCreateWithRetainedAddressSendsReservedIpv4()
    {
        _handler.QueueJsonResponse("""{"reserved_ip":{"id":"reserved-14","region":"ewr","ip_type":"v4","subnet":"192.0.2.14"}}""");
        _handler.QueueJsonResponse("""{"instance":{"id":"instance-15","label":"sleeping","main_ip":"192.0.2.15","status":"pending"}}""", HttpStatusCode.Accepted);

        var server = await CreateVultrClient().CreateServerAsync(
            new CreateServerRequest("sleeping", "vc2-1c-1gb", "ewr", [], "", Image: "2284", RetainedAddressId: "reserved-14"));

        Assert.Equal("192.0.2.14", server.Ipv4Address);
        using var create = JsonDocument.Parse(_handler.RecordedRequests[1].Body!);
        Assert.Equal("reserved-14", create.RootElement.GetProperty("reserved_ipv4").GetString());
    }

    [Fact]
    public async Task VultrMissingReservedAddressReleaseIsIdempotent()
    {
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));

        await CreateVultrClient().ReleaseRetainedAddressAsync("reserved-missing");

        Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Delete, _handler.RecordedRequests[0].Method);
    }

    [Fact]
    public async Task DigitalOceanRetentionCreatesAssignsAndPollsReservedIpAction()
    {
        _handler.QueueJsonResponse("""{"reserved_ips":[],"meta":{"total":0}}""");
        _handler.QueueJsonResponse("""{"droplet":{"id":600,"region":{"slug":"nyc1"}}}""");
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","region":{"slug":"nyc1"},"droplet":null}}""", HttpStatusCode.Created);
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","region":{"slug":"nyc1"},"droplet":null}}""");
        _handler.QueueJsonResponse("""{"action":{"id":8,"status":"in-progress","resource_id":600}}""", HttpStatusCode.Accepted);
        _handler.QueueJsonResponse("""{"action":{"id":8,"status":"completed","resource_id":600}}""");
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","region":{"slug":"nyc1"},"droplet":{"id":600}}}""");

        var address = await CreateDigitalOceanClient().RetainServerAddressAsync("600");

        Assert.Equal(new ProviderRetainedAddress("203.0.113.60", "203.0.113.60", "nyc1"), address);
        Assert.Equal(
            ["GET", "GET", "POST", "GET", "POST", "GET", "GET"],
            _handler.RecordedRequests.Select(x => x.Method.Method));
        using var create = JsonDocument.Parse(_handler.RecordedRequests[2].Body!);
        Assert.Equal("nyc1", create.RootElement.GetProperty("region").GetString());
        using var action = JsonDocument.Parse(_handler.RecordedRequests[4].Body!);
        Assert.Equal("assign", action.RootElement.GetProperty("type").GetString());
        Assert.Equal(600, action.RootElement.GetProperty("droplet_id").GetInt64());
        Assert.EndsWith("/reserved_ips/203.0.113.60/actions/8", _handler.RecordedRequests[5].Uri);
    }

    [Fact]
    public async Task DigitalOceanAssignIsIdempotentWhenAlreadyAssignedToRequestedDroplet()
    {
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":{"id":600}}}""");

        await CreateDigitalOceanClient().AssignRetainedAddressAsync("203.0.113.60", "600");

        Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Get, _handler.RecordedRequests[0].Method);
    }

    [Fact]
    public async Task DigitalOceanRetentionReusesExistingAddressAssignedToDroplet()
    {
        _handler.QueueJsonResponse("""{"reserved_ips":[{"ip":"203.0.113.60","region":{"slug":"nyc1"},"droplet":{"id":600}}],"meta":{"total":1}}""");

        var address = await CreateDigitalOceanClient().RetainServerAddressAsync("600");

        Assert.Equal(new ProviderRetainedAddress("203.0.113.60", "203.0.113.60", "nyc1"), address);
        Assert.Single(_handler.RecordedRequests);
    }

    [Fact]
    public async Task DigitalOceanCreateWithRetainedAddressAssignsAfterDropletCreation()
    {
        _handler.QueueJsonResponse("""{"droplet":{"id":601,"name":"sleeping","status":"new"}}""", HttpStatusCode.Created);
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":null}}""");
        _handler.QueueJsonResponse("""{"action":{"id":11,"status":"completed","resource_id":601}}""", HttpStatusCode.Accepted);
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":{"id":601}}}""");
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":{"id":601}}}""");

        var server = await CreateDigitalOceanClient().CreateServerAsync(
            new CreateServerRequest("sleeping", "s-1vcpu-1gb", "nyc1", [], "#cloud-config", RetainedAddressId: "203.0.113.60"));

        Assert.Equal("203.0.113.60", server.Ipv4Address);
        Assert.Equal(HttpMethod.Post, _handler.RecordedRequests[0].Method);
        Assert.Equal(HttpMethod.Post, _handler.RecordedRequests[2].Method);
        using var action = JsonDocument.Parse(_handler.RecordedRequests[2].Body!);
        Assert.Equal("assign", action.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task DigitalOceanReleaseUnassignsBeforeDeletingAndTreatsNotFoundAsSuccess()
    {
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":{"id":600}}}""");
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":{"id":600}}}""");
        _handler.QueueJsonResponse("""{"action":{"id":9,"status":"completed","resource_id":600}}""", HttpStatusCode.Accepted);
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":null}}""");
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateDigitalOceanClient();

        await client.ReleaseRetainedAddressAsync("203.0.113.60");
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        await client.ReleaseRetainedAddressAsync("203.0.113.61");

        Assert.Equal(HttpMethod.Post, _handler.RecordedRequests[2].Method);
        using var unassign = JsonDocument.Parse(_handler.RecordedRequests[2].Body!);
        Assert.Equal("unassign", unassign.RootElement.GetProperty("type").GetString());
        Assert.Equal(HttpMethod.Delete, _handler.RecordedRequests[4].Method);
        Assert.Equal(HttpMethod.Get, _handler.RecordedRequests[5].Method);
        Assert.EndsWith("/reserved_ips/203.0.113.61", _handler.RecordedRequests[5].Uri);
    }

    [Fact]
    public async Task DigitalOceanErroredAddressActionSurfacesProviderError()
    {
        _handler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":null}}""");
        _handler.QueueJsonResponse("""{"action":{"id":10,"status":"errored","resource_id":600,"error_code":"assignment_failed"}}""", HttpStatusCode.Accepted);

        var error = await Assert.ThrowsAsync<ProviderApiException>(
            () => CreateDigitalOceanClient().AssignRetainedAddressAsync("203.0.113.60", "600"));

        Assert.Equal(CloudProvider.DigitalOcean, error.Provider);
        Assert.Equal("assignment_failed", error.ProviderErrorCode);
        Assert.Contains("action failed", error.Message);
    }

    [Fact]
    public async Task DigitalOceanReservedIpCreationAndActionsDoNotRetryMutations()
    {
        var createHandler = new TestHttpMessageHandler();
        createHandler.QueueJsonResponse("""{"reserved_ips":[]}""");
        createHandler.QueueJsonResponse("""{"droplet":{"id":600,"region":{"slug":"nyc1"}}}""");
        createHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderApiException>(
            () => CreateRetryingDigitalOceanClient(createHandler).RetainServerAddressAsync("600"));

        Assert.Equal(3, createHandler.RecordedRequests.Count);
        Assert.Equal(HttpMethod.Post, createHandler.RecordedRequests[2].Method);

        var actionHandler = new TestHttpMessageHandler();
        actionHandler.QueueJsonResponse("""{"reserved_ip":{"ip":"203.0.113.60","droplet":null}}""");
        actionHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderApiException>(
            () => CreateRetryingDigitalOceanClient(actionHandler).AssignRetainedAddressAsync("203.0.113.60", "600"));

        Assert.Equal(2, actionHandler.RecordedRequests.Count);
        Assert.Equal(HttpMethod.Post, actionHandler.RecordedRequests[1].Method);
    }

    [Fact]
    public async Task VultrReservedIpConversionDoesNotRetryMutation()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"instance":{"id":"instance-14","main_ip":"192.0.2.14","status":"active"}}""");
        handler.QueueJsonResponse("""{"reserved_ips":[]}""");
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderApiException>(
            () => CreateRetryingVultrClient(handler).RetainServerAddressAsync("instance-14"));

        Assert.Equal(3, handler.RecordedRequests.Count);
        Assert.Equal(HttpMethod.Post, handler.RecordedRequests[2].Method);
    }

    [Fact]
    public async Task DigitalOceanCreateWithRetainedAddressDoesNotRetryServerAllocation()
    {
        var handler = new TestHttpMessageHandler();
        handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<ProviderApiException>(
            () => CreateRetryingDigitalOceanClient(handler).CreateServerAsync(
                new CreateServerRequest("sleeping", "s-1vcpu-1gb", "nyc1", [], "", RetainedAddressId: "203.0.113.60")));

        Assert.Single(handler.RecordedRequests);
    }

    private HetznerClient CreateHetznerClient() =>
        new(new HttpClient(_handler) { BaseAddress = new Uri(HetznerBaseUrl) }, new HetznerOptions { ApiToken = "fixture" });

    private VultrClient CreateVultrClient() =>
        new(new HttpClient(_handler) { BaseAddress = new Uri(VultrBaseUrl) }, new VultrOptions { ApiKey = "fixture", DefaultOsId = 2284 });

    private DigitalOceanClient CreateDigitalOceanClient() =>
        new(new HttpClient(_handler) { BaseAddress = new Uri(DigitalOceanBaseUrl) }, new DigitalOceanOptions { ApiToken = "fixture" });

    private static DigitalOceanClient CreateRetryingDigitalOceanClient(TestHttpMessageHandler handler) =>
        new(new HttpClient(new ResilientHttpRetryHandler(handler, maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1)))
            { BaseAddress = new Uri(DigitalOceanBaseUrl) },
            new DigitalOceanOptions { ApiToken = "fixture" });

    private static VultrClient CreateRetryingVultrClient(TestHttpMessageHandler handler) =>
        new(new HttpClient(new ResilientHttpRetryHandler(handler, maxRetries: 2, baseDelay: TimeSpan.FromMilliseconds(1)))
            { BaseAddress = new Uri(VultrBaseUrl) },
            new VultrOptions { ApiKey = "fixture", DefaultOsId = 2284 });
}
