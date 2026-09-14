using System.Net;
using System.Net.Http.Json;
using Minicloud.Cli;
using Minicloud.Cli.Api;
using Minicloud.Cli.Auth;
using Minicloud.Cli.Commands;

namespace Minicloud.Tests;

public sealed class CliDeploymentPollingTests
{
    [Fact]
    public async Task Status_watch_survives_transient_network_errors_and_waits_for_completion()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-poll-");
        try
        {
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new IntermittentNetworkDeploymentHandler();
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["status", "dep_test", "--watch"], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Contains("Network connection lost. Waiting to reconnect...", console.Output);
            Assert.Contains("Connection restored.", console.Output);
            Assert.Contains("Status: succeeded", console.Output);
            Assert.Contains("Service URL (web): https://web.example", console.Output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Deploy_survives_transient_network_errors_during_polling()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-deploy-poll-");
        try
        {
            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, """
                app: demo
                appId: app_main
                database: sqlite
                services:
                  web:
                    sourcePath: .
                    image: ghcr.io/example/web:123
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);

            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new DeployWithNetworkInterruptionHandler();
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["deploy", "--no-publish", "--config", configPath], CancellationToken.None);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Contains("Minicloud deployment dep_deploy_123", console.Output);
            Assert.Contains("Network connection lost. Waiting to reconnect...", console.Output);
            Assert.Contains("Connection restored.", console.Output);
            Assert.Contains("Status: succeeded", console.Output);
            Assert.Contains("Service URL (web): https://web.example", console.Output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Status_watch_aborts_promptly_when_cancellation_is_requested()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-cancel-");
        try
        {
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            using var cts = new CancellationTokenSource();
            var handler = new CancelingDeploymentHandler(cts);
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["status", "dep_test", "--watch"], cts.Token);

            Assert.Equal(CliExitCodes.Success, exitCode);
            Assert.Contains("Operation canceled.", console.Output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Status_watch_does_not_retry_permanent_api_errors()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-error-");
        try
        {
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");

            var handler = new NotFoundDeploymentHandler();
            var client = new MinicloudApiClient(environment, tokens, new HttpClient(handler));
            var app = new CliApplication(console, environment, tokens, client);

            var exitCode = await app.RunAsync(["status", "dep_missing", "--watch"], CancellationToken.None);

            Assert.Equal(CliExitCodes.NetworkOrApiUnavailable, exitCode);
            Assert.Contains("API error (deployment_not_found)", console.Output);
            Assert.DoesNotContain("Waiting to reconnect...", console.Output);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class IntermittentNetworkDeploymentHandler : HttpMessageHandler
    {
        private int _refreshCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/deployments/dep_test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test",
                        appId = "app_test",
                        organizationId = "org_test",
                        database = "sqlite",
                        status = "deploying",
                        services = new[]
                        {
                            new { name = "web", urls = new[] { "https://web.example" } }
                        }
                    })
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/deployments/dep_test/refresh")
            {
                _refreshCount++;
                if (_refreshCount == 1)
                {
                    // Simulate laptop closed / network disconnected
                    throw new HttpRequestException("An error occurred while sending the request.");
                }

                if (_refreshCount == 2)
                {
                    // Simulate HTTP client timeout
                    throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test",
                        appId = "app_test",
                        organizationId = "org_test",
                        database = "sqlite",
                        status = "succeeded",
                        services = new[]
                        {
                            new { name = "web", urls = new[] { "https://web.example" } }
                        }
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CancelingDeploymentHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/deployments/dep_test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test",
                        appId = "app_test",
                        organizationId = "org_test",
                        database = "sqlite",
                        status = "deploying",
                        services = Array.Empty<object>()
                    })
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/deployments/dep_test/refresh")
            {
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NotFoundDeploymentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new
                {
                    error = new
                    {
                        code = "deployment_not_found",
                        message = "Deployment was not found."
                    }
                })
            });
        }
    }

    private sealed class DeployWithNetworkInterruptionHandler : HttpMessageHandler
    {
        private int _refreshCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/me")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/apps/app_main")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
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
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/deployments")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_deploy_123",
                        appId = "app_main",
                        status = "dispatching"
                    })
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/deployments/dep_deploy_123/refresh")
            {
                _refreshCount++;
                if (_refreshCount == 1)
                {
                    // Laptop closed: network broken
                    throw new HttpRequestException("An error occurred while sending the request.");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_deploy_123",
                        appId = "app_main",
                        organizationId = "org_1",
                        database = "sqlite",
                        status = "succeeded",
                        services = new[]
                        {
                            new { name = "web", urls = new[] { "https://web.example" } }
                        }
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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
