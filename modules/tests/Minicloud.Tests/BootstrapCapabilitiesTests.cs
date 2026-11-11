using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class BootstrapCapabilitiesTests
{
    private const string Installation = "installation-test";
    private const string Operation = "operation-test";

    [Fact]
    public async Task ForeignBootstrapRecordBlocksCapabilitiesEvenWithoutUsersOrOrganizations()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        files.Db.BootstrapRecords.Add(new BootstrapRecord
        {
            InstallationId = "foreign-installation", OperationId = "foreign-operation", State = "required"
        });
        await files.Db.SaveChangesAsync();
        await using var factory = new CapabilitiesFactory(files, Installation);
        using var client = CreateClient(factory);
        using var response = await client.GetAsync("/api/account/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        Assert.Equal("blocked", (await response.Content.ReadFromJsonAsync<AccountCapabilitiesResponse>())!.BootstrapState);
        Assert.Equal(0, factory.Identities.InventoryCalls);
        Assert.Empty(await files.Db.Users.ToListAsync());
        Assert.Empty(await files.Db.Organizations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdentityOutageReturnsSanitized503AndNeverTransitionsToRequired(bool prepared)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        if (prepared)
            await files.Service.PrepareAsync(Installation, Operation, new BootstrapServiceTests.TokenOutput());
        var before = await files.Db.BootstrapRecords.AsNoTracking().SingleOrDefaultAsync();
        await using var factory = new CapabilitiesFactory(files, Installation);
        factory.Identities.Outage = true;
        using var client = CreateClient(factory);
        using (var response = await client.GetAsync("/api/account/capabilities"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            AssertNoStore(response);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-upstream", body);
            Assert.DoesNotContain("synthetic-secret", body);
            Assert.DoesNotContain("required", body);
            var error = (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error;
            Assert.Equal("auth_unavailable", error.Code);
            Assert.Equal("Identity service is temporarily unavailable.", error.Message);
        }
        var after = await files.Db.BootstrapRecords.AsNoTracking().SingleOrDefaultAsync();
        Assert.Equal(before?.State, after?.State);
        Assert.Equal(before?.SetupTokenHash, after?.SetupTokenHash);
        factory.Identities.Outage = false;
        using var recovered = await client.GetAsync("/api/account/capabilities");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        AssertNoStore(recovered);
        Assert.Equal("required", (await recovered.Content.ReadFromJsonAsync<AccountCapabilitiesResponse>())!.BootstrapState);
        Assert.Equal(2, factory.Identities.InventoryCalls);
    }

    [Fact]
    public async Task ReadyCapabilitiesAndHostReplayDoNotConsultUnavailableIdentityDependency()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        var token = new BootstrapServiceTests.TokenOutput();
        await files.Service.PrepareAsync(Installation, Operation, token);
        var original = await files.Service.CompleteHostAsync(Installation, Operation, new CompleteSetupRequest(
            token.Token!, "operator@example.test", "Synthetic-password-123!", "Operator", "Workspace", "workspace"));
        await using var factory = new CapabilitiesFactory(files, Installation);
        factory.Identities.Outage = true;
        files.Identities.Unavailable = true;
        using var client = CreateClient(factory);
        using var response = await client.GetAsync("/api/account/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        var capabilities = (await response.Content.ReadFromJsonAsync<AccountCapabilitiesResponse>())!;
        Assert.Equal("ready", capabilities.BootstrapState);
        Assert.True(capabilities.Password);
        Assert.True(capabilities.Google);
        Assert.True(capabilities.EmailRecovery);
        await using var scope = factory.Services.CreateAsyncScope();
        var replay = await scope.ServiceProvider.GetRequiredService<BootstrapService>()
            .CompleteHostAsync(Installation, Operation, null);
        Assert.Equal(original, replay);
        Assert.Equal(0, factory.Identities.InventoryCalls);
        Assert.Equal(1, files.Identities.Creates);
        Assert.Equal(0, files.Identities.PasswordChanges);
    }

    [Theory]
    [InlineData("required", "required")]
    [InlineData("in_progress", "required")]
    [InlineData("blocked", "blocked")]
    public async Task CapabilitiesCollapseOnlyPendingStates(string state, string expected)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        var token = new BootstrapServiceTests.TokenOutput();
        await files.Service.PrepareAsync(Installation, Operation, token);
        if (state == "in_progress")
        {
            files.Identities.FailAfterCreate = true;
            await Assert.ThrowsAsync<BootstrapException>(() => files.Service.CompleteHostAsync(
                Installation, Operation, new CompleteSetupRequest(token.Token!, "operator@example.test",
                    "Synthetic-password-123!", "Operator", "Workspace", "workspace")));
        }
        else if (state == "blocked")
        {
            var record = await files.Db.BootstrapRecords.SingleAsync();
            record.State = "unknown";
            await files.Db.SaveChangesAsync();
        }
        await using var factory = new CapabilitiesFactory(files, Installation);
        using var client = CreateClient(factory);
        using var response = await client.GetAsync("/api/account/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        Assert.Equal(expected, (await response.Content.ReadFromJsonAsync<AccountCapabilitiesResponse>())!.BootstrapState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid/installation")]
    public async Task MissingOrInvalidInstallationConfigurationIsBlockedWithoutUpstreamAccess(string? installation)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var factory = new CapabilitiesFactory(files, installation);
        factory.Identities.Outage = true;
        using var client = CreateClient(factory);
        using var response = await client.GetAsync("/api/account/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        Assert.Equal("blocked", (await response.Content.ReadFromJsonAsync<AccountCapabilitiesResponse>())!.BootstrapState);
        Assert.Equal(0, factory.Identities.InventoryCalls);
    }

    private static HttpClient CreateClient(CapabilitiesFactory factory) => factory.CreateClient(
        new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.example.test"), HandleCookies = false, AllowAutoRedirect = false
        });

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private sealed class CapabilitiesFactory(BootstrapServiceTests.Fixture files, string? installation)
        : WebApplicationFactory<Program>
    {
        public CapabilitiesIdentityStore Identities { get; } = new(files.Identities);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:RunMigrationsOnStartup"] = "false",
                    ["Database:PasswordFile"] = "",
                    ["ConnectionStrings:Minicloud"] = $"Data Source={Path.Combine(files.Directory, "state.db")};Pooling=False",
                    ["Bootstrap:InstallationId"] = installation,
                    ["Kratos:AdminBaseUrl"] = "http://synthetic-identity.invalid",
                    ["Kratos:GoogleEnabled"] = "true",
                    ["Kratos:EmailRecoveryEnabled"] = "true",
                    ["Secrets:EncryptionKey"] = "synthetic-bootstrap-capabilities-encryption-key",
                    ["Secrets:EncryptionKeyFile"] = ""
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(files.Directory, "state.db")};Pooling=False"));
                services.RemoveAll<IBootstrapIdentityStore>();
                services.AddSingleton<IBootstrapIdentityStore>(Identities);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(files.Clock);
                services.PostConfigure<SecretEncryptionOptions>(options =>
                {
                    options.EncryptionKey = "synthetic-bootstrap-capabilities-encryption-key";
                    options.EncryptionKeyFile = null;
                });
            });
        }
    }

    private sealed class CapabilitiesIdentityStore(BootstrapServiceTests.IdentityStore inner) : IBootstrapIdentityStore
    {
        public bool Outage { get; set; }
        public int InventoryCalls { get; private set; }
        public Task<bool> HasIdentitiesAsync(CancellationToken ct)
        {
            InventoryCalls++;
            if (Outage)
                throw new BootstrapException("auth_unavailable", 503, "private-upstream password=synthetic-secret");
            return inner.HasIdentitiesAsync(ct);
        }
        public Task<BootstrapIdentity?> FindOperationAsync(string installationId, string operationId, CancellationToken ct) =>
            inner.FindOperationAsync(installationId, operationId, ct);
        public Task<BootstrapIdentity> CreateAsync(string installationId, string operationId, string email,
            string password, string displayName, CancellationToken ct) =>
            inner.CreateAsync(installationId, operationId, email, password, displayName, ct);
        public Task<BootstrapIdentity> GetAsync(string identityId, CancellationToken ct) => inner.GetAsync(identityId, ct);
        public Task SetPasswordAsync(string identityId, string password, CancellationToken ct) =>
            inner.SetPasswordAsync(identityId, password, ct);
        public Task RevokeSessionsAsync(string identityId, CancellationToken ct) => inner.RevokeSessionsAsync(identityId, ct);
    }
}
