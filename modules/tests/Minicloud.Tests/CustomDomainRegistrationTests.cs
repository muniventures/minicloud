using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Services;
using Minicloud.Common.Migrations;

namespace Minicloud.Tests;

public sealed class CustomDomainRegistrationTests : IAsyncLifetime
{
    private readonly SqliteTestHarness harness = new();

    [Theory]
    [InlineData("apps.operator.test", true)]
    [InlineData("web.apps.operator.test", true)]
    [InlineData("api.operator.test", true)]
    [InlineData("child.api.operator.test", true)]
    [InlineData("customer.test", false)]
    [InlineData("cloud.muni.dev", false)]
    public async Task ReservesOnlyConfiguredInstallationDomains(string hostname, bool reserved)
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var service = new CustomDomainRegistrationService(db, new IdGenerator(), new Dns(), TimeProvider.System,
            new AuditEventService(db, new IdGenerator(), TimeProvider.System),
            Microsoft.Extensions.Options.Options.Create(new ManagedDomainOptions
            {
                Suffix = "apps.operator.test", ReservedHostnames = ["api.operator.test"]
            }));
        if (reserved)
            await Assert.ThrowsAsync<ArgumentException>(() => service.RegisterAsync(app.OrganizationId, app.Id, hostname, default));
        else
            Assert.Equal(hostname, (await service.RegisterAsync(app.OrganizationId, app.Id, hostname, default)).Hostname);
    }

    [Fact]
    public async Task OwnershipIsVerifiedWithoutAnyServiceOrServerAndDeletedWithApp()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var dns = new Dns();
        var registrations = Service(db, dns);
        var row = await registrations.RegisterAsync(app.OrganizationId, app.Id, "EXAMPLE.COM", default);
        Assert.Empty(await db.Domains.ToListAsync());
        Assert.Empty(await db.ServerInstances.ToListAsync());
        Assert.Equal("pending", row.Status);
        await registrations.VerifyAsync(row, default);
        Assert.Equal("pending", row.Status);
        dns.Token = row.VerificationToken;
        await registrations.VerifyAsync(row, default);
        Assert.Equal("verified", row.Status);
        Assert.All(dns.Queries, query => Assert.Equal(("_minicloud-verification.example.com", 16), query));
        db.Apps.Remove(app);
        await db.SaveChangesAsync();
        Assert.Empty(await db.CustomDomainRegistrations.ToListAsync());
    }

    [Fact]
    public async Task UnverifiedDomainsCannotBeAssignedAndVerifiedApexAuthorizesSubdomains()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var dns = new Dns();
        var registrations = Service(db, dns);
        var row = await registrations.RegisterAsync(app.OrganizationId, app.Id, "example.com", default);
        var domains = new DomainBindingService(db, new IdGenerator(), TimeProvider.System);
        DeploymentServiceRequest[] services = [new("api", "image", 80, true, "/", "/")];
        var rejected = await domains.CreateCustomBindingAsync(app, "api", "api.example.com", services, default);
        Assert.False(rejected.Succeeded);
        Assert.Empty(await db.Domains.ToListAsync());
        dns.Token = row.VerificationToken;
        await registrations.VerifyAsync(row, default);
        var accepted = await domains.CreateCustomBindingAsync(app, "api", "api.example.com", services, default);
        Assert.True(accepted.Succeeded);
        await db.SaveChangesAsync();
        Assert.Equal(row.VerificationToken, accepted.Binding!.VerificationToken);
        Assert.Equal("pending_dns", accepted.Binding.Status);
        accepted.Binding.DnsStatus = "verified";
        accepted.Binding.Status = "active";
        await db.SaveChangesAsync();
        dns.Token = "removed";
        await registrations.VerifyAsync(row, default);
        Assert.Equal("pending", row.Status);
        Assert.Equal("pending_dns", accepted.Binding.Status);
        Assert.Equal("pending", accepted.Binding.DnsStatus);
        Assert.Equal("dns_ownership_pending", accepted.Binding.FailureCode);
        Assert.Equal("pending", accepted.Binding.SslStatus);
    }

    [Fact]
    public async Task RegistrationIsIdempotentAndCannotBeUsedByAnotherOrganization()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var otherOrg = new Organization { Id = "other-org", Name = "Other", Slug = "other" };
        var other = new MinicloudApp { Id = "other-app", OrganizationId = otherOrg.Id, Organization = otherOrg, Name = "Other", Slug = "other" };
        db.Apps.Add(other);
        await db.SaveChangesAsync();
        var dns = new Dns();
        var registrations = Service(db, dns);
        var row = await registrations.RegisterAsync(app.OrganizationId, app.Id, "example.com", default);
        Assert.Equal(row.Id, (await registrations.RegisterAsync(app.OrganizationId, app.Id, "example.com", default)).Id);
        await Assert.ThrowsAsync<DomainBindingConflictException>(() => registrations.RegisterAsync(otherOrg.Id, other.Id, "example.com", default));
        dns.Token = row.VerificationToken;
        await registrations.VerifyAsync(row, default);
        var result = await new DomainBindingService(db, new IdGenerator(), TimeProvider.System).CreateCustomBindingAsync(other, "web", "other.example.com",
            [new("web", "image", 80, true, "/", "/")], default);
        Assert.False(result.Succeeded);
        Assert.Empty(await db.Domains.ToListAsync());
    }

    [Fact]
    public async Task UpgradePreservesExistingCustomDomainTokensAndBindings()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        db.Domains.Add(new Domain { Id = "old", OrganizationId = app.OrganizationId, AppId = app.Id, ServiceName = "web",
            Hostname = "existing.example.com", Kind = "custom_domain", VerificationToken = "mc-domain-verification=old",
            DnsStatus = "verified", ApplyStatus = "applied", SslStatus = "ready", Status = "active" });
        await db.SaveChangesAsync();
        db.CustomDomainRegistrations.Add(new CustomDomainRegistration { Id = "existing", OrganizationId = app.OrganizationId, AppId = app.Id,
            Hostname = "existing.example.com", VerificationToken = "mc-domain-verification=old", Status = "verified" });
        await db.SaveChangesAsync();
        var sql = new AppOwnedCustomDomains().UpOperations.OfType<SqlOperation>().Last().Sql;
        await db.Database.ExecuteSqlRawAsync(sql);
        var row = await db.CustomDomainRegistrations.SingleAsync();
        Assert.Equal("existing.example.com", row.Hostname);
        Assert.Equal("mc-domain-verification=old", row.VerificationToken);
        Assert.Equal("verified", row.Status);
        Assert.Null(row.LastCheckedAt);
        Assert.Equal("active", (await db.Domains.SingleAsync()).Status);
    }

    [Fact]
    public async Task DomainsAreExclusiveToAnAppAndSiblingSubdomainsCanBelongToDifferentApps()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var other = new MinicloudApp { Id = "second", OrganizationId = app.OrganizationId, Name = "Second", Slug = "second" };
        db.Apps.Add(other);
        await db.SaveChangesAsync();
        var dns = new Dns();
        var registrations = Service(db, dns);
        var first = await registrations.RegisterAsync(app.OrganizationId, app.Id, "app1.example.com", default);
        await Assert.ThrowsAsync<DomainBindingConflictException>(() => registrations.RegisterAsync(app.OrganizationId, other.Id, "app1.example.com", default));
        var second = await registrations.RegisterAsync(app.OrganizationId, other.Id, "app2.example.com", default);
        dns.Token = first.VerificationToken;
        await registrations.VerifyAsync(first, default);
        dns.Token = second.VerificationToken;
        await registrations.VerifyAsync(second, default);
        var domains = new DomainBindingService(db, new IdGenerator(), TimeProvider.System);
        DeploymentServiceRequest[] services = [new("api", "image", 80, true, "/", "/")];
        Assert.False((await domains.CreateCustomBindingAsync(other, "api", "api.app1.example.com", services, default)).Succeeded);
        Assert.True((await domains.CreateCustomBindingAsync(app, "api", "api.app1.example.com", services, default)).Succeeded);
        Assert.True((await domains.CreateCustomBindingAsync(other, "api", "api.app2.example.com", services, default)).Succeeded);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.Domains.CountAsync());
    }

    [Fact]
    public async Task UnassignedRegistrationCannotRouteAndExplicitAppRegistrationRequiresFreshProof()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var dns = new Dns();
        var row = new CustomDomainRegistration { Id = "orphan", OrganizationId = app.OrganizationId,
            Hostname = "example.com", VerificationToken = "original-token", Status = "verified", LastCheckedAt = DateTimeOffset.UtcNow };
        db.CustomDomainRegistrations.Add(row);
        await db.SaveChangesAsync();
        var service = Service(db, dns);
        await service.VerifyAsync(row, default);
        Assert.Empty(dns.Queries);
        var domains = new DomainBindingService(db, new IdGenerator(), TimeProvider.System);
        DeploymentServiceRequest[] services = [new("api", "image", 80, true, "/", "/")];
        Assert.False((await domains.CreateCustomBindingAsync(app, "api", "api.example.com", services, default)).Succeeded);
        var assigned = await service.RegisterAsync(app.OrganizationId, app.Id, "example.com", default);
        Assert.Equal(app.Id, assigned.AppId);
        Assert.Equal("original-token", assigned.VerificationToken);
        Assert.Equal("pending", assigned.Status);
        Assert.Null(assigned.LastCheckedAt);
        Assert.False((await domains.CreateCustomBindingAsync(app, "api", "api.example.com", services, default)).Succeeded);
        dns.Token = assigned.VerificationToken;
        await service.VerifyAsync(assigned, default);
        Assert.True((await domains.CreateCustomBindingAsync(app, "api", "api.example.com", services, default)).Succeeded);
    }

    [Fact]
    public async Task RemovalIsAppScopedAndAllowsFreshRegistration()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var service = Service(db, new Dns());
        var row = await service.RegisterAsync(app.OrganizationId, app.Id, "example.com", default);
        Assert.False(await service.RemoveAsync("another-app", row.Id, "test", default));
        Assert.Single(await db.CustomDomainRegistrations.ToListAsync());
        Assert.True(await service.RemoveAsync(app.Id, row.Id, "test", default));
        Assert.Empty(await db.CustomDomainRegistrations.ToListAsync());
        Assert.Contains(await db.AuditEvents.ToListAsync(), x => x.Action == "domain.registration_removed" && x.AppId == app.Id);
        Assert.False(await service.RemoveAsync(app.Id, row.Id, "test", default));
        var replacement = await service.RegisterAsync(app.OrganizationId, app.Id, "example.com", default);
        Assert.NotEqual(row.VerificationToken, replacement.VerificationToken);
        Assert.Equal("pending", replacement.Status);
    }

    [Fact]
    public async Task RemovalRefusesAssignedDomainsIncludingDisabledBindings()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var dns = new Dns();
        var service = Service(db, dns);
        var row = await service.RegisterAsync(app.OrganizationId, app.Id, "example.com", default);
        dns.Token = row.VerificationToken;
        await service.VerifyAsync(row, default);
        var domains = new DomainBindingService(db, new IdGenerator(), TimeProvider.System);
        var created = await domains.CreateCustomBindingAsync(app, "api", "api.example.com", [new("api", "image", 80, true, "/", "/")], default);
        await db.SaveChangesAsync();
        Assert.Equal(row.Id, created.Binding!.CustomDomainRegistrationId);
        created.Binding.Status = "disabled";
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<DomainBindingConflictException>(() => service.RemoveAsync(app.Id, row.Id, "test", default));
        Assert.Equal("domain_in_use", error.Code);
        Assert.Single(await db.CustomDomainRegistrations.ToListAsync());
        db.Domains.Remove(created.Binding);
        await db.SaveChangesAsync();
        Assert.True(await service.RemoveAsync(app.Id, row.Id, "test", default));
    }

    [Fact]
    public async Task AssignmentCannotPersistAfterConcurrentRegistrationRemoval()
    {
        await using var db = harness.CreateDbContext();
        var app = await SeedAsync(db);
        var dns = new Dns();
        var service = Service(db, dns);
        var row = await service.RegisterAsync(app.OrganizationId, app.Id, "example.com", default);
        dns.Token = row.VerificationToken;
        await service.VerifyAsync(row, default);
        var domains = new DomainBindingService(db, new IdGenerator(), TimeProvider.System);
        Assert.True((await domains.CreateCustomBindingAsync(app, "api", "api.example.com", [new("api", "image", 80, true, "/", "/")], default)).Succeeded);
        await using var concurrent = harness.CreateDbContext();
        Assert.True(await Service(concurrent, dns).RemoveAsync(app.Id, row.Id, "test", default));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static CustomDomainRegistrationService Service(Minicloud.Common.Data.MinicloudDbContext db, Dns dns) =>
        new(db, new IdGenerator(), dns, TimeProvider.System, new AuditEventService(db, new IdGenerator(), TimeProvider.System));

    private static async Task<MinicloudApp> SeedAsync(Minicloud.Common.Data.MinicloudDbContext db)
    {
        var org = new Organization { Id = "org", Name = "Org", Slug = "org" };
        var app = new MinicloudApp { Id = "app", OrganizationId = org.Id, Organization = org, Name = "App", Slug = "app" };
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app;
    }

    private sealed class Dns : ICustomDomainDnsResolver
    {
        public string? Token { get; set; }
        public List<(string, int)> Queries { get; } = [];
        public Task<IReadOnlyList<string>> QueryAsync(string hostname, int type, CancellationToken cancellationToken)
        {
            Queries.Add((hostname, type));
            return Task.FromResult<IReadOnlyList<string>>(["\"" + Token + "\""]);
        }
    }
    public Task InitializeAsync() => harness.InitializeAsync();
    public Task DisposeAsync() => harness.DisposeAsync();
}
