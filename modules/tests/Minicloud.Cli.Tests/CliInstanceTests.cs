// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;
using Minicloud.Common.Contracts;

namespace Minicloud.Tests;

public sealed class CliInstanceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("minicloud-instances-").FullName;
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("cloud.example.com", "https://cloud.example.com", "https://api.cloud.example.com")]
    [InlineData("https://CLOUD.example.com/", "https://cloud.example.com", "https://api.cloud.example.com")]
    [InlineData("http://localhost:3210", "http://localhost:3210", "http://api.localhost:3210")]
    public void Main_service_url_derives_api(string input, string expected, string api)
    {
        Assert.Equal(expected, InstanceStore.NormalizeUrl(input));
        Assert.Equal(api, InstanceStore.DeriveApiUrl(input));
    }

    [Theory]
    [InlineData("https://cloud.example.com/path")]
    [InlineData("https://cloud.example.com?token=secret")]
    [InlineData("https://cloud.example.com/#fragment")]
    [InlineData("https://user:password@cloud.example.com")]
    [InlineData("ftp://cloud.example.com")]
    [InlineData("http://cloud.example.com")]
    [InlineData("")]
    public void Reject_invalid_instance_urls(string input) =>
        Assert.Throws<CliCommandException>(() => InstanceStore.NormalizeUrl(input));

    [Fact]
    public async Task First_use_prompts_validates_and_persists_before_login()
    {
        var (app, console, env, handler) = Create();
        console.Input.Enqueue("cloud.example.com");
        Assert.Equal(0, await app.RunAsync(["login", "--token", "first-token"], default));
        Assert.Contains("Minicloud instance URL", console.Output);
        Assert.Equal("https://api.cloud.example.com/v1/instance", handler.Requests[0].Url);
        Assert.Null(handler.Requests[0].Token);
        Assert.Equal("first-token", handler.Requests[1].Token);
        Assert.Equal("apps.example.com", env.AppDomain);
        var reloaded = CliEnvironment.ForTests("", _directory);
        reloaded.LoadActiveInstance();
        Assert.Equal("https://api.cloud.example.com", reloaded.ApiBaseUrl);
        Assert.Equal("first-token", File.ReadAllText(reloaded.TokenFilePath).Trim());
    }

    [Fact]
    public async Task Switching_instances_preserves_independent_tokens_and_active_selection()
    {
        var (app, console, env, handler) = Create();
        Assert.Equal(0, await app.RunAsync(["instance", "add", "one", "cloud.one.example"], default));
        Assert.Equal(0, await app.RunAsync(["login", "--token", "one-token"], default));
        var firstPath = env.TokenFilePath;
        Assert.Equal(0, await app.RunAsync(["instance", "add", "two", "cloud.two.example"], default));
        Assert.False(File.Exists(env.TokenFilePath));
        Assert.Equal(0, await app.RunAsync(["login", "--token", "two-token"], default));
        Assert.NotEqual(firstPath, env.TokenFilePath);
        Assert.Equal(0, await app.RunAsync(["instance", "use", "one"], default));
        Assert.Equal(firstPath, env.TokenFilePath);
        Assert.Equal("one-token", File.ReadAllText(env.TokenFilePath).Trim());
        Assert.Equal("one", env.Instances.Load().Active);
        Assert.Equal(0, await app.RunAsync(["instance", "list"], default));
        Assert.Contains("* one  https://cloud.one.example", console.Output);
        Assert.Contains("  two  https://cloud.two.example", console.Output);
        Assert.All(handler.Requests.Where(x => x.Url.EndsWith("/instance")), x => Assert.Null(x.Token));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>other service</html>")]
    [InlineData("{\"product\":\"other\",\"protocolVersion\":1,\"appDomain\":\"apps.example.com\"}")]
    [InlineData("{\"product\":\"minicloud\",\"protocolVersion\":2,\"appDomain\":\"apps.example.com\"}")]
    public async Task Invalid_discovery_does_not_replace_active_instance(string body)
    {
        var (app, _, env, handler) = Create();
        await app.RunAsync(["instance", "add", "good", "cloud.example.com"], default);
        handler.DiscoveryBody = body;
        Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["instance", "add", "bad", "wrong.example.com"], default));
        Assert.Equal("good", env.Instances.Load().Active);
        Assert.Single(env.Instances.Load().Instances);
    }

    [Fact]
    public async Task Unreachable_discovery_and_unknown_selection_leave_state_unchanged()
    {
        var (app, _, env, handler) = Create();
        await app.RunAsync(["instance", "add", "good", "cloud.example.com"], default);
        handler.Status = HttpStatusCode.NotFound;
        Assert.Equal(CliExitCodes.NetworkOrApiUnavailable, await app.RunAsync(["instance", "add", "bad", "wrong.example.com"], default));
        Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["instance", "use", "missing"], default));
        Assert.Equal("good", env.Instances.Load().Active);
    }

    [Fact]
    public async Task Metadata_and_listing_do_not_prompt_or_contact_network()
    {
        var (app, _, env, handler) = Create();
        foreach (var args in new[] { new[] { "--help" }, ["--version"], ["--env"], ["instance", "list"] })
            Assert.Equal(0, await app.RunAsync(args, default));
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(Path.Combine(_directory, "instances.json")));
        Assert.Equal("", env.ApiBaseUrl);
    }

    [Fact]
    public async Task No_input_returns_actionable_error_without_network()
    {
        var (app, console, _, handler) = Create();
        Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["login"], default));
        Assert.Contains("instance add", console.Error);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Instances\":null}")]
    [InlineData("{\"Active\":\"missing\",\"Instances\":[]}")]
    public async Task Invalid_saved_state_returns_a_configuration_error(string json)
    {
        var (app, console, _, handler) = Create();
        File.WriteAllText(Path.Combine(_directory, "instances.json"), json);
        Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["login"], default));
        Assert.Contains("Invalid instance configuration", console.Error);
        Assert.Empty(handler.Requests);
    }

    private (CliApplication, TestConsole, CliEnvironment, Handler) Create()
    {
        var env = CliEnvironment.ForTests("", _directory);
        var console = new TestConsole();
        var tokens = new TokenStore(env);
        var handler = new Handler();
        return (new CliApplication(console, env, tokens, new MinicloudApiClient(env, tokens, new HttpClient(handler))), console, env, handler);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<(string Url, string? Token)> Requests { get; } = [];
        public string? DiscoveryBody { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = request.RequestUri.AbsolutePath.EndsWith("/instance")
                    ? DiscoveryBody is null ? JsonContent.Create(new InstanceInfoResponse("minicloud", 1, "apps.example.com")) : new StringContent(DiscoveryBody)
                    : new StringContent("""{"id":"user","email":"operator@example.com","organizations":[]}""")
            });
        }
    }

    private sealed class TestConsole : IConsole
    {
        public Queue<string> Input { get; } = new();
        public string Output { get; private set; } = "";
        public string Error { get; private set; } = "";
        public bool SupportsAnsi => false;
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + "\n";
        public void WriteError(string message) => Error += message;
        public string? ReadLine() => Input.TryDequeue(out var input) ? input : null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new NotSupportedException();
    }
}
