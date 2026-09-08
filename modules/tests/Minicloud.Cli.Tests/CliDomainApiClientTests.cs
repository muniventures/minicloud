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

            await client.GetAppServicesAsync("app_123", CancellationToken.None);
            await client.GetDomainsAsync("app_123", CancellationToken.None);
            await client.CreateDomainAsync("app_123", new CreateDomainBindingRequest("api", "org-app-api"), CancellationToken.None);
            await client.UpdateDomainAsync("app_123", "dom_123", new UpdateDomainBindingRequest(true), CancellationToken.None);
            await client.DeleteDomainAsync("app_123", "dom_123", CancellationToken.None);

            Assert.Equal([
                "GET /v1/domains/services?appId=app_123",
                "GET /v1/domains?appId=app_123",
                "POST /v1/domains?appId=app_123",
                "PATCH /v1/domains/dom_123?appId=app_123",
                "DELETE /v1/domains/dom_123?appId=app_123"
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

    [Fact]
    public async Task Artifact_methods_use_expected_api_paths_and_metadata()
    {
        var previousToken = Environment.GetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable);
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-cli-artifact-test-");
        try
        {
            Environment.SetEnvironmentVariable(CliEnvironment.TokenEnvironmentVariable, "mc_test");
            var artifactPath = Path.Combine(tempDirectory.FullName, "artifact.zip");
            await File.WriteAllTextAsync(artifactPath, "zip bytes");
            var handler = new CaptureHandler();
            var environment = CliEnvironment.ForTests("https://api.example", tempDirectory.FullName);
            var client = new MinicloudApiClient(environment, new TokenStore(environment), new HttpClient(handler));
            var manifest = new DeploymentArtifactManifest(1, "app_123", "api", "modules/api", null, ".", 8080, true, "/", "/health", "abc123", 1, 9, DateTimeOffset.Parse("2026-06-07T12:00:00Z"));

            var created = await client.CreateDeploymentArtifactAsync(
                new CreateDeploymentArtifactRequest("app_123", "api", "api.zip", "application/zip", 9, "abc", manifest),
                CancellationToken.None);
            await client.UploadDeploymentArtifactContentAsync(created.Id, created.UploadUrl, artifactPath, "abc", 9, CancellationToken.None);

            Assert.Equal([
                "POST /v1/artifacts",
                "PUT /v1/artifacts/art_123/file"
            ], handler.Requests.Select(x => $"{x.Method} {x.Path}").ToArray());
            Assert.All(handler.Requests, request => Assert.Equal("Bearer mc_test", request.Authorization));
            Assert.Contains(handler.Requests, request => request.Body?.Contains("\"serviceName\":\"api\"", StringComparison.Ordinal) == true);
            Assert.Contains(handler.Requests, request => request.Sha256 == "abc" && request.Size == "9");
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
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("X-Minicloud-Artifact-Sha256", out var shaValues) ? shaValues.FirstOrDefault() : null,
                request.Headers.TryGetValues("X-Minicloud-Artifact-Size", out var sizeValues) ? sizeValues.FirstOrDefault() : null));

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            var payload = request.RequestUri!.AbsolutePath == "/v1/artifacts"
                ? """{"id":"art_123","appId":"app_123","serviceName":"api","status":"uploading","uploadUrl":"/v1/artifacts/art_123/file","sizeBytes":9,"sha256":"abc"}"""
                : request.RequestUri!.AbsolutePath.EndsWith("/file", StringComparison.Ordinal)
                ? """{"id":"art_123","appId":"app_123","serviceName":"api","status":"ready","sizeBytes":9,"sha256":"abc","createdAt":"2026-06-07T12:00:00Z"}"""
                : request.RequestUri!.AbsolutePath.EndsWith("/services", StringComparison.Ordinal)
                ? """[{"name":"api","image":"ghcr.io/acme/demo/api:abc123","port":8080,"public":true,"path":"/","healthPath":"/health","runtime":{"name":"api","container":"demo-api-1","state":"running","health":"healthy","image":"ghcr.io/acme/demo/api:abc123","restartCount":0},"domains":[{"id":"dom_123","appId":"app_123","serviceName":"api","hostname":"org-app-api.app.muni.dev","kind":"minicloud_subdomain","pathPrefix":"/","dnsStatus":"verified","applyStatus":"pending","sslStatus":"pending","status":"pending_apply","createdAt":"2026-05-17T12:00:00Z","updatedAt":"2026-05-17T12:00:00Z"}]}]"""
                : request.Method == HttpMethod.Get
                ? """[{"id":"dom_123","appId":"app_123","serviceName":"api","hostname":"org-app-api.app.muni.dev","kind":"minicloud_subdomain","pathPrefix":"/","dnsStatus":"verified","applyStatus":"pending","sslStatus":"pending","status":"pending_apply","createdAt":"2026-05-17T12:00:00Z","updatedAt":"2026-05-17T12:00:00Z"}]"""
                : """{"id":"dom_123","appId":"app_123","serviceName":"api","hostname":"org-app-api.app.muni.dev","kind":"minicloud_subdomain","pathPrefix":"/","dnsStatus":"verified","applyStatus":"pending","sslStatus":"pending","status":"pending_apply","createdAt":"2026-05-17T12:00:00Z","updatedAt":"2026-05-17T12:00:00Z"}""";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record RequestCapture(string Method, string Path, string? Authorization, string? Body, string? Sha256, string? Size);
}
