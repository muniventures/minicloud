// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliEnvironmentDeploymentTests
{
    [Theory]
    [InlineData(false, null, "app_main", 0)]
    [InlineData(true, null, null, 2)]
    [InlineData(true, "unknown", null, 2)]
    [InlineData(true, "staging", "app_staging", 0)]
    [InlineData(true, "STAGING", "app_staging", 0)]
    [InlineData(true, "prod", "app_main", 0)]
    [InlineData(true, "failed", null, 2)]
    public async Task Deploy_selects_environment_explicitly_without_dashboard_default(bool multiple, string? selection, string? target, int expected)
    {
        await using var fixture = await Fixture.CreateAsync(new Handler(multiple));
        var args = new List<string> { "deploy", "web", "--no-publish", "--config", fixture.Config };
        if (selection is not null) args.AddRange(["--environment", selection]);

        Assert.Equal(expected, await fixture.Cli.RunAsync(args.ToArray(), default));

        Assert.Equal(target, fixture.Handler.SubmittedEnvironment);
        Assert.All(fixture.Handler.SecretTargets, id => Assert.Equal(target, id));
        if (target is null) Assert.Empty(fixture.Handler.SecretTargets);
        else Assert.Equal("application_demo", fixture.Handler.SubmittedApplication);
        Assert.Contains("/api/apps/application_demo", fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("app_deleted")]
    [InlineData("app_staging")]
    public async Task Runtime_ids_are_not_retried_as_application_aliases(string id)
    {
        await using var fixture = await Fixture.CreateAsync(new Handler(false), id);

        Assert.NotEqual(CliExitCodes.Success, await fixture.Cli.RunAsync(["deploy", "web", "--no-publish", "--config", fixture.Config], default));
        Assert.Equal(new[] { "/api/me", $"/api/apps/{id}" }, fixture.Handler.Requests);
        Assert.Null(fixture.Handler.SubmittedEnvironment);
    }

    [Theory]
    [InlineData("list", 0)]
    [InlineData("deploy", 2)]
    [InlineData("create", 0)]
    [InlineData("branch", 2)]
    [InlineData("plans", 0)]
    public async Task Empty_application_can_be_discovered_and_create_an_environment(string action, int expectedExit)
    {
        await using var fixture = await Fixture.CreateAsync(new Handler(false, empty: true));
        string[] args = action switch
        {
            "list" => ["apps", "list"],
            "plans" => ["plans", "list", "--config", fixture.Config],
            "create" => ["environments", "create", "staging", "--plan", "Small", "--config", fixture.Config],
            "branch" => ["deploy", "branch", "--no-publish", "--config", fixture.Config],
            _ => ["deploy", "web", "--no-publish", "--config", fixture.Config]
        };
        Assert.Equal(expectedExit, await fixture.Cli.RunAsync(args, default));
        if (action == "list") Assert.Contains("application_demo", fixture.Console.Output);
        if (action is "deploy" or "branch") Assert.Contains("no persistent environments", fixture.Console.Output);
        if (action == "plans") Assert.Contains("No plans configured", fixture.Console.Output);
        Assert.Equal(action == "create", fixture.Handler.Created);
    }

    private sealed class Fixture(DirectoryInfo directory, string config, Handler handler, Console console, CliApplication cli) : IAsyncDisposable
    {
        public string Config { get; } = config;
        public Handler Handler { get; } = handler;
        public Console Console { get; } = console;
        public CliApplication Cli { get; } = cli;
        public static async Task<Fixture> CreateAsync(Handler handler, string applicationId = "application_demo")
        {
            var directory = Directory.CreateTempSubdirectory("environment-deployment-");
            var config = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(config, $"app: demo\nappId: {applicationId}\ndatabase: sqlite\nservices:\n  web:\n    image: example/web:latest\n    port: 8080\n    public: true\n    path: /\n    healthPath: /health\n");
            var console = new Console();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment); tokens.SaveToken("mc_test");
            return new Fixture(directory, config, handler, console,
                new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler))));
        }
        public ValueTask DisposeAsync() { directory.Delete(true); return ValueTask.CompletedTask; }
    }

    private sealed class Handler(bool multiple, bool empty = false) : HttpMessageHandler
    {
        public string? SubmittedApplication, SubmittedEnvironment;
        public bool Created;
        public List<string> Requests { get; } = [];
        public List<string> SecretTargets { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            if (path is "/api/apps/app_deleted" or "/api/apps/app_staging") return new(HttpStatusCode.NotFound);
            var environments = new List<object>();
            if (!empty)
            {
                environments.Add(CliApiFixtures.EnvironmentSummary("app_main", "prod"));
                if (multiple)
                {
                    environments.Add(CliApiFixtures.EnvironmentSummary("app_staging", "staging"));
                    environments.Add(CliApiFixtures.EnvironmentSummary("app_failed", "failed", deletionStatus: "failed"));
                }
            }
            var application = new { id = "application_demo", organizationId = "org", name = "Demo", slug = "demo", defaultEnvironmentId = "app_staging", environments };
            object response;
            if (path == "/api/me") response = new { id = "user", email = "test@example.com", organizations = new[] { new { id = "org", name = "Test", slug = "test", role = "owner" } } };
            else if (path == "/api/apps") response = new[] { application };
            else if (path == "/api/apps/application_demo") response = application;
            else if (path.StartsWith("/api/environments/"))
                response = new { id = path.Split('/')[3], applicationId = "application_demo", organizationId = "org", name = "Environment", slug = "environment", plan = "p0", database = "sqlite", branches = Array.Empty<object>(), domains = Array.Empty<object>() };
            else if (path == "/api/apps/application_demo/environments" && request.Method == HttpMethod.Post)
            {
                Created = true;
                response = new { id = "app_staging", applicationId = "application_demo", organizationId = "org", name = "staging", kind = "persistent", plan = "Small", database = "none" };
            }
            else if (path == "/api/plans") response = Array.Empty<object>();
            else if (path == "/api/secrets")
            {
                SecretTargets.Add(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["appId"]!);
                response = Array.Empty<object>();
            }
            else if (path == "/api/deployments")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                SubmittedApplication = json.RootElement.GetProperty("applicationId").GetString();
                SubmittedEnvironment = json.RootElement.GetProperty("environmentId").GetString();
                Assert.Equal(SubmittedEnvironment, json.RootElement.GetProperty("appId").GetString());
                response = new { id = "dep_env", number = "20261110.2", appId = SubmittedEnvironment, status = "queued" };
            }
            else if (path.StartsWith("/api/deployments/20261110.2"))
                response = path.EndsWith("/logs") ? Array.Empty<object>() : new { id = "dep_env", number = "20261110.2", appId = SubmittedEnvironment, organizationId = "org", database = "sqlite", status = "succeeded", services = Array.Empty<object>() };
            else throw new InvalidOperationException(request.Method + " " + path);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }

    private sealed class Console : IConsole
    {
        public string Output = "";
        public bool SupportsAnsi => false;
        public void Write(string message) { Output += message; }
        public void WriteLine(string message = "") { Output += message + "\n"; }
        public void WriteError(string message) { Output += message + "\n"; }
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
