using System.Net;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliBranchCommandTests
{
    [Fact]
    public async Task Branch_destroy_requires_confirmation_and_targets_selected_child()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-cli-branch-test-");
        try
        {
            var configPath = Path.Combine(tempDirectory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, """
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    sourcePath: .
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);
            var handler = new BranchApiHandler();
            var console = new TestConsole(["yes"]);
            var environment = CliEnvironment.ForTests("https://api.example", tempDirectory.FullName);
            var tokenStore = new TokenStore(environment);
            tokenStore.SaveToken("mc_test");
            var app = new CliApplication(console, environment, tokenStore, new MinicloudApiClient(environment, tokenStore, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["branch", "destroy", "feature-cart", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Equal([
                "GET /v1/apps/app_main",
                "DELETE /v1/apps/app_main/branches/app_branch"
            ], handler.Requests);
            Assert.Contains("Destroy branch 'feature-cart' and its Vultr VPS? [y/N]:", console.Output);
            Assert.Contains("Branch destroy queued: feature-cart", console.Output);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private sealed class BranchApiHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"app_main","organizationId":"org_123","name":"Demo","slug":"demo","parentAppId":null,"branchName":null,"plan":"p0","database":"sqlite","branches":[{"id":"app_branch","name":"Demo (feature-cart)","slug":"demo-feature-cart","branchName":"feature-cart","plan":"p0-v-0","database":"sqlite","websiteUrl":"https://org-demo-web-feature-cart.app.muni.dev","createdAt":"2026-07-14T12:00:00Z"}],"latestDeployment":null}""")
            });
        }
    }

    private sealed class TestConsole(IEnumerable<string?> lines) : IConsole
    {
        private readonly Queue<string?> _lines = new(lines);
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + Environment.NewLine;
        public void WriteError(string message) => Output += message + Environment.NewLine;
        public string? ReadLine() => _lines.Count == 0 ? null : _lines.Dequeue();
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException("Interactive list was not expected.");
    }
}
