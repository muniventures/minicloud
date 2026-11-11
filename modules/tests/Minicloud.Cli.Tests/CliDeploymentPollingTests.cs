// SPDX-License-Identifier: MPL-2.0

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
    public async Task CancelUsesExplicitDeploymentWithoutConfigOrPolling()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-cancel-");
        try
        {
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new CancelHandler();
            var app = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
            Assert.Equal(CliExitCodes.Success, await app.RunAsync(["deploy", "cancel", "20261110.1"], default));
            Assert.Equal("/api/deployments/20261110.1/cancel?organizationId=org_test", handler.PathAndQuery);
            Assert.Contains("Cancellation requested for deployment 20261110.1", console.Output);
            Assert.DoesNotContain("dep_test", console.Output);
            Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["deploy", "cancel"], default));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("dep_test")]
    [InlineData("20261110.01")]
    [InlineData("20261310.1")]
    [InlineData("20261110.0")]
    [InlineData("20261110.9223372036854775808")]
    [InlineData("2026111.1")]
    public async Task Deployment_commands_reject_malformed_numbers_before_any_request(string value)
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-number-");
        try
        {
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new CancelHandler();
            var app = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));
            Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["deploy", "cancel", value], default));
            Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["status", value], default));
            Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["logs", value], default));
            Assert.Equal(0, handler.RequestCount);
            Assert.Contains("is not a deployment number", console.Output);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Deployment_numbers_are_resolved_in_the_explicitly_selected_organization()
    {
        var directory = Directory.CreateTempSubdirectory("minicloud-cli-orgs-");
        try
        {
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("mc_test");
            var handler = new TwoOrganizationHandler();
            var app = new CliApplication(console, environment, tokens, new MinicloudApiClient(environment, tokens, new HttpClient(handler)));

            Assert.Equal(CliExitCodes.ValidationError, await app.RunAsync(["status", "20261110.1"], default));
            Assert.Contains("--organization", console.Output);
            Assert.DoesNotContain(handler.Requests, x => x.StartsWith("/api/deployments", StringComparison.Ordinal));

            Assert.Equal(CliExitCodes.Success, await app.RunAsync(["status", "20261110.1", "--organization", "org_b"], default));
            Assert.Contains("App: app_b", console.Output);
            Assert.DoesNotContain("App: app_a", console.Output);
            Assert.Contains("/api/deployments/20261110.1?organizationId=org_b", handler.Requests);
            Assert.DoesNotContain("dep_b", console.Output);

            Assert.Equal(CliExitCodes.Success, await app.RunAsync(["status", "20261110.1", "--organization=org_a", "--diagnostics"], default));
            Assert.Contains("App: app_a", console.Output);
            Assert.Contains("Deployment ID (diagnostic correlation): dep_a", console.Output);

            Assert.Equal(CliExitCodes.Success, await app.RunAsync(["logs", "--organization", "org_b"], default));
            Assert.Contains("/api/deployments?organizationId=org_b", handler.Requests);
            Assert.Contains("/api/deployments/20261110.1/logs?organizationId=org_b", handler.Requests);
            Assert.Contains("log from org_b", console.Output);
            Assert.DoesNotContain("log from org_a", console.Output);

            Assert.Equal(CliExitCodes.AuthError, await app.RunAsync(["status", "20261110.1", "--organization", "org_other"], default));

            var configPath = Path.Combine(directory.FullName, "minicloud.yml");
            await File.WriteAllTextAsync(configPath, """
                app: demo
                appId: app_b
                database: sqlite
                services:
                  web:
                    image: ghcr.io/example/web:123
                    port: 3000
                    public: true
                    path: /
                    healthPath: /health
                """);
            handler.Requests.Clear();
            Assert.Equal(CliExitCodes.Success, await app.RunAsync(["deploy", "cancel", "20261110.1", "--config", configPath], default));
            Assert.Equal(["/api/apps/app_b", "/api/deployments/20261110.1/cancel?organizationId=org_b"], handler.Requests);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("20261110.1", true)]
    [InlineData("20261310.1", true)]
    [InlineData("dep_abc", true)]
    [InlineData("demo", false)]
    [InlineData("app_123", false)]
    [InlineData("api-v2", false)]
    public void Logs_distinguishes_deployment_numbers_from_app_names(string value, bool deployment) =>
        Assert.Equal(deployment, CliApplication.LooksLikeDeploymentReference(value));

    private sealed class TwoOrganizationHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (CliApiFixtures.TryMe(request, "org_a", "org_b") is { } me) return Task.FromResult(me);
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            Requests.Add(pathAndQuery);
            var organization = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["organizationId"];
            var suffix = organization == "org_a" ? "a" : "b";
            object body = request.RequestUri.AbsolutePath switch
            {
                "/api/deployments" => new[] { Deployment(suffix) },
                "/api/deployments/20261110.1" => Deployment(suffix),
                "/api/deployments/20261110.1/cancel" => new { id = "dep_" + suffix, number = "20261110.1", cancellationRequested = true },
                "/api/apps/app_b" => CliApiFixtures.Application("app_b", "org_b", "env_b"),
                "/api/deployments/20261110.1/logs" => new[] { new { id = "log_1", source = "worker", content = "log from " + organization, createdAt = DateTimeOffset.UnixEpoch } },
                _ => throw new InvalidOperationException(pathAndQuery),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }

        private static object Deployment(string suffix) => new
        {
            id = "dep_" + suffix, number = "20261110.1", appId = "app_" + suffix, organizationId = "org_" + suffix,
            database = "none", status = "succeeded", services = Array.Empty<object>()
        };
    }

    private sealed class CancelHandler : HttpMessageHandler
    {
        public string? PathAndQuery { get; private set; }
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (CliApiFixtures.TryMe(request, "org_test") is { } me) return Task.FromResult(me);
            Assert.Equal(HttpMethod.Post, request.Method);
            PathAndQuery = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(new { id = "dep_test", number = "20261110.1", cancellationRequested = true }) });
        }
    }

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

            var exitCode = await app.RunAsync(["status", "20261110.1", "--watch"], CancellationToken.None);

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
            Assert.Contains("Deploy: created deployment 20261110.7", console.Output);
            Assert.Contains("Deploy: network connection lost, waiting to reconnect...", console.Output);
            Assert.Contains("Deploy: connection restored", console.Output);
            Assert.Contains("Deploy: succeeded", console.Output);
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

            var exitCode = await app.RunAsync(["status", "20261110.1", "--watch"], cts.Token);

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

            var exitCode = await app.RunAsync(["status", "20261110.99", "--watch"], CancellationToken.None);

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
            if (CliApiFixtures.TryMe(request, "org_test") is { } me) return Task.FromResult(me);
            if (request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery == "/api/deployments/20261110.1?organizationId=org_test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test",
                        number = "20261110.1",
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

            if (request.Method == HttpMethod.Post && request.RequestUri!.PathAndQuery == "/api/deployments/20261110.1/refresh?organizationId=org_test")
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
                        number = "20261110.1",
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
            if (CliApiFixtures.TryMe(request, "org_test") is { } me) return Task.FromResult(me);
            if (request.Method == HttpMethod.Get && request.RequestUri!.PathAndQuery == "/api/deployments/20261110.1?organizationId=org_test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_test",
                        number = "20261110.1",
                        appId = "app_test",
                        organizationId = "org_test",
                        database = "sqlite",
                        status = "deploying",
                        services = Array.Empty<object>()
                    })
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.PathAndQuery == "/api/deployments/20261110.1/refresh?organizationId=org_test")
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
            if (CliApiFixtures.TryMe(request, "org_test") is { } me) return Task.FromResult(me);
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
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/me")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "usr_1",
                        email = "dev@example.com",
                        organizations = new[]
                        {
                            new { id = "org_1", name = "Test Org", slug = "test-org", role = "owner" }
                        }
                    })
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/apps/app_main")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(CliApiFixtures.Application("app_main", "org_1", "app_main")) });

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/environments/app_main")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "app_main",
                        applicationId = "app_main",
                        organizationId = "org_1",
                        name = "Demo",
                        slug = "demo",
                        plan = "p0",
                        database = "sqlite",
                        branches = Array.Empty<object>()
                    })
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/deployments")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = JsonContent.Create(new
                    {
                        id = "dep_deploy_123",
                        number = "20261110.7",
                        appId = "app_main",
                        status = "dispatching"
                    })
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.PathAndQuery == "/api/deployments/20261110.7/refresh?organizationId=org_1")
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
                        number = "20261110.7",
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
