using System.Net;
using System.Net.Http.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;
using Minicloud.Cli.Config;

namespace Minicloud.Tests;

public sealed class CliDeploymentChangeDetectionTests
{
    [Fact]
    public async Task Deploy_skips_deployment_when_no_services_have_changed()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-change-detect-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            File.WriteAllText(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");

            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    sourcePath: {{webDir.FullName}}
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var configResult = MinicloudConfigLoader.Load(configPath);
            var expectedHash = ServiceChecksumCalculator.Compute("web", configResult.Config!.Services["web"]);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            var handler = new ChangeDetectionHandler(new Dictionary<string, string>
            {
                ["web"] = expectedHash
            });
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["deploy", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Contains("No services have changed since the last deployment.", console.Output);
            Assert.Equal(0, handler.DeploymentsCreatedCount);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deploy_deploys_only_changed_services()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-change-detect-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            File.WriteAllText(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");

            var apiDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "api"));
            File.WriteAllText(Path.Combine(apiDir.FullName, "server.js"), "console.log('api');");
            File.WriteAllText(Path.Combine(apiDir.FullName, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"server.js\"]");

            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    sourcePath: {{webDir.FullName}}
                    image: ghcr.io/example/web:123
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                  api:
                    sourcePath: {{apiDir.FullName}}
                    image: ghcr.io/example/api:123
                    port: 8080
                    public: false
                    path: /
                    healthPath: /health
                """);

            var configResult = MinicloudConfigLoader.Load(configPath);
            var apiHash = ServiceChecksumCalculator.Compute("api", configResult.Config!.Services["api"]);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            // api is unchanged, but web is changed (or not yet recorded)
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>
            {
                ["api"] = apiHash,
                ["web"] = "old_web_hash_123"
            });
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["deploy", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Contains("Deploying changed services: web (skipping unchanged: api)", console.Output);
            Assert.Equal(1, handler.DeploymentsCreatedCount);
            Assert.Single(handler.LastCreatedRequest!.Services);
            Assert.Equal("web", handler.LastCreatedRequest.Services[0].Name);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deploy_all_deploys_all_services_regardless_of_hashes()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-change-detect-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            File.WriteAllText(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nCMD [\"node\", \"index.js\"]");

            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    sourcePath: {{webDir.FullName}}
                    image: ghcr.io/example/web:123
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var configResult = MinicloudConfigLoader.Load(configPath);
            var webHash = ServiceChecksumCalculator.Compute("web", configResult.Config!.Services["web"]);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            var handler = new ChangeDetectionHandler(new Dictionary<string, string>
            {
                ["web"] = webHash
            });
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["deploy", "all", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Equal(1, handler.DeploymentsCreatedCount);
            Assert.Single(handler.LastCreatedRequest!.Services);
            Assert.Equal("web", handler.LastCreatedRequest.Services[0].Name);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class ChangeDetectionHandler(IReadOnlyDictionary<string, string> deployedHashes) : HttpMessageHandler
    {
        public int DeploymentsCreatedCount { get; private set; }
        public CreateDeploymentRequest? LastCreatedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/me")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        userId = "usr_1",
                        email = "dev@example.com",
                        organizations = new[]
                        {
                            new { id = "org_1", name = "Test Org", slug = "test-org", role = "owner" }
                        }
                    })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/apps/app_main")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "app_main",
                        organizationId = "org_1",
                        name = "Demo",
                        slug = "demo",
                        plan = "p0",
                        database = "sqlite",
                        branches = Array.Empty<object>()
                    })
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/domains/services")
            {
                var services = deployedHashes.Select(kvp => new
                {
                    name = kvp.Key,
                    image = "ghcr.io/example/" + kvp.Key + ":latest",
                    port = 3000,
                    @public = true,
                    path = "/",
                    healthPath = "/health",
                    domains = Array.Empty<object>(),
                    sha256 = kvp.Value
                }).ToArray();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(services)
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/deployments")
            {
                DeploymentsCreatedCount++;
                LastCreatedRequest = await request.Content!.ReadFromJsonAsync<CreateDeploymentRequest>(cancellationToken: cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test_123",
                        appId = "app_main",
                        status = "succeeded"
                    })
                };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/deployments/dep_test_123/refresh")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test_123",
                        appId = "app_main",
                        organizationId = "org_1",
                        database = "sqlite",
                        status = "succeeded",
                        services = Array.Empty<object>()
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
