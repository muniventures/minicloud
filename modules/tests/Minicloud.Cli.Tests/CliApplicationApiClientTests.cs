// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Common.Contracts;

namespace Minicloud.Tests;

public sealed class CliApplicationApiClientTests
{
    [Fact]
    public async Task Application_and_environment_use_distinct_canonical_endpoints()
    {
        await using var fixture = new Fixture();
        await fixture.Client.GetApplicationAsync("application_one", default);
        await fixture.Client.GetEnvironmentAsync("environment_one", default);
        Assert.Equal(new[] { "GET /api/apps/application_one", "GET /api/environments/environment_one" }, fixture.Handler.Requests);
    }

    [Fact]
    public async Task Application_creation_nests_initial_environment_configuration()
    {
        await using var fixture = new Fixture();
        await fixture.Client.CreateAppAsync(new CreateApplicationRequest("org", "Project", "project",
            new CreateInitialEnvironmentRequest("production", "Small", "postgres")), default);

        Assert.Equal(new[] { "POST /api/apps" }, fixture.Handler.Requests);
        using var json = JsonDocument.Parse(Assert.Single(fixture.Handler.Bodies));
        Assert.Equal("org", json.RootElement.GetProperty("organizationId").GetString());
        var initial = json.RootElement.GetProperty("initialEnvironment");
        Assert.Equal("production", initial.GetProperty("name").GetString());
        Assert.Equal("Small", initial.GetProperty("plan").GetString());
        Assert.Equal("postgres", initial.GetProperty("database").GetString());
        Assert.False(json.RootElement.TryGetProperty("plan", out _));
        Assert.False(json.RootElement.TryGetProperty("database", out _));
    }

    [Fact]
    public async Task Missing_application_is_returned_without_identity_or_route_fallback()
    {
        await using var fixture = new Fixture(HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<ApiException>(() => fixture.Client.GetApplicationAsync("missing", default));
        Assert.Equal(new[] { "GET /api/apps/missing" }, fixture.Handler.Requests);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("canonical-api-");
        private readonly HttpClient _http;
        public Handler Handler { get; }
        public MinicloudApiClient Client { get; }
        public Fixture(HttpStatusCode status = HttpStatusCode.OK)
        {
            var environment = CliEnvironment.ForTests("https://api.example", _directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("test");
            Handler = new Handler(status);
            _http = new HttpClient(Handler);
            Client = new MinicloudApiClient(environment, tokens, _http);
        }
        public ValueTask DisposeAsync() { _http.Dispose(); _directory.Delete(true); return ValueTask.CompletedTask; }
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add($"{request.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK
                    ? """{"id":"canonical","organizationId":"org","name":"Project","slug":"project","domains":[],"branches":[],"environments":[]}"""
                    : """{"error":{"code":"app_not_found","message":"Not found"}}""")
            };
        }
    }
}
