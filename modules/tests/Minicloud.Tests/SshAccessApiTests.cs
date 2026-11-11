using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

// T50 acceptance coverage for the SSH inventory and environment selection HTTP contract.
// Provider traffic is served by a synthetic in-process handler; no keys are registered externally.
public sealed class SshAccessApiTests : IAsyncLifetime
{
    private const string OrganizationId = "org_ssh_api";
    private const string OtherOrganizationId = "org_ssh_api_other";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SshApiFactory _factory = default!;
    private string _ownerToken = default!;

    private static readonly string LaptopKey = Ed25519(1, "laptop");
    private static readonly string SharedKey = Ed25519(2, "shared");
    private static readonly string NewKey = Ed25519(3, "new");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new SshApiFactory(_connection);
        _factory.CreateClient().Dispose();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "SSH API", Slug = "ssh-api" });
        db.Organizations.Add(new Organization { Id = OtherOrganizationId, Name = "SSH API other", Slug = "ssh-api-other" });
        await db.SaveChangesAsync();
        _ownerToken = await AddKeyAsync(OrganizationId, ApiKeyScopes.All);
        _factory.Cloud.Keys["synthetic-a1"] = [(42, "Laptop", LaptopKey), (43, "Shared", SharedKey)];
        _factory.Cloud.Keys["synthetic-a2"] = [(77, "Shared on second", SharedKey)];
        _factory.Cloud.Keys["synthetic-other"] = [(90, "Other tenant", LaptopKey)];
    }

    [Fact]
    public async Task InventoryIsIsolatedByOrganizationAndProviderAccount()
    {
        using var owner = Client(_ownerToken);
        var first = await CreateAccountAsync(owner, "First", "synthetic-a1");
        var second = await CreateAccountAsync(owner, "Second", "synthetic-a2");

        var firstRaw = await (await owner.GetAsync(KeysUrl(first.Id))).Content.ReadAsStringAsync();
        var firstKeys = JsonSerializer.Deserialize<ProviderSshKeyResponse[]>(firstRaw, JsonOptions)!;
        var secondKeys = await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(second.Id)));

        Assert.Equal(["Laptop", "Shared"], firstKeys.Select(x => x.Name));
        Assert.All(firstKeys, key => Assert.Equal(first.Id, key.ProviderAccountId));
        Assert.All(firstKeys, key => Assert.Equal("discovered", key.RegistrationState));
        Assert.DoesNotContain("synthetic-a1", firstRaw);
        Assert.DoesNotContain("providerKeyId", firstRaw, StringComparison.OrdinalIgnoreCase);
        var sharedInSecond = Assert.Single(secondKeys);
        var sharedInFirst = firstKeys.Single(x => x.Name == "Shared");
        Assert.Equal(sharedInFirst.Fingerprint, sharedInSecond.Fingerprint);
        Assert.NotEqual(sharedInFirst.Id, sharedInSecond.Id);
        Assert.Equal(second.Id, sharedInSecond.ProviderAccountId);

        var otherToken = await AddKeyAsync(OtherOrganizationId, ApiKeyScopes.All);
        using var other = Client(otherToken);
        var requestsBefore = _factory.Cloud.SshKeyRequests("synthetic-a1");
        await ErrorAsync(await other.GetAsync(KeysUrl(first.Id)), "forbidden", HttpStatusCode.Forbidden);
        await ErrorAsync(await other.GetAsync($"/api/providers/{first.Id}/ssh-keys?organizationId={OtherOrganizationId}"),
            "provider_not_found", HttpStatusCode.NotFound);
        Assert.Equal(requestsBefore, _factory.Cloud.SshKeyRequests("synthetic-a1"));

        // Local key ids are not portable across tenants, even when the same public key exists there.
        var otherAccount = await CreateAccountAsync(other, "Other", "synthetic-other", OtherOrganizationId);
        Assert.Single(await SuccessAsync<ProviderSshKeyResponse[]>(await other.GetAsync(
            $"/api/providers/{otherAccount.Id}/ssh-keys?organizationId={OtherOrganizationId}")));
        var otherPlan = await CreatePlanAsync(other, otherAccount.Id, "Other small", OtherOrganizationId);
        var otherEnv = await CreateAppAsync(other, "other-app", otherPlan.Name, organizationId: OtherOrganizationId);
        var laptopInFirst = firstKeys.Single(x => x.Name == "Laptop");
        await ErrorAsync(await other.PatchAsJsonAsync(EnvUrl(otherEnv),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("existing", KeyId: laptopInFirst.Id, ExpectedRevision: 0))),
            "ssh_key_not_found", HttpStatusCode.NotFound);

        // Within one organization, a key id from another provider account is also not selectable.
        var secondPlan = await CreatePlanAsync(owner, second.Id, "Second small");
        var secondEnv = await CreateAppAsync(owner, "second-app", secondPlan.Name);
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(secondEnv),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("existing", KeyId: sharedInFirst.Id, ExpectedRevision: 0))),
            "ssh_key_not_found", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task InventoryCoalescesNewDuplicateFingerprintsAndRepeatedEntries()
    {
        using var owner = Client(_ownerToken);
        var account = await CreateAccountAsync(owner, "Duplicates", "synthetic-a1");
        _factory.Cloud.Keys["synthetic-a1"] =
        [
            (42, "Laptop", LaptopKey),
            (44, "Laptop alias", LaptopKey),
            (44, "Laptop alias", LaptopKey)
        ];

        var key = Assert.Single(await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id))));
        Assert.Equal("Laptop alias", key.Name);
        Assert.Equal("discovered", key.RegistrationState);
        var refreshed = Assert.Single(await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id))));
        Assert.Equal(key, refreshed);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var stored = Assert.Single(await db.ProviderSshKeys.ToListAsync());
        Assert.Equal(key.Id, stored.Id);
        Assert.Equal(key.Fingerprint, stored.Fingerprint);
        Assert.Equal("44", stored.ProviderKeyId);
        Assert.Equal(0, _factory.Cloud.Posts);
    }

    [Fact]
    public async Task InventoryCoalescesExistingAndNewKeysWithoutChangingSelectionIdentity()
    {
        using var owner = Client(_ownerToken);
        var account = await CreateAccountAsync(owner, "Mixed", "synthetic-a1");
        var initial = await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id)));
        var laptop = initial.Single(x => x.Name == "Laptop");
        var plan = await CreatePlanAsync(owner, account.Id, "Mixed small");
        var env = await CreateAppAsync(owner, "duplicate-selection", plan.Name,
            new SshAccessRequest("existing", KeyId: laptop.Id));
        _factory.Cloud.Keys["synthetic-a1"] =
        [
            (44, "Laptop alias", LaptopKey),
            (42, "Laptop", LaptopKey),
            (50, "New", NewKey),
            (51, "New alias", NewKey),
            (51, "New alias", NewKey)
        ];

        var refreshed = await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id)));
        Assert.Equal(2, refreshed.Length);
        Assert.Equal(laptop, refreshed.Single(x => x.Id == laptop.Id));
        var added = refreshed.Single(x => x.Id != laptop.Id);
        Assert.Equal("New alias", added.Name);
        Assert.NotEqual(laptop.Fingerprint, added.Fingerprint);
        var unchanged = await SuccessAsync<ApplicationEnvironmentResponse>(await owner.GetAsync(EnvUrl(env)));
        Assert.Equal(laptop.Id, unchanged.SshAccess!.KeyId);
        Assert.Equal(laptop.Fingerprint, unchanged.SshAccess.Fingerprint);
        Assert.Equal(env.SshAccess!.Revision, unchanged.SshAccess.Revision);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var stored = await db.ProviderSshKeys.ToListAsync();
        Assert.Equal(3, stored.Count);
        Assert.Equal("42", stored.Single(x => x.Id == laptop.Id).ProviderKeyId);
        Assert.Equal("51", stored.Single(x => x.Id == added.Id).ProviderKeyId);
        Assert.Equal(0, _factory.Cloud.Posts);
    }

    [Fact]
    public async Task InventoryKeepsChangedProviderReferenceUnselectableWhenFingerprintAlreadyExists()
    {
        using var owner = Client(_ownerToken);
        var account = await CreateAccountAsync(owner, "Changed", "synthetic-a1");
        var initial = await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id)));
        var laptop = initial.Single(x => x.Name == "Laptop");
        var shared = initial.Single(x => x.Name == "Shared");
        _factory.Cloud.Keys["synthetic-a1"] = [(42, "Changed laptop", SharedKey), (43, "Shared", SharedKey)];

        var refreshed = await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id)));
        Assert.Equal(2, refreshed.Length);
        Assert.Equal("unknown", refreshed.Single(x => x.Id == laptop.Id).RegistrationState);
        Assert.Equal(shared, refreshed.Single(x => x.Id == shared.Id));
        var plan = await CreatePlanAsync(owner, account.Id, "Changed small");
        var env = await CreateAppAsync(owner, "changed-reference", plan.Name);
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("existing", KeyId: laptop.Id, ExpectedRevision: 0))),
            "ssh_key_not_found", HttpStatusCode.NotFound);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var stored = await db.ProviderSshKeys.SingleAsync(x => x.Id == laptop.Id);
        Assert.Equal("42", stored.ProviderKeyId);
        Assert.Equal(laptop.Fingerprint, stored.Fingerprint);
        Assert.Equal(LaptopKey, stored.PublicKey);
    }

    [Fact]
    public async Task InventoryDenialIsForbiddenAndOutageIsUnavailableNeverAnEmptyList()
    {
        using var owner = Client(_ownerToken);
        var account = await CreateAccountAsync(owner, "Inventory", "synthetic-a1");

        _factory.Cloud.Keys["synthetic-a1"] = [];
        Assert.Equal("[]", (await SuccessRawAsync(await owner.GetAsync(KeysUrl(account.Id)))).Trim());

        foreach (var (mode, status, code) in new[]
        {
            (InventoryMode.Forbidden, HttpStatusCode.Forbidden, "forbidden"),
            (InventoryMode.Unauthorized, HttpStatusCode.Forbidden, "forbidden"),
            (InventoryMode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, "ssh_inventory_unavailable"),
            (InventoryMode.ServerError, HttpStatusCode.ServiceUnavailable, "ssh_inventory_unavailable"),
            (InventoryMode.TransportFailure, HttpStatusCode.ServiceUnavailable, "ssh_inventory_unavailable"),
            (InventoryMode.MalformedJson, HttpStatusCode.ServiceUnavailable, "ssh_inventory_unavailable")
        })
        {
            _factory.Cloud.Mode = mode;
            await ErrorAsync(await owner.GetAsync(KeysUrl(account.Id)), code, status);
        }
        _factory.Cloud.Mode = InventoryMode.Normal;

        _factory.Secrets.Available = false;
        await ErrorAsync(await owner.GetAsync(KeysUrl(account.Id)), "ssh_inventory_unavailable", HttpStatusCode.ServiceUnavailable);
        _factory.Secrets.Available = true;

        // A caller without providers:read is denied before any provider traffic.
        var appsOnly = await AddKeyAsync(OrganizationId, [ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite]);
        using var limited = Client(appsOnly);
        var before = _factory.Cloud.SshKeyRequests("synthetic-a1");
        await ErrorAsync(await limited.GetAsync(KeysUrl(account.Id)), "forbidden", HttpStatusCode.Forbidden);
        Assert.Equal(before, _factory.Cloud.SshKeyRequests("synthetic-a1"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Empty(await db.ProviderSshKeys.ToListAsync());
    }

    [Fact]
    public async Task ExistingKeyNeedsProvidersReadAndNewKeyNeedsProvidersWrite()
    {
        using var owner = Client(_ownerToken);
        var account = await CreateAccountAsync(owner, "Permissions", "synthetic-a1");
        var plan = await CreatePlanAsync(owner, account.Id, "Permission small");
        var laptop = (await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id)))).Single(x => x.Name == "Laptop");
        var env = await CreateAppAsync(owner, "permission-app", plan.Name);

        using var appsOnly = Client(await AddKeyAsync(OrganizationId, [ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite]));
        await ErrorAsync(await appsOnly.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("existing", KeyId: laptop.Id, ExpectedRevision: 0))),
            "forbidden", HttpStatusCode.Forbidden);
        await ErrorAsync(await appsOnly.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("new", Name: "New", PublicKey: NewKey, ExpectedRevision: 0))),
            "forbidden", HttpStatusCode.Forbidden);

        using var reader = Client(await AddKeyAsync(OrganizationId,
            [ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite, ApiKeyScopes.ProvidersRead, ApiKeyScopes.PlansRead]));
        var existing = await SuccessAsync<ApplicationEnvironmentResponse>(await reader.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("existing", KeyId: laptop.Id, ExpectedRevision: 0))));
        Assert.Equal("existing", existing.SshAccess!.Mode);
        Assert.Equal(laptop.Id, existing.SshAccess.KeyId);
        Assert.Equal(1, existing.SshAccess.Revision);
        var denied = await reader.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("new", Name: "New", PublicKey: NewKey, ExpectedRevision: 1)));
        await ErrorAsync(denied, "forbidden", HttpStatusCode.Forbidden);
        var initialDenied = await reader.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(OrganizationId, "Denied", "denied-app",
            new CreateInitialEnvironmentRequest("production", plan.Name, "none", new SshAccessRequest("new", Name: "New", PublicKey: NewKey))));
        await ErrorAsync(initialDenied, "forbidden", HttpStatusCode.Forbidden);

        using var writer = Client(await AddKeyAsync(OrganizationId,
            [ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite, ApiKeyScopes.ProvidersRead, ApiKeyScopes.ProvidersWrite]));
        var drafted = await SuccessAsync<ApplicationEnvironmentResponse>(await writer.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("new", Name: "New", PublicKey: NewKey, ExpectedRevision: 1))));
        Assert.Equal("new", drafted.SshAccess!.Mode);
        Assert.Equal("not_registered", drafted.SshAccess.RegistrationState);
        Assert.Null(drafted.SshAccess.KeyId);
        Assert.Equal(2, drafted.SshAccess.Revision);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.False(await db.Applications.AnyAsync(x => x.Slug == "denied-app"));
        // Saving a new-key draft never registers it or advertises it as provider inventory.
        Assert.All(await db.ProviderSshKeys.ToListAsync(), key => Assert.Equal("discovered", key.RegistrationState));
        Assert.Equal(0, _factory.Cloud.Posts);
    }

    [Fact]
    public async Task AccountChangeRequiresExplicitDestinationSelectionAndRevalidatesExistingKeys()
    {
        using var owner = Client(_ownerToken);
        var first = await CreateAccountAsync(owner, "First", "synthetic-a1");
        var second = await CreateAccountAsync(owner, "Second", "synthetic-a2");
        var firstPlan = await CreatePlanAsync(owner, first.Id, "First small");
        var secondPlan = await CreatePlanAsync(owner, second.Id, "Second small");
        var laptop = (await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(first.Id)))).Single(x => x.Name == "Laptop");
        var sharedOnSecond = Assert.Single(await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(second.Id))));
        var env = await CreateAppAsync(owner, "account-change", firstPlan.Name, new SshAccessRequest("existing", KeyId: laptop.Id));
        Assert.Equal(1, env.SshAccess!.Revision);

        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), new UpdateEnvironmentRequest(Plan: secondPlan.Name)),
            "ssh_selection_account_mismatch", HttpStatusCode.Conflict);
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), new UpdateEnvironmentRequest(Plan: secondPlan.Name,
            SshAccess: new SshAccessRequest("existing", KeyId: laptop.Id, ExpectedRevision: 1))),
            "ssh_key_not_found", HttpStatusCode.NotFound);
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), new UpdateEnvironmentRequest(Plan: secondPlan.Name,
            SshAccess: new SshAccessRequest("existing", KeyId: sharedOnSecond.Id, ExpectedRevision: 0))),
            "ssh_selection_revision_conflict", HttpStatusCode.Conflict);
        var unchanged = await SuccessAsync<ApplicationEnvironmentResponse>(await owner.GetAsync(EnvUrl(env)));
        Assert.Equal(firstPlan.Id, unchanged.PlanId);
        Assert.Equal(first.Id, unchanged.SshAccess!.ProviderAccountId);
        Assert.Equal(laptop.Id, unchanged.SshAccess.KeyId);

        // The plan-only selection endpoint cannot orphan the SSH selection on another account either.
        await ErrorAsync(await owner.PostAsJsonAsync($"/api/environments/{env.Id}/plan", new SelectAppPlanRequest(secondPlan.Name)),
            "ssh_selection_account_mismatch", HttpStatusCode.Conflict);
        Assert.Equal(firstPlan.Id, (await SuccessAsync<ApplicationEnvironmentResponse>(await owner.GetAsync(EnvUrl(env)))).PlanId);

        var moved = await SuccessAsync<ApplicationEnvironmentResponse>(await owner.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(Plan: secondPlan.Name,
                SshAccess: new SshAccessRequest("existing", KeyId: sharedOnSecond.Id, ExpectedRevision: 1))));
        Assert.Equal(secondPlan.Id, moved.PlanId);
        Assert.Equal(second.Id, moved.SshAccess!.ProviderAccountId);
        Assert.Equal(sharedOnSecond.Id, moved.SshAccess.KeyId);
        Assert.Equal(2, moved.SshAccess.Revision);

        // Stored material that no longer matches its recorded fingerprint, or a key that is not
        // a confirmed inventory entry, cannot be selected.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            (await db.ProviderSshKeys.SingleAsync(x => x.Id == sharedOnSecond.Id)).PublicKey = NewKey;
            (await db.ProviderSshKeys.SingleAsync(x => x.Id == laptop.Id)).RegistrationState = "pending";
            await db.SaveChangesAsync();
        }
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), new UpdateEnvironmentRequest(
            SshAccess: new SshAccessRequest("existing", KeyId: sharedOnSecond.Id, ExpectedRevision: 2))),
            "ssh_key_changed", HttpStatusCode.Conflict);
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), new UpdateEnvironmentRequest(Plan: firstPlan.Name,
            SshAccess: new SshAccessRequest("existing", KeyId: laptop.Id, ExpectedRevision: 2))),
            "ssh_key_not_found", HttpStatusCode.NotFound);

        var reset = await SuccessAsync<ApplicationEnvironmentResponse>(await owner.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(Plan: firstPlan.Name, SshAccess: new SshAccessRequest("none", ExpectedRevision: 2))));
        Assert.Equal(firstPlan.Id, reset.PlanId);
        Assert.Equal("none", reset.SshAccess!.Mode);
        Assert.Equal(first.Id, reset.SshAccess.ProviderAccountId);
        Assert.Null(reset.SshAccess.Fingerprint);
        Assert.Equal(3, reset.SshAccess.Revision);
    }

    [Fact]
    public async Task SelectionIsLockedDuringAmbiguousAllocationAndUnlockedOnlyForReconciledAbsentServer()
    {
        using var owner = Client(_ownerToken);
        var account = await CreateAccountAsync(owner, "Lock", "synthetic-a1");
        var plan = await CreatePlanAsync(owner, account.Id, "Lock small");
        var laptop = (await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id)))).Single(x => x.Name == "Laptop");
        var env = await CreateAppAsync(owner, "lock-app", plan.Name, new SshAccessRequest("existing", KeyId: laptop.Id));
        var toNone = new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("none", ExpectedRevision: 1));
        await SuccessAsync<DeploymentCreateResponse>(await owner.PostAsJsonAsync("/api/deployments/preparations",
            new StartDeploymentPreparationRequest(env.Id, Guid.NewGuid().ToString("D"), ["api"])));

        string deploymentId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var deployment = await db.Deployments.SingleAsync(x => x.AppId == env.Id);
            deploymentId = deployment.Id;
            Assert.NotNull(deployment.SshSnapshotTakenAt);
            Assert.Equal(laptop.Id, deployment.SshKeyId);
            deployment.Status = DeploymentStatus.Queued;
            await db.SaveChangesAsync();
        }
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), toNone), "ssh_selection_locked", HttpStatusCode.Conflict);
        Assert.True((await SuccessAsync<ApplicationEnvironmentResponse>(await owner.GetAsync(EnvUrl(env)))).SshAccess!.SelectionLocked);

        // A failed attempt whose provider create outcome is unknown keeps the selection locked.
        await UpdateDeploymentAsync(deploymentId, x => { x.Status = DeploymentStatus.Failed; x.ProviderCreationAttempted = true; });
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), toNone), "ssh_selection_locked", HttpStatusCode.Conflict);

        // So does an ambiguous key registration, even after the deployment itself failed.
        await UpdateDeploymentAsync(deploymentId, x => { x.ProviderCreationAttempted = false; x.SshRegistrationState = "unknown"; });
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env), toNone), "ssh_selection_locked", HttpStatusCode.Conflict);

        // Reconciliation that confirms no server exists permits a new selection and revision.
        await UpdateDeploymentAsync(deploymentId, x => x.SshRegistrationState = "failed");
        var updated = await SuccessAsync<ApplicationEnvironmentResponse>(await owner.PatchAsJsonAsync(EnvUrl(env), toNone));
        Assert.Equal("none", updated.SshAccess!.Mode);
        Assert.Equal(2, updated.SshAccess.Revision);
        Assert.False(updated.SshAccess.SelectionLocked);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.ServerInstances.Add(new ServerInstance { Id = "lock-server", OrganizationId = OrganizationId, AppId = env.Id,
                ProviderAccountId = account.Id, Provider = "Hetzner", ProviderServerId = "501", Region = "nbg1",
                Status = "running", BootstrapStatus = "ready" });
            await db.SaveChangesAsync();
        }
        await ErrorAsync(await owner.PatchAsJsonAsync(EnvUrl(env),
            new UpdateEnvironmentRequest(SshAccess: new SshAccessRequest("existing", KeyId: laptop.Id, ExpectedRevision: 2))),
            "ssh_selection_locked", HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SshConnectionIsNotExposedWithoutVerifiedImageLoginIdentity()
    {
        using var owner = Client(_ownerToken);
        var account = await CreateAccountAsync(owner, "Connection", "synthetic-a1");
        var plan = await CreatePlanAsync(owner, account.Id, "Connection small");
        var laptop = (await SuccessAsync<ProviderSshKeyResponse[]>(await owner.GetAsync(KeysUrl(account.Id)))).Single(x => x.Name == "Laptop");
        var env = await CreateAppAsync(owner, "connection-app", plan.Name, new SshAccessRequest("existing", KeyId: laptop.Id));
        Assert.Null(env.SshConnection);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            (await db.Apps.SingleAsync(x => x.Id == env.Id)).SshRegistrationState = "ready";
            db.ServerInstances.Add(new ServerInstance { Id = "connection-server", OrganizationId = OrganizationId, AppId = env.Id,
                ProviderAccountId = account.Id, Provider = "Hetzner", ProviderServerId = "502", IpAddress = "192.0.2.10",
                Region = "nbg1", Status = "running", BootstrapStatus = "ready" });
            await db.SaveChangesAsync();
        }

        foreach (var url in new[] { EnvUrl(env), $"/api/environments/{env.Id}", $"/api/apps/{env.ApplicationId}" })
        {
            var raw = await SuccessRawAsync(await owner.GetAsync(url));
            Assert.DoesNotContain("\"root\"", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("\"username\"", raw, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(raw);
            foreach (var connection in FindProperties(document.RootElement, "sshConnection"))
                Assert.Equal(JsonValueKind.Null, connection.ValueKind);
        }
    }

    private static IEnumerable<JsonElement> FindProperties(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name)) yield return property.Value;
                foreach (var nested in FindProperties(property.Value, name)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in FindProperties(item, name)) yield return nested;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static string KeysUrl(string accountId) => $"/api/providers/{accountId}/ssh-keys?organizationId={OrganizationId}";
    private static string EnvUrl(ApplicationEnvironmentResponse env) => $"/api/apps/{env.ApplicationId}/environments/{env.Id}";

    private async Task UpdateDeploymentAsync(string id, Action<Deployment> update)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        update(await db.Deployments.SingleAsync(x => x.Id == id));
        await db.SaveChangesAsync();
    }

    private HttpClient Client(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<ApplicationEnvironmentResponse> CreateAppAsync(HttpClient client, string slug, string plan,
        SshAccessRequest? sshAccess = null, string organizationId = OrganizationId)
    {
        var created = await SuccessAsync<ApplicationResponse>(await client.PostAsJsonAsync("/api/apps",
            new CreateApplicationRequest(organizationId, slug, slug,
                new CreateInitialEnvironmentRequest("production", plan, "none", sshAccess))));
        return Assert.Single(created.Environments);
    }

    private static async Task<ProviderPlanResponse> CreatePlanAsync(HttpClient client, string accountId, string name,
        string organizationId = OrganizationId) =>
        await SuccessAsync<ProviderPlanResponse>(await client.PostAsJsonAsync("/api/plans",
            new CreateProviderPlanRequest(organizationId, accountId, "nbg1", "cx23", Name: name)));

    private static async Task<ProviderAccountResponse> CreateAccountAsync(HttpClient client, string name, string credential,
        string organizationId = OrganizationId) =>
        await SuccessAsync<ProviderAccountResponse>(await client.PostAsJsonAsync("/api/providers",
            new CreateProviderRequest(organizationId, name, "hetzner", credential)));

    private static async Task<T> SuccessAsync<T>(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<T>(await SuccessRawAsync(response), JsonOptions)!;

    private static async Task<string> SuccessRawAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {raw}");
        return raw;
    }

    private static async Task ErrorAsync(HttpResponseMessage response, string code, HttpStatusCode status)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"Expected {(int)status} {code}, got {(int)response.StatusCode}: {raw}");
        var body = JsonSerializer.Deserialize<ErrorResponse>(raw, JsonOptions);
        Assert.Equal(code, body!.Error.Code);
    }

    private async Task<string> AddKeyAsync(string organizationId, IEnumerable<string> scopes)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        var token = tokens.Generate();
        db.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid().ToString("N"), OrganizationId = organizationId, Name = "SSH fixture",
            KeyPrefix = token.Prefix, KeyHash = tokens.Hash(token.Token), ScopesJson = JsonSerializer.Serialize(scopes),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return token.Token;
    }

    private static string Ed25519(byte seed, string comment)
    {
        using var stream = new MemoryStream();
        WriteString(stream, Encoding.ASCII.GetBytes("ssh-ed25519"));
        WriteString(stream, Enumerable.Range(0, 32).Select(x => (byte)(x + seed * 37)).ToArray());
        return "ssh-ed25519 " + Convert.ToBase64String(stream.ToArray()) + " " + comment;
    }

    private static void WriteString(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private enum InventoryMode { Normal, Forbidden, Unauthorized, ServiceUnavailable, ServerError, TransportFailure, MalformedJson }

    private sealed class SshApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public FakeSecrets Secrets { get; } = new();
        public FakeCloud Cloud { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:",
                ["Cors:AllowedOrigins:0"] = "https://dashboard.example.test",
                ["OpenBao:EnvironmentPrefix"] = "ssh-api-tests"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<ISecretStore>(); services.AddSingleton<ISecretStore>(Secrets);
                services.AddHttpClient(ProviderAccountClientFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Cloud);
            });
        }
    }

    private sealed class FakeSecrets : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public bool Available { get; set; } = true;

        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
        {
            var path = OpenBaoSecretStore.ProviderPathFor("ssh-api-tests", request.OrganizationId,
                request.ProviderAccountId!, request.CredentialGeneration!);
            _values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }

        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            if (!Available || !_values.TryGetValue(reference.Path, out var value))
                throw new SecretStoreUnavailableException("Synthetic secret-store outage.");
            return Task.FromResult(value);
        }

        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            _values.TryRemove(reference.Path, out _);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCloud : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _sshKeyRequests = new();
        public ConcurrentDictionary<string, (long Id, string Name, string PublicKey)[]> Keys { get; } = new();
        public InventoryMode Mode { get; set; } = InventoryMode.Normal;
        public int Posts;

        public int SshKeyRequests(string credential) => _sshKeyRequests.Count(x => x == credential);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var credential = request.Headers.Authorization?.Parameter ?? "";
            if (request.Method != HttpMethod.Get)
            {
                Interlocked.Increment(ref Posts);
                throw new InvalidOperationException("Synthetic provider only accepts reads.");
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/ssh_keys", StringComparison.Ordinal))
            {
                _sshKeyRequests.Enqueue(credential);
                return Mode switch
                {
                    InventoryMode.Forbidden => Status(HttpStatusCode.Forbidden),
                    InventoryMode.Unauthorized => Status(HttpStatusCode.Unauthorized),
                    InventoryMode.ServiceUnavailable => Status(HttpStatusCode.ServiceUnavailable),
                    InventoryMode.ServerError => Status(HttpStatusCode.InternalServerError),
                    InventoryMode.TransportFailure => throw new HttpRequestException("Synthetic connection reset."),
                    InventoryMode.MalformedJson => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        { Content = new StringContent("{\"ssh_keys\":[{\"id\":", Encoding.UTF8, "application/json") }),
                    _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            ssh_keys = Keys.GetValueOrDefault(credential, []).Select(x => new { id = x.Id, name = x.Name, public_key = x.PublicKey })
                        })
                    })
                };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { servers = Array.Empty<object>(), droplets = Array.Empty<object>(), instances = Array.Empty<object>() })
            });
        }

        private static Task<HttpResponseMessage> Status(HttpStatusCode status) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = JsonContent.Create(new { error = new { message = "synthetic" } }) });
    }
}
