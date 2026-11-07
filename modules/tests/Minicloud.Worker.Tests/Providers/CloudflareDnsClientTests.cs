using System.Net;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.Dns;
using Xunit;

namespace Minicloud.Worker.Tests.Providers;

public class CloudflareDnsClientTests
{
    private readonly TestHttpMessageHandler _handler = new();
    private readonly CloudflareOptions _options = new()
    {
        ApiToken = "cf_token_secret_1234567890",
        ZoneId = "zone-abc-987",
        BaseUrl = "https://api.cloudflare.com/client/v4/",
        Ttl = 120,
        Proxied = false,
        PropagationPollInterval = TimeSpan.FromMilliseconds(5)
    };

    private CloudflareDnsClient CreateClient(IDnsResolver? dnsResolver = null)
    {
        var httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri(_options.BaseUrl)
        };
        return new CloudflareDnsClient(httpClient, _options, dnsResolver);
    }

    [Fact]
    public async Task DeleteDnsRecordsAsync_DeletesExactAAndAaaaRecords()
    {
        _handler.QueueJsonResponse("""{"result":[{"id":"ipv4","name":"test.app.muni.dev"}],"success":true}""");
        _handler.QueueJsonResponse("""{"success":true}""");
        _handler.QueueJsonResponse("""{"result":[{"id":"ipv6","name":"test.app.muni.dev"}],"success":true}""");
        _handler.QueueJsonResponse("""{"success":true}""");
        await CreateClient().DeleteDnsRecordsAsync("test.app.muni.dev");
        Assert.Equal(2, _handler.RecordedRequests.Count(x => x.Method == HttpMethod.Delete));
    }

    [Fact]
    public async Task DeleteDnsRecordsAsync_RejectsUnexpectedHostname()
    {
        _handler.QueueJsonResponse("""{"result":[{"id":"other","name":"other.app.muni.dev"}],"success":true}""");
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateClient().DeleteDnsRecordsAsync("test.app.muni.dev"));
        Assert.DoesNotContain(_handler.RecordedRequests, x => x.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task UpsertDnsRecordAsync_WhenRecordDoesNotExist_CreatesNewRecord()
    {
        // Arrange
        // GET returns empty result
        _handler.QueueJsonResponse("""{"result": [], "success": true}""");
        // POST creates record
        _handler.QueueJsonResponse("""{"result": {"id": "new-rec-1"}, "success": true}""", HttpStatusCode.Created);

        var client = CreateClient();

        // Act
        await client.UpsertDnsRecordAsync("app.example.com", "1.2.3.4");

        // Assert
        Assert.Equal(2, _handler.RecordedRequests.Count);

        // First request is GET
        var getReq = _handler.RecordedRequests[0];
        Assert.Equal(HttpMethod.Get, getReq.Method);
        Assert.Contains("zones/zone-abc-987/dns_records?type=A&name=app.example.com", getReq.Uri);
        Assert.Equal("Bearer cf_token_secret_1234567890", getReq.AuthorizationHeader);

        // Second request is POST
        var postReq = _handler.RecordedRequests[1];
        Assert.Equal(HttpMethod.Post, postReq.Method);
        Assert.Contains("zones/zone-abc-987/dns_records", postReq.Uri);
        Assert.Contains("\"type\":\"A\"", postReq.Body);
        Assert.Contains("\"name\":\"app.example.com\"", postReq.Body);
        Assert.Contains("\"content\":\"1.2.3.4\"", postReq.Body);
    }

    [Fact]
    public async Task UpsertDnsRecordAsync_WhenRecordExists_UpdatesExistingRecord()
    {
        // Arrange
        // GET returns existing record
        const string existingRecordJson = """
        {
            "result": [
                {
                    "id": "existing-rec-99",
                    "type": "A",
                    "name": "app.example.com",
                    "content": "5.6.7.8"
                }
            ],
            "success": true
        }
        """;
        _handler.QueueJsonResponse(existingRecordJson);
        // PUT updates record
        _handler.QueueJsonResponse("""{"result": {"id": "existing-rec-99"}, "success": true}""");

        var client = CreateClient();

        // Act
        await client.UpsertDnsRecordAsync("app.example.com", "1.2.3.4");

        // Assert
        Assert.Equal(2, _handler.RecordedRequests.Count);

        var putReq = _handler.RecordedRequests[1];
        Assert.Equal(HttpMethod.Put, putReq.Method);
        Assert.Contains("zones/zone-abc-987/dns_records/existing-rec-99", putReq.Uri);
        Assert.Contains("\"content\":\"1.2.3.4\"", putReq.Body);
    }

    [Fact]
    public async Task UpsertDnsRecordAsync_WhenIpv6_UsesAaaaRecordType()
    {
        // Arrange
        _handler.QueueJsonResponse("""{"result": [], "success": true}""");
        _handler.QueueJsonResponse("""{"result": {"id": "v6-rec"}, "success": true}""", HttpStatusCode.Created);

        var client = CreateClient();

        // Act
        await client.UpsertDnsRecordAsync("v6.example.com", "2001:db8::1");

        // Assert
        var postReq = _handler.RecordedRequests[1];
        Assert.Contains("\"type\":\"AAAA\"", postReq.Body);
        Assert.Contains("\"content\":\"2001:db8::1\"", postReq.Body);
    }

    [Fact]
    public async Task WaitForPropagationAsync_WhenResolverReturnsExpectedIp_Succeeds()
    {
        // Arrange
        var fakeResolver = new MockDnsResolver(new[]
        {
            new List<string> { "9.9.9.9" },
            new List<string> { "1.2.3.4" }
        });
        var client = CreateClient(fakeResolver);

        // Act
        await client.WaitForPropagationAsync("app.example.com", "1.2.3.4", TimeSpan.FromSeconds(2));

        // Assert
        Assert.Equal(2, fakeResolver.InvocationCount);
    }

    [Fact]
    public async Task WaitForPropagationAsync_WhenTimeoutExpires_ThrowsTimeoutException()
    {
        // Arrange
        var fakeResolver = new MockDnsResolver(new[]
        {
            new List<string> { "9.9.9.9" }
        });
        var client = CreateClient(fakeResolver);

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.WaitForPropagationAsync("app.example.com", "1.2.3.4", TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task ApiError_ThrowsProviderApiException_WithRedactedToken()
    {
        // Arrange
        _handler.QueueJsonResponse("""{"errors": [{"message": "Invalid token cf_token_secret_1234567890"}]}""", HttpStatusCode.Unauthorized);
        var client = CreateClient();

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ProviderApiException>(() =>
            client.UpsertDnsRecordAsync("app.example.com", "1.2.3.4"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("cf_token_secret_1234567890", ex.Message);
        Assert.Contains("[REDACTED]", ex.Message);
    }

    private sealed class MockDnsResolver : IDnsResolver
    {
        private readonly IEnumerator<IReadOnlyList<string>> _enumerator;
        private IReadOnlyList<string> _last = Array.Empty<string>();
        public int InvocationCount { get; private set; }

        public MockDnsResolver(IEnumerable<IReadOnlyList<string>> sequence)
        {
            _enumerator = sequence.GetEnumerator();
        }

        public Task<IReadOnlyList<string>> ResolveIpAddressesAsync(string hostname, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            if (_enumerator.MoveNext())
            {
                _last = _enumerator.Current;
            }
            return Task.FromResult(_last);
        }
    }
}
