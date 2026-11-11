using System.Net;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Models;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public class HetznerClientTests
{
    private readonly TestHttpMessageHandler _handler = new();
    private readonly HetznerOptions _options = new()
    {
        ApiToken = "hcloud-secret-token-12345",
        BaseUrl = "https://api.hetzner.cloud/v1/"
    };

    private HetznerClient CreateClient()
    {
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri(_options.BaseUrl)
        };
        return new HetznerClient(httpClient, _options);
    }

    [Fact]
    public async Task FindServerAsync_WhenServerExists_ReturnsServerInfo()
    {
        // Arrange
        const string json = """
        {
            "servers": [
                {
                    "id": 1001,
                    "name": "minicloud-test-web",
                    "status": "running",
                    "public_net": {
                        "ipv4": { "ip": "159.69.1.2" },
                        "ipv6": { "ip": "2a01:4f8:1c1c::1" }
                    },
                    "labels": { "managed-by": "minicloud", "env": "prod" }
                }
            ]
        }
        """;
        _handler.QueueJsonResponse(json);
        var client = CreateClient();

        // Act
        var result = await client.FindServerAsync("minicloud-test-web");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1001", result.Id);
        Assert.Equal("minicloud-test-web", result.Name);
        Assert.Equal("159.69.1.2", result.Ipv4Address);
        Assert.Equal("running", result.Status);
        Assert.Equal("2a01:4f8:1c1c::1", result.Ipv6Address);
        Assert.Equal("prod", result.Labels?["env"]);

        var req = Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("servers?name=minicloud-test-web", req.Uri);
        Assert.Equal("Bearer hcloud-secret-token-12345", req.AuthorizationHeader);
    }

    [Fact]
    public async Task FindServerAsync_WhenServerNotFound_ReturnsNull()
    {
        // Arrange
        _handler.QueueJsonResponse("""{"servers": []}""");
        var client = CreateClient();

        // Act
        var result = await client.FindServerAsync("missing-server");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task FindServerAsync_When404_ReturnsNull()
    {
        // Arrange
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient();

        // Act
        var result = await client.FindServerAsync("missing-server");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateServerAsync_SendsValidPayload_AndParsesResponse()
    {
        // Arrange
        const string createResponse = """
        {
            "server": {
                "id": 2002,
                "name": "minicloud-app-srv",
                "status": "initializing",
                "public_net": {
                    "ipv4": { "ip": "159.69.5.6" }
                }
            },
            "action": { "id": 1, "status": "running" }
        }
        """;
        _handler.QueueJsonResponse(createResponse, HttpStatusCode.Created);
        var client = CreateClient();

        var request = new CreateServerRequest(
            Name: "minicloud-app-srv",
            Plan: "cx22",
            Region: "fsn1",
            SshKeyReferences: ["123", "456"],
            UserDataCloudInit: "#cloud-config\nusers: []",
            Labels: new Dictionary<string, string> { ["customer"] = "acme" });

        // Act
        var result = await client.CreateServerAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("2002", result.Id);
        Assert.Equal("minicloud-app-srv", result.Name);
        Assert.Equal("159.69.5.6", result.Ipv4Address);
        Assert.Equal("initializing", result.Status);

        var req = Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("\"name\":\"minicloud-app-srv\"", req.Body);
        Assert.Contains("\"server_type\":\"cx22\"", req.Body);
        Assert.Contains("\"location\":\"fsn1\"", req.Body);
        Assert.Contains("\"ssh_keys\":[123,456]", req.Body);
        Assert.Contains("\"customer\":\"acme\"", req.Body);
    }

    [Fact]
    public async Task SshKeyInventoryAndRegistrationUseHetznerApiShapes()
    {
        _handler.QueueJsonResponse("""{"ssh_keys":[{"id":123,"name":"work","public_key":"ssh-ed25519 fixture"}],"meta":{"pagination":{"next_page":null}}}""");
        _handler.QueueJsonResponse("""{"ssh_key":{"id":456,"name":"new","public_key":"ssh-ed25519 fixture"}}""", HttpStatusCode.Created);
        var client = CreateClient();

        var listed = await client.ListSshKeysAsync();
        var registered = await client.RegisterSshKeyAsync("new", "ssh-ed25519 AAAA");

        Assert.Equal(new ProviderSshKeyInfo("123", "work", "ssh-ed25519 fixture"), Assert.Single(listed));
        Assert.Equal(new ProviderSshKeyInfo("456", "new", "ssh-ed25519 fixture"), registered);
        Assert.Contains("/v1/ssh_keys?per_page=50&page=1", _handler.RecordedRequests[0].Uri);
        Assert.Contains("\"public_key\":\"ssh-ed25519 AAAA\"", _handler.RecordedRequests[1].Body);
    }

    [Fact]
    public async Task CreateServerAsync_CloudInitKeyDoesNotBecomeProviderKeyReference()
    {
        _handler.QueueJsonResponse("""{"server":{"id":2002,"name":"test","status":"initializing"}}""", HttpStatusCode.Created);
        var cloudInit = Minicloud.Worker.Pipeline.Steps.ResolveProvisionServerStep.BuildCloudInit(
            "org", "app", "ssh-ed25519 TEST_PUBLIC_KEY");

        await CreateClient().CreateServerAsync(new CreateServerRequest(
            "test", "cx23", "nbg1", [], cloudInit));

        var request = Assert.Single(_handler.RecordedRequests);
        using var body = System.Text.Json.JsonDocument.Parse(request.Body!);
        Assert.False(body.RootElement.TryGetProperty("ssh_keys", out _));
        Assert.Equal(cloudInit, body.RootElement.GetProperty("user_data").GetString());
    }

    [Fact]
    public async Task GetServerStatusAsync_WhenRunning_ReturnsRunningStatus()
    {
        // Arrange
        const string statusResponse = """
        {
            "server": {
                "id": 3003,
                "name": "minicloud-status-srv",
                "status": "running",
                "public_net": {
                    "ipv4": { "ip": "159.69.10.11" },
                    "ipv6": { "ip": "2a01:4f8:1c1c::2" }
                }
            }
        }
        """;
        _handler.QueueJsonResponse(statusResponse);
        var client = CreateClient();

        // Act
        var status = await client.GetServerStatusAsync("3003");

        // Assert
        Assert.Equal("3003", status.Id);
        Assert.Equal("running", status.Status);
        Assert.Equal("159.69.10.11", status.Ipv4Address);
        Assert.True(status.IsRunning);
    }

    [Fact]
    public async Task GetServerStatusAsync_When404_ReturnsNotFoundStatusCleanly()
    {
        // Arrange
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient();

        // Act
        var status = await client.GetServerStatusAsync("9999");

        // Assert
        Assert.NotNull(status);
        Assert.Equal("9999", status.Id);
        Assert.Equal("not_found", status.Status);
        Assert.False(status.IsRunning);
    }

    [Fact]
    public async Task DeleteServerAsync_WhenSuccess_SendsDeleteRequest()
    {
        // Arrange
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient();

        // Act
        await client.DeleteServerAsync("4004");

        // Assert
        var req = Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Delete, req.Method);
        Assert.Contains("servers/4004", req.Uri);
    }

    [Fact]
    public async Task DeleteServerAsync_When404_CompletesWithoutError()
    {
        // Arrange
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient();

        // Act & Assert (should not throw)
        await client.DeleteServerAsync("non-existent-server");
    }

    [Fact]
    public async Task ApiError_ThrowsProviderApiException_WithRedactedToken()
    {
        // Arrange
        _handler.QueueJsonResponse("""{"error": {"message": "Invalid token hcloud-secret-token-12345"}}""", HttpStatusCode.Unauthorized);
        var client = CreateClient();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("test"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("hcloud-secret-token-12345", ex.Message);
        Assert.Equal("Hetzner API request failed with HTTP 401.", ex.Message);
        Assert.DoesNotContain("Invalid token", ex.Message);
        Assert.NotNull(ex.ResponseBody);
        Assert.DoesNotContain("hcloud-secret-token-12345", ex.ResponseBody);
        Assert.Contains("[REDACTED]", ex.ResponseBody);
    }
}
