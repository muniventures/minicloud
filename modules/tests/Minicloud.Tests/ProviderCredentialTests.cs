using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ProviderCredentialTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness harness;
    public ProviderCredentialTests(SqliteTestHarness harness) => this.harness = harness;

    [Fact]
    public async Task ConcurrentAccountsHaveIsolatedAuthorizationAndUnavailableStoreNeverDispatches()
    {
        await using var db = harness.CreateDbContext();
        var org = Organization(); db.Organizations.Add(org); await db.SaveChangesAsync();
        var store = new MemorySecrets();
        var requests = new ConcurrentBag<(string Token, string Query)>();
        var transport = new Transport((request, token) =>
        {
            requests.Add((token, request.RequestUri!.Query));
            return Json("{\"servers\":[]}");
        });
        var factory = Factory(db, store, transport);
        var service = Accounts(db, store, factory);
        var a = await service.CreateAsync(new CreateProviderRequest(org.Id, "A", "hetzner", "token-a"), default);
        await db.SaveChangesAsync();
        var b = await service.CreateAsync(new CreateProviderRequest(org.Id, "B", "hetzner", "token-b"), default);
        await db.SaveChangesAsync();
        requests.Clear();
        var clientA = await factory.CreateAsync(org.Id, a.Id, default);
        var clientB = await factory.CreateAsync(org.Id, b.Id, default);
        await Task.WhenAll(clientA.FindServerAsync("account-a"), clientB.FindServerAsync("account-b"));
        Assert.Contains(requests, x => x.Token == "token-a" && x.Query.Contains("account-a"));
        Assert.Contains(requests, x => x.Token == "token-b" && x.Query.Contains("account-b"));
        Assert.DoesNotContain(requests, x => x.Token == "token-a" && x.Query.Contains("account-b"));
        var count = requests.Count;
        store.Unavailable = true;
        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => factory.CreateAsync(org.Id, a.Id, default));
        Assert.Equal(count, requests.Count);
    }

    [Fact]
    public async Task RotationVerifiesOriginalResourcesAndKeepsOperationGenerationStable()
    {
        await using var db = harness.CreateDbContext();
        var org = Organization(); db.Organizations.Add(org); await db.SaveChangesAsync();
        var serverName = $"minicloud-{org.Slug}-production-api";
        var observedTokens = new ConcurrentBag<string>();
        var transport = new Transport((request, token) =>
        {
            observedTokens.Add(token);
            var match = request.RequestUri!.Query.Contains(Uri.EscapeDataString(serverName)) && token is "original" or "rotated";
            return Json(match ? System.Text.Json.JsonSerializer.Serialize(new
            {
                servers = new[] { new { id = 123, name = serverName, status = "running", public_net = new { ipv4 = new { ip = "203.0.113.10" } } } }
            }) : "{\"servers\":[]}");
        });
        var store = new MemorySecrets();
        var factory = Factory(db, store, transport);
        var accounts = Accounts(db, store, factory);
        var account = await accounts.CreateAsync(new CreateProviderRequest(org.Id, "Account", "hetzner", "original"), default);
        await db.SaveChangesAsync();
        var app = new MinicloudApp { Id = "app_" + Guid.NewGuid().ToString("N"), OrganizationId = org.Id, Name = "API", Slug = "api" };
        db.Apps.Add(app);
        db.ServerInstances.Add(new ServerInstance
        {
            Id = "srv_" + Guid.NewGuid().ToString("N"), OrganizationId = org.Id, AppId = app.Id,
            ProviderAccountId = account.Id, Provider = "Hetzner", ProviderServerId = "123", Region = "nbg1", Status = "running", BootstrapStatus = "ready"
        });
        await db.SaveChangesAsync();
        var originalPath = account.CredentialPath;
        var originalGeneration = account.CredentialGeneration;
        var capturedClient = await factory.CreateAsync(org.Id, account.Id, default);
        var wrongScope = await Assert.ThrowsAsync<ProviderCatalogException>(() => accounts.ReplaceCredentialsAsync(account,
            new ReplaceProviderCredentialRequest("wrong-scope", account.Revision), default));
        Assert.Equal("provider_scope_unverified", wrongScope.Code);
        await using (var fresh = harness.CreateDbContext())
        {
            var persisted = await fresh.ProviderAccounts.SingleAsync(x => x.Id == account.Id);
            Assert.Equal(originalPath, persisted.CredentialPath);
            Assert.Equal(originalGeneration, persisted.CredentialGeneration);
        }
        // Restore tracking after the rejected candidate; production request contexts are disposed.
        await db.Entry(account).ReloadAsync();
        await accounts.ReplaceCredentialsAsync(account, new ReplaceProviderCredentialRequest("rotated", account.Revision), default);
        await db.SaveChangesAsync();
        Assert.NotEqual(originalPath, account.CredentialPath);
        Assert.True(store.Values.ContainsKey(originalPath));
        observedTokens.Clear();
        await capturedClient.FindServerAsync("captured-operation");
        var newClient = await factory.CreateAsync(org.Id, account.Id, default);
        await newClient.FindServerAsync("new-operation");
        Assert.Contains("original", observedTokens);
        Assert.Contains("rotated", observedTokens);
        Assert.DoesNotContain("rotated", account.CredentialPath);
    }

    [Fact]
    public async Task OpenBaoUsesProviderNamespaceExactVersionsAndSanitizedErrors()
    {
        var urls = new List<string>();
        var reject = false;
        using var http = new HttpClient(new Transport((request, _) =>
        {
            urls.Add(request.RequestUri!.PathAndQuery);
            if (reject) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("sensitive-provider-token and private-secret-path") };
            return Json(request.Method == HttpMethod.Post ? "{\"data\":{\"version\":7}}" : "{\"data\":{\"data\":{\"value\":\"credential\"}}}");
        }));
        var store = new OpenBaoSecretStore(http, Options.Create(new OpenBaoSecretStoreOptions { Address = "https://bao.fixture", Token = "bao-token", EnvironmentPrefix = "test" }));
        var reference = await store.PutAsync(new SecretStoreWriteRequest("org", "", "", "credential", "credential")
            { ProviderAccountId = "account", CredentialGeneration = "generation" }, default);
        Assert.Equal("env/test/orgs/org/providers/account/credentials/generation", reference.Path);
        Assert.Equal("credential", await store.GetAsync(reference, default));
        Assert.EndsWith("?version=7", urls[^1]);
        Assert.DoesNotContain(urls, x => x.Contains("/apps/"));
        reject = true;
        var failure = await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => store.GetAsync(reference, default));
        Assert.DoesNotContain("sensitive", failure.Message);
        Assert.DoesNotContain("private-secret-path", failure.Message);
    }

    [Fact]
    public async Task OpenBaoReadsRenewableTokenFileOnEveryRequestAndNeverExposesIt()
    {
        var fixture = CreateOpenBaoTokenFixture();
        try
        {
            var tokenFile = Path.Combine(fixture, "agent-token");
            await File.WriteAllTextAsync(tokenFile, "agent-token-one\r\n");
            var observedTokens = new List<string>();
            var reject = false;
            using var http = new HttpClient(new Transport((request, _) =>
            {
                observedTokens.Add(request.Headers.GetValues("X-Vault-Token").Single());
                return reject
                    ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("agent-token-two") }
                    : Json("{\"data\":{\"data\":{\"value\":\"fixture\"}}}");
            }));
            var store = new OpenBaoSecretStore(http, Options.Create(new OpenBaoSecretStoreOptions
            {
                Address = "https://bao.fixture",
                Token = "legacy-token",
                TokenFile = tokenFile
            }));
            var reference = new StoredSecretReference("openbao", "synthetic/path", null);

            Assert.Equal("fixture", await store.GetAsync(reference, default));
            await File.WriteAllTextAsync(tokenFile, "agent-token-two\n");
            Assert.Equal("fixture", await store.GetAsync(reference, default));
            Assert.Equal(["agent-token-one", "agent-token-two"], observedTokens);

            reject = true;
            var failure = await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => store.GetAsync(reference, default));
            Assert.DoesNotContain("agent-token-two", failure.ToString());
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public async Task OpenBaoConfiguredTokenFileFailsClosedWhenMissingOrEmpty()
    {
        var fixture = CreateOpenBaoTokenFixture();
        try
        {
            var tokenFile = Path.Combine(fixture, "missing-token");
            var requests = 0;
            using var http = new HttpClient(new Transport((_, _) =>
            {
                requests++;
                return Json("{\"data\":{\"data\":{\"value\":\"fixture\"}}}");
            }));
            var store = new OpenBaoSecretStore(http, Options.Create(new OpenBaoSecretStoreOptions
            {
                Address = "https://bao.fixture",
                Token = "legacy-token-must-not-be-used",
                TokenFile = tokenFile
            }));
            var reference = new StoredSecretReference("openbao", "synthetic/path", null);

            var missing = await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => store.GetAsync(reference, default));
            Assert.DoesNotContain("legacy-token-must-not-be-used", missing.ToString());
            await File.WriteAllTextAsync(tokenFile, "\r\n");
            var empty = await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => store.GetAsync(reference, default));
            Assert.DoesNotContain("legacy-token-must-not-be-used", empty.ToString());
            Assert.Equal(0, requests);
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }
    }

    [Fact]
    public async Task VultrPlansRejectIgnoredImageNamesAndAcceptExplicitOsOverrides()
    {
        await using var db = harness.CreateDbContext();
        var org = Organization(); db.Organizations.Add(org);
        var account = new ProviderAccount
        {
            Id = "provider_" + Guid.NewGuid().ToString("N"), OrganizationId = org.Id, Name = "Vultr",
            ProviderType = CloudProvider.Vultr, CredentialPath = "synthetic", CredentialGeneration = "fixture"
        };
        db.ProviderAccounts.Add(account); await db.SaveChangesAsync();
        var catalog = new ProviderCatalogService(db, new IdGenerator(), TimeProvider.System);
        var error = await Assert.ThrowsAsync<ProviderCatalogException>(() => catalog.CreateAsync(
            new CreateProviderPlanRequest(org.Id, account.Id, "ewr", "fixture-sku", Image: "ubuntu-24.04"), default));
        Assert.Equal("plan_mapping_invalid", error.Code);
        var plan = await catalog.CreateAsync(new CreateProviderPlanRequest(org.Id, account.Id, "ewr", "fixture-sku", Image: "2284"), default);
        await db.SaveChangesAsync();
        Assert.Equal("2284", plan.Image);
    }

    [Fact]
    public async Task ConnectionCheckCannotClearPendingCredentialDeletionIntent()
    {
        await using var db = harness.CreateDbContext();
        var org = Organization(); db.Organizations.Add(org);
        var account = new ProviderAccount
        {
            Id = "provider_" + Guid.NewGuid().ToString("N"), OrganizationId = org.Id, Name = "Deleting",
            ProviderType = CloudProvider.Hetzner, CredentialPath = "already-removed", CredentialGeneration = "fixture",
            Enabled = false, ValidationStatus = "deleting"
        };
        db.ProviderAccounts.Add(account); await db.SaveChangesAsync();
        var store = new MemorySecrets();
        var factory = Factory(db, store, new Transport((_, _) => throw new InvalidOperationException("No cloud call is permitted.")));
        var service = Accounts(db, store, factory);
        var failure = await Assert.ThrowsAsync<ProviderCatalogException>(() => service.ValidateAsync(account, default));
        Assert.Equal("provider_deleting", failure.Code);
        Assert.Equal("deleting", account.ValidationStatus);
        Assert.Throws<ProviderCatalogException>(() => service.Update(account, new UpdateProviderRequest(account.Revision, Enabled: true)));
        Assert.False(account.Enabled);
    }

    private static ProviderAccountClientFactory Factory(MinicloudDbContext db, ISecretStore store, Transport transport) =>
        new(db, store, new HttpFactory(transport), Options.Create(new HetznerOptions()), Options.Create(new DigitalOceanOptions()),
            Options.Create(new VultrOptions()), NullLoggerFactory.Instance);
    private static ProviderAccountService Accounts(MinicloudDbContext db, ISecretStore store, ProviderAccountClientFactory factory) =>
        new(db, store, factory, new IdGenerator(), TimeProvider.System, Options.Create(new OpenBaoSecretStoreOptions { EnvironmentPrefix = "test" }));
    private static Organization Organization() => new() { Id = "org_" + Guid.NewGuid().ToString("N"), Name = "Org", Slug = "o" + Guid.NewGuid().ToString("N")[..8] };
    private static string CreateOpenBaoTokenFixture()
    {
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "packaging", "server", "README.md")))
        {
            root = root.Parent;
        }

        if (root is null) throw new InvalidOperationException("Repository root not found.");
        var fixture = Path.Combine(root.FullName, ".local", "artifacts", "openbao-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        return fixture;
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Transport(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request, request.Headers.Authorization?.Parameter ?? ""));
    }
    private sealed class HttpFactory(Transport transport) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(transport, disposeHandler: false);
    }
    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool Unavailable { get; set; }
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct)
        {
            var path = OpenBaoSecretStore.ProviderPathFor("test", request.OrganizationId, request.ProviderAccountId!, request.CredentialGeneration!);
            Values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) => Unavailable
            ? throw new SecretStoreUnavailableException("Fixture unavailable") : Task.FromResult(Values[reference.Path]);
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct) { Values.Remove(reference.Path); return Task.CompletedTask; }
    }
}
