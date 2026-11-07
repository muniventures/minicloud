// SPDX-License-Identifier: MPL-2.0

using System.Net;
using System.Net.Http.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliDeploymentSecretTests
{
    [Theory]
    [InlineData("missing", false)]
    [InlineData("other-service", false)]
    [InlineData("deleted", false)]
    [InlineData("app", true)]
    [InlineData("service", true)]
    [InlineData("local", true)]
    public async Task Deploy_checks_secret_names_before_upload_or_submission(string source, bool available)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-secret-validation-");
        try
        {
            var config = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(config, $$"""
                app: demo
                appId: app_test
                database: none
                services:
                  api:
                    buildMode: source
                    sourcePath: '{{directory.FullName}}'
                    image: example/api:latest
                    port: 8080
                    public: true
                    path: /
                    healthPath: /health
                    secretEnv:
                      Instagram__AppSecret: INSTAGRAM_APP_SECRET
                """);
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "Dockerfile"), "FROM alpine\n");
            if (source == "local")
            {
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, "minicloud.secrets.env"), "INSTAGRAM_APP_SECRET=sentinel-secret\n");
            }
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new SecretHandler(source);
            var app = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
            var result = await app.RunAsync(["deploy", "api", "--no-publish", "--config", config], default);
            Assert.Equal(available ? CliExitCodes.Success : CliExitCodes.ValidationError, result);
            Assert.Equal(available, handler.DeploymentCreated);
            Assert.DoesNotContain("sentinel-secret", console.Output);
            if (!available)
            {
                Assert.Contains("missing_service_secret", console.Output);
                Assert.Contains("INSTAGRAM_APP_SECRET", console.Output);
                Assert.Contains("Instagram__AppSecret", console.Output);
                Assert.DoesNotContain(handler.Requests, x => x.StartsWith("POST", StringComparison.Ordinal) && !x.EndsWith("/validate-secrets", StringComparison.Ordinal));
            }
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("deploy")]
    [InlineData("logs")]
    public async Task Commands_display_persisted_failure_code_and_message(string command)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-secret-error-");
        try
        {
            var config = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(config, "app: demo\nappId: app_test\ndatabase: none\nservices:\n  api:\n    image: example/api:latest\n    port: 8080\n    public: true\n    path: /\n    healthPath: /health\n");
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new SecretHandler("failure");
            var app = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
            var args = command is "status" or "logs" ? new[] { command, "dep_test" } : new[] { "deploy", "api", "--no-publish", "--config", config };
            var result = await app.RunAsync(args, default);
            Assert.Equal(command == "logs" ? CliExitCodes.Success : CliExitCodes.DeploymentFailed, result);
            Assert.Contains("missing_service_secret", console.Output);
            Assert.Contains("INSTAGRAM_APP_SECRET", console.Output);
        }
        finally { directory.Delete(true); }
    }

    private sealed class SecretHandler(string source) : HttpMessageHandler
    {
        public bool DeploymentCreated { get; private set; }
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            if (path == "/v1/deployments/validate-secrets")
            {
                Assert.DoesNotContain("sentinel-secret", request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                return Task.FromResult(source is "service" or "app"
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = new { code = "missing_service_secret", message = "Service 'api' references missing secret 'INSTAGRAM_APP_SECRET' for environment variable 'Instagram__AppSecret'.", details = Array.Empty<object>() } }) });
            }
            object response = path switch
            {
                "/v1/me" => new { id = "usr_test", email = "test@example.com", organizations = new[] { new { id = "org_test", name = "Test", slug = "test", role = "owner" } } },
                "/v1/apps/app_test" => new { id = "app_test", organizationId = "org_test", name = "Demo", slug = "demo", plan = "p0", database = "none", branches = Array.Empty<object>() },
                "/v1/secrets" when request.Method == HttpMethod.Get => source is "service" or "app" or "other-service" or "deleted"
                    ? new[] { new { id = "sec_test", appId = "app_test", serviceName = source == "app" ? "" : source == "other-service" ? "worker" : "api", name = "INSTAGRAM_APP_SECRET", status = source == "deleted" ? "deleted" : "active" } }
                    : Array.Empty<object>(),
                "/v1/secrets" => new { id = "sec_synced", appId = "app_test", serviceName = "api", name = "INSTAGRAM_APP_SECRET", status = "active" },
                "/v1/deployments/dep_test/logs" => Array.Empty<object>(),
                "/v1/deployments" => new { id = "dep_test", appId = "app_test", status = "queued" },
                "/v1/deployments/dep_test" or "/v1/deployments/dep_test/refresh" => new { id = "dep_test", appId = "app_test", organizationId = "org_test", database = "none", status = source == "failure" ? "failed" : "succeeded", failureCode = "missing_service_secret", failureMessage = "Service 'api' references missing secret 'INSTAGRAM_APP_SECRET' for environment variable 'Instagram__AppSecret'.", services = Array.Empty<object>() },
                _ => throw new InvalidOperationException("Unexpected request: " + request.Method + " " + path)
            };
            if (path == "/v1/deployments" && request.Method == HttpMethod.Post) { DeploymentCreated = true; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }
    }

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + Environment.NewLine;
        public void WriteError(string message) => Output += message + Environment.NewLine;
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
