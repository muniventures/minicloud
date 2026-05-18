using System.Net;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;

namespace Minicloud.Cli.Tests;

public sealed class CliDomainApiClientTests
{
    [Fact]
    public async Task Domain_methods_use_expected_api_paths()
    {
        var previousToken = Environment.GetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable);
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-cli-domain-test-");
        try
        {
            Environment.SetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable, "mc_test");
            var handler = new CaptureHandler();
            var environment = CliEnvironment.ForTests("https://api.example", tempDirectory.FullName);
            var client = new MinicloudApiClient(environment, new TokenStore(environment), new HttpClient(handler));

            await client.GetDomainsAsync("app_123", CancellationToken.None);
            await client.CreateDomainAsync("app_123", new CreateDomainBindingRequest("api", "org-app-api"), CancellationToken.None);
            await client.UpdateDomainAsync("app_123", "dom_123", new UpdateDomainBindingRequest(true), CancellationToken.None);
            await client.DeleteDomainAsync("app_123", "dom_123", CancellationToken.None);

            Assert.Equal([
                "GET /v1/apps/app_123/domains",
                "POST /v1/apps/app_123/domains",
                "PATCH /v1/apps/app_123/domains/dom_123",
                "DELETE /v1/apps/app_123/domains/dom_123"
            ], handler.Requests.Select(x => $"{x.Method} {x.Path}").ToArray());
            Assert.All(handler.Requests, request => Assert.Equal("Bearer mc_test", request.Authorization));
            Assert.Contains(handler.Requests, request => request.Body?.Contains("\"serviceName\":\"api\"", StringComparison.Ordinal) == true);
            Assert.Contains(handler.Requests, request => request.Body?.Contains("\"disabled\":true", StringComparison.Ordinal) == true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable, previousToken);
            tempDirectory.Delete(recursive: true);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<RequestCapture> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestCapture(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var payload = request.Method == HttpMethod.Get
                ? """[{"id":"dom_123","appId":"app_123","serviceName":"api","hostname":"org-app-api.app.muni.dev","kind":"minicloud_subdomain","pathPrefix":"/","dnsStatus":"verified","applyStatus":"pending","sslStatus":"pending","status":"pending_apply","createdAt":"2026-05-17T12:00:00Z","updatedAt":"2026-05-17T12:00:00Z"}]"""
                : """{"id":"dom_123","appId":"app_123","serviceName":"api","hostname":"org-app-api.app.muni.dev","kind":"minicloud_subdomain","pathPrefix":"/","dnsStatus":"verified","applyStatus":"pending","sslStatus":"pending","status":"pending_apply","createdAt":"2026-05-17T12:00:00Z","updatedAt":"2026-05-17T12:00:00Z"}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RequestCapture(string Method, string Path, string? Authorization, string? Body);
}
