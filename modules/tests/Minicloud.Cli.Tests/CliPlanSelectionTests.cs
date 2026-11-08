// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class CliPlanSelectionTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Hetzner secondary small", null, "Hetzner secondary small")]
    [InlineData("Hetzner secondary small", "Produção econômica", "Produção econômica")]
    public async Task Deploy_selects_named_plan_after_environment_and_before_submission(string? yamlPlan, string? flag, string? expected)
    {
        var directory = Directory.CreateTempSubdirectory("plan-selection-");
        try
        {
            var path = Path.Combine(directory.FullName, "minicloud.yml");
            var config = Config() with { Plan = yamlPlan };
            await File.WriteAllTextAsync(path, MinicloudConfigWriter.Write(config));
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment); tokens.SaveToken("mc_test");
            var handler = new Handler();
            var application = new CliApplication(new Console(), environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
            var args = new List<string> { "deploy", "web", "--no-publish", "--config", path, "--environment", "staging" };
            if (flag is not null) args.AddRange(["--plan", flag]);
            Assert.Equal(CliExitCodes.Success, await application.RunAsync(args.ToArray(), default));
            Assert.Equal(expected, handler.SelectedPlan);
            Assert.Equal(expected, handler.SubmittedPlan);
            Assert.Equal("app_staging", handler.SubmittedApp);
            if (expected is not null)
            {
                Assert.Equal("app_staging", handler.SelectionApp);
                Assert.True(handler.Paths.IndexOf("/v1/apps/app_staging/plan") < handler.Paths.IndexOf("/v1/deployments"));
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public void Config_round_trips_named_plan_and_preserves_it_when_filtering_services()
    {
        var config = Config() with { Plan = "Produção econômica #2" };
        var loaded = MinicloudConfigLoader.Parse(MinicloudConfigWriter.Write(config).Split('\n'));
        Assert.True(loaded.IsValid);
        Assert.Equal(config.Plan, loaded.Config!.Plan);
        Assert.Equal(config.Plan, CliApplication.FilterConfigServices(loaded.Config, ["web"]).Plan);
        Assert.Equal(["web"], CliApplication.DeployServiceNamesFromArgs(["web", "--plan", "large Hetzner", "--environment", "staging"]));
    }

    [Fact]
    public async Task Invalid_plan_stops_before_source_preparation_upload_and_deployment()
    {
        var directory = Directory.CreateTempSubdirectory("invalid-plan-");
        try
        {
            var path = Path.Combine(directory.FullName, "minicloud.yml");
            var config = Config() with { Plan = "missing", Services = new Dictionary<string, MinicloudServiceConfig>
                { ["web"] = new(Path.Combine(directory.FullName, "not-read"), "Dockerfile", null, 8080, true, "/", "/health", BuildMode: "source") } };
            await File.WriteAllTextAsync(path, MinicloudConfigWriter.Write(config));
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment); tokens.SaveToken("mc_test");
            var handler = new Handler();
            var application = new CliApplication(new Console(), environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
            Assert.Equal(CliExitCodes.ValidationError, await application.RunAsync(["deploy", "web", "--environment", "staging", "--config", path], default));
            Assert.Null(handler.SubmittedApp);
            Assert.DoesNotContain(handler.Paths, x => x.Contains("artifact", StringComparison.Ordinal) || x.StartsWith("/v1/deployments", StringComparison.Ordinal));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("[production]")]
    [InlineData("*secondary")]
    [InlineData("{small}")]
    [InlineData("true")]
    [InlineData("#small")]
    public void Config_writer_quotes_arbitrary_plan_names(string name)
    {
        var loaded = MinicloudConfigLoader.Parse(MinicloudConfigWriter.Write(Config() with { Plan = name }).Split('\n'));
        Assert.True(loaded.IsValid);
        Assert.Equal(name, loaded.Config!.Plan);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    public void Config_rejects_invalid_explicit_plan_names(string plan) =>
        Assert.Contains(MinicloudConfigValidator.Validate(Config() with { Plan = plan }), x => x.Field == "plan");

    private static MinicloudConfig Config() => new("demo", "sqlite", null,
        new Dictionary<string, MinicloudServiceConfig> { ["web"] = new(null, null, "example/web:latest", 8080, true, "/", "/health") }) { AppId = "app_main" };

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths = [];
        public string? SelectedPlan, SelectionApp, SubmittedPlan, SubmittedApp;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath; Paths.Add(path);
            object response;
            if (path == "/v1/me") response = new { id = "user", email = "test@example.com", organizations = new[] { new { id = "org", name = "Test", slug = "test", role = "owner" } } };
            else if (path.StartsWith("/v1/apps/"))
            {
                var id = path.Split('/')[3];
                if (path.EndsWith("/plan"))
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    SelectedPlan = body.RootElement.GetProperty("plan").GetString(); SelectionApp = id;
                    if (SelectedPlan == "missing")
                        return new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { code = "plan_not_found", message = "Unknown plan." } }) };
                }
                response = new { id, organizationId = "org", name = "Demo", slug = "demo", plan = SelectedPlan ?? "saved-plan", database = "sqlite", branches = Array.Empty<object>(),
                    environments = new[] { new { id = "app_main", name = "production", plan = "saved-plan", database = "sqlite" }, new { id = "app_staging", name = "staging", plan = "saved-plan", database = "sqlite" } } };
            }
            else if (path == "/v1/secrets") response = Array.Empty<object>();
            else if (path == "/v1/deployments")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                SubmittedApp = body.RootElement.GetProperty("appId").GetString();
                SubmittedPlan = body.RootElement.GetProperty("plan").GetString();
                response = new { id = "dep_plan", appId = SubmittedApp, status = "queued" };
            }
            else if (path.StartsWith("/v1/deployments/dep_plan"))
                response = path.EndsWith("/logs") ? Array.Empty<object>() : new { id = "dep_plan", appId = SubmittedApp, organizationId = "org", database = "sqlite", status = "succeeded", services = Array.Empty<object>() };
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
