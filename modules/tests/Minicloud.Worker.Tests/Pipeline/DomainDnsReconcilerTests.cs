using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Services;
using Minicloud.Worker.Pipeline;

namespace Minicloud.Worker.Tests.Pipeline;

public sealed class DomainDnsReconcilerTests : IClassFixture<WorkerSqliteTestHarness>
{
    private readonly WorkerSqliteTestHarness _harness;
    public DomainDnsReconcilerTests(WorkerSqliteTestHarness harness) => _harness = harness;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddedAliasGetsDnsWithoutDeploymentAndVerifiedBindingIsSkipped(bool manageDnsRecords)
    {
        var (app, domain) = await SeedAsync("dns-alias-" + manageDnsRecords);
        var dns = new DnsClient();
        await using var db = _harness.CreateDbContext();
        var service = new DomainDnsReconciler(db, dns, TimeProvider.System, NullLogger<DomainDnsReconciler>.Instance,
            Options.Create(new ManagedDomainOptions { ManageDnsRecords = manageDnsRecords }));
        await service.ReconcileAsync(default);
        if (manageDnsRecords) Assert.Contains((domain.Hostname, "192.0.2.10"), dns.Upserts);
        else Assert.Empty(dns.Upserts);
        Assert.Equal(1, dns.PropagationChecks);
        var updated = await db.Domains.FindAsync(domain.Id);
        Assert.Equal(DomainBindingService.DnsVerified, updated!.DnsStatus);
        Assert.Equal("192.0.2.10", updated.TargetIp);
        Assert.Equal(DomainBindingService.StatusPendingApply, updated.Status);
        await service.ReconcileAsync(default);
        Assert.Equal(manageDnsRecords ? 1 : 0, dns.Upserts.Count);
    }

    [Fact]
    public async Task DisabledBindingAndActiveDeploymentDoNotMutateDns()
    {
        var (app, domain) = await SeedAsync("dns-skip");
        await using var db = _harness.CreateDbContext();
        var deployment = await db.Deployments.SingleAsync(d => d.AppId == app.Id);
        deployment.OwnershipToken = "owned";
        await db.SaveChangesAsync();
        var dns = new DnsClient();
        var service = new DomainDnsReconciler(db, dns, TimeProvider.System, NullLogger<DomainDnsReconciler>.Instance);
        await service.ReconcileAsync(default);
        Assert.Empty(dns.Upserts);
        deployment.OwnershipToken = null;
        (await db.Domains.FindAsync(domain.Id))!.Status = DomainBindingService.StatusDisabled;
        await db.SaveChangesAsync();
        await service.ReconcileAsync(default);
        Assert.Empty(dns.Upserts);
    }

    [Fact]
    public async Task ProviderFailureRemainsPendingAndIsRetried()
    {
        var (_, domain) = await SeedAsync("dns-retry");
        await using var db = _harness.CreateDbContext();
        var dns = new DnsClient { Fail = true };
        var service = new DomainDnsReconciler(db, dns, TimeProvider.System, NullLogger<DomainDnsReconciler>.Instance);
        await service.ReconcileAsync(default);
        var unchanged = await db.Domains.FindAsync(domain.Id);
        Assert.Equal(DomainBindingService.DnsPending, unchanged!.DnsStatus);
        Assert.Null(unchanged.TargetIp);
        dns.Fail = false;
        await service.ReconcileAsync(default);
        Assert.Equal(DomainBindingService.DnsVerified, unchanged.DnsStatus);
    }

    private async Task<(MinicloudApp App, Domain Domain)> SeedAsync(string name)
    {
        var (org, app) = await _harness.SeedOrgAndAppAsync(name);
        var now = DateTimeOffset.UtcNow;
        await using var db = _harness.CreateDbContext();
        var deployment = new Deployment
        {
            Id = "dep-" + name,
            OrganizationId = org.Id,
            AppId = app.Id,
            Status = DeploymentStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        deployment.Services.Add(new DeploymentService
        {
            Id = "svc-" + name,
            Name = "web",
            Public = true,
            Port = 8080,
            Image = "fixture",
            Path = "/",
            HealthPath = "/health"
        });
        db.Deployments.Add(deployment);
        db.ServerInstances.Add(new ServerInstance
        {
            Id = "srv-" + name,
            OrganizationId = org.Id,
            AppId = app.Id,
            Provider = "hetzner",
            ProviderServerId = name,
            IpAddress = "192.0.2.10",
            Region = "test",
            Status = "ready",
            BootstrapStatus = "ready",
            CreatedAt = now,
            UpdatedAt = now
        });
        var domain = new Domain
        {
            Id = "dom-" + name,
            OrganizationId = org.Id,
            AppId = app.Id,
            ServiceName = "web",
            Hostname = name + ".app.muni.dev",
            Kind = DomainBindingService.KindMinicloudSubdomain,
            PathPrefix = "/",
            Status = DomainBindingService.StatusPendingApply,
            DnsStatus = DomainBindingService.DnsPending,
            ApplyStatus = DomainBindingService.ApplyPending,
            SslStatus = DomainBindingService.SslPending,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Domains.Add(domain);
        await db.SaveChangesAsync();
        return (app, domain);
    }

    private sealed class DnsClient : ICloudflareDnsClient
    {
        public List<(string Host, string Ip)> Upserts { get; } = [];
        public int PropagationChecks { get; private set; }
        public bool Fail { get; set; }
        public Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default)
        {
            if (Fail) { throw new InvalidOperationException("provider failure"); }
            Upserts.Add((hostname, ipAddress));
            return Task.CompletedTask;
        }
        public Task WaitForPropagationAsync(string hostname, string expectedIp, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            PropagationChecks++;
            return Task.CompletedTask;
        }
    }
}
