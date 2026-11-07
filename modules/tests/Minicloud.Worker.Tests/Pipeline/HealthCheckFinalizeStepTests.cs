using System.Net;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class HealthCheckFinalizeStepTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public HealthCheckFinalizeStepTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task CancellationRequestWinsBeforeSuccessFinalization()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("cancel-health");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { Id = "dep-cancel-health", OrganizationId = org.Id, AppId = app.Id,
                Status = DeploymentStatus.Verifying, OwnershipToken = "owner-cancel-health", WorkerId = "worker",
                CancellationRequestedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var step = new HealthCheckFinalizeStep(_harness.CreateScopeFactory(), new HttpClient(),
            new DeploymentPipelineOptions(), TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.ExecuteAsync(new PipelineStepContext {
            DeploymentId = "dep-cancel-health", OrganizationId = org.Id, AppId = app.Id,
            OwnershipToken = "owner-cancel-health"
        }, default));
        await using var verify = _harness.CreateDbContext();
        var deployment = await verify.Deployments.FindAsync("dep-cancel-health");
        Assert.Equal(DeploymentStatus.Verifying, deployment!.Status);
        Assert.NotNull(deployment.OwnershipToken);
        Assert.Null(deployment.CompletedAt);
    }

    [Fact]
    public async Task MarksSucceededWhenHealthCheckPasses()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("healthpass");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var dep = new Deployment
            {
                Id = "dep-health-pass",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Verifying,
                WorkerId = "test-worker",
                OwnershipToken = "owner-health-pass",
                OwnershipAcquiredAt = now,
                Domain = "app.example.com",
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(dep);

            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-api-pass",
                DeploymentId = dep.Id,
                Name = "api",
                Image = "test/api:latest",
                Path = "/",
                Public = true,
                HealthPath = "/health",
                Port = 8080
            });
            await db.SaveChangesAsync();
        }

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("OK")
            });
        });

        var httpClient = new HttpClient(handler);
        var step = new HealthCheckFinalizeStep(
            _harness.CreateScopeFactory(),
            httpClient,
            Options.Create(new DeploymentPipelineOptions
            {
                HealthCheckTimeout = TimeSpan.FromSeconds(5),
                HealthCheckInterval = TimeSpan.FromMilliseconds(50)
            }),
            TimeProvider.System,
            NullLogger<HealthCheckFinalizeStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-health-pass",
            OrganizationId = org.Id,
            AppId = app.Id,
            OwnershipToken = "owner-health-pass",
            Hostnames = ["app.example.com"]
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        await using (var db = _harness.CreateDbContext())
        {
            var updated = await db.Deployments.FindAsync("dep-health-pass");
            Assert.NotNull(updated);
            Assert.Equal(DeploymentStatus.Succeeded, updated.Status);
            Assert.NotNull(updated.CompletedAt);
            Assert.Contains("https://app.example.com", updated.WebsiteUrl);
        }
    }

    [Fact]
    public async Task ThrowsHealthCheckFailedWhenEndpointReturnsNon200()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("healthfail");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var dep = new Deployment
            {
                Id = "dep-health-fail",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Verifying,
                WorkerId = "test-worker",
                OwnershipToken = "owner-health-fail",
                OwnershipAcquiredAt = now,
                Domain = "app.example.com",
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(dep);

            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-api-fail",
                DeploymentId = dep.Id,
                Name = "api",
                Image = "test/api:latest",
                Path = "/",
                Public = true,
                HealthPath = "/health",
                Port = 8080
            });
            await db.SaveChangesAsync();
        }

        var handler = new MockHttpMessageHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Service Unavailable")
            });
        });

        var httpClient = new HttpClient(handler);
        var step = new HealthCheckFinalizeStep(
            _harness.CreateScopeFactory(),
            httpClient,
            Options.Create(new DeploymentPipelineOptions
            {
                HealthCheckTimeout = TimeSpan.FromMilliseconds(200),
                HealthCheckInterval = TimeSpan.FromMilliseconds(50)
            }),
            TimeProvider.System,
            NullLogger<HealthCheckFinalizeStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-health-fail",
            OrganizationId = org.Id,
            AppId = app.Id,
            OwnershipToken = "owner-health-fail",
            Hostnames = ["app.example.com"]
        };

        var ex = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal(HealthCheckFinalizeStep.FailureCode, ex.FailureCode);
    }

    [Fact]
    public async Task ProbesEachServiceAtItsResolvedCustomHostname()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("health-custom-hosts");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var dep = new Deployment
            {
                Id = "dep-health-custom-hosts",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Verifying,
                WorkerId = "test-worker",
                OwnershipToken = "owner-health-custom-hosts",
                OwnershipAcquiredAt = now,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(dep);
            db.DeploymentServices.AddRange(
                new DeploymentService
                {
                    Id = "svc-api-custom",
                    DeploymentId = dep.Id,
                    Name = "api",
                    Image = "test/api:latest",
                    Path = "/",
                    Public = true,
                    HealthPath = "/ready",
                    Port = 8080
                },
                new DeploymentService
                {
                    Id = "svc-web-custom",
                    DeploymentId = dep.Id,
                    Name = "web",
                    Image = "test/web:latest",
                    Path = "/",
                    Public = true,
                    HealthPath = "/health",
                    Port = 3000
                });
            await db.SaveChangesAsync();
        }

        var requested = new ConcurrentBag<string>();
        var handler = new MockHttpMessageHandler((request, _) =>
        {
            requested.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var step = new HealthCheckFinalizeStep(
            _harness.CreateScopeFactory(),
            new HttpClient(handler),
            Options.Create(new DeploymentPipelineOptions
            {
                HealthCheckTimeout = TimeSpan.FromSeconds(2),
                HealthCheckInterval = TimeSpan.FromMilliseconds(10),
                MaxParallelNetworkOperations = 2
            }),
            TimeProvider.System,
            NullLogger<HealthCheckFinalizeStep>.Instance);
        var context = new PipelineStepContext
        {
            DeploymentId = "dep-health-custom-hosts",
            OrganizationId = org.Id,
            AppId = app.Id,
            OwnershipToken = "owner-health-custom-hosts",
            Hostnames = ["api.customer.example", "www.customer.example"]
        };
        context.ServiceHostnames["api"] = ["api.customer.example"];
        context.ServiceHostnames["web"] = ["www.customer.example", "legacy.customer.example"];

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Contains("https://api.customer.example/ready", requested);
        Assert.Contains("https://www.customer.example/health", requested);
        Assert.Contains("https://legacy.customer.example/health", requested);
    }

    [Fact]
    public async Task ProbesDisposableTlsEndpointWithoutLiveDns()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("health-tls");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            var deployment = new Deployment
            {
                Id = "dep-health-tls",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Verifying,
                WorkerId = "test-worker",
                OwnershipToken = "owner-health-tls",
                OwnershipAcquiredAt = now,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(deployment);
            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-health-tls",
                DeploymentId = deployment.Id,
                Name = "web",
                Image = "test/web:latest",
                Path = "/",
                Public = true,
                HealthPath = "/ready",
                Port = 8080
            });
            await db.SaveChangesAsync();
        }

        await using var server = await DisposableTlsServer.StartAsync("health.test");
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    certificate is not null && errors != SslPolicyErrors.None
            },
            ConnectCallback = async (_, cancellationToken) =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
                return client.GetStream();
            }
        };
        var step = new HealthCheckFinalizeStep(
            _harness.CreateScopeFactory(), new HttpClient(handler),
            Options.Create(new DeploymentPipelineOptions
            {
                HealthCheckTimeout = TimeSpan.FromSeconds(2),
                HealthCheckInterval = TimeSpan.FromMilliseconds(10)
            }), TimeProvider.System, NullLogger<HealthCheckFinalizeStep>.Instance);
        var context = new PipelineStepContext
        {
            DeploymentId = "dep-health-tls",
            OrganizationId = org.Id,
            AppId = app.Id,
            OwnershipToken = "owner-health-tls",
            Hostnames = ["health.test"]
        };
        context.ServiceHostnames["web"] = ["health.test"];

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("/ready", await server.RequestPath);
    }

    private sealed class MockHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class DisposableTlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TaskCompletionSource<string> _requestPath =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _serverTask;

        private DisposableTlsServer(TcpListener listener, X509Certificate2 certificate)
        {
            _listener = listener;
            _certificate = certificate;
            _serverTask = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<string> RequestPath => _requestPath.Task;

        public static Task<DisposableTlsServer> StartAsync(string hostname)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName(hostname);
            request.CertificateExtensions.Add(names.Build());
            var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new DisposableTlsServer(listener, certificate));
        }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(_certificate, false, false);
                using var reader = new StreamReader(tls, Encoding.ASCII, false, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_cancellation.Token);
                if (requestLine is not null)
                {
                    _requestPath.TrySetResult(requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
                }
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_cancellation.Token)))
                {
                }
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                await tls.WriteAsync(response, _cancellation.Token);
                await tls.FlushAsync(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                _requestPath.TrySetCanceled(_cancellation.Token);
            }
            catch (Exception exception)
            {
                _requestPath.TrySetException(exception);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cancellation.CancelAsync();
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            _certificate.Dispose();
            _cancellation.Dispose();
        }
    }
}
