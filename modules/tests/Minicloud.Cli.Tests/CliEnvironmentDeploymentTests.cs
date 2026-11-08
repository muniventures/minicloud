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
    public async Task Deploy_resolves_environment_before_submission(bool multiple, string? selection, string? target, int expected)
    {
        var directory = Directory.CreateTempSubdirectory("environment-deployment-");
        try
        {
            var config = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(config, "app: demo\nappId: app_main\ndatabase: sqlite\nservices:\n  web:\n    image: example/web:latest\n    port: 8080\n    public: true\n    path: /\n    healthPath: /health\n");
            var console = new Console();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment); tokens.SaveToken("mc_test");
            var handler = new Handler(multiple);
            var application = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
            var args = new List<string> { "deploy", "web", "--no-publish", "--config", config };
            if (selection is not null) args.AddRange(["--environment", selection]);
            Assert.Equal(expected, await application.RunAsync(args.ToArray(), default));
            Assert.Equal(target, handler.SubmittedApp);
            Assert.All(handler.SecretTargets, id => Assert.Equal(target, id));
            if (target is null) Assert.Empty(handler.SecretTargets);
        }
        finally { directory.Delete(true); }
    }

    private sealed class Handler(bool multiple) : HttpMessageHandler
    {
        public string? SubmittedApp;
        public List<string> SecretTargets = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object response;
            if (path == "/v1/me") response = new { id = "user", email = "test@example.com", organizations = new[] { new { id = "org", name = "Test", slug = "test", role = "owner" } } };
            else if (path.StartsWith("/v1/apps/"))
            {
                var id = path.Split('/').Last();
                var environments = new List<object> { new { id = "app_main", name = "prod", plan = "p0-h-0", database = "sqlite" } };
                if (multiple) environments.Add(new { id = "app_staging", name = "staging", plan = "p0-v-0", database = "sqlite" });
                response = new { id, organizationId = "org", name = "Demo", slug = "demo", plan = "p0-h-0", database = "sqlite", branches = Array.Empty<object>(), environments };
            }
            else if (path == "/v1/secrets")
            {
                SecretTargets.Add(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["appId"]!);
                response = Array.Empty<object>();
            }
            else if (path == "/v1/deployments")
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                SubmittedApp = json.RootElement.GetProperty("appId").GetString();
                response = new { id = "dep_env", appId = SubmittedApp, status = "queued" };
            }
            else if (path.StartsWith("/v1/deployments/dep_env"))
                response = path.EndsWith("/logs") ? Array.Empty<object>() : new { id = "dep_env", appId = SubmittedApp, organizationId = "org", database = "sqlite", status = "succeeded", services = Array.Empty<object>() };
            else throw new InvalidOperationException(request.Method + " " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) };
        }
    }
    private sealed class Console : IConsole
    {
        public bool SupportsAnsi => false;
        public void Write(string message) { }
        public void WriteLine(string message = "") { }
        public void WriteError(string message) { }
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
