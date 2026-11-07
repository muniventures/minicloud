using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class ResolveProvisionServerStepTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public ResolveProvisionServerStepTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task CancelAfterProviderCreationRetainsPendingServerIdentity()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("cancel-provision");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { Id = "dep-cancel-provision", OrganizationId = org.Id, AppId = app.Id,
                Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var provider = new MockVpsProviderClient(createServer: request =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ProviderServerInfo("srv-cancel-provision", request.Name, "", "starting"));
        });
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => provider,
            new DeploymentPipelineOptions { SshPublicKey = "ssh-ed25519 TEST" }, TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step.ExecuteAsync(new PipelineStepContext {
            DeploymentId = "dep-cancel-provision", OrganizationId = org.Id, AppId = app.Id
        }, cancellation.Token));
        await using var verify = _harness.CreateDbContext();
        var registered = await verify.ServerInstances.SingleAsync(x => x.AppId == app.Id);
        Assert.Equal("srv-cancel-provision", registered.ProviderServerId);
        Assert.Equal("pending", registered.BootstrapStatus);
    }

    [Fact]
    public void BuildCloudInit_IncludesCompleteBootstrap()
    {
        var cloudInit = ResolveProvisionServerStep.BuildCloudInit(
            "myorg", "myapp", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGeneratedKey");

        Assert.Contains("#cloud-config", cloudInit);
        Assert.Contains("hostname: minicloud-myorg-myapp", cloudInit);
        Assert.Contains("name: minicloud", cloudInit);
        Assert.Contains("ssh_authorized_keys:", cloudInit);
        Assert.Contains("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIGeneratedKey", cloudInit);
        Assert.Contains("fallocate -l 2G /swapfile", cloudInit);
        Assert.Contains("vm.swappiness=10", cloudInit);
        Assert.Contains("apt-get install -y python3 ca-certificates curl gnupg jq ufw caddy docker.io docker-compose-v2 docker-buildx", cloudInit);
        Assert.Contains("docker-buildx e2fsprogs util-linux", cloudInit);
        Assert.Contains("usermod -aG docker minicloud", cloudInit);
        Assert.Contains("printf 'import /opt/minicloud/caddy/apps/*.caddy\\n' > /etc/caddy/Caddyfile", cloudInit);
        Assert.Contains("ufw allow OpenSSH", cloudInit);
        Assert.Contains("ufw allow 80/tcp", cloudInit);
        Assert.Contains("ufw allow 443/tcp", cloudInit);
        Assert.Contains("systemctl enable --now docker", cloudInit);
        Assert.Contains("systemctl enable --now caddy", cloudInit);
    }

    [Fact]
    public async Task ReusesExistingServerWhenPresentInDb()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("reuse");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.ServerInstances.Add(new ServerInstance
            {
                Id = "srv-existing-123",
                OrganizationId = org.Id,
                AppId = app.Id,
                Provider = "hetzner",
                ProviderServerId = "provider-123",
                IpAddress = "192.0.2.55",
                Region = "nbg1",
                Status = "active",
                BootstrapStatus = "ready",
                CreatedAt = now,
                UpdatedAt = now
            });

            db.Deployments.Add(new Deployment
            {
                Id = "dep-reuse-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var providerCalled = false;
        var mockProvider = new MockVpsProviderClient(() =>
        {
            providerCalled = true;
            return Task.FromResult<ProviderServerInfo?>(null);
        }, getServerStatus: id => Task.FromResult(new ProviderServerStatus(id, "running", "192.0.2.55", null, true)));

        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(),
            _ => mockProvider,
            Options.Create(new DeploymentPipelineOptions()),
            TimeProvider.System,
            NullLogger<ResolveProvisionServerStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-reuse-1",
            OrganizationId = org.Id,
            AppId = app.Id
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.False(providerCalled, "Provider client should not be called when server already exists in DB");
        Assert.Equal("192.0.2.55", context.ServerIp);
        Assert.Equal("srv-existing-123", context.ServerInstanceId);
    }

    [Fact]
    public async Task ProvisionsNewServerWhenMissingFromDb()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("prov");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment
            {
                Id = "dep-prov-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        CreateServerRequest? capturedRequest = null;
        var mockProvider = new MockVpsProviderClient(
            findServer: () => Task.FromResult<ProviderServerInfo?>(null),
            createServer: req =>
            {
                capturedRequest = req;
                return Task.FromResult(new ProviderServerInfo(
                    "srv-hetz-999", req.Name, "198.51.100.42", "running", null, req.Labels));
            },
            getServerStatus: id => Task.FromResult(new ProviderServerStatus(id, "running", "198.51.100.42", null, true)));

        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(),
            _ => mockProvider,
            Options.Create(new DeploymentPipelineOptions
            {
                SshPublicKey = "ssh-ed25519 TEST_PUBLIC_KEY",
                SshReadyTimeout = TimeSpan.FromMilliseconds(100)
            }),
            TimeProvider.System,
            NullLogger<ResolveProvisionServerStep>.Instance,
            sshProbe: (_, _, _) => Task.FromResult(true));

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-prov-1",
            OrganizationId = org.Id,
            AppId = app.Id
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Empty(capturedRequest.SshKeyIdOrPublicKey);
        Assert.Contains("TEST_PUBLIC_KEY", capturedRequest.UserDataCloudInit);
        Assert.Equal("198.51.100.42", context.ServerIp);

        await using (var db = _harness.CreateDbContext())
        {
            var saved = db.ServerInstances.FirstOrDefault(s => s.AppId == app.Id);
            Assert.NotNull(saved);
            Assert.Equal("198.51.100.42", saved.IpAddress);
            Assert.Equal("srv-hetz-999", saved.ProviderServerId);
        }
    }

    [Fact]
    public async Task MapsFailureToProviderProvisionFailedCode()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("fail");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment
            {
                Id = "dep-fail-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var mockProvider = new MockVpsProviderClient(
            findServer: () => Task.FromResult<ProviderServerInfo?>(null),
            createServer: _ => throw new HttpRequestException("Hetzner API unavailable"));

        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(),
            _ => mockProvider,
            Options.Create(new DeploymentPipelineOptions()),
            TimeProvider.System,
            NullLogger<ResolveProvisionServerStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-fail-1",
            OrganizationId = org.Id,
            AppId = app.Id
        };

        var ex = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal("provider_provision_failed", ex.FailureCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeRegistrationIsReusedAndConflictsBlockCreation(bool conflicting)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("runtime-" + conflicting);
        await using (var db = _harness.CreateDbContext())
        {
            db.RuntimeServers.Add(new RuntimeServer
            {
                Id = "runtime-" + conflicting, OrganizationId = org.Id, AppId = app.Id,
                Provider = "hetzner", ProviderServerId = "original-server"
            });
            if (conflicting)
            {
                db.ServerInstances.Add(new ServerInstance
                {
                    Id = "accidental-server", OrganizationId = org.Id, AppId = app.Id,
                    Provider = "hetzner", ProviderServerId = "replacement-server", Region = "nbg1",
                    Status = "active", BootstrapStatus = "ready"
                });
            }
            await db.SaveChangesAsync();
        }
        var client = new MockVpsProviderClient(
            findServer: () => throw new Exception("Must not discover by name"),
            createServer: _ => throw new Exception("Must not create"),
            getServerStatus: id =>
            {
                Assert.Equal("original-server", id);
                return Task.FromResult(new ProviderServerStatus(id, "running", "192.0.2.10", null, true));
            });
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => client,
            new DeploymentPipelineOptions(), TimeProvider.System);
        var context = new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "unused" };
        if (conflicting)
        {
            var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
            Assert.Contains("conflict", error.Message);
        }
        else
        {
            // Events require a deployment row.
            await using var db = _harness.CreateDbContext();
            db.Deployments.Add(new Deployment { Id = "unused", AppId = app.Id, OrganizationId = org.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
            await step.ExecuteAsync(context, default);
            Assert.Equal("192.0.2.10", context.ServerIp);
            Assert.Equal(CloudProvider.Hetzner, context.Provider);
        }
    }

    [Fact]
    public async Task PreviouslyDeployedAppCannotSilentlyProvisionReplacement()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("established");
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment { Id = "prior-success", AppId = app.Id, OrganizationId = org.Id, Status = DeploymentStatus.Succeeded });
            await db.SaveChangesAsync();
        }
        var client = new MockVpsProviderClient(createServer: _ => throw new Exception("Must not create"));
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => client,
            new DeploymentPipelineOptions(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(
            new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "unused" }, default));
        Assert.Contains("refusing to create a replacement", error.Message);
    }

    [Fact]
    public void ServerNamePreservesProductionIdentity()
    {
        Assert.Equal("minicloud-muni-production-example-suite", ResolveProvisionServerStep.BuildServerName("muni", "example-suite"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoversEstablishedProductionNameWithoutCreating(bool initiallyPending)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("discovery-" + initiallyPending);
        await using (var db = _harness.CreateDbContext())
        {
            app = await db.Apps.FindAsync(app.Id) ?? throw new Exception();
            app.Plan = AppPlan.P0H0;
            db.Deployments.Add(new Deployment { Id = "discover-" + initiallyPending, AppId = app.Id, OrganizationId = org.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        var expectedName = ResolveProvisionServerStep.BuildServerName(org.Slug, app.Slug);
        var found = new ProviderServerInfo("original", expectedName, initiallyPending ? "" : "192.0.2.44",
            initiallyPending ? "initializing" : "running", Labels: new Dictionary<string, string>
            {
                ["managed-by"] = "minicloud", ["customer"] = org.Slug, ["app"] = app.Slug, ["env"] = "production"
            });
        var client = new MockVpsProviderClient(findServer: () => Task.FromResult<ProviderServerInfo?>(found),
            createServer: _ => throw new Exception("Must not create replacement"));
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ => client,
            new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.Zero, ProvisionTimeout = TimeSpan.Zero }, TimeProvider.System);
        var context = new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "discover-" + initiallyPending };
        if (initiallyPending)
        {
            var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
            Assert.Contains("did not become ready", error.Message);
        }
        else
        {
            await step.ExecuteAsync(context, default);
            Assert.Equal("192.0.2.44", context.ServerIp);
        }
        Assert.Equal(expectedName, client.RequestedName);
    }

    private sealed class MockVpsProviderClient(
        Func<Task<ProviderServerInfo?>>? findServer = null,
        Func<CreateServerRequest, Task<ProviderServerInfo>>? createServer = null,
        Func<string, Task<ProviderServerStatus>>? getServerStatus = null) : IVpsProviderClient
    {
        public CloudProvider Provider => CloudProvider.Hetzner;

        public string? RequestedName { get; private set; }

        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken cancellationToken = default)
        {
            RequestedName = name;
            return findServer is not null ? findServer() : Task.FromResult<ProviderServerInfo?>(null);
        }

        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) =>
            createServer is not null
                ? createServer(request)
                : Task.FromResult(new ProviderServerInfo("id-1", request.Name, "127.0.0.1", "running", null, request.Labels));

        public Task<ProviderServerStatus> GetServerStatusAsync(string serverId, CancellationToken cancellationToken = default) =>
            getServerStatus is not null
                ? getServerStatus(serverId)
                : Task.FromResult(new ProviderServerStatus(serverId, "running", "127.0.0.1", null, true));

        public Task DeleteServerAsync(string serverId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
