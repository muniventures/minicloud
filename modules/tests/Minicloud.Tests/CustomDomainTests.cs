using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class CustomDomainTests : IAsyncLifetime
{
    private readonly SqliteTestHarness harness = new();

    [Theory]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("api.example.co.uk", "api.example.co.uk")]
    [InlineData("bücher.de", "xn--bcher-kva.de")]
    [InlineData("*.example.com", null)]
    [InlineData("https://example.com", null)]
    [InlineData("example.com/path", null)]
    [InlineData("example.com:443", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("localhost", null)]
    [InlineData("a..example.com", null)]
    [InlineData("example.com..", null)]
    [InlineData("example.1abc", null)]
    [InlineData("a.app.muni.dev", "a.app.muni.dev")]
    [InlineData("cloud.muni.dev", "cloud.muni.dev")]
    public void NormalizesAndValidatesHostnames(string hostname, string? expected) =>
        Assert.Equal(expected, DomainBindingService.NormalizeCustomHostname(hostname));

    [Theory]
    [InlineData(true, "192.0.2.10", true)]
    [InlineData(false, "192.0.2.10", false)]
    [InlineData(true, "192.0.2.11", false)]
    [InlineData(true, "192.0.2.10,192.0.2.11", false)]
    [InlineData(true, "192.0.2.10,2001:db8::1", false)]
    [InlineData(true, "", false)]
    public async Task RequiresOwnershipAndOnlyExpectedAddresses(bool ownership, string ips, bool expected)
    {
        await using var db = harness.CreateDbContext();
        var (app, binding) = await SeedAsync(db);
        var dns = new Dns { Token = ownership ? binding.VerificationToken : "wrong", Ips = ips.Split(',', StringSplitOptions.RemoveEmptyEntries) };
        var verifier = new CustomDomainVerificationService(db, dns, TimeProvider.System);
        await verifier.VerifyAsync(binding, default);
        Assert.Equal(expected ? "verified" : "pending", binding.DnsStatus);
        Assert.Equal(expected ? "pending_apply" : "pending_dns", binding.Status);
        Assert.NotNull(binding.LastCheckedAt);
        Assert.Equal("192.0.2.10", binding.TargetIp);
        Assert.Contains("_minicloud-verification.example.com", dns.Names);
        if (expected)
        {
            binding.ApplyStatus = "applied";
            binding.SslStatus = "ready";
            binding.Status = "active";
            binding.FailureCode = "https_not_ready";
            binding.FailureMessage = "Preserve runtime diagnostics.";
            await db.SaveChangesAsync();
            await verifier.VerifyAsync(binding, default);
            Assert.Equal("active", binding.Status);
            Assert.Equal("Preserve runtime diagnostics.", binding.FailureMessage);
            dns.Token = "removed";
            await verifier.VerifyAsync(binding, default);
            Assert.Equal("pending_dns", binding.Status);
            Assert.Equal("pending", binding.ApplyStatus);
        }
    }

    [Fact]
    public async Task RejectsDuplicateAndPrivateServiceAndPreservesOwnershipAfterUnbinding()
    {
        await using var db = harness.CreateDbContext();
        var (app, binding) = await SeedAsync(db);
        var domains = new DomainBindingService(db, new IdGenerator(), TimeProvider.System);
        DeploymentServiceRequest[] services = [new("web", "image", 80, true, "/", "/"), new("worker", "image", 80, false, "/", "/")];
        var duplicate = await domains.CreateCustomBindingAsync(app, "web", "EXAMPLE.COM", services, default);
        Assert.True(duplicate.IsConflict);
        Assert.False((await domains.CreateCustomBindingAsync(app, "worker", "other.example.com", services, default)).Succeeded);
        Assert.False((await domains.CreateCustomBindingAsync(app, "missing", "other.example.com", services, default)).Succeeded);
        var oldToken = binding.VerificationToken;
        db.Domains.Remove(binding);
        await db.SaveChangesAsync();
        var replacement = await domains.CreateCustomBindingAsync(app, "web", "example.com", services, default);
        Assert.Equal(oldToken, replacement.Binding!.VerificationToken);
        Assert.Equal("pending_dns", replacement.Binding.Status);
    }

    [Fact]
    public async Task DisabledOrDeletedDuringLookupCannotBeReactivated()
    {
        await using var db = harness.CreateDbContext();
        var (_, binding) = await SeedAsync(db);
        var dns = new Dns { Token = binding.VerificationToken, Ips = ["192.0.2.10"] };
        dns.OnQuery = async () =>
        {
            await using var other = harness.CreateDbContext();
            var row = await other.Domains.SingleAsync();
            row.Status = "disabled";
            row.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await other.SaveChangesAsync();
        };
        await new CustomDomainVerificationService(db, dns, TimeProvider.System).VerifyAsync(binding, default);
        Assert.Equal("disabled", binding.Status);
    }

    [Fact]
    public async Task ResolverFailureFailsClosedWithSafeDiagnostic()
    {
        await using var db = harness.CreateDbContext();
        var (_, binding) = await SeedAsync(db);
        var dns = new Dns { OnQuery = () => throw new InvalidOperationException("secret provider diagnostic") };
        await new CustomDomainVerificationService(db, dns, TimeProvider.System).VerifyAsync(binding, default);
        Assert.Equal("pending", binding.DnsStatus);
        Assert.Equal("dns_lookup_failed", binding.FailureCode);
        Assert.DoesNotContain("secret", binding.FailureMessage!);
    }

    private async Task<(MinicloudApp, Domain)> SeedAsync(Minicloud.Common.Data.MinicloudDbContext db)
    {
        var org = new Organization { Id = "org", Name = "Org", Slug = "org" };
        var app = new MinicloudApp { Id = "app", OrganizationId = org.Id, Organization = org, Name = "App", Slug = "app" };
        db.Apps.Add(app);
        db.ServerInstances.Add(new ServerInstance { Id = "server", AppId = app.Id, OrganizationId = org.Id,
            Provider = "hetzner", ProviderServerId = "owned", IpAddress = "192.0.2.10", Region = "test", Status = "ready", BootstrapStatus = "ready" });
        await db.SaveChangesAsync();
        db.CustomDomainRegistrations.Add(new CustomDomainRegistration
        {
            Id = "reg", OrganizationId = org.Id, AppId = app.Id, Hostname = "example.com", VerificationToken = "mc-domain-verification=test",
            Status = "verified", LastCheckedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var result = await new DomainBindingService(db, new IdGenerator(), TimeProvider.System).CreateCustomBindingAsync(app, "web", "example.com",
            [new("web", "image", 80, true, "/", "/")], default);
        await db.SaveChangesAsync();
        Assert.NotNull(result.Binding);
        return (app, result.Binding);
    }

    private sealed class Dns : ICustomDomainDnsResolver
    {
        public string? Token { get; set; }
        public string[] Ips { get; set; } = [];
        public List<string> Names { get; } = [];
        public Func<Task>? OnQuery { get; set; }
        public async Task<IReadOnlyList<string>> QueryAsync(string hostname, int type, CancellationToken cancellationToken)
        {
            Names.Add(hostname);
            if (OnQuery is not null) { await OnQuery(); }
            return type == 16 ? ["\"" + Token + "\""] : Ips.Where(ip => ip.Contains(':') == (type == 28)).ToArray();
        }
    }

    public Task InitializeAsync() => harness.InitializeAsync();
    public Task DisposeAsync() => harness.DisposeAsync();
}
