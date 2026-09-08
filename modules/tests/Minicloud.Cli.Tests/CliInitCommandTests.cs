using System.Net;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliInitCommandTests
{
    [Fact]
    public async Task Init_uses_existing_config_before_starting_wizard()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-cli-init-test-");
        try
        {
            var configPath = Path.Combine(tempDirectory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, """
                app: as-tools
                appId: app_123
                database: postgres
                services:
                  as-route-analyzer:
                    sourcePath: .
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var handler = new InitApiHandler();
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", tempDirectory.FullName);
            var tokenStore = new TokenStore(environment);
            tokenStore.SaveToken("mc_test");
            var app = new CliApplication(console, environment, tokenStore, new MinicloudApiClient(environment, tokenStore, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["init", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Equal(["GET /v1/apps/app_123"], handler.Requests);
            Assert.Contains($"Using existing {configPath}", console.Output);
            Assert.Contains("App: AS Tools (as-tools, app_123)", console.Output);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Init_warns_and_cancels_when_existing_config_app_is_not_accessible()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("minicloud-cli-init-test-");
        try
        {
            const string Config = """
                app: as-tools
                appId: app_denied
                database: postgres
                services:
                  as-route-analyzer:
                    sourcePath: .
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """;
            var configPath = Path.Combine(tempDirectory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, Config);

            var handler = new InitApiHandler();
            var console = new TestConsole(["n"]);
            var environment = CliEnvironment.ForTests("https://api.example", tempDirectory.FullName);
            var tokenStore = new TokenStore(environment);
            tokenStore.SaveToken("mc_test");
            var app = new CliApplication(console, environment, tokenStore, new MinicloudApiClient(environment, tokenStore, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["init", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Equal(["GET /v1/apps/app_denied"], handler.Requests);
            Assert.Contains("cannot access that app", console.Errors);
            Assert.Contains($"Update '{configPath}' now? [y/N]:", console.Output);
            Assert.Contains("Init canceled.", console.Output);
            Assert.Equal(Config, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    private sealed class InitApiHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri!.PathAndQuery}");
            if (request.RequestUri.PathAndQuery == "/v1/apps/app_denied")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"error":{"code":"forbidden","message":"Forbidden"}}""")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"app_123","organizationId":"org_123","name":"AS Tools","slug":"as-tools","parentAppId":null,"branchName":null,"plan":"p0","database":"postgres","branches":[],"latestDeployment":null}""")
            });
        }
    }

    private sealed class TestConsole : IConsole
    {
        private readonly Queue<string?> _lines;

        public TestConsole(IEnumerable<string?>? lines = null)
        {
            _lines = new Queue<string?>(lines ?? []);
        }

        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public string Errors { get; private set; } = "";

        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + Environment.NewLine;
        public void WriteError(string message) => Errors += message + Environment.NewLine;
        public string? ReadLine() => _lines.Count == 0 ? null : _lines.Dequeue();
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException("Interactive key prompt was not expected.");
    }
}
