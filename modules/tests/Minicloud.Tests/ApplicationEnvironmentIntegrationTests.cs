extern alias WorkerAssembly;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Minicloud.Common.Providers.Dns;
using AppDeletionProcessor = WorkerAssembly::Minicloud.Worker.Deletion.AppDeletionProcessor;
using Minicloud.Api.Auth;
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ApplicationEnvironmentIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ApiFactory? _factory;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new ApiFactory(_connection);
        _factory.CreateClient().Dispose();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task CanonicalApplicationRoutes_ReplaceBothVersionedApisAndIdentityAliases()
    {
        var org = await SeedOrgWithCatalogAsync("org_routes", DateTimeOffset.UtcNow);
        using var client = CreateAuthorizedClient(org.ApiKeyToken);
        foreach (var route in new[] { "/v1/apps", "/api/apps/resolve/missing" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        var routes = _factory!.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>().Select(x => x.RoutePattern.RawText ?? string.Empty);
        Assert.DoesNotContain(routes, route => route.StartsWith("v1/apps", StringComparison.Ordinal) || route.StartsWith("v2/apps", StringComparison.Ordinal));
        var response = await client.GetAsync($"/api/apps?organizationId={org.OrganizationId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<ApplicationResponse[]>())!);
    }

    [Fact]
    public async Task ApplicationCreate_RejectsRemovedFlatEnvironmentFields()
    {
        var org = await SeedOrgWithCatalogAsync("org_flat_create", DateTimeOffset.UtcNow);
        using var client = CreateAuthorizedClient(org.ApiKeyToken);
        var response = await client.PostAsJsonAsync("/api/apps", new
        {
            organizationId = org.OrganizationId, name = "Flat", slug = "flat", plan = "p0", database = "sqlite"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.False(await db.Applications.AnyAsync(x => x.OrganizationId == org.OrganizationId));
        Assert.False(await db.Apps.AnyAsync(x => x.OrganizationId == org.OrganizationId));
    }

    [Fact]
    public async Task AppsLifecycle_AtomicCreate_ListOnce_ExcludeInternal_AndStaleRuntimeNeverHealthy()
    {
        var org = await SeedOrgWithCatalogAsync("org_application_lifecycle", DateTimeOffset.UtcNow);
        using var client = CreateAuthorizedClient(org.ApiKeyToken);

        // 1. Create application with initial environment atomically via POST /api/apps
        var createResp = await client.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(
            org.OrganizationId,
            "MuniPages",
            "munipages",
            new CreateInitialEnvironmentRequest("production", "p0", "postgres")));
        Assert.True(createResp.StatusCode == HttpStatusCode.Created, await createResp.Content.ReadAsStringAsync());

        var createdApp = (await createResp.Content.ReadFromJsonAsync<ApplicationResponse>())!;
        Assert.StartsWith("appl_", createdApp.Id);
        Assert.Equal("MuniPages", createdApp.Name);
        Assert.Equal("munipages", createdApp.Slug);
        Assert.Equal(1, createdApp.EnvironmentCount);
        Assert.Equal(1, createdApp.PersistentEnvironmentCount);
        Assert.Equal(0, createdApp.PreviewEnvironmentCount);

        var prodEnv = Assert.Single(createdApp.Environments);
        Assert.Equal(createdApp.DefaultEnvironmentId, prodEnv.Id);
        Assert.Equal("production", prodEnv.Name);
        Assert.Equal("persistent", prodEnv.Kind);
        Assert.Equal("p0", prodEnv.Plan);
        Assert.Equal("postgres", prodEnv.Database);
        Assert.Equal("unprovisioned", prodEnv.RuntimeObservation.State);
        Assert.False(prodEnv.RuntimeObservation.Connected);

        // 2. Add a second persistent environment (staging) via POST /api/apps/{applicationId}/environments
        var createStagingResp = await client.PostAsJsonAsync(
            $"/api/apps/{createdApp.Id}/environments",
            new CreateEnvironmentRequest("staging", "p0-v-0", "sqlite"));
        Assert.Equal(HttpStatusCode.Created, createStagingResp.StatusCode);
        var stagingEnv = (await createStagingResp.Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>())!;
        Assert.Equal("staging", stagingEnv.Name);
        Assert.Equal("persistent", stagingEnv.Kind);
        Assert.Equal("munipages-staging", stagingEnv.RuntimeSlug);

        // Case-insensitive duplicate environment name is rejected
        var dupResp = await client.PostAsJsonAsync(
            $"/api/apps/{createdApp.Id}/environments",
            new CreateEnvironmentRequest("STAGING", "p0", "sqlite"));
        Assert.Equal(HttpStatusCode.BadRequest, dupResp.StatusCode);
        Assert.Contains("environment_name_taken", await dupResp.Content.ReadAsStringAsync());

        // 3. Seed an internal database record (db-*) and stale runtime heartbeat on production
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Apps.Add(new MinicloudApp
            {
                Id = "app_internal_db_1",
                OrganizationId = org.OrganizationId,
                Name = "db-analytics",
                Slug = "db-analytics",
                EnvironmentName = "production",
                NormalizedEnvironmentName = "production",
                EnvironmentKind = EnvironmentKinds.Internal,
                Plan = AppPlan.P0,
                Database = DatabaseMode.Postgres,
                CreatedAt = now,
                UpdatedAt = now
            });

            var staleTime = now.AddMinutes(-10); // stale (> 120s)
            var runtimeServer = new RuntimeServer
            {
                Id = "rserver_prod_1",
                OrganizationId = org.OrganizationId,
                AppId = prodEnv.Id,
                Provider = "hetzner",
                LastSeenAt = staleTime,
                CreatedAt = staleTime,
                UpdatedAt = staleTime
            };
            db.RuntimeServers.Add(runtimeServer);
            db.RuntimeStatusSnapshots.Add(new RuntimeStatusSnapshot
            {
                Id = "rsnap_prod_1",
                OrganizationId = org.OrganizationId,
                AppId = prodEnv.Id,
                RuntimeServerId = runtimeServer.Id,
                StatusJson = JsonSerializer.Serialize(new
                {
                    observedAt = staleTime,
                    services = new[] { new { name = "web", state = "running", health = "healthy" } }
                }),
                ObservedAt = staleTime,
                ReceivedAt = staleTime
            });
            await db.SaveChangesAsync();
        }

        // 4. GET /api/apps returns MuniPages once with 2 environments, excludes internal db-analytics,
        // and reports stale runtime observation as stale (never "healthy")
        var list = (await client.GetFromJsonAsync<List<ApplicationResponse>>($"/api/apps?organizationId={org.OrganizationId}"))!;
        var singleApp = Assert.Single(list);
        Assert.Equal(createdApp.Id, singleApp.Id);
        Assert.Equal(2, singleApp.EnvironmentCount);
        Assert.DoesNotContain(list, a => a.Slug == "db-analytics");

        var listedProd = singleApp.Environments.Single(e => e.Id == prodEnv.Id);
        Assert.True(listedProd.RuntimeObservation.Stale);
        Assert.Equal("stale", listedProd.RuntimeObservation.State);
        Assert.NotEqual("healthy", listedProd.RuntimeObservation.State);
    }

    [Fact]
    public async Task ApplicationMetadataMutation_PreservesEnvironmentIdentityAndRejectsInvalidFields()
    {
        var org = await SeedOrgWithCatalogAsync("org_application_resolve", DateTimeOffset.UtcNow);
        using var client = CreateAuthorizedClient(org.ApiKeyToken);

        var createdApp = await (await client.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(
            org.OrganizationId,
            "Billing Portal",
            "billing-portal",
            new CreateInitialEnvironmentRequest("production", "p0", "sqlite"))))
            .Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.NotNull(createdApp);
        var prodId = createdApp!.Environments[0].Id;

        var stagingEnv = await (await client.PostAsJsonAsync(
            $"/api/apps/{createdApp.Id}/environments",
            new CreateEnvironmentRequest("staging", "p0", "sqlite")))
            .Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>();
        Assert.NotNull(stagingEnv);

        // Set staging as dashboard default via PATCH /api/apps/{applicationId}
        var patchAppResp = await client.PatchAsJsonAsync(
            $"/api/apps/{createdApp.Id}",
            new UpdateApplicationRequest("Billing Portal Renamed", stagingEnv!.Id));
        Assert.Equal(HttpStatusCode.OK, patchAppResp.StatusCode);
        var updatedApp = (await patchAppResp.Content.ReadFromJsonAsync<ApplicationResponse>())!;
        Assert.Equal("Billing Portal Renamed", updatedApp.Name);
        Assert.Equal("billing-portal", updatedApp.Slug);
        Assert.Equal(stagingEnv.Id, updatedApp.DefaultEnvironmentId);

        // Reject plan/database fields on application PATCH
        var invalidPatch = await client.PatchAsJsonAsync(
            $"/api/apps/{createdApp.Id}",
            new UpdateApplicationRequest(Plan: "p0-v-0"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidPatch.StatusCode);
        Assert.Contains("validation_failed", await invalidPatch.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/apps/{prodId}")).StatusCode);
        var canonical = (await client.GetFromJsonAsync<ApplicationResponse>($"/api/apps/{createdApp.Id}"))!;
        Assert.Equal(stagingEnv.Id, canonical.DefaultEnvironmentId);

        // Rename environment via PATCH /api/apps/{applicationId}/environments/{environmentId} preserves runtime ID & slug
        var renameEnvResp = await client.PatchAsJsonAsync(
            $"/api/apps/{createdApp.Id}/environments/{stagingEnv.Id}",
            new UpdateEnvironmentRequest(Name: "qa-preprod", Plan: "p0-v-0"));
        Assert.Equal(HttpStatusCode.OK, renameEnvResp.StatusCode);
        var renamedEnv = (await renameEnvResp.Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>())!;
        Assert.Equal(stagingEnv.Id, renamedEnv.Id);
        Assert.Equal("qa-preprod", renamedEnv.Name);
        Assert.Equal(stagingEnv.RuntimeSlug, renamedEnv.RuntimeSlug);
        Assert.Equal("p0-v-0", renamedEnv.Plan);
    }

    [Fact]
    public async Task DeletionLifecycle_RootEnvironmentDelete_ZeroEnvironmentApp_AndFullAppDelete()
    {
        var org = await SeedOrgWithCatalogAsync("org_application_deletion", DateTimeOffset.UtcNow);
        using var client = CreateAuthorizedClient(org.ApiKeyToken);

        var app = await (await client.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(
            org.OrganizationId,
            "DeleteTest",
            "delete-test",
            new CreateInitialEnvironmentRequest("production", "p0", "sqlite"))))
            .Content.ReadFromJsonAsync<ApplicationResponse>();
        var prodId = app!.Environments[0].Id;

        var staging = await (await client.PostAsJsonAsync(
            $"/api/apps/{app.Id}/environments",
            new CreateEnvironmentRequest("staging", "p0", "sqlite")))
            .Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>();
        var stagingId = staging!.Id;

        // Revoked shared-database bindings still reference their environment.
        // Cleanup must remove only the deleted environment's bindings and keep
        // the independent source database and sibling environment intact.
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.ManagedDatabases.Add(new ManagedDatabase
            {
                Id = "database_shared_source", OrganizationId = org.OrganizationId,
                Name = "shared-source", Placement = "shared", Status = "ready"
            });
            db.ManagedDatabaseBindings.AddRange(
                new ManagedDatabaseBinding { Id = "binding_prod_revoked", OrganizationId = org.OrganizationId,
                    DatabaseId = "database_shared_source", AppId = prodId, ServiceName = "web", Status = "revoked" },
                new ManagedDatabaseBinding { Id = "binding_staging_revoked", OrganizationId = org.OrganizationId,
                    DatabaseId = "database_shared_source", AppId = stagingId, ServiceName = "web", Status = "revoked" });
            await db.SaveChangesAsync();
        }

        // 1. Wrong confirmation on environment delete is rejected
        var badDelete = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/apps/{app.Id}/environments/{prodId}")
        {
            Content = JsonContent.Create(new DeleteEnvironmentRequest("wrong-name"))
        });
        Assert.Equal(HttpStatusCode.BadRequest, badDelete.StatusCode);

        // 2. Delete the initial/root environment (production) via DELETE /api/apps/{applicationId}/environments/{prodId}
        var deleteProdResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/apps/{app.Id}/environments/{prodId}")
        {
            Content = JsonContent.Create(new DeleteEnvironmentRequest("production"))
        });
        Assert.Equal(HttpStatusCode.Accepted, deleteProdResp.StatusCode);

        // Exercise actual worker cleanup, including audit detachment and sibling preservation.
        await RunDeletionWorkerAsync();

        var afterProdDelete = (await client.GetFromJsonAsync<ApplicationResponse>($"/api/apps/{app.Id}"))!;
        Assert.Single(afterProdDelete.Environments);
        Assert.Equal(stagingId, afterProdDelete.Environments[0].Id);
        Assert.Equal(stagingId, afterProdDelete.DefaultEnvironmentId);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var retainedBinding = Assert.Single(await db.ManagedDatabaseBindings.ToListAsync());
            Assert.Equal("binding_staging_revoked", retainedBinding.Id);
            Assert.Equal(stagingId, retainedBinding.AppId);
            Assert.Equal("ready", (await db.ManagedDatabases.SingleAsync()).Status);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/apps/{prodId}")).StatusCode);

        // 3. Delete the last remaining environment (staging) -> leaves a valid zero-environment application
        var deleteStagingResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/apps/{app.Id}/environments/{stagingId}")
        {
            Content = JsonContent.Create(new DeleteEnvironmentRequest("staging"))
        });
        Assert.Equal(HttpStatusCode.Accepted, deleteStagingResp.StatusCode);

        await RunDeletionWorkerAsync();

        var zeroEnvApp = (await client.GetFromJsonAsync<ApplicationResponse>($"/api/apps/{app.Id}"))!;
        Assert.Equal(0, zeroEnvApp.EnvironmentCount);
        Assert.Empty(zeroEnvApp.Environments);
        Assert.Null(zeroEnvApp.DefaultEnvironmentId);

        // Re-add an environment to the zero-environment application
        var recreatedEnvResp = await client.PostAsJsonAsync(
            $"/api/apps/{app.Id}/environments",
            new CreateEnvironmentRequest("production", "p0", "sqlite"));
        Assert.Equal(HttpStatusCode.Created, recreatedEnvResp.StatusCode);

        // 4. Delete the entire application via DELETE /api/apps/{applicationId}
        var deleteAppResp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/apps/{app.Id}")
        {
            Content = JsonContent.Create(new DeleteApplicationRequest("delete-test"))
        });
        Assert.Equal(HttpStatusCode.Accepted, deleteAppResp.StatusCode);

        var deletingApp = (await client.GetFromJsonAsync<ApplicationResponse>($"/api/apps/{app.Id}"))!;
        Assert.Equal("queued", deletingApp.DeletionStatus);
        Assert.All(deletingApp.Environments, e => Assert.Equal("queued", e.DeletionStatus));

        // Adding a new environment while application is deleting is rejected with 409 Conflict
        var blockedCreate = await client.PostAsJsonAsync(
            $"/api/apps/{app.Id}/environments",
            new CreateEnvironmentRequest("another", "p0", "sqlite"));
        Assert.Equal(HttpStatusCode.Conflict, blockedCreate.StatusCode);
        await RunDeletionWorkerAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/apps/{app.Id}")).StatusCode);
    }

    [Fact]
    public async Task ChildDeletionStaysScopedAfterInitialEnvironmentDeletion()
    {
        var org = await SeedOrgWithCatalogAsync("org_application_detached_legacy", DateTimeOffset.UtcNow);
        using var client = CreateAuthorizedClient(org.ApiKeyToken);
        var app = (await (await client.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(
            org.OrganizationId, "Detached", "detached", new CreateInitialEnvironmentRequest("production", "p0", "sqlite"))))
            .Content.ReadFromJsonAsync<ApplicationResponse>())!;
        var prod = Assert.Single(app.Environments);
        var stage = (await (await client.PostAsJsonAsync($"/api/apps/{app.Id}/environments", new CreateEnvironmentRequest("staging", "p0", "sqlite")))
            .Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>())!;
        var qa = (await (await client.PostAsJsonAsync($"/api/apps/{app.Id}/environments", new CreateEnvironmentRequest("qa", "p0", "sqlite")))
            .Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>())!;
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/apps/{app.Id}/environments/{prod.Id}")
            { Content = JsonContent.Create(new DeleteEnvironmentRequest("production")) })).StatusCode);
        await RunDeletionWorkerAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/apps/{app.Id}/environments/{stage.Id}")
            { Content = JsonContent.Create(new DeleteEnvironmentRequest("staging")) })).StatusCode);
        await RunDeletionWorkerAsync();
        var remaining = (await client.GetFromJsonAsync<ApplicationResponse>($"/api/apps/{app.Id}"))!;
        Assert.Null(remaining.DeletionStatus);
        Assert.Equal(qa.Id, Assert.Single(remaining.Environments).Id);
    }

    [Fact]
    public async Task OperationalEndpoints_RejectMismatchedApplicationOrEnvironmentOwnershipBeforeSideEffects()
    {
        var org = await SeedOrgWithCatalogAsync("org_application_ownership", DateTimeOffset.UtcNow);
        using var client = CreateAuthorizedClient(org.ApiKeyToken);

        var appA = (await (await client.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(
            org.OrganizationId,
            "AppA",
            "app-a",
            new CreateInitialEnvironmentRequest("production", "p0", "sqlite"))))
            .Content.ReadFromJsonAsync<ApplicationResponse>())!;
        var envAProd = appA.Environments[0].Id;

        var envAStaging = (await (await client.PostAsJsonAsync(
            $"/api/apps/{appA.Id}/environments",
            new CreateEnvironmentRequest("staging", "p0", "sqlite")))
            .Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>())!.Id;

        var appB = (await (await client.PostAsJsonAsync("/api/apps", new CreateApplicationRequest(
            org.OrganizationId,
            "AppB",
            "app-b",
            new CreateInitialEnvironmentRequest("production", "p0", "sqlite"))))
            .Content.ReadFromJsonAsync<ApplicationResponse>())!;

        var service = new DeploymentServiceRequest("web", "example/web:1", 8080, true, "/", "/health");

        // 1. Deployment creation with mismatched ApplicationId returns 409 target_mismatch
        var badDeployApp = await client.PostAsJsonAsync("/api/deployments", new CreateDeploymentRequest(
            envAProd,
            "sqlite",
            null,
            [service],
            ApplicationId: appB.Id,
            EnvironmentId: envAProd));
        Assert.Equal(HttpStatusCode.Conflict, badDeployApp.StatusCode);
        Assert.Contains("target_mismatch", await badDeployApp.Content.ReadAsStringAsync());

        // 2. Deployment creation with mismatched EnvironmentId returns 409 target_mismatch
        var badDeployEnv = await client.PostAsJsonAsync("/api/deployments", new CreateDeploymentRequest(
            envAProd,
            "sqlite",
            null,
            [service],
            ApplicationId: appA.Id,
            EnvironmentId: envAStaging));
        Assert.Equal(HttpStatusCode.Conflict, badDeployEnv.StatusCode);
        Assert.Contains("target_mismatch", await badDeployEnv.Content.ReadAsStringAsync());

        // 3. Deployment preparation with mismatched EnvironmentId returns 409 target_mismatch
        var badPrep = await client.PostAsJsonAsync("/api/deployments/preparations", new StartDeploymentPreparationRequest(
            envAProd,
            "idem-key-1",
            ["web"],
            ApplicationId: appA.Id,
            EnvironmentId: envAStaging));
        Assert.Equal(HttpStatusCode.Conflict, badPrep.StatusCode);
        Assert.Contains("target_mismatch", await badPrep.Content.ReadAsStringAsync());

        // 4. Secret validation with mismatched EnvironmentId returns 409 target_mismatch
        var badSecrets = await client.PostAsJsonAsync("/api/deployments/validate-secrets", new ValidateDeploymentSecretsRequest(
            envAProd,
            [new DeploymentSecretServiceRequest("web", new Dictionary<string, string> { ["API_KEY"] = "API_KEY" })],
            ApplicationId: appA.Id,
            EnvironmentId: envAStaging));
        Assert.Equal(HttpStatusCode.Conflict, badSecrets.StatusCode);
        Assert.Contains("target_mismatch", await badSecrets.Content.ReadAsStringAsync());

        // 5. Query-string target mismatch on domains/runtime returns 409 target_mismatch
        var badDomains = await client.GetAsync($"/api/domains?appId={envAProd}&applicationId={appA.Id}&environmentId={envAStaging}");
        Assert.Equal(HttpStatusCode.Conflict, badDomains.StatusCode);
        Assert.Contains("target_mismatch", await badDomains.Content.ReadAsStringAsync());

        // 6. Matching ApplicationId + EnvironmentId succeeds
        var goodDeploy = await client.PostAsJsonAsync("/api/deployments", new CreateDeploymentRequest(
            envAProd,
            "sqlite",
            null,
            [service],
            ApplicationId: appA.Id,
            EnvironmentId: envAProd));
        Assert.Equal(HttpStatusCode.Created, goodDeploy.StatusCode);
        var createdDeployment = (await goodDeploy.Content.ReadFromJsonAsync<DeploymentResponse>())!;
        Assert.Equal(appA.Id, createdDeployment.ApplicationId);
        Assert.Equal(envAProd, createdDeployment.EnvironmentId);
    }

    private HttpClient CreateAuthorizedClient(string token)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(string OrganizationId, string ApiKeyToken)> SeedOrgWithCatalogAsync(string organizationId, DateTimeOffset now)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var apiKeyTokenService = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        var apiKeyToken = apiKeyTokenService.Generate();

        db.Organizations.Add(new Organization
        {
            Id = organizationId,
            Name = organizationId,
            Slug = organizationId.Replace('_', '-'),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.ApiKeys.Add(new ApiKey
        {
            Id = $"key_{Guid.NewGuid():N}",
            OrganizationId = organizationId,
            Name = "v2-api",
            KeyPrefix = apiKeyToken.Prefix,
            KeyHash = apiKeyTokenService.Hash(apiKeyToken.Token),
            ScopesJson = JsonSerializer.Serialize(new[]
            {
                ApiKeyScopes.AppsRead,
                ApiKeyScopes.AppsWrite,
                ApiKeyScopes.DeploymentsCreate,
                ApiKeyScopes.DeploymentsRead
            }),
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var catalog = scope.ServiceProvider.GetRequiredService<ProviderCatalogService>();
        foreach (var type in Enum.GetValues<Minicloud.Common.Providers.CloudProvider>())
        {
            var account = new ProviderAccount
            {
                Id = $"provider_{organizationId}_{type}",
                OrganizationId = organizationId,
                Name = type.ToString(),
                ProviderType = type,
                CredentialPath = $"fixtures/{organizationId}/{type}",
                CredentialGeneration = "1"
            };
            db.ProviderAccounts.Add(account);
            await db.SaveChangesAsync();
            foreach (var plan in Enum.GetValues<AppPlan>())
            {
                var mapping = ProviderPlanMapping.FromAppPlan(plan, new ProviderPlanOptions());
                if (mapping.Provider != type) continue;
                await catalog.CreateAsync(new(
                    organizationId,
                    account.Id,
                    mapping.Region,
                    mapping.Sku,
                    Minicloud.Common.Validation.ControlPlaneValidation.ToWireValue(plan)), default);
            }
            await db.SaveChangesAsync();
        }

        return (organizationId, apiKeyToken.Token);
    }

    private async Task RunDeletionWorkerAsync()
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var worker = new AppDeletionProcessor(db, _ => throw new InvalidOperationException("Unprovisioned fixture must not call a provider."),
            new FixtureDns(), NullLogger<AppDeletionProcessor>.Instance);
        Assert.True(await worker.ProcessNextAsync(default));
        Assert.DoesNotContain(await db.Apps.ToListAsync(), x => x.DeletionStatus == "failed");
    }

    private sealed class FixtureDns : ICloudflareDnsClient
    {
        public Task DeleteDnsRecordsAsync(string hostname, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertDnsRecordAsync(string hostname, string ipAddress, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task WaitForPropagationAsync(string hostname, string ipAddress, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    private sealed class ApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Providers:AllowLegacyConfiguration"] = "true",
                    ["ManagedDomains:Suffix"] = "app.muni.dev",
                    ["Firebase:ProjectId"] = "minicloud-test",
                    ["Database:RunMigrationsOnStartup"] = "false",
                    ["ConnectionStrings:Minicloud"] = "Data Source=:memory:"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<ISecretStore>();
                services.AddSingleton<ISecretStore, FixtureSecretStore>();
            });
        }
    }

    private sealed class FixtureSecretStore : ISecretStore
    {
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken ct) =>
            Task.FromResult(new StoredSecretReference("fixture", "fixture/secret", "1"));
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken ct) => Task.FromResult("synthetic-provider-token");
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken ct) => Task.CompletedTask;
    }
}
