using System.Net;
using System.Text;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Models;
using Minicloud.Common.Providers.Vultr;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public class VultrClientTests
{
    private readonly TestHttpMessageHandler _handler = new();
    private readonly VultrOptions _options = new()
    {
        ApiKey = "fixture",
        BaseUrl = "https://api.vultr.com/v2/",
        DefaultOsId = 2284
    };

    private VultrClient CreateClient()
    {
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri(_options.BaseUrl)
        };
        return new VultrClient(httpClient, _options);
    }

    [Fact]
    public async Task FindServerAsync_WhenInstanceExists_ReturnsServerInfo()
    {
        // Arrange
        const string json = """
        {
            "instances": [
                {
                    "id": "vultr-uuid-1",
                    "label": "minicloud-vultr-web",
                    "status": "active",
                    "power_status": "running",
                    "main_ip": "45.76.1.2",
                    "v6_main_ip": "2001:19f0:5:21c3::1",
                    "tags": ["minicloud"]
                }
            ]
        }
        """;
        _handler.QueueJsonResponse(json);
        var client = CreateClient();

        // Act
        var result = await client.FindServerAsync("minicloud-vultr-web");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("vultr-uuid-1", result.Id);
        Assert.Equal("minicloud-vultr-web", result.Name);
        Assert.Equal("45.76.1.2", result.Ipv4Address);
        Assert.Equal("active", result.Status);
        Assert.Equal("2001:19f0:5:21c3::1", result.Ipv6Address);

        var req = Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("instances?label=minicloud-vultr-web", req.Uri);
        Assert.Equal("Bearer fixture", req.AuthorizationHeader);
    }

    [Fact]
    public async Task CreateServerAsync_Base64EncodesUserData_AndParsesResponse()
    {
        // Arrange
        const string createResponse = """
        {
            "instance": {
                "id": "vultr-new-uuid-2",
                "label": "minicloud-vultr-srv",
                "main_ip": "45.76.5.6",
                "status": "pending",
                "power_status": "running"
            }
        }
        """;
        _handler.QueueJsonResponse(createResponse, HttpStatusCode.Accepted);
        var client = CreateClient();

        const string rawUserData = "#cloud-config\npackage_update: true";
        var expectedBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawUserData));

        var request = new CreateServerRequest(
            Name: "minicloud-vultr-srv",
            Plan: "vc2-1c-1gb",
            Region: "ewr",
            SshKeyIdOrPublicKey: "vultr-ssh-key",
            UserDataCloudInit: rawUserData,
            Hostname: "vultr.app.muni.dev");

        // Act
        var result = await client.CreateServerAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("vultr-new-uuid-2", result.Id);
        Assert.Equal("minicloud-vultr-srv", result.Name);
        Assert.Equal("45.76.5.6", result.Ipv4Address);
        Assert.Equal("pending", result.Status);

        var req = Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("instances", req.Uri);
        Assert.Contains($"\"user_data\":\"{expectedBase64}\"", req.Body);
        Assert.Contains("\"os_id\":2284", req.Body);
        Assert.Contains("\"hostname\":\"vultr.app.muni.dev\"", req.Body);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2284)]
    public async Task CreateServerAsync_UsesPerRequestOsIdWithoutCatalogLookupOrChangingAccountDefault(int? defaultOsId)
    {
        _handler.QueueJsonResponse("""{"instance":{"id":"mapped-os","label":"mapped-os","status":"pending"}}""", HttpStatusCode.Accepted);
        _options.DefaultOsId = defaultOsId;
        var request = new CreateServerRequest("mapped-os", "vc2-1c-1gb", "ewr", "", "", Image: "1743");

        await CreateClient().CreateServerAsync(request);

        var sent = Assert.Single(_handler.RecordedRequests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Contains("\"os_id\":1743", sent.Body);
        Assert.Equal(defaultOsId, _options.DefaultOsId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("ubuntu-24.04")]
    [InlineData("2147483648")]
    [InlineData(" 2284")]
    [InlineData("2284.0")]
    public async Task CreateServerAsync_InvalidPerRequestOsIdFailsBeforeAnyNetworkCall(string image)
    {
        _options.DefaultOsId = null;
        var request = new CreateServerRequest("invalid-os", "vc2-1c-1gb", "ewr", "", "", Image: image);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateClient().CreateServerAsync(request));

        Assert.Empty(_handler.RecordedRequests);
    }

    [Fact]
    public async Task GetServerStatusAsync_WhenActiveAndRunning_ReturnsIsRunningTrue()
    {
        // Arrange
        const string json = """
        {
            "instance": {
                "id": "vultr-uuid-3",
                "label": "vultr-active",
                "status": "active",
                "power_status": "running",
                "main_ip": "45.76.10.11"
            }
        }
        """;
        _handler.QueueJsonResponse(json);
        var client = CreateClient();

        // Act
        var status = await client.GetServerStatusAsync("vultr-uuid-3");

        // Assert
        Assert.Equal("vultr-uuid-3", status.Id);
        Assert.Equal("active", status.Status);
        Assert.Equal("45.76.10.11", status.Ipv4Address);
        Assert.True(status.IsRunning);
    }

    [Fact]
    public async Task GetServerStatusAsync_WhenPendingOr0Ip_ReturnsIsRunningFalseAndNullIp()
    {
        // Arrange
        const string json = """
        {
            "instance": {
                "id": "vultr-uuid-4",
                "status": "pending",
                "power_status": "running",
                "main_ip": "0.0.0.0"
            }
        }
        """;
        _handler.QueueJsonResponse(json);
        var client = CreateClient();

        // Act
        var status = await client.GetServerStatusAsync("vultr-uuid-4");

        // Assert
        Assert.Equal("vultr-uuid-4", status.Id);
        Assert.False(status.IsRunning);
        Assert.Null(status.Ipv4Address);
    }

    [Fact]
    public async Task GetServerStatusAsync_When404_ReturnsNotFoundCleanly()
    {
        // Arrange
        _handler.QueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient();

        // Act
        var status = await client.GetServerStatusAsync("missing-id");

        // Assert
        Assert.Equal("missing-id", status.Id);
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
        await client.DeleteServerAsync("missing-id");
    }

    [Fact]
    public async Task ApiError_ThrowsProviderApiException_WithRedactedApiKey()
    {
        // Arrange
        _handler.QueueJsonResponse("""{"error": "Unauthorized key fixture"}""", HttpStatusCode.Unauthorized);
        var client = CreateClient();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() => client.FindServerAsync("test"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("fixture", ex.Message);
        Assert.Equal("Vultr API request failed with HTTP 401.", ex.Message);
        Assert.DoesNotContain("Unauthorized key", ex.Message);
        Assert.NotNull(ex.ResponseBody);
        Assert.DoesNotContain("fixture", ex.ResponseBody);
        Assert.Contains("[REDACTED]", ex.ResponseBody);
    }
}
