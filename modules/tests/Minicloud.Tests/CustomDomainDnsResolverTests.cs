using System.Net;
using System.Text;
using Minicloud.Common.Providers.Dns;

namespace Minicloud.Tests;

public sealed class CustomDomainDnsResolverTests
{
    [Theory]
    [InlineData(1, "192.0.2.10")]
    [InlineData(28, "2001:db8::1")]
    [InlineData(16, "mc-domain-verification=token")]
    public async Task UsesFixedDnsEndpointAndFiltersCnameAnswers(int type, string value)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { Status = 0, Answer = new[] { new { type = 5, data = "alias.example.com" }, new { type, data = value } } });
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("https://cloudflare-dns.com/dns-query?name=example.com&type=" + type, request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/dns-json", Assert.Single(request.Headers.Accept).MediaType);
            return body;
        }));
        Assert.Equal(value, Assert.Single(await new CustomDomainDnsResolver(http).QueryAsync("example.com", type, default)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task MissingRecordsRemainUnverified(int status)
    {
        using var http = new HttpClient(new Handler(_ => "{\"Status\":" + status + "}"));
        Assert.Empty(await new CustomDomainDnsResolver(http).QueryAsync("example.com", 16, default));
    }

    [Fact]
    public async Task ResolverErrorsCannotBeTreatedAsValidEvidence()
    {
        using var http = new HttpClient(new Handler(_ => "{\"Status\":2}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CustomDomainDnsResolver(http).QueryAsync("example.com", 16, default));
    }

    private sealed class Handler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/dns-json") });
    }
}
