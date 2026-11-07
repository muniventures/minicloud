using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;
using Minicloud.Worker.Pipeline.Steps;
using Minicloud.Common.Providers.Dns;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class DnsSetupStepTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;

    public DnsSetupStepTests(WorkerSqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task UpsertsDnsRecordForPublicServices()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("dns");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var dep = new Deployment
            {
                Id = "dep-dns-1",
                OwnershipToken = "owner-dns-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Domain = "custom.example.com",
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(dep);
            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-web",
                DeploymentId = dep.Id,
                Name = "web",
                Image = "test/web:latest",
                Path = "/",
                HealthPath = "/health",
                Public = true,
                Port = 8080
            });
            await db.SaveChangesAsync();
        }

        var upserted = new List<(string Hostname, string Ip)>();
        var waited = new List<string>();

        var mockDns = new MockCloudflareDnsClient(
            upsert: (host, ip) =>
            {
                upserted.Add((host, ip));
                return Task.CompletedTask;
            },
            waitForProp: (host, ip, timeout) =>
            {
                waited.Add(host);
                return Task.CompletedTask;
            });

        var step = new DnsSetupStep(
            _harness.CreateScopeFactory(),
            mockDns,
            Options.Create(new DeploymentPipelineOptions()),
            TimeProvider.System,
            NullLogger<DnsSetupStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-dns-1",
            OwnershipToken = "owner-dns-1",
            OrganizationId = org.Id,
            AppId = app.Id,
            ServerIp = "192.0.2.100"
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.NotEmpty(upserted);
        Assert.Equal("192.0.2.100", upserted[0].Ip);
        Assert.NotEmpty(waited);
        Assert.Contains(context.PrimaryHostname, waited);
    }

    [Fact]
    public async Task SkipsDnsWhenNoPublicServices()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("nopub");
        var now = DateTimeOffset.UtcNow;

        await using (var db = _harness.CreateDbContext())
        {
            var dep = new Deployment
            {
                Id = "dep-nopub-1",
                OwnershipToken = "owner-nopub-1",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(dep);
            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-internal",
                DeploymentId = dep.Id,
                Name = "worker",
                Image = "test/worker:latest",
                Path = "/",
                HealthPath = "/health",
                Public = false,
                Port = 8080
            });
            await db.SaveChangesAsync();
        }

        var called = false;
        var mockDns = new MockCloudflareDnsClient(
            upsert: (host, ip) =>
            {
                called = true;
                return Task.CompletedTask;
            });

        var step = new DnsSetupStep(
            _harness.CreateScopeFactory(),
            mockDns,
            Options.Create(new DeploymentPipelineOptions()),
            TimeProvider.System,
            NullLogger<DnsSetupStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-nopub-1",
            OwnershipToken = "owner-nopub-1",
            OrganizationId = org.Id,
            AppId = app.Id,
            ServerIp = "192.0.2.100"
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.False(called, "DNS should not be called when no services are public");
    }

    [Fact]
    public async Task DisabledBindingIsNotReplacedWithGeneratedDefaultHostname()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("disabled-domain");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            var deployment = new Deployment
            {
                Id = "dep-disabled-domain",
                OwnershipToken = "owner-disabled-domain",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Deployments.Add(deployment);
            db.DeploymentServices.Add(new DeploymentService
            {
                Id = "svc-disabled-domain",
                DeploymentId = deployment.Id,
                Name = "web",
                Image = "test/web:latest",
                Path = "/",
                HealthPath = "/health",
                Public = true,
                Port = 8080
            });
            db.Domains.Add(new Domain
            {
                Id = "dom-disabled",
                OrganizationId = org.Id,
                AppId = app.Id,
                ServiceName = "web",
                Hostname = "disabled.example.test",
                Kind = DomainBindingService.KindMinicloudSubdomain,
                PathPrefix = "/",
                DnsStatus = DomainBindingService.DnsVerified,
                ApplyStatus = DomainBindingService.ApplyApplied,
                SslStatus = DomainBindingService.SslReady,
                Status = DomainBindingService.StatusDisabled,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var called = false;
        var step = new DnsSetupStep(_harness.CreateScopeFactory(),
            new MockCloudflareDnsClient(upsert: (_, _) => { called = true; return Task.CompletedTask; }),
            Options.Create(new DeploymentPipelineOptions()), TimeProvider.System,
            NullLogger<DnsSetupStep>.Instance);
        var context = new PipelineStepContext
        {
            DeploymentId = "dep-disabled-domain",
            OwnershipToken = "owner-disabled-domain",
            OrganizationId = org.Id,
            AppId = app.Id,
            OrganizationSlug = org.Slug,
            AppSlug = app.Slug,
            ServerIp = "192.0.2.100"
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        Assert.False(called);
        Assert.Empty(context.ServiceHostnames);
        Assert.Empty(context.Hostnames);
    }

    [Fact]
    public async Task PreservesEveryEnabledServiceBinding()
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync("multiple-domains");
        var now = DateTimeOffset.UtcNow;
        await using (var db = _harness.CreateDbContext())
        {
            db.Deployments.Add(new Deployment
            {
                Id = "dep-multiple-domains",
                OwnershipToken = "owner-multiple-domains",
                OrganizationId = org.Id,
                AppId = app.Id,
                Status = DeploymentStatus.Provisioning,
                Database = DatabaseMode.None,
                CreatedAt = now,
                UpdatedAt = now
            });
            foreach (var name in new[] { "portal", "worker" })
            {
                db.DeploymentServices.Add(new DeploymentService
                {
                    Id = "svc-multiple-" + name,
                    DeploymentId = "dep-multiple-domains",
                    Name = name,
                    Image = "test/web:latest",
                    Path = "/",
                    HealthPath = "/health",
                    Public = name == "portal",
                    Port = 8080
                });
            }
            foreach (var host in new[] { "nexus.app.muni.dev", "legacy.app.muni.dev", "disabled-multiple.app.muni.dev", "private-multiple.app.muni.dev", "custom.example.com", "unverified.example.com", "moved.example.com" })
            {
                db.Domains.Add(new Domain
                {
                    Id = host,
                    OrganizationId = org.Id,
                    AppId = app.Id,
                    ServiceName = host.StartsWith("private") ? "worker" : "portal",
                    Hostname = host,
                    Kind = host.EndsWith(".app.muni.dev") ? DomainBindingService.KindMinicloudSubdomain : DomainBindingService.KindCustomDomain,
                    TargetIp = host.StartsWith("moved") ? "192.0.2.101" : "192.0.2.100",
                    PathPrefix = "/",
                    DnsStatus = host.StartsWith("unverified") ? DomainBindingService.DnsPending : DomainBindingService.DnsVerified,
                    ApplyStatus = DomainBindingService.ApplyPending,
                    SslStatus = DomainBindingService.SslPending,
                    Status = host.StartsWith("disabled") ? DomainBindingService.StatusDisabled : DomainBindingService.StatusPendingApply,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            await db.SaveChangesAsync();
        }
        var upserted = new List<string>();
        var waited = new List<string>();
        var step = new DnsSetupStep(_harness.CreateScopeFactory(),
            new MockCloudflareDnsClient(
                upsert: (host, _) => { upserted.Add(host); return Task.CompletedTask; },
                waitForProp: (host, _, _) => { waited.Add(host); return Task.CompletedTask; }),
            Options.Create(new DeploymentPipelineOptions { MaxParallelNetworkOperations = 1 }), TimeProvider.System);
        var context = new PipelineStepContext
        {
            DeploymentId = "dep-multiple-domains",
            OwnershipToken = "owner-multiple-domains",
            OrganizationId = org.Id,
            AppId = app.Id,
            ServerIp = "192.0.2.100"
        };
        await step.ExecuteAsync(context, CancellationToken.None);
        var expected = new[] { "custom.example.com", "legacy.app.muni.dev", "nexus.app.muni.dev" };
        var managed = expected.Where(x => x.EndsWith(".app.muni.dev")).ToArray();
        Assert.Equal(expected, context.ServiceHostnames["portal"]);
        Assert.Equal(expected, context.Hostnames);
        Assert.Equal(managed, upserted);
        Assert.Equal(managed, waited);
        Assert.False(context.ServiceHostnames.ContainsKey("worker"));
    }

    [Fact]
    public async Task ThrowsWhenServerIpMissing()
    {
        var step = new DnsSetupStep(
            _harness.CreateScopeFactory(),
            new MockCloudflareDnsClient(),
            Options.Create(new DeploymentPipelineOptions()),
            TimeProvider.System,
            NullLogger<DnsSetupStep>.Instance);

        var context = new PipelineStepContext
        {
            DeploymentId = "dep-noip-1",
            OrganizationId = "org-1",
            AppId = "app-1",
            ServerIp = ""
        };

        var ex = await Assert.ThrowsAsync<PipelineStepException>(() => step.ExecuteAsync(context, CancellationToken.None));
        Assert.Equal("provider_provision_failed", ex.FailureCode);
    }

    private sealed class MockCloudflareDnsClient(
        Func<string, string, Task>? upsert = null,
        Func<string, string, TimeSpan, Task>? waitForProp = null) : ICloudflareDnsClient
    {
        public Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default) =>
            upsert is not null
                ? upsert(hostname, ipAddress)
                : Task.CompletedTask;

        public Task WaitForPropagationAsync(string hostname, string expectedIp, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            waitForProp is not null
                ? waitForProp(hostname, expectedIp, timeout)
                : Task.CompletedTask;
    }
}
