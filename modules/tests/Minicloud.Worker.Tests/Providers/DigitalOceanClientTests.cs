using System.Net;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Models;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public class DigitalOceanClientTests
{
    private readonly TestHttpMessageHandler _handler = new();
    private readonly DigitalOceanOptions _options = new()
    {
        ApiToken = "dop_v1_secret_token_abcdef123456",
        ProjectId = "project-uuid-9876",
        BaseUrl = "https://api.digitalocean.com/v2/"
    };

    private DigitalOceanClient CreateClient()
    {
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri(_options.BaseUrl)
        };
        return new DigitalOceanClient(httpClient, _options);
    }

    [Fact]
    public async Task FindServerAsync_WhenDropletFound_ReturnsServerInfo()
    {
        // Arrange
        const string json = """
        {
            "droplets": [
                {
                    "id": 5001,
                    "name": "minicloud-do-web",
                    "status": "active",
                    "networks": {
                        "v4": [
                            { "ip_address": "10.0.0.1", "type": "private" },
                            { "ip_address": "159.203.1.2", "type": "public" }
                        ],
                        "v6": [
                            { "ip_address": "2604:a880:800:10::4b:6001", "type": "public" }
                        ]
                    },
                    "tags": ["minicloud", "env-prod"]
                }
            ]
        }
        """;
        _handler.QueueJsonResponse(json);
        var client = CreateClient();

        // Act
        var result = await client.FindServerAsync("minicloud-do-web");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("5001", result.Id);
        Assert.Equal("minicloud-do-web", result.Name);
        Assert.Equal("159.203.1.2", result.Ipv4Address);
        Assert.Equal("active", result.Status);
        Assert.Equal("2604:a880:800:10::4b:6001", result.Ipv6Address);

        var req = Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("droplets?name=minicloud-do-web", req.Uri);
        Assert.Equal("Bearer dop_v1_secret_token_abcdef123456", req.AuthorizationHeader);
    }

    [Fact]
    public async Task FindServerAsync_WhenDropletNotFound_ReturnsNull()
    {
        // Arrange
        _handler.QueueJsonResponse("""{"droplets": []}""");
        var client = CreateClient();

        // Act
        var result = await client.FindServerAsync("missing-droplet");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateServerAsync_CreatesDroplet_AndAssignsToProject()
    {
        // Arrange
        const string createDropletResponse = """
        {
            "droplet": {
                "id": 6002,
                "name": "minicloud-do-app",
                "status": "new",
                "networks": {
                    "v4": [ { "ip_address": "159.203.8.9", "type": "public" } ]
                }
            }
        }
        """;
        const string assignResponse = """
        {
            "resources": [
                { "urn": "do:droplet:6002", "assigned_at": "2026-09-09T10:00:00Z" }
            ]
        }
        """;

        _handler.QueueJsonResponse(createDropletResponse, HttpStatusCode.Created);
        _handler.QueueJsonResponse(assignResponse, HttpStatusCode.OK);

        var client = CreateClient();
        var request = new CreateServerRequest(
            Name: "minicloud-do-app",
            Plan: "s-1vcpu-1gb",
            Region: "nyc1",
            SshKeyIdOrPublicKey: "ssh-key-id",
            UserDataCloudInit: "#cloud-config",
            Tags: ["minicloud-app-test"]);

        // Act
        var result = await client.CreateServerAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("6002", result.Id);
        Assert.Equal("minicloud-do-app", result.Name);
        Assert.Equal("159.203.8.9", result.Ipv4Address);

        Assert.Equal(2, _handler.RecordedRequests.Count);

        // First request: create droplet
        var createReq = _handler.RecordedRequests[0];
        Assert.Equal(HttpMethod.Post, createReq.Method);
        Assert.Contains("droplets", createReq.Uri);
        Assert.Contains("\"name\":\"minicloud-do-app\"", createReq.Body);
        Assert.Contains("\"size\":\"s-1vcpu-1gb\"", createReq.Body);
        Assert.Contains("\"region\":\"nyc1\"", createReq.Body);

        // Second request: assign project
        var assignReq = _handler.RecordedRequests[1];
        Assert.Equal(HttpMethod.Post, assignReq.Method);
        Assert.Contains("projects/project-uuid-9876/resources", assignReq.Uri);
        Assert.Contains("\"do:droplet:6002\"", assignReq.Body);
    }

    [Fact]
    public async Task GetServerStatusAsync_WhenActive_ReturnsRunningStatus()
    {
        // Arrange
        const string statusResponse = """
        {
            "droplet": {
                "id": 7003,
                "name": "minicloud-active-droplet",
                "status": "active",
                "networks": {
                    "v4": [ { "ip_address": "159.203.11.12", "type": "public" } ]
                }
            }
        }
        """;
        _handler.QueueJsonResponse(statusResponse);
        var client = CreateClient();

        // Act
        var status = await client.GetServerStatusAsync("7003");

        // Assert
        Assert.Equal("7003", status.Id);
        Assert.Equal("active", status.Status);
        Assert.Equal("159.203.11.12", status.Ipv4Address);
        Assert.True(status.IsRunning);
    }

    [Fact]
    public async Task GetServerStatusAsync_When404_ReturnsNotFoundCleanly()
    {
        // Arrange
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient();

        // Act
        var status = await client.GetServerStatusAsync("99999");

        // Assert
        Assert.Equal("99999", status.Id);
        Assert.Equal("not_found", status.Status);
        Assert.False(status.IsRunning);
    }

    [Fact]
    public async Task DeleteServerAsync_When404_CompletesWithoutError()
    {
        // Arrange
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient();

        // Act & Assert (should not throw)
        await client.DeleteServerAsync("missing-droplet");
    }

    [Fact]
    public async Task ApiError_ThrowsProviderApiException_WithRedactedToken()
    {
        // Arrange
        _handler.QueueJsonResponse("""{"id": "forbidden", "message": "Invalid token dop_v1_secret_token_abcdef123456"}""", HttpStatusCode.Forbidden);
        var client = CreateClient();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("test"));
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.DoesNotContain("dop_v1_secret_token_abcdef123456", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }
}
