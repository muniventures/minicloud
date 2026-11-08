using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Models;
using Minicloud.Worker.Deletion;
using Minicloud.Worker.Pipeline;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Providers.Models;

namespace Minicloud.Worker.Tests;

public sealed class AppDeletionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task CleansRegisteredInfrastructureAndPreservesAudit(bool failDns, bool manageDnsRecords)
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync();
        await using var db = harness.CreateDbContext();
        app = await db.Apps.SingleAsync();
        app.DeletionStatus = "queued";
        db.ServerInstances.Add(new ServerInstance { Id = "server", AppId = app.Id, OrganizationId = org.Id,
            Provider = "hetzner", ProviderServerId = "owned", Region = "test", Status = "ready", BootstrapStatus = "ready" });
        db.Domains.Add(new Domain { Id = "domain", AppId = app.Id, OrganizationId = org.Id,
            ServiceName = "web", Status = "active", DnsStatus = "verified", ApplyStatus = "applied", Hostname = "test.app.muni.dev", Kind = "minicloud_subdomain", SslStatus = "ready" });
        db.AuditEvents.Add(new AuditEvent { Id = "audit", AppId = app.Id, OrganizationId = org.Id, Actor = "user", Action = "app.delete_requested" });
        await db.SaveChangesAsync();
        var provider = new FakeProvider();
        var dns = new Dns { Fail = failDns };
        var processor = new AppDeletionProcessor(db, _ => provider, dns, NullLogger<AppDeletionProcessor>.Instance,
            Options.Create(new Minicloud.Common.Services.ManagedDomainOptions { ManageDnsRecords = manageDnsRecords }));
        Assert.True(await processor.ProcessNextAsync(default));
        Assert.Equal(["owned"], provider.Deleted);
        if (failDns && manageDnsRecords)
        {
            Assert.Equal("failed", (await db.Apps.SingleAsync()).DeletionStatus);
            Assert.Single(await db.ServerInstances.ToListAsync());
            dns.Fail = false;
            (await db.Apps.SingleAsync()).DeletionStatus = "queued";
            await db.SaveChangesAsync();
            await processor.ProcessNextAsync(default);
        }
        Assert.Empty(await db.Apps.ToListAsync());
        Assert.Empty(await db.ServerInstances.ToListAsync());
        Assert.Empty(await db.Domains.ToListAsync());
        Assert.All(await db.AuditEvents.ToListAsync(), x => Assert.Null(x.AppId));
    }

    [Fact]
    public async Task CustomerDomainsAreDeletedWithoutCallingCloudflare()
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync();
        await using var db = harness.CreateDbContext();
        (await db.Apps.SingleAsync()).DeletionStatus = "queued";
        db.Domains.Add(new Domain { Id = "custom-domain", AppId = app.Id, OrganizationId = org.Id,
            ServiceName = "web", Status = "pending_dns", DnsStatus = "pending", ApplyStatus = "pending",
            Hostname = "example.com", Kind = "custom_domain", SslStatus = "pending" });
        await db.SaveChangesAsync();
        var processor = new AppDeletionProcessor(db, _ => throw new Exception("No server"), new Dns { Fail = true }, NullLogger<AppDeletionProcessor>.Instance);
        await processor.ProcessNextAsync(default);
        Assert.Empty(await db.Apps.ToListAsync());
        Assert.Empty(await db.Domains.ToListAsync());
    }

    [Fact]
    public async Task EmptyApplicationDoesNotNeedProviderConfiguration()
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        await harness.SeedOrgAndAppAsync();
        await using var db = harness.CreateDbContext();
        (await db.Apps.SingleAsync()).DeletionStatus = "queued";
        await db.SaveChangesAsync();
        var processor = new AppDeletionProcessor(db, _ => throw new Exception("Provider should not be used"), new Dns(), NullLogger<AppDeletionProcessor>.Instance);
        await processor.ProcessNextAsync(default);
        Assert.Empty(await db.Apps.ToListAsync());
    }

    [Fact]
    public async Task SharedServerFailsBeforeAnyExternalDeletion()
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync("first");
        var (otherOrg, other) = await harness.SeedOrgAndAppAsync("other");
        await using var db = harness.CreateDbContext();
        (await db.Apps.SingleAsync(x => x.Id == app.Id)).DeletionStatus = "queued";
        foreach (var pair in new[] { (org, app), (otherOrg, other) })
        {
            db.ServerInstances.Add(new ServerInstance { Id = pair.Item2.Id, AppId = pair.Item2.Id, OrganizationId = pair.Item1.Id,
                Provider = "hetzner", ProviderServerId = "shared", Region = "test", Status = "ready", BootstrapStatus = "ready" });
        }
        await db.SaveChangesAsync();
        var provider = new FakeProvider();
        var processor = new AppDeletionProcessor(db, _ => provider, new Dns(), NullLogger<AppDeletionProcessor>.Instance);
        await processor.ProcessNextAsync(default);
        Assert.Empty(provider.Deleted);
        Assert.Equal("failed", (await db.Apps.SingleAsync(x => x.Id == app.Id)).DeletionStatus);
    }

    [Fact]
    public async Task DeletesChildrenBeforeParent()
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync();
        await using var db = harness.CreateDbContext();
        (await db.Apps.SingleAsync()).DeletionStatus = "queued";
        db.Apps.Add(new MinicloudApp { Id = "child", Name = "Child", Slug = "child", ParentAppId = app.Id, OrganizationId = org.Id, DeletionStatus = "queued" });
        await db.SaveChangesAsync();
        var processor = new AppDeletionProcessor(db, _ => new FakeProvider(), new Dns(), NullLogger<AppDeletionProcessor>.Instance);
        await processor.ProcessNextAsync(default);
        Assert.Equal(app.Id, (await db.Apps.SingleAsync()).Id);
        await processor.ProcessNextAsync(default);
        Assert.Empty(await db.Apps.ToListAsync());
    }

    [Fact]
    public async Task DeletesOnlyRegisteredAccountWhenAnotherAccountHasTheSameExternalId()
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync("account-delete");
        var (otherOrg, other) = await harness.SeedOrgAndAppAsync("account-other");
        await using var db = harness.CreateDbContext();
        foreach (var pair in new[] { (org, app), (otherOrg, other) })
        {
            db.ProviderAccounts.Add(new ProviderAccount { Id = pair.Item2.Id + "-account", OrganizationId = pair.Item1.Id,
                Name = "Hetzner", ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/path", CredentialGeneration = "test" });
            db.ServerInstances.Add(new ServerInstance { Id = pair.Item2.Id, AppId = pair.Item2.Id, OrganizationId = pair.Item1.Id,
                ProviderAccountId = pair.Item2.Id + "-account", Provider = "hetzner", ProviderServerId = "same-id",
                Region = "nbg1", Status = "active", BootstrapStatus = "ready" });
        }
        await db.SaveChangesAsync();
        var provider = new FakeProvider();
        var factory = new AccountFactory(provider);
        var processor = new AppDeletionProcessor(db, _ => throw new Exception("Must not use global credentials."), new Dns(),
            NullLogger<AppDeletionProcessor>.Instance, accountClients: factory);
        await processor.DeleteInfrastructureAsync(app, default);
        Assert.Equal([app.Id + "-account"], factory.Accounts);
        Assert.Equal(["same-id"], provider.Deleted);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, false)]
    public async Task UnregisteredAccountPlacementUsesReservedAccountOrFailsClosed(
        bool canceledAttempt, bool serverExists, bool missingAccount, bool deleted)
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync();
        await using var db = harness.CreateDbContext();
        var account = new ProviderAccount { Id = "reserved", OrganizationId = org.Id, Name = "Hetzner",
            ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/path", CredentialGeneration = "test" };
        db.ProviderPlans.Add(new ProviderPlan { Id = "plan", ProviderAccount = account, ProviderAccountId = account.Id,
            OrganizationId = org.Id, Name = "Small", NormalizedName = "small", Region = "nbg1", Sku = "cx23" });
        app = await db.Apps.SingleAsync();
        app.PlanId = "plan";
        app.DeletionStatus = "queued";
        db.Deployments.Add(new Deployment { Id = "attempt", AppId = app.Id, OrganizationId = org.Id, PlanId = "plan",
            ProviderAccountId = missingAccount ? null : account.Id, ProviderType = "Hetzner",
            Status = canceledAttempt ? DeploymentStatus.Canceled : DeploymentStatus.Failed,
            ProviderCreationAttempted = canceledAttempt });
        await db.SaveChangesAsync();
        var provider = new FakeProvider { FoundServer = serverExists ? new("unregistered", "name", "192.0.2.1", "active") : null };
        var factory = new AccountFactory(provider);
        var processor = new AppDeletionProcessor(db, _ => throw new Exception("Must not use global credentials"),
            new Dns(), NullLogger<AppDeletionProcessor>.Instance, accountClients: factory);
        Assert.True(await processor.ProcessNextAsync(default));
        var canDiscover = !canceledAttempt && !missingAccount;
        Assert.Equal(canDiscover ? new[] { account.Id } : [], factory.Accounts);
        Assert.Empty(provider.Deleted);
        if (deleted) { Assert.Empty(await db.Apps.ToListAsync()); }
        else { Assert.Equal("failed", (await db.Apps.SingleAsync()).DeletionStatus); }
        Assert.Equal(canDiscover ? new[] { $"minicloud-{org.Slug}-production-{app.Slug}" } : [], provider.Lookups);
        if (canceledAttempt)
        {
            Assert.True((await db.Deployments.SingleAsync()).ProviderCreationAttempted);
            Assert.True(await db.Apps.AnyAsync(x => x.Id == app.Id));
        }
    }

    [Fact]
    public async Task AmbiguousAllocationReservationSurvivesAppDeletionEvenWithoutDiscovery()
    {
        await using var harness = new WorkerSqliteTestHarness();
        await harness.InitializeAsync();
        var (org, app) = await harness.SeedOrgAndAppAsync("unresolved-allocation");
        await using var db = harness.CreateDbContext();
        db.ProviderAccounts.Add(new ProviderAccount { Id = "unresolved-account", OrganizationId = org.Id,
            Name = "Original", ProviderType = CloudProvider.Hetzner, CredentialPath = "synthetic/path", CredentialGeneration = "test" });
        db.Deployments.Add(new Deployment { Id = "unresolved-attempt", AppId = app.Id, OrganizationId = org.Id,
            ProviderAccountId = "unresolved-account", ProviderType = "Hetzner", ProviderCreationAttempted = true,
            Status = DeploymentStatus.Failed });
        await db.SaveChangesAsync();
        var provider = new FakeProvider();
        var factory = new AccountFactory(provider);
        var processor = new AppDeletionProcessor(db, _ => throw new Exception("Must not use global credentials."), new Dns(),
            NullLogger<AppDeletionProcessor>.Instance, accountClients: factory);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => processor.DeleteInfrastructureAsync(app, default));
        Assert.Contains("unresolved", error.Message);
        Assert.Empty(factory.Accounts);
        Assert.Empty(provider.Deleted);
        Assert.True(await db.Apps.AnyAsync(x => x.Id == app.Id));
        Assert.True((await db.Deployments.SingleAsync()).ProviderCreationAttempted);
    }

    private sealed class AccountFactory(IVpsProviderClient client) : ProviderAccountClientFactory(null!, null!, null!, null!, null!, null!, null!)
    {
        public List<string> Accounts { get; } = [];
        public override Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId, CancellationToken ct, bool allowDisabled = true)
        {
            Accounts.Add(providerAccountId);
            return Task.FromResult(client);
        }
    }

    private sealed class FakeProvider : IVpsProviderClient
    {
        public List<string> Deleted { get; } = [];
        public List<string> Lookups { get; } = [];
        public ProviderServerInfo? FoundServer { get; set; }
        public CloudProvider Provider => CloudProvider.Hetzner;
        public Task DeleteServerAsync(string id, CancellationToken cancellationToken = default) { Deleted.Add(id); return Task.CompletedTask; }
        public Task<ProviderServerInfo?> FindServerAsync(string name, CancellationToken cancellationToken = default)
        {
            Lookups.Add(name);
            return Task.FromResult(FoundServer);
        }
        public Task<ProviderServerInfo> CreateServerAsync(CreateServerRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ProviderServerStatus> GetServerStatusAsync(string id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
    private sealed class Dns : ICloudflareDnsClient
    {
        public bool Fail { get; set; }
        public Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default) => Fail ? throw new Exception("DNS unavailable") : Task.CompletedTask;
        public Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task WaitForPropagationAsync(string hostname, string ipAddress, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
