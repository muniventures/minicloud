using System.Net;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliServiceUrlTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_prints_all_service_urls_without_a_primary_url(bool hasDomains)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-urls-");
        try
        {
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(new DeploymentHandler(hasDomains)));
            var app = new CliApplication(console, environment, tokens, client);

            Assert.Equal(CliExitCodes.Success, await app.RunAsync(["status", "dep_test"], CancellationToken.None));
            Assert.DoesNotContain("https://old-main.example", console.Output);
            Assert.DoesNotContain("Website URL", console.Output);
            Assert.Contains("Console: https://console.example", console.Output);
            if (hasDomains)
            {
                Assert.Contains("Service URL (admin): https://admin.example", console.Output);
                Assert.Contains("Service URL (web): https://web.example", console.Output);
                Assert.Contains("Service URL (web): https://alias.example", console.Output);
                Assert.DoesNotContain("Service URL (worker)", console.Output);
            }
            else
            {
                Assert.DoesNotContain("Service URL", console.Output);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class DeploymentHandler(bool hasDomains) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/deployments/dep_test", request.RequestUri!.AbsolutePath);
            var payload = new
            {
                id = "dep_test", appId = "app_test", organizationId = "org_test", database = "sqlite",
                status = "succeeded", websiteUrl = "https://old-main.example", consoleUrl = "https://console.example",
                createdAt = "2026-09-08T12:00:00Z",
                services = new[]
                {
                    new { name = "admin", urls = hasDomains ? new[] { "https://admin.example" } : [] },
                    new { name = "web", urls = hasDomains ? new[] { "https://web.example", "https://alias.example" } : [] },
                    new { name = "worker", urls = Array.Empty<string>() }
                }
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload))
            });
        }
    }

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + Environment.NewLine;
        public void WriteError(string message) => Output += message + Environment.NewLine;
        public string? ReadLine() => throw new InvalidOperationException();
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
