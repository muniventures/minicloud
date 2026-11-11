using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Models;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Providers.Common;
using System.Net;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Worker.Tests.Providers;

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
        Assert.Empty(capturedRequest.SshKeyReferences);
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
    [InlineData("db-0123456789abcdef01234567")]
    [InlineData("dbrepo-0123456789abcdef01234567")]
    public void GeneratedDatabaseServerNamesUseTheSharedDiscoveryIdentity(string appSlug)
    {
        const string organization = "minicloud-staging";
        var name = ResolveProvisionServerStep.BuildServerName(organization, appSlug);
        Assert.Equal(ManagedServerName.ForApp(organization, appSlug), name);
        Assert.InRange(name.Length, 1, 63);
        var longOrganization = new string('o', 55);
        Assert.Contains("hostname: " + ManagedServerName.ForHostname(longOrganization, appSlug),
            ResolveProvisionServerStep.BuildCloudInit(longOrganization, appSlug));
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

    [Fact]
    public async Task CatalogPlanWithoutReservationNeverUsesGlobalProviderCredentials()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("missing-reservation");
        await using (var db = _harness.CreateDbContext())
        {
            var account = new ProviderAccount { Id = "missing-reservation-account", OrganizationId = org.Id,
                Name = "secondary", ProviderType = CloudProvider.Hetzner,
                CredentialPath = "synthetic/path", CredentialGeneration = "test" };
            var plan = new ProviderPlan { Id = "missing-reservation-plan", OrganizationId = org.Id,
                ProviderAccount = account, ProviderAccountId = account.Id, Name = "secondary small", NormalizedName = "SECONDARY SMALL",
                Region = "nbg1", Sku = "cx23" };
            db.ProviderPlans.Add(plan);
            (await db.Apps.SingleAsync(x => x.Id == app.Id)).PlanId = plan.Id;
            db.Deployments.Add(new Deployment { Id = "missing-reservation-deployment", AppId = app.Id, OrganizationId = org.Id,
                PlanId = plan.Id, Status = DeploymentStatus.Provisioning });
            await db.SaveChangesAsync();
        }
        var globalCalled = false;
        var step = new ResolveProvisionServerStep(_harness.CreateScopeFactory(), _ =>
        {
            globalCalled = true;
            throw new InvalidOperationException("Global credentials must not be used.");
        }, new DeploymentPipelineOptions(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(new PipelineStepContext
        { AppId = app.Id, OrganizationId = org.Id, DeploymentId = "missing-reservation-deployment" }, default));
        Assert.Contains("reservation", error.Message);
        Assert.False(globalCalled);
    }

    [Fact]
    public async Task CatalogProvisioningUsesReservedAccountAndPersistsIt()
    {
        var context = await SeedCatalogAsync("catalog-provision");
        var factory = new AccountFactory(new MockVpsProviderClient());
        var step = CatalogStep(factory);
        await step.ExecuteAsync(context, default);
        Assert.Equal([context.ProviderAccountId!], factory.RequestedAccounts);
        await using var db = _harness.CreateDbContext();
        var server = await db.ServerInstances.SingleAsync(x => x.AppId == context.AppId);
        Assert.Equal(context.ProviderAccountId, server.ProviderAccountId);
        Assert.Equal(context.PlanId, server.PlanId);
        Assert.Equal("cx23", server.Sku);
    }

    [Fact]
    public async Task AmbiguousCatalogCreateCannotIssueAnotherCreateWhenDiscoveryIsEmpty()
    {
        var context = await SeedCatalogAsync("catalog-ambiguous");
        var creates = 0;
        var factory = new AccountFactory(new MockVpsProviderClient(createServer: _ =>
        {
            creates++;
            throw new HttpRequestException("Response lost.");
        }));
        var step = CatalogStep(factory);
        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        var retry = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        Assert.Contains("unresolved", retry.Message);
        Assert.Equal(1, creates);
        await using var db = _harness.CreateDbContext();
        Assert.True((await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted);
        Assert.All(factory.RequestedAccounts, x => Assert.Equal(context.ProviderAccountId, x));
    }

    [Fact]
    public async Task DefiniteCatalogQuotaRejectionCanRetryOnlyTheReservedAccount()
    {
        var context = await SeedCatalogAsync("catalog-quota");
        var creates = 0;
        var factory = new AccountFactory(new MockVpsProviderClient(createServer: request =>
        {
            if (++creates == 1) { throw new ProviderApiException("quota", HttpStatusCode.TooManyRequests); }
            return Task.FromResult(new ProviderServerInfo("quota-retry-server", request.Name, "192.0.2.25", "running"));
        }));
        var step = CatalogStep(factory);
        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        await using (var db = _harness.CreateDbContext())
        { Assert.False((await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted); }
        await step.ExecuteAsync(context, default);
        Assert.Equal(2, creates);
        Assert.All(factory.RequestedAccounts, x => Assert.Equal(context.ProviderAccountId, x));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task DigitalOceanProjectAssignmentFailureRetainsCreationMarkerAndPreventsAnotherAllocation(HttpStatusCode status)
    {
        var context = await SeedCatalogAsync("do-" + (int)status + "-project");
        await using (var db = _harness.CreateDbContext())
        {
            (await db.ProviderAccounts.SingleAsync(x => x.Id == context.ProviderAccountId)).ProviderType = CloudProvider.DigitalOcean;
            var plan = await db.ProviderPlans.SingleAsync(x => x.Id == context.PlanId);
            plan.Region = "nyc1";
            plan.Sku = "s-1vcpu-1gb";
            var deployment = await db.Deployments.SingleAsync(x => x.Id == context.DeploymentId);
            deployment.ProviderType = "DigitalOcean";
            deployment.ProviderRegion = plan.Region;
            deployment.ProviderSku = plan.Sku;
            await db.SaveChangesAsync();
        }

        var handler = new TestHttpMessageHandler();
        handler.QueueJsonResponse("""{"droplets":[]}""");
        handler.QueueJsonResponse("""{"droplet":{"id":6002,"name":"created-droplet","status":"new"}}""", HttpStatusCode.Created);
        handler.QueueJsonResponse("""{"message":"Project assignment rejected"}""", status);
        handler.QueueJsonResponse("""{"droplets":[]}""");
        using var http = new HttpClient(handler);
        var factory = new AccountFactory(new DigitalOceanClient(http, new DigitalOceanOptions
        {
            ApiToken = "synthetic-token", ProjectId = "synthetic-project", BaseUrl = "https://api.digitalocean.com/v2/"
        }));
        var step = CatalogStep(factory);

        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        var retry = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));

        Assert.Contains("unresolved", retry.Message);
        Assert.Single(handler.RecordedRequests, x => x.Method == HttpMethod.Post && x.Uri.EndsWith("/droplets", StringComparison.Ordinal));
        await using var verify = _harness.CreateDbContext();
        Assert.True((await verify.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted);
        Assert.All(factory.RequestedAccounts, x => Assert.Equal(context.ProviderAccountId, x));
    }

    [Fact]
    public async Task DisabledCatalogAccountReusesItsRegisteredServerDespiteExternalIdInAnotherAccount()
    {
        var context = await SeedCatalogAsync("catalog-reuse");
        var (otherOrg, otherApp) = await _harness.SeedOrgAndAppAsync("catalog-reuse-other");
        await using (var db = _harness.CreateDbContext())
        {
            var original = await db.ProviderAccounts.SingleAsync(x => x.Id == context.ProviderAccountId);
            original.Enabled = false;
            db.ProviderAccounts.Add(new ProviderAccount { Id = "reuse-other-account", OrganizationId = otherOrg.Id,
                Name = "Other scope", ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/other", CredentialGeneration = "test" });
            db.ServerInstances.AddRange(new ServerInstance { Id = "reuse-original-server", AppId = context.AppId, OrganizationId = context.OrganizationId,
                ProviderAccountId = context.ProviderAccountId, PlanId = context.PlanId, Provider = "hetzner", ProviderServerId = "same-id",
                Region = "nbg1", Status = "active", BootstrapStatus = "ready" },
                new ServerInstance { Id = "reuse-other-server", AppId = otherApp.Id, OrganizationId = otherOrg.Id,
                    ProviderAccountId = "reuse-other-account", Provider = "hetzner", ProviderServerId = "same-id",
                    Region = "nbg1", Status = "active", BootstrapStatus = "ready" });
            await db.SaveChangesAsync();
        }
        var factory = new AccountFactory(new MockVpsProviderClient(findServer: () => throw new Exception("Registered server must not use discovery.")));
        await CatalogStep(factory).ExecuteAsync(context, default);
        Assert.Equal([context.ProviderAccountId!], factory.RequestedAccounts);
        Assert.Equal("127.0.0.1", context.ServerIp);
    }

    [Fact]
    public async Task DiscoveredCatalogResourceKeepsOriginalScopeWhenReadinessFails()
    {
        var context = await SeedCatalogAsync("catalog-discovered");
        ProviderServerInfo found;
        await using (var db = _harness.CreateDbContext())
        {
            var app = await db.Apps.Include(x => x.Organization).SingleAsync(x => x.Id == context.AppId);
            found = new ProviderServerInfo("discovered-id", ResolveProvisionServerStep.BuildServerName(app.Organization.Slug, app.Slug), "", "starting",
                Labels: new Dictionary<string, string> { ["managed-by"] = "minicloud", ["customer"] = app.Organization.Slug,
                    ["app"] = app.Slug, ["env"] = "production" });
        }
        var factory = new AccountFactory(new MockVpsProviderClient(findServer: () => Task.FromResult<ProviderServerInfo?>(found),
            createServer: _ => throw new Exception("Must not create after discovery.")));
        var step = new ResolveProvisionServerStep(
            _harness.CreateScopeFactory(services => services.AddSingleton<ProviderAccountClientFactory>(factory)),
            _ => throw new Exception("Must not use global credentials."),
            new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.Zero, ProvisionTimeout = TimeSpan.Zero }, TimeProvider.System);
        await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, default));
        await using var verify = _harness.CreateDbContext();
        Assert.True((await verify.Deployments.SingleAsync(x => x.Id == context.DeploymentId)).ProviderCreationAttempted);
        var saved = await verify.ServerInstances.SingleAsync(x => x.AppId == context.AppId);
        Assert.Equal(context.ProviderAccountId, saved.ProviderAccountId);
        Assert.Equal("discovered-id", saved.ProviderServerId);
    }

    private ResolveProvisionServerStep CatalogStep(AccountFactory factory) => new(
        _harness.CreateScopeFactory(services => services.AddSingleton<ProviderAccountClientFactory>(factory)),
        _ => throw new InvalidOperationException("Catalog plan cannot use global provider credentials."),
        new DeploymentPipelineOptions { SshReadyTimeout = TimeSpan.Zero }, TimeProvider.System);

    private async Task<PipelineStepContext> SeedCatalogAsync(string name)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync(name);
        var account = new ProviderAccount { Id = name + "-account", OrganizationId = org.Id,
            Name = name, ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/" + name, CredentialGeneration = "test" };
        var plan = new ProviderPlan { Id = name + "-plan", OrganizationId = org.Id, ProviderAccount = account,
            ProviderAccountId = account.Id, Name = name, NormalizedName = name.ToUpperInvariant(), Region = "nbg1", Sku = "cx23" };
        await using var db = _harness.CreateDbContext();
        db.ProviderPlans.Add(plan);
        (await db.Apps.SingleAsync(x => x.Id == app.Id)).PlanId = plan.Id;
        db.Deployments.Add(new Deployment { Id = name + "-deployment", AppId = app.Id, OrganizationId = org.Id,
            Status = DeploymentStatus.Provisioning, PlanId = plan.Id, ProviderAccountId = account.Id,
            ProviderType = "Hetzner", ProviderRegion = "nbg1", ProviderSku = "cx23" });
        await db.SaveChangesAsync();
        return new PipelineStepContext { AppId = app.Id, OrganizationId = org.Id, DeploymentId = name + "-deployment",
            PlanId = plan.Id, ProviderAccountId = account.Id };
    }

    private sealed class AccountFactory(IVpsProviderClient client) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!)
    {
        public List<string> RequestedAccounts { get; } = [];
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true)
        {
            RequestedAccounts.Add(providerAccountId);
            return Task.FromResult(client);
        }
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
