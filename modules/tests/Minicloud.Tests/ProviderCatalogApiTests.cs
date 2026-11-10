using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

public sealed class ProviderCatalogApiTests : IAsyncLifetime
{
    private static CreateApplicationRequest ApplicationRequest(string organizationId, string name, string slug, string plan, string database) =>
        new(organizationId, name, slug, new CreateInitialEnvironmentRequest("production", plan, database));

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private CatalogApiFactory _factory = default!;
    private string _managementToken = default!;
    private readonly Dictionary<string, string> _dashboardCsrf = new(StringComparer.Ordinal);
    private const string OrganizationId = "org_provider_catalog";

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new CatalogApiFactory(_connection);
        _factory.CreateClient().Dispose();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = OrganizationId, Name = "Provider catalog", Slug = "provider-catalog" });
        await db.SaveChangesAsync();
        _managementToken = await AddKeyAsync(OrganizationId, ApiKeyScopes.All);
    }

    [Fact]
    public async Task SameProviderAccountsKeepIndependentWriteOnlyCredentials()
    {
        using var client = Client();
        var first = await CreateAccountAsync(client, "Hetzner first", "synthetic-first-token");
        var second = await CreateAccountAsync(client, "Hetzner second", "synthetic-second-token");
        var third = await CreateAccountAsync(client, "Vultr", "synthetic-vultr-token", "vultr");
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal("hetzner", first.ProviderType);
        Assert.Equal("hetzner", second.ProviderType);
        Assert.Equal("vultr", third.ProviderType);
        var accounts = (await client.GetFromJsonAsync<ProviderAccountResponse[]>($"/api/providers?organizationId={OrganizationId}"))!;
        Assert.Equal(3, accounts.Length);
        foreach (var id in new[] { first.Id, second.Id, third.Id })
        {
            var response = await client.GetAsync($"/api/providers/{id}");
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("synthetic-", raw);
            Assert.DoesNotContain("credentialPath", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credentialGeneration", raw, StringComparison.OrdinalIgnoreCase);
            using var body = JsonDocument.Parse(raw);
            Assert.DoesNotContain(body.RootElement.EnumerateObject(), property => property.Name.Equals("credential", StringComparison.OrdinalIgnoreCase));
        }
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var stored = await db.ProviderAccounts.AsNoTracking().ToListAsync();
        Assert.DoesNotContain("synthetic-", JsonSerializer.Serialize(stored));
        Assert.Equal(3, _factory.Secrets.Values.Count);
        Assert.All(stored, item => Assert.StartsWith($"env/provider-catalog-tests/orgs/{OrganizationId}/providers/{item.Id}/credentials/", item.CredentialPath));
        Assert.Contains(_factory.Cloud.Requests, request => request.Credential == "synthetic-first-token");
        Assert.Contains(_factory.Cloud.Requests, request => request.Credential == "synthetic-second-token");
        Assert.All(_factory.Cloud.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task CredentialScopeDiscoveryUsesTheBoundedManagedServerName()
    {
        using var client = Client();
        var account = await CreateAccountAsync(client, "Long runtime name", "synthetic-first-token");
        const string organizationSlug = "minicloud-staging";
        const string appSlug = "dbrepo-0123456789abcdef01234567";
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            (await db.Organizations.SingleAsync(x => x.Id == OrganizationId)).Slug = organizationSlug;
            db.Apps.Add(new MinicloudApp { Id = "long-name-runtime", OrganizationId = OrganizationId,
                Name = "Database repository runtime", Slug = appSlug, EnvironmentKind = "internal" });
            db.ServerInstances.Add(new ServerInstance { Id = "long-name-server", OrganizationId = OrganizationId,
                AppId = "long-name-runtime", ProviderAccountId = account.Id, Provider = "Hetzner",
                ProviderServerId = "123", Region = "nbg1", Status = "ready", BootstrapStatus = "ready" });
            await db.SaveChangesAsync();
        }
        await ErrorAsync(await client.PutAsJsonAsync($"/api/providers/{account.Id}/credentials",
            new ReplaceProviderCredentialRequest("synthetic-next-token", account.Revision)), "provider_scope_unverified");
        var expected = ManagedServerName.ForApp(organizationSlug, appSlug);
        Assert.Contains(_factory.Cloud.Requests, request => request.Credential == "synthetic-next-token"
            && request.Uri?.Query == "?name=" + expected);
    }

    [Fact]
    public async Task PlanNamesStartAsUuidSupportUnicodeAndRetireOldNames()
    {
        using var client = Client();
        var first = await CreateAccountAsync(client, "First", "synthetic-first-token");
        var second = await CreateAccountAsync(client, "Second", "synthetic-second-token");
        var generated = await CreatePlanAsync(client, first.Id);
        Assert.True(Guid.TryParse(generated.Name, out _));
        var renamed = await SuccessAsync<ProviderPlanResponse>(await client.PatchAsJsonAsync($"/api/plans/{generated.Id}",
            new UpdateProviderPlanRequest(generated.Revision, Name: "Small São Paulo")));
        Assert.Equal(generated.Id, renamed.Id);
        Assert.Equal("Small São Paulo", renamed.Name);
        Assert.True(renamed.Revision > generated.Revision);
        await ErrorAsync(await client.PostAsJsonAsync("/api/plans", PlanRequest(second.Id, "small sa\u0303o paulo")), "plan_name_taken");
        await ErrorAsync(await client.PostAsJsonAsync("/api/plans", PlanRequest(second.Id, generated.Name)), "plan_name_taken");

        var oldName = ApplicationRequest(OrganizationId, "Old plan reference", "old-plan-ref", generated.Name, "none");
        await ErrorAsync(await client.PostAsJsonAsync("/api/apps", oldName), "plan_name_retired");
        var createdApplication = await SuccessAsync<ApplicationResponse>(await client.PostAsJsonAsync("/api/apps", oldName with { InitialEnvironment = oldName.InitialEnvironment! with { Plan = "small são paulo" } }));
        var app = Assert.Single(createdApplication.Environments);
        Assert.Equal("Small São Paulo", app.Plan);
        var renamedAgain = await SuccessAsync<ProviderPlanResponse>(await client.PatchAsJsonAsync($"/api/plans/{generated.Id}",
            new UpdateProviderPlanRequest(renamed.Revision, Name: "Primary small")));
        Assert.Equal("Primary small", (await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{app.Id}"))!.Plan);
        await ErrorAsync(await client.DeleteAsync($"/api/plans/{generated.Id}"), "plan_in_use");
        await ErrorAsync(await client.PostAsJsonAsync("/api/plans", PlanRequest(second.Id, renamed.Name)), "plan_name_taken");
        Assert.Equal(generated.Id, renamedAgain.Id);
    }

    [Fact]
    public async Task DeletedPlansKeepNameTombstonesAndNamesCanRepeatInAnotherOrganization()
    {
        using var client = Client();
        var account = await CreateAccountAsync(client, "Primary", "synthetic-first-token");
        var plan = await CreatePlanAsync(client, account.Id, "Small shared name");
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/plans/{plan.Id}")).StatusCode);
        await ErrorAsync(await client.PostAsJsonAsync("/api/plans", PlanRequest(account.Id, plan.Name)), "plan_name_taken");
        var otherToken = await AddOrganizationAsync("org_other");
        using var other = Client(otherToken);
        var otherAccount = await CreateAccountAsync(other, "Other primary", "synthetic-other-token", organizationId: "org_other");
        var sameName = await SuccessAsync<ProviderPlanResponse>(await other.PostAsJsonAsync("/api/plans",
            PlanRequest(otherAccount.Id, plan.Name) with { OrganizationId = "org_other" }));
        Assert.Equal(plan.Name, sameName.Name);
    }

    [Fact]
    public async Task CrossOrganizationIdsCannotListReadEditOrSelectAnotherAccountsPlan()
    {
        using var owner = Client();
        var account = await CreateAccountAsync(owner, "Primary", "synthetic-first-token");
        var plan = await CreatePlanAsync(owner, account.Id, "Private plan");
        using var foreign = Client(await AddOrganizationAsync("org_foreign"));
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync($"/api/providers?organizationId={OrganizationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await foreign.GetAsync($"/api/plans?organizationId={OrganizationId}")).StatusCode);
        Assert.False((await foreign.GetAsync($"/api/providers/{account.Id}")).IsSuccessStatusCode);
        Assert.False((await foreign.GetAsync($"/api/plans/{plan.Id}")).IsSuccessStatusCode);
        Assert.False((await foreign.PatchAsJsonAsync($"/api/providers/{account.Id}", new UpdateProviderRequest(account.Revision, Name: "Hijacked"))).IsSuccessStatusCode);
        Assert.False((await foreign.PatchAsJsonAsync($"/api/plans/{plan.Id}", new UpdateProviderPlanRequest(plan.Revision, Name: "Hijacked"))).IsSuccessStatusCode);
        await ErrorAsync(await foreign.PostAsJsonAsync("/api/plans", PlanRequest(account.Id, "Foreign binding") with { OrganizationId = "org_foreign" }), "provider_not_found");
        await ErrorAsync(await foreign.PostAsJsonAsync("/api/apps", ApplicationRequest("org_foreign", "Foreign app", "foreign-app", plan.Name, "none")), "plan_not_found");
        Assert.Equal(account.Name, (await owner.GetFromJsonAsync<ProviderAccountResponse>($"/api/providers/{account.Id}"))!.Name);
    }

    [Fact]
    public async Task DeploymentKeyCannotManageProviderCredentialsOrPlans()
    {
        using var owner = Client();
        var account = await CreateAccountAsync(owner, "Primary", "synthetic-first-token");
        var plan = await CreatePlanAsync(owner, account.Id, "Deployable small");
        using var deploy = Client(await AddKeyAsync(OrganizationId,
            [ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite, ApiKeyScopes.DeploymentsCreate, ApiKeyScopes.DeploymentsRead, ApiKeyScopes.PlansRead]));
        Assert.Single((await deploy.GetFromJsonAsync<ProviderPlanResponse[]>($"/api/plans?organizationId={OrganizationId}"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await deploy.PostAsJsonAsync("/api/providers", new CreateProviderRequest(OrganizationId, "Unauthorized", "hetzner", "synthetic-denied"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deploy.PutAsJsonAsync($"/api/providers/{account.Id}/credentials", new ReplaceProviderCredentialRequest("synthetic-denied", account.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deploy.PostAsJsonAsync("/api/plans", PlanRequest(account.Id, "Unauthorized"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await deploy.PatchAsJsonAsync($"/api/plans/{plan.Id}", new UpdateProviderPlanRequest(plan.Revision, Name: "Unauthorized"))).StatusCode);
        var createdApplication = await SuccessAsync<ApplicationResponse>(await deploy.PostAsJsonAsync("/api/apps", ApplicationRequest(OrganizationId, "Allowed app", "allowed-app", plan.Name, "none")));
        var app = Assert.Single(createdApplication.Environments);
        Assert.Equal(plan.Name, app.Plan);
        Assert.DoesNotContain(_factory.Cloud.Requests, request => request.Credential == "synthetic-denied");
    }

    [Theory]
    [InlineData(OrganizationRole.Owner, true)]
    [InlineData(OrganizationRole.Admin, true)]
    [InlineData(OrganizationRole.Developer, false)]
    [InlineData(OrganizationRole.Viewer, false)]
    public async Task DashboardRolePolicySeparatesCatalogReadsAndCredentialManagement(OrganizationRole role, bool canManage)
    {
        var token = await AddDashboardUserAsync(role);
        using var client = Client(token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/providers?organizationId={OrganizationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/plans?organizationId={OrganizationId}")).StatusCode);
        var response = await client.PostAsJsonAsync("/api/providers", new CreateProviderRequest(OrganizationId, role.ToString(), "hetzner", "synthetic-role-token"));
        Assert.Equal(canManage ? HttpStatusCode.Created : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task BrowserRequestsRequireCsrfAndInvalidBearerDoesNotFallBackToCookie()
    {
        var cookie = await AddDashboardUserAsync(OrganizationRole.Owner);
        using var client = Client(cookie);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-bearer");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/providers?organizationId={OrganizationId}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.example.test");
        var deniedOrigin = await client.PostAsJsonAsync(
            "/api/providers",
            new CreateProviderRequest(OrganizationId, "Rejected", "hetzner", "synthetic-token"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedOrigin.StatusCode);
        Assert.Equal("csrf_invalid", (await deniedOrigin.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);

        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://dashboard.example.test");
        client.DefaultRequestHeaders.Remove("X-Minicloud-CSRF");
        var deniedCsrf = await client.PostAsJsonAsync(
            "/api/providers",
            new CreateProviderRequest(OrganizationId, "Rejected", "hetzner", "synthetic-token"));
        Assert.Equal(HttpStatusCode.Forbidden, deniedCsrf.StatusCode);
    }

    [Fact]
    public async Task RevisionsAndDisabledStatesAreVisibleAndRejectNewSelection()
    {
        using var client = Client();
        var account = await CreateAccountAsync(client, "Primary", "synthetic-first-token");
        var plan = await CreatePlanAsync(client, account.Id, "Small");
        var disabledPlan = await SuccessAsync<ProviderPlanResponse>(await client.PatchAsJsonAsync($"/api/plans/{plan.Id}", new UpdateProviderPlanRequest(plan.Revision, Enabled: false)));
        await ErrorAsync(await client.PatchAsJsonAsync($"/api/plans/{plan.Id}", new UpdateProviderPlanRequest(plan.Revision, Name: "Stale edit")), "revision_conflict");
        Assert.False(disabledPlan.Enabled);
        await ErrorAsync(await client.PostAsJsonAsync("/api/apps", ApplicationRequest(OrganizationId, "Disabled", "disabled", plan.Name, "none")), "plan_disabled");
        await SuccessAsync<ProviderPlanResponse>(await client.PatchAsJsonAsync($"/api/plans/{plan.Id}", new UpdateProviderPlanRequest(disabledPlan.Revision, Enabled: true)));
        var disabledAccount = await SuccessAsync<ProviderAccountResponse>(await client.PatchAsJsonAsync($"/api/providers/{account.Id}", new UpdateProviderRequest(account.Revision, Enabled: false)));
        await ErrorAsync(await client.PatchAsJsonAsync($"/api/providers/{account.Id}", new UpdateProviderRequest(account.Revision, Name: "Stale account")), "revision_conflict");
        Assert.False(disabledAccount.Enabled);
        var catalog = Assert.Single((await client.GetFromJsonAsync<ProviderPlanResponse[]>($"/api/plans?organizationId={OrganizationId}"))!);
        Assert.True(catalog.Enabled);
        Assert.False(catalog.AccountEnabled);
        await ErrorAsync(await client.PostAsJsonAsync("/api/apps", ApplicationRequest(OrganizationId, "Disabled account", "disabled-account", plan.Name, "none")), "provider_disabled");
        await ErrorAsync(await client.PostAsJsonAsync("/api/plans", PlanRequest(account.Id, "New mapping")), "provider_disabled");
    }

    [Fact]
    public async Task FailedCredentialReplacementPreservesOldReferenceAndUnavailableSecretsDispatchNothing()
    {
        using var client = Client();
        var account = await CreateAccountAsync(client, "Primary", "synthetic-first-token");
        string originalPath;
        await using (var scope = _factory.Services.CreateAsyncScope())
            originalPath = (await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().ProviderAccounts.SingleAsync()).CredentialPath;
        var replacement = await client.PutAsJsonAsync($"/api/providers/{account.Id}/credentials", new ReplaceProviderCredentialRequest("synthetic-rejected-token", account.Revision));
        Assert.False(replacement.IsSuccessStatusCode);
        Assert.DoesNotContain("synthetic-rejected-token", await replacement.Content.ReadAsStringAsync());
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().ProviderAccounts.SingleAsync();
            Assert.Equal(originalPath, saved.CredentialPath);
            Assert.Equal(account.Revision, saved.Revision);
        }
        var check = await SuccessAsync<ProviderValidationResponse>(await client.PostAsync($"/api/providers/{account.Id}/validate", null));
        Assert.Equal("valid", check.Status);
        var requestCount = _factory.Cloud.Requests.Count;
        _factory.Secrets.Available = false;
        var unavailable = await SuccessAsync<ProviderValidationResponse>(await client.PostAsync($"/api/providers/{account.Id}/validate", null));
        Assert.Equal("credentials_unavailable", unavailable.Status);
        Assert.Equal(requestCount, _factory.Cloud.Requests.Count);
    }

    [Fact]
    public async Task UnsupportedProviderTypesAreRejectedBeforeSecretsOrCloudDispatch()
    {
        using var client = Client();
        var types = (await client.GetFromJsonAsync<ProviderTypeResponse[]>("/api/provider-types"))!;
        Assert.Equal(new[] { "digital-ocean", "hetzner", "vultr" }, types.Select(x => x.Type).OrderBy(x => x).ToArray());
        Assert.True(types.Single(x => x.Type == "hetzner").SupportsStorage);
        Assert.All(types.Where(x => x.Type != "hetzner"), type => Assert.False(type.SupportsStorage));
        await ErrorAsync(await client.PostAsJsonAsync("/api/providers", new CreateProviderRequest(OrganizationId, "Unsupported", "arbitrary-provider", "synthetic-token")), "provider_type_unsupported");
        Assert.Empty(_factory.Secrets.Values);
        Assert.Empty(_factory.Cloud.Requests);
    }

    [Fact]
    public async Task RetainedVolumeWithoutAnAppRowPreventsProviderAndCredentialDeletion()
    {
        using var client = Client();
        var account = await CreateAccountAsync(client, "Retained storage", "synthetic-first-token");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.AppStorageVolumes.Add(new AppStorageVolume { Id = "retained-volume", AppId = "deleted-app",
                OrganizationId = OrganizationId, ProviderAccountId = account.Id, Name = "uploads", Region = "nbg1", SizeGb = 20, ProviderVolumeId = "volume-1" });
            await db.SaveChangesAsync();
        }
        await ErrorAsync(await client.DeleteAsync($"/api/providers/{account.Id}"), "provider_in_use");
        Assert.Single(_factory.Secrets.Values);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/providers/{account.Id}")).StatusCode);
    }

    [Fact]
    public async Task PreparationReservesPlacementAndLocksMappingWithoutPreventingRename()
    {
        using var client = Client();
        var account = await CreateAccountAsync(client, "Primary", "synthetic-first-token");
        var plan = await CreatePlanAsync(client, account.Id, "Original name");
        var createdApplication = await SuccessAsync<ApplicationResponse>(await client.PostAsJsonAsync("/api/apps", ApplicationRequest(OrganizationId, "Prepared app", "prepared-app", plan.Name, "none")));
        var app = Assert.Single(createdApplication.Environments);
        await SuccessAsync<DeploymentCreateResponse>(await client.PostAsJsonAsync("/api/deployments/preparations",
            new StartDeploymentPreparationRequest(app.Id, Guid.NewGuid().ToString("D"), ["api"])));
        var locked = (await client.GetFromJsonAsync<ProviderPlanResponse>($"/api/plans/{plan.Id}"))!;
        Assert.True(locked.ProvisioningLocked);
        await ErrorAsync(await client.PatchAsJsonAsync($"/api/plans/{plan.Id}", new UpdateProviderPlanRequest(locked.Revision, Region: "hel1")), "plan_mapping_locked");
        var renamed = await SuccessAsync<ProviderPlanResponse>(await client.PatchAsJsonAsync($"/api/plans/{plan.Id}", new UpdateProviderPlanRequest(locked.Revision, Name: "Current name")));
        Assert.Equal("Current name", renamed.Name);
        await using var scope = _factory.Services.CreateAsyncScope();
        var reservation = await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().Deployments.SingleAsync();
        Assert.Equal(account.Id, reservation.ProviderAccountId);
        Assert.Equal(plan.Id, reservation.PlanId);
        Assert.Equal("nbg1", reservation.ProviderRegion);
        Assert.Equal("cx23", reservation.ProviderSku);
        Assert.All(_factory.Cloud.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("canceled", false, true)]
    [InlineData("failed", false, true)]
    [InlineData("failed", true, false)]
    public async Task TerminalPreparationAllowsExplicitAccountChangeOnlyWhenNoAllocationIsUnresolved(
        string terminalStatus, bool creationAttempted, bool canChangePlan)
    {
        using var client = Client();
        var first = await CreateAccountAsync(client, "First account", "synthetic-first-token");
        var second = await CreateAccountAsync(client, "Second account", "synthetic-second-token");
        var firstPlan = await CreatePlanAsync(client, first.Id, "First small");
        var secondPlan = await CreatePlanAsync(client, second.Id, "Second small");
        var createdApplication = await SuccessAsync<ApplicationResponse>(await client.PostAsJsonAsync("/api/apps",
            ApplicationRequest(OrganizationId, "Unprovisioned app", "unprovisioned-app", firstPlan.Name, "none")));
        var app = Assert.Single(createdApplication.Environments);
        var preparation = await SuccessAsync<DeploymentCreateResponse>(await client.PostAsJsonAsync("/api/deployments/preparations",
            new StartDeploymentPreparationRequest(app.Id, Guid.NewGuid().ToString("D"), ["api"])));
        if (terminalStatus == "canceled")
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/deployments/{preparation.Id}/cancel", null)).StatusCode);
        else
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/deployments/preparations/{preparation.Id}",
                new UpdateDeploymentPreparationRequest(app.Id, "failed"))).StatusCode);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var terminal = await db.Deployments.SingleAsync(x => x.Id == preparation.Id);
            Assert.Equal(terminalStatus == "canceled" ? DeploymentStatus.Canceled : DeploymentStatus.Failed, terminal.Status);
            Assert.Null(terminal.OwnershipToken);
            terminal.ProviderCreationAttempted = creationAttempted;
            await db.SaveChangesAsync();
        }
        var dispatchCount = _factory.Cloud.Requests.Count;
        var selection = await client.PostAsJsonAsync($"/api/environments/{app.Id}/plan", new SelectAppPlanRequest(secondPlan.Name));
        if (canChangePlan)
        {
            var selected = await SuccessAsync<EnvironmentDetailResponse>(selection);
            Assert.Equal(secondPlan.Id, selected.PlanId);
            var retry = await SuccessAsync<DeploymentCreateResponse>(await client.PostAsJsonAsync("/api/deployments/preparations",
                new StartDeploymentPreparationRequest(app.Id, Guid.NewGuid().ToString("D"), ["api"])));
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            Assert.Equal(first.Id, (await db.Deployments.SingleAsync(x => x.Id == preparation.Id)).ProviderAccountId);
            Assert.Equal(second.Id, (await db.Deployments.SingleAsync(x => x.Id == retry.Id)).ProviderAccountId);
        }
        else
        {
            await ErrorAsync(selection, "plan_change_requires_migration");
            Assert.Equal(firstPlan.Id, (await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{app.Id}"))!.PlanId);
        }
        Assert.Equal(dispatchCount, _factory.Cloud.Requests.Count);
    }

    [Fact]
    public async Task ReservedUnprovisionedAccountCannotRotateIntoAnUnverifiedScope()
    {
        using var client = Client();
        var account = await CreateAccountAsync(client, "Reserved account", "synthetic-original-token");
        var plan = await CreatePlanAsync(client, account.Id, "Reserved small");
        var createdApplication = await SuccessAsync<ApplicationResponse>(await client.PostAsJsonAsync("/api/apps",
            ApplicationRequest(OrganizationId, "Reserved app", "reserved-app", plan.Name, "none")));
        var app = Assert.Single(createdApplication.Environments);
        await SuccessAsync<DeploymentCreateResponse>(await client.PostAsJsonAsync("/api/deployments/preparations",
            new StartDeploymentPreparationRequest(app.Id, Guid.NewGuid().ToString("D"), ["api"])));
        string originalPath, originalGeneration;
        string? originalVersion;
        long originalRevision;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var before = await db.ProviderAccounts.SingleAsync();
            originalPath = before.CredentialPath; originalGeneration = before.CredentialGeneration;
            originalVersion = before.CredentialVersion; originalRevision = before.Revision;
            Assert.Empty(await db.ServerInstances.ToListAsync());
            Assert.False((await db.Deployments.SingleAsync()).ProviderCreationAttempted);
        }
        var response = await client.PutAsJsonAsync($"/api/providers/{account.Id}/credentials",
            new ReplaceProviderCredentialRequest("synthetic-different-scope-token", originalRevision));
        await ErrorAsync(response, "provider_scope_unverified");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var after = await db.ProviderAccounts.SingleAsync();
            Assert.Equal(originalPath, after.CredentialPath);
            Assert.Equal(originalGeneration, after.CredentialGeneration);
            Assert.Equal(originalVersion, after.CredentialVersion);
            Assert.Equal(originalRevision, after.Revision);
            Assert.Contains(await db.ProviderCredentialCleanups.ToListAsync(), cleanup => cleanup.Path != originalPath);
        }
        Assert.Equal("synthetic-original-token", _factory.Secrets.Values[originalPath]);
        Assert.All(_factory.Cloud.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    private HttpClient Client(string? token = null)
    {
        var client = _factory.CreateClient();
        var credential = token ?? _managementToken;
        if (_dashboardCsrf.TryGetValue(credential, out var csrf))
        {
            client.DefaultRequestHeaders.Add("Cookie", $"__Host-minicloud-session={credential}");
            client.DefaultRequestHeaders.Add("Origin", "https://dashboard.example.test");
            client.DefaultRequestHeaders.Add("X-Minicloud-CSRF", csrf);
        }
        else
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        }
        return client;
    }

    private static CreateProviderPlanRequest PlanRequest(string accountId, string? name) => new(OrganizationId, accountId, "nbg1", "cx23", Name: name);
    private static Task<ProviderPlanResponse> CreatePlanAsync(HttpClient client, string accountId, string? name = null) =>
        CreatePlanCoreAsync(client, PlanRequest(accountId, name));
    private static async Task<ProviderPlanResponse> CreatePlanCoreAsync(HttpClient client, CreateProviderPlanRequest body) =>
        await SuccessAsync<ProviderPlanResponse>(await client.PostAsJsonAsync("/api/plans", body));
    private static async Task<ProviderAccountResponse> CreateAccountAsync(HttpClient client, string name, string credential, string providerType = "hetzner", string organizationId = OrganizationId) =>
        await SuccessAsync<ProviderAccountResponse>(await client.PostAsJsonAsync("/api/providers", new CreateProviderRequest(organizationId, name, providerType, credential)));
    private static async Task<T> SuccessAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private static async Task ErrorAsync(HttpResponseMessage response, string code)
    {
        Assert.False(response.IsSuccessStatusCode);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal(code, body!.Error.Code);
    }
    private async Task<string> AddOrganizationAsync(string id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        db.Organizations.Add(new Organization { Id = id, Name = id, Slug = id.Replace('_', '-') });
        await db.SaveChangesAsync();
        return await AddKeyAsync(id, ApiKeyScopes.All);
    }
    private async Task<string> AddKeyAsync(string organizationId, IEnumerable<string> scopes)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        var token = tokens.Generate();
        db.ApiKeys.Add(new ApiKey { Id = Guid.NewGuid().ToString("N"), OrganizationId = organizationId, Name = "Catalog fixture",
            KeyPrefix = token.Prefix, KeyHash = tokens.Hash(token.Token), ScopesJson = JsonSerializer.Serialize(scopes), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return token.Token;
    }
    private async Task<string> AddDashboardUserAsync(OrganizationRole role)
    {
        var identityId = "identity_" + role;
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var user = new User { Id = "user_" + role, KratosIdentityId = identityId, Email = role + "@example.test" };
        db.Users.Add(user);
        db.OrganizationMembers.Add(new OrganizationMember { Id = "member_" + role, OrganizationId = OrganizationId, UserId = user.Id, Role = role });
        await db.SaveChangesAsync();
        var sessions = scope.ServiceProvider.GetRequiredService<BrowserSessionService>();
        var session = await sessions.CreateAsync(user.Id, identityId, "session_" + role, CancellationToken.None);
        _dashboardCsrf.Add(session.Cookie, session.CsrfToken);
        return session.Cookie;
    }
    public async Task DisposeAsync() { await _factory.DisposeAsync(); await _connection.DisposeAsync(); }

    private sealed class CatalogApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
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
                ["OpenBao:EnvironmentPrefix"] = "provider-catalog-tests"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<ISecretStore>(); services.AddSingleton<ISecretStore>(Secrets);
                services.RemoveAll<IKratosSessionValidator>(); services.AddSingleton<IKratosSessionValidator>(new FakeKratos());
                services.AddHttpClient(ProviderAccountClientFactory.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Cloud);
            });
        }
    }
    private sealed class FakeKratos : IKratosSessionValidator
    {
        public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken) =>
            Task.FromResult(sessionId.StartsWith("session_", StringComparison.Ordinal));

        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
    private sealed class FakeSecrets : ISecretStore
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public bool Available { get; set; } = true;
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken)
        {
            if (!Available) throw new SecretStoreUnavailableException("Synthetic secret-store outage.");
            var path = OpenBaoSecretStore.ProviderPathFor("provider-catalog-tests", request.OrganizationId, request.ProviderAccountId!, request.CredentialGeneration!);
            Values[path] = request.Value;
            return Task.FromResult(new StoredSecretReference("openbao", path, "1"));
        }
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken)
        {
            if (!Available || !Values.TryGetValue(reference.Path, out var value)) throw new SecretStoreUnavailableException("Synthetic secret-store outage.");
            return Task.FromResult(value);
        }
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken) { Values.TryRemove(reference.Path, out _); return Task.CompletedTask; }
    }
    private sealed class FakeCloud : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpMethod Method, string Credential, Uri? Uri)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var credential = request.Headers.Authorization?.Parameter ?? "";
            Requests.Enqueue((request.Method, credential, request.RequestUri));
            if (credential == "synthetic-rejected-token") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = JsonContent.Create(new { error = new { message = "synthetic-rejected-token" } }) });
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { servers = Array.Empty<object>(), droplets = Array.Empty<object>(), instances = Array.Empty<object>() }) });
        }
    }
}
