using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using Minicloud.Common.Models;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Providers.Models;
using Minicloud.Worker.Ssh;
using Minicloud.Worker.Storage;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class DeploymentPipelineExecutorTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public DeploymentPipelineExecutorTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task ConnectedLocalSshPipeline_CompletesWithOneDeploymentId()
    {
        var portText = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PORT");
        var privateKeyPath = Environment.GetEnvironmentVariable("MINICLOUD_TEST_SSH_PRIVATE_KEY");
        var runtimeScriptPath = Environment.GetEnvironmentVariable("MINICLOUD_TEST_RUNTIME_SCRIPT");
        if (string.IsNullOrWhiteSpace(portText) || string.IsNullOrWhiteSpace(privateKeyPath) || string.IsNullOrWhiteSpace(runtimeScriptPath)) return;

        var (org, app) = await _harness.SeedOrgAndAppAsync("connectedtrace");
        var now = DateTimeOffset.UtcNow;
        var deployment = new Deployment
        {
            Id = "dep_connected_trace", OrganizationId = org.Id, AppId = app.Id,
            Status = DeploymentStatus.Provisioning, WorkerId = "connected-worker",
            OwnershipToken = "connected-owner", OwnershipAcquiredAt = now,
            Database = DatabaseMode.None, CreatedAt = now, UpdatedAt = now
        };
        byte[] artifactBytes;
        await using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                var entry = archive.CreateEntry("Dockerfile");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("FROM alpine\nRUN echo connected\n");
            }
            artifactBytes = buffer.ToArray();
        }
        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance { Id = "srv-connected", OrganizationId = org.Id, AppId = app.Id, Provider = "hetzner", ProviderServerId = "provider-connected", IpAddress = "127.0.0.1", Region = "nbg1", Status = "active", BootstrapStatus = "ready", CreatedAt = now, UpdatedAt = now });
            var artifact = new DeploymentArtifact { Id = "art-connected", OrganizationId = org.Id, AppId = app.Id, ServiceName = "web", FileName = "web.zip", ContentType = "application/zip", ManifestJson = "{}", StoragePath = "unused", SizeBytes = artifactBytes.Length, Sha256 = "fixture", Status = DeploymentArtifactStatus.Ready, CreatedAt = now, UpdatedAt = now, ExpiresAt = now.AddHours(1) };
            db.DeploymentArtifacts.Add(artifact);
            deployment.Services.Add(new DeploymentService { Id = "svc-connected", DeploymentId = deployment.Id, Name = "web", Image = "placeholder", ArtifactId = artifact.Id, Port = 8080, Public = true, Path = "/", HealthPath = "/health" });
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        var tempDir = Directory.CreateTempSubdirectory("minicloud-connected-pipeline-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            File.Copy(runtimeScriptPath, Path.Combine(scriptDir, "remote-deploy.sh"));
            var scopeFactory = _harness.CreateScopeFactory();
            var pipelineOptions = Options.Create(new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.Zero, MaxParallelBuilds = 1, MaxParallelNetworkOperations = 1 });
            var sshOptions = Options.Create(new SshDeploymentOptions { Port = int.Parse(portText), Username = "minicloud", PrivateKeyContent = await File.ReadAllTextAsync(privateKeyPath), MaxParallelArtifactTransfers = 1, CommandTimeout = TimeSpan.FromMinutes(2) });
            var ssh = new SshDeploymentClient(sshOptions.Value);
            var executor = new DeploymentPipelineExecutor(
                scopeFactory,
                new ResolveProvisionServerStep(scopeFactory, _ => new MockVpsProviderClient("127.0.0.1"), pipelineOptions, TimeProvider.System),
                new DnsSetupStep(scopeFactory, new MockCloudflareDnsClient(), pipelineOptions, TimeProvider.System),
                new SshDeployStep(scopeFactory, new MockArtifactRetriever(artifactBytes), ssh, sshOptions, pipelineOptions, new TestHostEnvironment { ContentRootPath = tempDir.FullName }, TimeProvider.System),
                new HealthCheckFinalizeStep(scopeFactory, new HttpClient(new MockHttpMessageHandler()), pipelineOptions, TimeProvider.System),
                ssh, sshOptions, TimeProvider.System);

            await executor.ExecuteAsync(deployment, CancellationToken.None);
            await using var verify = _harness.CreateDbContext();
            var finished = await verify.Deployments.Include(item => item.Events).SingleAsync(item => item.Id == deployment.Id);
            Assert.Equal(DeploymentStatus.Succeeded, finished.Status);
            Assert.Contains(finished.Events, item => item.MetadataJson != null && item.MetadataJson.Contains("\"phase\":\"ssh-deploy\""));
            var logRows = await verify.DeploymentLogs.Where(item => item.DeploymentId == deployment.Id).ToListAsync();
            var logs = logRows.OrderBy(item => item.CreatedAt).Select(item => item.Content).ToList();
            Assert.Contains(logs, line => line.Contains("phase=sftp_transfer"));
            var tracePath = Environment.GetEnvironmentVariable("MINICLOUD_SSH_TIMING_TRACE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(tracePath))
            {
                var traceLines = finished.Events.Where(item => item.MetadataJson != null)
                    .OrderBy(item => item.CreatedAt)
                    .Select(item => $"WORKER_EVENT {item.MetadataJson}")
                    .Concat(logs);
                await File.WriteAllLinesAsync(tracePath, traceLines);
            }
        }
        finally { tempDir.Delete(true); }
    }

    [Fact]
    public async Task ExecutesAllStepsSuccessfully()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("pipeok");
        var now = DateTimeOffset.UtcNow;

        Deployment deployment;
        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance
            {
                Id = "srv-pipe-ok",
                OrganizationId = org.Id,
                AppId = app.Id,
                Provider = "hetzner",
                ProviderServerId = "prov-pipe-ok",
                IpAddress = "192.0.2.88",
                Region = "nbg1",
                Status = "active",
                BootstrapStatus = "ready",
                CreatedAt = now,
                UpdatedAt = now
            });

            deployment = new Deployment
            {
                Id = "dep-pipe-ok",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                WorkerId = "test-worker",
                OwnershipToken = "owner-pipe-ok",
                OwnershipAcquiredAt = now,
                Domain = "app-ok.example.com",
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(deployment);

            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-pipe-ok",
                DeploymentId = deployment.Id,
                Name = "web",
                Image = "test/web:latest",
                Path = "/",
                Public = true,
                Port = 8080,
                HealthPath = "/health"
            });
            await db.SaveChangesAsync();
        }

        var tempDir = Directory.CreateTempSubdirectory("minicloud-test-pipe-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            await File.WriteAllTextAsync(Path.Combine(scriptDir, "remote-deploy.sh"), "#!/bin/bash\necho ok");

            var env = new TestHostEnvironment { ContentRootPath = tempDir.FullName };
            var scopeFactory = _harness.CreateScopeFactory();
            var pipelineOptions = Options.Create(new DeploymentPipelineOptions
            {
                HealthCheckTimeout = TimeSpan.FromSeconds(5),
                HealthCheckInterval = TimeSpan.FromMilliseconds(50)
            });
            var sshOptions = Options.Create(new SshDeploymentOptions
            {
                PrivateKeyContent = "MOCK_KEY"
            });

            var mockProvider = new MockVpsProviderClient();
            var mockDns = new MockCloudflareDnsClient();
            var mockArtifact = new MockArtifactRetriever();
            var mockSsh = new MockSshDeploymentClient();
            var mockHttp = new HttpClient(new MockHttpMessageHandler());

            var provisionStep = new ResolveProvisionServerStep(
                scopeFactory, _ => mockProvider, pipelineOptions, TimeProvider.System, NullLogger<ResolveProvisionServerStep>.Instance);
            var dnsStep = new DnsSetupStep(
                scopeFactory, mockDns, pipelineOptions, TimeProvider.System, NullLogger<DnsSetupStep>.Instance);
            var sshStep = new SshDeployStep(
                scopeFactory, mockArtifact, mockSsh, sshOptions, pipelineOptions, env, TimeProvider.System, NullLogger<SshDeployStep>.Instance);
            var healthStep = new HealthCheckFinalizeStep(
                scopeFactory, mockHttp, pipelineOptions, TimeProvider.System, NullLogger<HealthCheckFinalizeStep>.Instance);

            var executor = new DeploymentPipelineExecutor(
                scopeFactory,
                provisionStep,
                dnsStep,
                sshStep,
                healthStep,
                mockSsh,
                sshOptions,
                TimeProvider.System,
                NullLogger<DeploymentPipelineExecutor>.Instance);

            await executor.ExecuteAsync(deployment, CancellationToken.None);

            await using (var db = _harness.CreateDbContext())
            {
                var finished = await db.Deployments.FindAsync("dep-pipe-ok");
                Assert.NotNull(finished);
                Assert.Equal(DeploymentStatus.Succeeded, finished.Status);
                Assert.NotNull(finished.CompletedAt);
            }
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task TriggersRollbackAndMarksFailedOnSshDeployError()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("piperoll");
        var now = DateTimeOffset.UtcNow;

        Deployment deployment;
        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance
            {
                Id = "srv-pipe-roll",
                OrganizationId = org.Id,
                AppId = app.Id,
                Provider = "hetzner",
                ProviderServerId = "prov-pipe-roll",
                IpAddress = "192.0.2.89",
                Region = "nbg1",
                Status = "active",
                BootstrapStatus = "ready",
                CreatedAt = now,
                UpdatedAt = now
            });

            deployment = new Deployment
            {
                Id = "dep-pipe-roll",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                WorkerId = "test-worker",
                OwnershipToken = "owner-pipe-roll",
                OwnershipAcquiredAt = now,
                Domain = "app-roll.example.com",
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        var rollbackExecuted = false;
        var mockSsh = new MockSshDeploymentClient(
            execDeploy: (_, _) => Task.FromResult(new SshDeploymentResult(1, false, null, ["Build error"])),
            execCmd: (ip, user, key, cmd) =>
            {
                if (cmd.Contains("echo rollback-attempted", StringComparison.Ordinal))
                {
                    rollbackExecuted = true;
                    Assert.Contains("install -m 0644", cmd);
                    Assert.Contains("sudo -u caddy caddy validate", cmd);
                }
                return Task.FromResult(0);
            });

        var tempDir = Directory.CreateTempSubdirectory("minicloud-test-pipe-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            await File.WriteAllTextAsync(Path.Combine(scriptDir, "remote-deploy.sh"), "#!/bin/bash\necho ok");

            var env = new TestHostEnvironment { ContentRootPath = tempDir.FullName };
            var scopeFactory = _harness.CreateScopeFactory();
            var pipelineOptions = Options.Create(new DeploymentPipelineOptions());
            var sshOptions = Options.Create(new SshDeploymentOptions
            {
                PrivateKeyContent = "MOCK_KEY"
            });

            var provisionStep = new ResolveProvisionServerStep(
                scopeFactory, _ => new MockVpsProviderClient(), pipelineOptions, TimeProvider.System);
            var dnsStep = new DnsSetupStep(
                scopeFactory, new MockCloudflareDnsClient(), pipelineOptions, TimeProvider.System);
            var sshStep = new SshDeployStep(
                scopeFactory, new MockArtifactRetriever(), mockSsh, sshOptions, pipelineOptions, env, TimeProvider.System);
            var healthStep = new HealthCheckFinalizeStep(
                scopeFactory, new HttpClient(new MockHttpMessageHandler()), pipelineOptions, TimeProvider.System);

            var executor = new DeploymentPipelineExecutor(
                scopeFactory,
                provisionStep,
                dnsStep,
                sshStep,
                healthStep,
                mockSsh,
                sshOptions,
                TimeProvider.System);

            var ex = await Assert.ThrowsAsync<PipelineStepException>(() => executor.ExecuteAsync(deployment, CancellationToken.None));
            Assert.Equal("docker_build_failed", ex.FailureCode);
            Assert.True(rollbackExecuted, "Rollback SSH command should be invoked after ssh deploy failure");

            await using (var db = _harness.CreateDbContext())
            {
                var failed = await db.Deployments.FindAsync("dep-pipe-roll");
                Assert.NotNull(failed);
                Assert.Equal(DeploymentStatus.Failed, failed.Status);
                Assert.Equal("docker_build_failed", failed.FailureCode);
                var rollbackEvent = await db.DeploymentEvents.SingleAsync(e =>
                    e.DeploymentId == failed.Id && e.Message.StartsWith("Rollback command attempted"));
                Assert.Equal(EventLevel.Warning, rollbackEvent.Level);
                Assert.Contains("health was not verified", rollbackEvent.Message);
            }
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task DnsFailure_PrefersUnconfirmedRemoteQuiescenceFromCanceledSshWork()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("pipe-quiescence");
        var now = DateTimeOffset.UtcNow;
        Deployment deployment;
        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance
            {
                Id = "srv-pipe-quiescence",
                OrganizationId = org.Id,
                AppId = app.Id,
                Provider = "hetzner",
                ProviderServerId = "prov-pipe-quiescence",
                IpAddress = "192.0.2.90",
                Region = "nbg1",
                Status = "active",
                BootstrapStatus = "ready",
                CreatedAt = now,
                UpdatedAt = now
            });
            deployment = new Deployment
            {
                Id = "dep-pipe-quiescence",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                WorkerId = "test-worker",
                OwnershipToken = "owner-pipe-quiescence",
                OwnershipAcquiredAt = now,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            deployment.Services.Add(new DeploymentService
            {
                Id = "svc-pipe-quiescence",
                DeploymentId = deployment.Id,
                Name = "web",
                Image = "test/web:latest",
                Public = true,
                Port = 8080,
                Path = "/",
                HealthPath = "/health"
            });
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        var remoteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockSsh = new MockSshDeploymentClient(execDeploy: async (_, cancellationToken) =>
        {
            remoteStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            }
            catch (OperationCanceledException)
            {
                throw new RemoteQuiescenceException("remote process group is still active");
            }
        });
        var mockDns = new MockCloudflareDnsClient(async (_, cancellationToken) =>
        {
            await remoteStarted.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException("dns failed");
        });

        var tempDir = Directory.CreateTempSubdirectory("minicloud-test-pipe-");
        try
        {
            var scriptDir = Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy");
            Directory.CreateDirectory(scriptDir);
            await File.WriteAllTextAsync(Path.Combine(scriptDir, "remote-deploy.sh"), "#!/bin/bash\necho ok");
            var scopeFactory = _harness.CreateScopeFactory();
            var pipelineOptions = Options.Create(new DeploymentPipelineOptions());
            var sshOptions = Options.Create(new SshDeploymentOptions { PrivateKeyContent = "MOCK_KEY" });
            var executor = new DeploymentPipelineExecutor(
                scopeFactory,
                new ResolveProvisionServerStep(scopeFactory, _ => new MockVpsProviderClient(), pipelineOptions, TimeProvider.System),
                new DnsSetupStep(scopeFactory, mockDns, pipelineOptions, TimeProvider.System),
                new SshDeployStep(scopeFactory, new MockArtifactRetriever(), mockSsh, sshOptions, pipelineOptions,
                    new TestHostEnvironment { ContentRootPath = tempDir.FullName }, TimeProvider.System),
                new HealthCheckFinalizeStep(scopeFactory, new HttpClient(new MockHttpMessageHandler()), pipelineOptions, TimeProvider.System),
                mockSsh,
                sshOptions,
                TimeProvider.System);

            var exception = await Assert.ThrowsAsync<RemoteQuiescenceException>(
                () => executor.ExecuteAsync(deployment, CancellationToken.None));
            Assert.Contains("still active", exception.Message);

            await using var db = _harness.CreateDbContext();
            var retained = await db.Deployments.FindAsync(deployment.Id);
            Assert.NotNull(retained);
            Assert.Equal("owner-pipe-quiescence", retained.OwnershipToken);
            Assert.NotEqual(DeploymentStatus.Failed, retained.Status);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Theory]
    [InlineData("missing_service_secret")]
    [InlineData("secret_store_unavailable")]
    [InlineData("docker_build_failed")]
    public async Task PreparationFailureAfterDns_CancelsGateAndPersistsOriginalError(string code)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("preparation-" + code);
        var now = DateTimeOffset.UtcNow;
        var deployment = new Deployment
        {
            Id = "dep-" + code, OrganizationId = org.Id, AppId = app.Id,
            Status = DeploymentStatus.Provisioning, OwnershipToken = "owner-" + code,
            WorkerId = "test-worker", Database = DatabaseMode.None, CreatedAt = now, UpdatedAt = now
        };
        deployment.Services.Add(new DeploymentService { Id = "svc-" + code, Name = "api", Image = "example/api:latest", Port = 8080, Public = true, Path = "/", HealthPath = "/health" });
        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance { Id = "srv-" + code, OrganizationId = org.Id, AppId = app.Id, Provider = "hetzner", ProviderServerId = "provider-" + code, IpAddress = "192.0.2.88", Region = "nbg1", Status = "active", BootstrapStatus = "ready", CreatedAt = now, UpdatedAt = now });
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }
        var gateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ssh = new WaitingGateSshClient(gateStarted, code == "docker_build_failed");
        var tempDir = Directory.CreateTempSubdirectory("minicloud-preparation-failure-");
        try
        {
            var scripts = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "scripts", "github-actions", "minicloud-deploy"));
            await File.WriteAllTextAsync(Path.Combine(scripts.FullName, "remote-deploy.sh"), "#!/bin/bash\necho ok");
            var scopeFactory = _harness.CreateScopeFactory(services =>
                Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<Minicloud.Common.Services.IAppServiceSecretResolver>(services,
                    _ => new FailingSecretResolver(gateStarted.Task, code)));
            var options = Options.Create(new DeploymentPipelineOptions());
            var sshOptions = Options.Create(new SshDeploymentOptions { PrivateKeyContent = "MOCK_KEY" });
            var executor = new DeploymentPipelineExecutor(scopeFactory,
                new ResolveProvisionServerStep(scopeFactory, _ => new MockVpsProviderClient(), options, TimeProvider.System),
                new DnsSetupStep(scopeFactory, new MockCloudflareDnsClient(), options, TimeProvider.System),
                new SshDeployStep(scopeFactory, new MockArtifactRetriever(), ssh, sshOptions, options,
                    new TestHostEnvironment { ContentRootPath = tempDir.FullName }, TimeProvider.System),
                new HealthCheckFinalizeStep(scopeFactory, new HttpClient(new MockHttpMessageHandler()), options, TimeProvider.System),
                ssh, sshOptions, TimeProvider.System);
            var failure = await Assert.ThrowsAsync<PipelineStepException>(() => executor.ExecuteAsync(deployment, default).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(code, failure.FailureCode);
            Assert.DoesNotContain("SSH preparation did not become ready", failure.Message);
            if (code == "missing_service_secret")
            {
                Assert.Contains("INSTAGRAM_APP_SECRET", failure.Message);
                Assert.Contains("api", failure.Message);
            }
            Assert.True(ssh.GateCanceled);
            Assert.False(ssh.GateReleased);
            await using var verify = _harness.CreateDbContext();
            var failed = await verify.Deployments.FindAsync(deployment.Id);
            Assert.Equal(DeploymentStatus.Failed, failed!.Status);
            Assert.Equal(code, failed.FailureCode);
            Assert.Equal(failure.Message, failed.FailureMessage);
            if (code != "docker_build_failed")
            {
                Assert.False(ssh.RollbackAttempted);
                Assert.Equal(0, ssh.DeployCalls);
            }
        }
        finally { tempDir.Delete(true); }
    }

    private sealed class FailingSecretResolver(Task gateStarted, string code) : Minicloud.Common.Services.IAppServiceSecretResolver
    {
        public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> ResolveForDeploymentAsync(
            string appId, IReadOnlyList<Minicloud.Common.Contracts.DeploymentServiceRequest> services, CancellationToken cancellationToken)
        {
            await gateStarted.WaitAsync(cancellationToken);
            if (code == "missing_service_secret")
            {
                throw new Minicloud.Common.Services.MissingAppServiceSecretException("api", "Instagram__AppSecret", "INSTAGRAM_APP_SECRET");
            }
            if (code == "secret_store_unavailable")
            {
                throw new Minicloud.Common.Services.SecretStoreUnavailableException("Secret store offline");
            }
            return new Dictionary<string, IReadOnlyDictionary<string, string>>();
        }
    }

    private sealed class WaitingGateSshClient(TaskCompletionSource gateStarted, bool failTransfer) : ISshDeploymentClient
    {
        public bool GateCanceled { get; private set; }
        public bool GateReleased { get; private set; }
        public bool RollbackAttempted { get; private set; }
        public int DeployCalls { get; private set; }
        public Task<SshDeploymentResult> ExecuteDeploymentAsync(SshDeploymentRequest request, Func<string, string, Task> onLogLine, CancellationToken cancellationToken)
        {
            DeployCalls++;
            if (failTransfer) { throw new IOException("Artifact transfer failed"); }
            throw new InvalidOperationException("Remote execution must not start");
        }
        public async Task<int> ExecuteCommandAsync(string host, string username, string privateKeyContent, string command, Func<string, string, Task> onLogLine, CancellationToken cancellationToken)
        {
            if (command.Contains("preparation.ready", StringComparison.Ordinal))
            {
                gateStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { GateCanceled = true; throw; }
            }
            if (command.Contains("rollout.ready", StringComparison.Ordinal)) { GateReleased = true; }
            if (command.Contains("rollback", StringComparison.Ordinal)) { RollbackAttempted = true; }
            return 0;
        }
    }

    private sealed class MockVpsProviderClient(string ipAddress = "192.0.2.88") : IVpsProviderClient
    {
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<ProviderServerInfo?>(null);
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderServerInfo("srv-1", request.Name, ipAddress, "running", null, request.Labels));
        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderServerStatus(serverId, "running", ipAddress, null, true));
        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MockCloudflareDnsClient(
        Func<string, CancellationToken, Task>? waitForPropagation = null) : ICloudflareDnsClient
    {
        public Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task WaitForPropagationAsync(string hostname, string expectedIp, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            waitForPropagation is null ? Task.CompletedTask : waitForPropagation(hostname, cancellationToken);
    }

    private sealed class MockSshDeploymentClient(
        Func<SshDeploymentRequest, CancellationToken, Task<SshDeploymentResult>>? execDeploy = null,
        Func<string, string, string, string, Task<int>>? execCmd = null) : ISshDeploymentClient
    {
        public Task<SshDeploymentResult> ExecuteDeploymentAsync(
            SshDeploymentRequest request,
            Func<string, string, Task>? onOutputLine = null,
            CancellationToken cancellationToken = default) =>
            execDeploy is not null
                ? execDeploy(request, cancellationToken)
                : Task.FromResult(new SshDeploymentResult(0, true, "sha123", ["__MINICLOUD_DEPLOY_COMPLETE__=sha123"]));

        public Task<int> ExecuteCommandAsync(
            string serverIp,
            string username,
            string privateKey,
            string command,
            Func<string, string, Task>? onOutputLine = null,
            CancellationToken cancellationToken = default) =>
            execCmd is not null
                ? execCmd(serverIp, username, privateKey, command)
                : Task.FromResult(0);
    }

    private sealed class MockArtifactRetriever(byte[]? content = null) : IArtifactRetriever
    {
        public Task<Stream> OpenArtifactStreamAsync(string artifactId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(content ?? "mock"u8.ToArray()));
        public Task<string> DownloadArtifactToTempFileAsync(string artifactId, CancellationToken cancellationToken = default) =>
            Task.FromResult("/tmp/mock.zip");
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") });
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
