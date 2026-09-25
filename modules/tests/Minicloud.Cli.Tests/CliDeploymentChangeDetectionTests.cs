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
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCMD [\"node\", \"index.js\"]");

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

            Assert.True(exitCode == CliExitCodes.Success, console.Output);
            Assert.Contains("No services have changed since the last deployment.", console.Output);
            Assert.Contains("Timing: phase=change_detection_hash", console.Output);
            Assert.Contains("duration_ms=", console.Output);
            Assert.Contains("items=1 outcome=succeeded", console.Output);
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
            File.WriteAllText(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCMD [\"node\", \"index.js\"]");

            var apiDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "api"));
            File.WriteAllText(Path.Combine(apiDir.FullName, "server.js"), "console.log('api');");
            File.WriteAllText(Path.Combine(apiDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 8080\nCMD [\"node\", \"server.js\"]");

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

            var exitCode = await app.RunAsync(["deploy", "--config", configPath], CancellationToken.None);

            Assert.True(exitCode == CliExitCodes.Success, console.Output);
            Assert.Contains("Deploying changed services: web (skipping unchanged: api)", console.Output);
            Assert.Equal(1, handler.DeploymentsCreatedCount);
            Assert.Single(handler.LastCreatedRequest!.Services);
            Assert.Equal("web", handler.LastCreatedRequest.Services[0].Name);
            var timingTracePath = Environment.GetEnvironmentVariable("MINICLOUD_CLI_TIMING_TRACE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(timingTracePath))
            {
                await File.WriteAllTextAsync(timingTracePath, console.Output);
            }
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

    [Fact]
    public async Task Deploy_does_not_submit_when_artifact_upload_fails()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-upload-fail-");
        try
        {
            var webDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "web"));
            await File.WriteAllTextAsync(Path.Combine(webDir.FullName, "index.js"), "console.log('web');");
            await File.WriteAllTextAsync(Path.Combine(webDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000\nCMD [\"node\", \"index.js\"]");
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    sourcePath: {{webDir.FullName}}
                    port: 3000
                    public: false
                    path: /
                    healthPath: /health
                """);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>(), failArtifactUpload: true);
            var app = new CliApplication(console, environment, tokens,
                new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["deploy", "web", "--config", configPath], CancellationToken.None);

            Assert.True(exitCode == CliExitCodes.NetworkOrApiUnavailable, console.Output);
            Assert.Equal(0, handler.DeploymentsCreatedCount);
            Assert.Contains("API error", console.Output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deploy_does_not_create_any_artifact_when_later_bundle_fails()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-bundle-fail-");
        try
        {
            var firstDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "first"));
            await File.WriteAllTextAsync(Path.Combine(firstDir.FullName, "index.js"), "console.log('first');");
            await File.WriteAllTextAsync(Path.Combine(firstDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 3000");
            var secondDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "second"));
            await File.WriteAllTextAsync(Path.Combine(secondDir.FullName, "index.js"), "console.log('second');");
            await File.WriteAllTextAsync(Path.Combine(secondDir.FullName, "Dockerfile"), "FROM node:20\nEXPOSE 4000");
            await File.WriteAllTextAsync(Path.Combine(secondDir.FullName, DeploymentArtifactBundler.ManifestEntryName), "reserved");
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, $$"""
                app: demo
                appId: app_main
                database: sqlite
                services:
                  first:
                    sourcePath: {{firstDir.FullName}}
                    port: 3000
                    public: false
                    path: /
                    healthPath: /health
                  second:
                    sourcePath: {{secondDir.FullName}}
                    port: 4000
                    public: false
                    path: /
                    healthPath: /health
                """);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new ChangeDetectionHandler(new Dictionary<string, string>());
            var app = new CliApplication(console, environment, tokens,
                new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            var exitCode = await app.RunAsync(["deploy", "all", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.ValidationError, exitCode);
            Assert.Equal(0, handler.ArtifactsCreatedCount);
            Assert.Equal(0, handler.DeploymentsCreatedCount);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class ChangeDetectionHandler(
        IReadOnlyDictionary<string, string> deployedHashes,
        bool failArtifactUpload = false) : HttpMessageHandler
    {
        public int DeploymentsCreatedCount { get; private set; }
        public int ArtifactsCreatedCount { get; private set; }
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

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/artifacts")
            {
                ArtifactsCreatedCount++;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "art_upload_test",
                        appId = "app_main",
                        serviceName = "web",
                        status = "uploading",
                        uploadUrl = "/v1/artifacts/art_upload_test/file"
                    })
                };
            }

            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == "/v1/artifacts/art_upload_test/file")
            {
                return failArtifactUpload
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = JsonContent.Create(new
                        {
                            error = new { code = "artifact_upload_failed", message = "simulated upload failure" }
                        })
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            id = "art_upload_test", appId = "app_main", serviceName = "web", status = "ready"
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
