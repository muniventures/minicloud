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
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppApiIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private AppApiFactory? _factory;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new AppApiFactory(_connection);
        _factory.CreateClient().Dispose();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task NamedEnvironmentsHaveIndependentPlansDomainsAndTenantAuthorization()
    {
        var seed = await SeedAsync("org_environments", "app_environments", DateTimeOffset.UtcNow);
        var foreign = await SeedAsync("org_env_foreign", "app_env_foreign", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        var url = $"/api/apps/{seed.ApplicationId}/environments";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("staging", "p0-v-0"))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreign.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("staging", "p0-v-0"))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var response = await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("staging", "p0-v-0"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var createdEnvironment = (await response.Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>())!;
        var staging = (await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{createdEnvironment.Id}"))!;
        Assert.Equal("p0-v-0", staging.Plan);
        Assert.Equal("staging", staging.EnvironmentName);
        var main = (await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{seed.AppId}"))!;
        Assert.Equal(2, main.Environments!.Count);
        Assert.Empty(main.Branches);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("STAGING", "p0-h-0"))).StatusCode);
        var service = new DeploymentServiceRequest("web", "example/web:1", 8080, true, "/", "/health");
        foreach (var id in new[] { main.Id, staging.Id })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/deployments", new CreateDeploymentRequest(id, "sqlite", null, [service]))).StatusCode);
        main = (await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{main.Id}"))!;
        staging = (await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{staging.Id}"))!;
        Assert.NotEqual(Assert.Single(main.Domains).Hostname, Assert.Single(staging.Domains).Hostname);
        var renamed = await client.PatchAsJsonAsync($"/api/apps/{seed.ApplicationId}/environments/{staging.Id}", new UpdateEnvironmentRequest("qa"));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        var afterRename = (await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{staging.Id}"))!;
        Assert.Equal("qa", afterRename.EnvironmentName);
        Assert.Equal(staging.Domains[0].Hostname, afterRename.Domains[0].Hostname);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/environments/{main.Id}/branches/{staging.Id}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreign.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/apps/{seed.ApplicationId}/environments/{staging.Id}", new UpdateEnvironmentRequest("other"))).StatusCode);
    }

    [Fact]
    public async Task Instance_discovery_is_anonymous_and_reports_configured_domain()
    {
        using var factory = new AppApiFactory(_connection, "apps.discovery.example");
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/instance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<InstanceInfoResponse>();
        Assert.NotNull(info);
        Assert.Equal("minicloud", info.Product);
        Assert.Equal(1, info.ProtocolVersion);
        Assert.Equal("apps.discovery.example", info.AppDomain);
    }

    [Fact]
    public async Task Instance_discovery_reports_packaged_release_metadata_anonymously()
    {
        using var metadata = new ReleaseMetadataFixture();
        using var factory = new AppApiFactory(_connection, "apps.discovery.example", metadata.Path);
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/instance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = (await response.Content.ReadFromJsonAsync<InstanceInfoResponse>())!;
        Assert.Equal("minicloud", info.Product);
        Assert.Equal(1, info.ProtocolVersion);
        Assert.Equal("apps.discovery.example", info.AppDomain);
        Assert.Equal("1.2.3-rc.1", info.ServerVersion);
        Assert.Equal(new string('a', 40), info.SourceRevision);
        Assert.Equal(new string('b', 40), Assert.Single(info.CliCompatibility!.SourceRevisions));
    }

    [Fact]
    public void Instance_discovery_metadata_missing_from_a_packaged_server_fails_startup()
    {
        using var metadata = new ReleaseMetadataFixture();
        File.Delete(metadata.Path);
        using var factory = new AppApiFactory(_connection, releaseMetadataPath: metadata.Path);
        Assert.Throws<FileNotFoundException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task NamedPlanSelectionUsesDeploymentPermissionAndFreezesPlacementAtAdmission()
    {
        var seed = await SeedAsync("org_plan_selection", "app_plan_selection", DateTimeOffset.UtcNow);
        string planId;
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var key = await db.ApiKeys.SingleAsync(x => x.OrganizationId == seed.OrganizationId);
            key.ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsRead, ApiKeyScopes.DeploymentsCreate });
            var catalog = scope.ServiceProvider.GetRequiredService<ProviderCatalogService>();
            planId = (await catalog.CreateAsync(new(seed.OrganizationId, $"provider_{seed.OrganizationId}_Hetzner", "nbg1", "cx23", "Produção econômica"), default)).Id;
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var route = $"/api/environments/{seed.AppId}/plan";
        var selected = await client.PostAsJsonAsync(route, new SelectAppPlanRequest("Produção econômica"));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        var app = (await selected.Content.ReadFromJsonAsync<EnvironmentDetailResponse>())!;
        Assert.Equal(planId, app.PlanId);
        Assert.Equal("Produção econômica", app.Plan);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PatchAsJsonAsync($"/api/apps/{seed.ApplicationId}/environments/{seed.AppId}", new UpdateEnvironmentRequest(Name: "unauthorized-rename"))).StatusCode);
        var invalid = await client.PostAsJsonAsync(route, new SelectAppPlanRequest("missing"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("plan_not_found", await invalid.Content.ReadAsStringAsync());
        var deployment = await client.PostAsJsonAsync("/api/deployments", new CreateDeploymentRequest(seed.AppId, "none", null,
            [new("web", "example/web:1", 8080, true, "/", "/health")], Plan: "Produção econômica"));
        Assert.Equal(HttpStatusCode.Created, deployment.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new SelectAppPlanRequest("p0"))).StatusCode);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var saved = await db.Deployments.SingleAsync(x => x.AppId == seed.AppId);
            Assert.Equal(planId, saved.PlanId);
            Assert.Equal($"provider_{seed.OrganizationId}_Hetzner", saved.ProviderAccountId);
            Assert.Equal("cx23", saved.ProviderSku);
            Assert.NotNull((await db.ProviderPlans.SingleAsync(x => x.Id == planId)).ProvisioningLockedAt);
        }
    }

    [Fact]
    public async Task StorageAdmissionRejectsOtherProvidersAndPersistsHetznerMounts()
    {
        var seed = await SeedAsync("org_storage", "app_storage", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var request = new CreateDeploymentRequest(seed.AppId, "none", null,
            [new DeploymentServiceRequest("api", "example/api:1", 8080, true, "/", "/health",
                Storage: new Dictionary<string, StorageMountRequest> { ["uploads"] = new(20, "/app/uploads", 1000, 1000) })]);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            (await db.Apps.SingleAsync(x => x.Id == seed.AppId)).Plan = AppPlan.P0V0;
            await db.SaveChangesAsync();
        }
        var unsupported = await client.PostAsJsonAsync("/api/deployments", request);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Contains("storage_provider_unsupported", await unsupported.Content.ReadAsStringAsync());
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            Assert.False(await db.Deployments.AnyAsync(x => x.AppId == seed.AppId));
            (await db.Apps.SingleAsync(x => x.Id == seed.AppId)).Plan = AppPlan.P0H0;
            await db.SaveChangesAsync();
        }
        var accepted = await client.PostAsJsonAsync("/api/deployments", request);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var created = (await accepted.Content.ReadFromJsonAsync<DeploymentCreateResponse>())!;
        var saved = await client.GetFromJsonAsync<DeploymentResponse>("/api/deployments/" + created.Id);
        Assert.Equal(new StorageMountRequest(20, "/app/uploads", 1000, 1000), Assert.Single(saved!.Services).Storage!["uploads"]);
    }

    [Fact]
    public async Task StorageInventoryRequiresAppAccessAndIncludesRetainedDisks()
    {
        var seed = await SeedAsync("org_storage_list", "app_storage_list", DateTimeOffset.UtcNow);
        var other = await SeedAsync("org_storage_other", "app_storage_other", DateTimeOffset.UtcNow);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.AppStorageVolumes.Add(new AppStorageVolume { Id = "storage_list", AppId = seed.AppId, Name = "uploads", Region = "nbg1", SizeGb = 20, ProviderVolumeId = "901" });
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        var url = $"/api/environments/{seed.AppId}/storage";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var volumes = await client.GetFromJsonAsync<AppStorageVolumeResponse[]>(url);
        Assert.Equal("901", Assert.Single(volumes!).ProviderVolumeId);
        Assert.Equal("registered", volumes![0].Status);
    }

    [Fact]
    public async Task StorageAdmissionRejectsShrinkAndOwnershipChange()
    {
        var seed = await SeedAsync("org_storage_resize", "app_storage_resize", DateTimeOffset.UtcNow);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            (await db.Apps.SingleAsync(x => x.Id == seed.AppId)).Plan = AppPlan.P0H0;
            db.AppStorageVolumes.Add(new AppStorageVolume { Id = "storage_resize", AppId = seed.AppId, Name = "uploads", Region = "nbg1", SizeGb = 30, Uid = 1000, Gid = 1000 });
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        foreach (var mount in new[] { new StorageMountRequest(20, "/app/data", 1000, 1000), new(30, "/app/data", 0, 0) })
        {
            var request = new CreateDeploymentRequest(seed.AppId, "none", null,
                [new DeploymentServiceRequest("api", "example/api:1", 8080, true, "/", "/health", Storage: new Dictionary<string, StorageMountRequest> { ["uploads"] = mount })]);
            var response = await client.PostAsJsonAsync("/api/deployments", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_storage_change", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task CancelEndpointAuthenticatesAndReturnsDurableAcknowledgement()
    {
        var seed = await SeedAsync("org_cancel_api", "app_cancel_api", DateTimeOffset.UtcNow);
        var other = await SeedAsync("org_cancel_other", "app_cancel_other", DateTimeOffset.UtcNow);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.Deployments.Add(new Deployment { Id = "dep_cancel_api", OrganizationId = seed.OrganizationId, AppId = seed.AppId,
                Status = DeploymentStatus.Queued, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/deployments/dep_cancel_api/cancel", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.ApiKeyToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/deployments/dep_cancel_api/cancel", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var result = await client.PostAsync("/api/deployments/dep_cancel_api/cancel", null);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.True((await result.Content.ReadFromJsonAsync<DeploymentCancellationResponse>())!.CancellationRequested);
        var deployment = await client.GetFromJsonAsync<DeploymentResponse>("/api/deployments/dep_cancel_api");
        Assert.Equal("canceled", deployment!.Status);
        Assert.NotNull(deployment.CancellationRequestedAt);
        Assert.NotNull(deployment.CompletedAt);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/deployments/dep_cancel_api/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task CancelingLocalBuildClosesPreparationAndPreventsAdmission()
    {
        var seed = await SeedAsync("org_cancel_build", "app_cancel_build", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var start = new StartDeploymentPreparationRequest(seed.AppId, Guid.NewGuid().ToString("D"), ["api"]);
        using var response = await client.PostAsJsonAsync("/api/deployments/preparations", start);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var pending = (await response.Content.ReadFromJsonAsync<DeploymentCreateResponse>())!;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/deployments/{pending.Id}/cancel", null)).StatusCode);

        var deployment = await client.GetFromJsonAsync<DeploymentResponse>($"/api/deployments/{pending.Id}");
        Assert.Equal("canceled", deployment!.Status);
        Assert.NotNull(deployment.CancellationRequestedAt);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/deployments/preparations/{pending.Id}",
            new UpdateDeploymentPreparationRequest(seed.AppId, "heartbeat"))).StatusCode);
        var submit = new CreateDeploymentRequest(seed.AppId, "none", null,
            [new DeploymentServiceRequest("api", "example/api:latest", 8080, true, "/", "/health")], PreparationId: pending.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/deployments", submit)).StatusCode);
        var redispatch = await client.PostAsync($"/api/deployments/{pending.Id}/dispatch", null);
        Assert.Equal(HttpStatusCode.Conflict, redispatch.StatusCode);
        using var redispatchError = JsonDocument.Parse(await redispatch.Content.ReadAsStringAsync());
        Assert.Equal("preparation_not_submitted",
            redispatchError.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/deployments/preparations",
            start with { IdempotencyKey = Guid.NewGuid().ToString("D") })).StatusCode);
    }

    [Fact]
    public async Task DispatchRejectsActiveDeploymentWithStructuredCodeAndPreservesOwnership()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_active_redispatch", "app_active_redispatch", now);
        const string deploymentId = "dep_active_redispatch";
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.Deployments.Add(new Deployment
            {
                Id = deploymentId,
                OrganizationId = seed.OrganizationId,
                AppId = seed.AppId,
                Status = DeploymentStatus.Deploying,
                OwnershipToken = "synthetic-active-owner",
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var response = await client.PostAsync($"/api/deployments/{deploymentId}/dispatch", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("deployment_active", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var deployment = await verifyDb.Deployments.SingleAsync(x => x.Id == deploymentId);
        Assert.Equal(DeploymentStatus.Deploying, deployment.Status);
        Assert.Equal("synthetic-active-owner", deployment.OwnershipToken);
    }

    [Fact]
    public async Task LocalBuildPreparation_IsVisibleIdempotentAndCompletesSameRecord()
    {
        var seed = await SeedAsync("org_local_prepare", "app_local_prepare", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var start = new StartDeploymentPreparationRequest(seed.AppId, Guid.NewGuid().ToString("D"), ["api"]);
        var response = await client.PostAsJsonAsync("/api/deployments/preparations", start);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var pending = (await response.Content.ReadFromJsonAsync<DeploymentCreateResponse>())!;
        Assert.Equal("waiting_for_build", pending.Status);
        var replay = await client.PostAsJsonAsync("/api/deployments/preparations", start);
        Assert.Equal(pending.Id, (await replay.Content.ReadFromJsonAsync<DeploymentCreateResponse>())!.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/deployments/preparations", start with { IdempotencyKey = Guid.NewGuid().ToString("D") })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/deployments/preparations/{pending.Id}", new UpdateDeploymentPreparationRequest(seed.AppId, "heartbeat"))).StatusCode);
        var request = new CreateDeploymentRequest(seed.AppId, "none", null,
            [new DeploymentServiceRequest("api", "example/api:latest", 8080, true, "/", "/health")], PreparationId: pending.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/deployments", request with { PreparationId = null })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/deployments", request with { Services = [request.Services[0] with { Name = "other" }] })).StatusCode);
        var queued = await client.PostAsJsonAsync("/api/deployments", request);
        Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
        var created = (await queued.Content.ReadFromJsonAsync<DeploymentCreateResponse>())!;
        Assert.Equal(pending.Id, created.Id);
        Assert.Equal("queued", created.Status);
        // A lost admission response must never let failure/cancellation overwrite queued work.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/api/deployments/preparations/{pending.Id}", new UpdateDeploymentPreparationRequest(seed.AppId, "failed"))).StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var stored = await db.Deployments.Include(x => x.Services).Include(x => x.Events).SingleAsync(x => x.AppId == seed.AppId);
        Assert.Equal(DeploymentStatus.Queued, stored.Status);
        Assert.Equal(pending.CreatedAt, stored.CreatedAt);
        Assert.Null(stored.StartedAt);
        Assert.Single(stored.Services);
        Assert.Contains(stored.Events, x => x.Message.Contains("Waiting for the CLI"));
        Assert.Contains(stored.Events, x => x.Message == "Deployment queued.");
    }

    [Theory]
    [InlineData("failed", "failed", "local_build_failed")]
    [InlineData("canceled", "canceled", null)]
    [InlineData("expired", "failed", "local_build_abandoned")]
    public async Task LocalBuildPreparation_ClosesFailuresCancellationAndExpiredClients(string action, string expected, string? failure)
    {
        var seed = await SeedAsync("org_prepare_close", "app_prepare_close", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var start = new StartDeploymentPreparationRequest(seed.AppId, Guid.NewGuid().ToString("D"), ["1_api"]);
        var pending = (await (await client.PostAsJsonAsync("/api/deployments/preparations", start)).Content.ReadFromJsonAsync<DeploymentCreateResponse>())!;
        if (action == "expired")
        {
            await using var editScope = _factory.Services.CreateAsyncScope();
            var edit = editScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            (await edit.Deployments.SingleAsync(x => x.Id == pending.Id)).UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-6);
            await edit.SaveChangesAsync();
        }
        var closed = await client.PostAsJsonAsync($"/api/deployments/preparations/{pending.Id}", new UpdateDeploymentPreparationRequest(seed.AppId, action == "expired" ? "heartbeat" : action));
        Assert.Equal(action == "expired" ? HttpStatusCode.Conflict : HttpStatusCode.NoContent, closed.StatusCode);
        var detail = (await client.GetFromJsonAsync<DeploymentResponse>($"/api/deployments/{pending.Id}"))!;
        Assert.Equal(expected, detail.Status);
        Assert.Equal(failure, detail.FailureCode);
        Assert.NotNull(detail.CompletedAt);
        Assert.Null(detail.StartedAt);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/deployments/preparations", start with { IdempotencyKey = Guid.NewGuid().ToString("D") })).StatusCode);
    }

    [Fact]
    public async Task LocalBuildPreparation_RequiresAppPermissionAndRejectsForeignCompletion()
    {
        var first = await SeedAsync("org_prepare_owner", "app_prepare_owner", DateTimeOffset.UtcNow);
        var other = await SeedAsync("org_prepare_other", "app_prepare_other", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.ApiKeyToken);
        var start = new StartDeploymentPreparationRequest(first.AppId, Guid.NewGuid().ToString("D"), ["api"]);
        var pending = (await (await client.PostAsJsonAsync("/api/deployments/preparations", start)).Content.ReadFromJsonAsync<DeploymentCreateResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/deployments/preparations", start)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/deployments/preparations/{pending.Id}", new UpdateDeploymentPreparationRequest(first.AppId, "failed"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/deployments/preparations/{pending.Id}", new UpdateDeploymentPreparationRequest(other.AppId, "failed"))).StatusCode);
        var foreignRequest = new CreateDeploymentRequest(other.AppId, "none", null,
            [new DeploymentServiceRequest("api", "example/api:latest", 8080, true, "/", "/health")], PreparationId: pending.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/deployments", foreignRequest)).StatusCode);
    }

    [Fact]
    public async Task ManagedImageAdmission_FailsClosedBeforeCreatingDeploymentOrDomains()
    {
        var seed = await SeedAsync("org_image_gate", "app_image_gate", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var request = new CreateDeploymentRequest(seed.AppId, "none", null,
            [new DeploymentServiceRequest("api", null, 8080, true, "/", "/health", ImagePublicationId: "img_unavailable")]);
        var response = await client.PostAsJsonAsync("/api/deployments", request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(ImagePublicationErrors.Unavailable, await response.Content.ReadAsStringAsync());
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.False(await db.Deployments.AnyAsync(x => x.AppId == seed.AppId));
        Assert.False(await db.Domains.AnyAsync(x => x.AppId == seed.AppId));
        var foreign = await SeedAsync("org_image_foreign", "app_image_foreign", DateTimeOffset.UtcNow);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreign.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/deployments", request)).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingSecretAdmission_ReturnsNamedErrorBeforeCreatingDeploymentOrDomains(bool retained)
    {
        var seed = await SeedAsync("org_missing_secret", "app_missing_secret", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var request = new CreateDeploymentRequest(seed.AppId, "none", null,
            [new DeploymentServiceRequest("api", "example/api:latest", 8080, true, "/", "/health",
                SecretEnv: new Dictionary<string, string> { ["Instagram__AppSecret"] = "INSTAGRAM_APP_SECRET" })]);
        if (retained)
        {
            await using var seedScope = _factory.Services.CreateAsyncScope();
            var seedDb = seedScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var prior = new Deployment
            {
                Id = "dep_retained_secret", AppId = seed.AppId, OrganizationId = seed.OrganizationId,
                Status = DeploymentStatus.Succeeded, Database = DatabaseMode.None, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            prior.Services.Add(new DeploymentService
            {
                Id = "svc_retained_secret", Name = "api", Image = "example/api:latest", Port = 8080, Public = true,
                Path = "/", HealthPath = "/health", SecretEnvJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["Instagram__AppSecret"] = "INSTAGRAM_APP_SECRET" })
            });
            seedDb.Deployments.Add(prior);
            await seedDb.SaveChangesAsync();
            request = request with { Services = [new DeploymentServiceRequest("portal", "example/portal:latest", 8080, true, "/", "/health")] };
        }
        var response = await client.PostAsJsonAsync("/api/deployments", request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.Equal("missing_service_secret", error!.Error.Code);
        Assert.Contains("INSTAGRAM_APP_SECRET", error.Error.Message);
        Assert.Contains("api", error.Error.Message);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Equal(retained ? 1 : 0, await db.Deployments.CountAsync(x => x.AppId == seed.AppId));
        Assert.False(await db.Domains.AnyAsync(x => x.AppId == seed.AppId));
    }

    [Fact]
    public async Task SecretPreflight_UsesDeploymentPermissionAndReturnsNoValues()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_secret_preflight", "app_secret_preflight", now);
        var foreign = await SeedAsync("org_secret_preflight_foreign", "app_secret_preflight_foreign", now);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var request = new ValidateDeploymentSecretsRequest(seed.AppId,
            [new DeploymentSecretServiceRequest("api", new Dictionary<string, string> { ["TOKEN"] = "API_TOKEN" })]);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/secrets?appId={seed.AppId}")).StatusCode);
        var missing = await client.PostAsJsonAsync("/api/deployments/validate-secrets", request);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("missing_service_secret", await missing.Content.ReadAsStringAsync());
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.AppServiceSecrets.Add(new AppServiceSecret
            {
                Id = "secret_preflight", AppId = seed.AppId, OrganizationId = seed.OrganizationId,
                ServiceName = "__app", Name = "API_TOKEN", Status = "active", StoreProvider = "unavailable-test-store",
                StorePath = "must-not-be-read", CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        var valid = await client.PostAsJsonAsync("/api/deployments/validate-secrets", request);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        Assert.Empty(await valid.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreign.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/deployments/validate-secrets", request)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var invalid = request with { Services = [new DeploymentSecretServiceRequest("api", new Dictionary<string, string> { ["TOKEN"] = "invalid secret" })] };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/deployments/validate-secrets", invalid)).StatusCode);
    }

    [Fact]
    public async Task EnvironmentEndpoints_LoadRequiredNavigationData()
    {
        var now = new DateTimeOffset(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);
        var seed = await SeedAsync("org_app_api_mapping", "app_api_mapping", now);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);

        var getResponse = await client.GetAsync($"/api/environments/{seed.AppId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var app = await getResponse.Content.ReadFromJsonAsync<EnvironmentDetailResponse>();
        Assert.NotNull(app);
        Assert.Empty(app!.Domains);
        Assert.Empty(app.Branches);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PatchAsJsonAsync($"/api/environments/{seed.AppId}", new { name = "removed-adapter" })).StatusCode);
        var updateResponse = await client.PatchAsJsonAsync($"/api/apps/{seed.ApplicationId}/environments/{seed.AppId}",
            new UpdateEnvironmentRequest(Name: "updated-env"));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>();
        Assert.NotNull(updated);
        Assert.Equal("updated-env", updated!.Name);
        Assert.Empty(updated.Domains);

        var createResponse = await client.PostAsJsonAsync("/api/apps", new
        {
            organizationId = seed.OrganizationId,
            name = "Created App API Mapping",
            slug = "created-app-api-mapping",
            initialEnvironment = new { name = "production", plan = "p0", database = "sqlite" }
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<ApplicationResponse>();
        Assert.NotNull(created);
        Assert.Empty(Assert.Single(created!.Environments).Domains);
    }

    [Fact]
    public async Task AssetPublicationIsQueuedScopedAndDoesNotChangeActiveServices()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_assets", "app_assets", now);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var app = await db.Apps.SingleAsync(x => x.Id == seed.AppId);
            await scope.ServiceProvider.GetRequiredService<ProviderCatalogService>().BindAsync(app, "p0", default);
            db.DeploymentArtifacts.Add(new DeploymentArtifact
            {
                Id = "art_asset",
                OrganizationId = seed.OrganizationId,
                AppId = seed.AppId,
                ServiceName = "cli",
                FileName = "cli.zip",
                ContentType = "application/zip",
                Sha256 = new string('a', 64),
                SizeBytes = 12,
                StoragePath = "fixture.zip",
                ManifestJson = "{}",
                Status = DeploymentArtifactStatus.Ready,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddDays(1)
            });
            await db.SaveChangesAsync();
        }
        var response = await client.PostAsJsonAsync("/api/assets", new PublishAssetRequest(seed.AppId, "cli", "art_asset", new string('b', 64), "/cli-releases"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var release = (await response.Content.ReadFromJsonAsync<AssetReleaseResponse>())!;
        Assert.Equal("queued", release.Status);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var deployment = await db.Deployments.Include(d => d.Services).SingleAsync(d => d.Id == release.DeploymentId);
            Assert.Empty(deployment.Services);
            Assert.Equal(release.Id, deployment.AssetReleaseId);
            Assert.NotNull(deployment.PlanId);
            Assert.Equal($"provider_{seed.OrganizationId}_Hetzner", deployment.ProviderAccountId);
            Assert.Equal("Hetzner", deployment.ProviderType);
            Assert.Null(deployment.Domain);
            Assert.Empty(await new DeploymentServiceSetService().BuildActiveServicesAsync(db, seed.AppId, default));
        }
        var read = await client.GetFromJsonAsync<AssetReleaseResponse>("/api/assets/" + release.Id);
        Assert.Equal(release.Id, read!.Id);
        var bind = await client.PostAsJsonAsync("/api/deployments", new CreateDeploymentRequest(seed.AppId, "sqlite", null,
            [new("backend", "example:1", 8080, true, "/", "/health", AssetMounts: new Dictionary<string, AssetMountRequest> { ["cli"] = new(release.Id, "/app/cli-releases") })]));
        Assert.Equal(HttpStatusCode.BadRequest, bind.StatusCode);
        var foreign = await SeedAsync("org_asset_foreign", "app_asset_foreign", now);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", foreign.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/assets/" + release.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/assets", new PublishAssetRequest(foreign.AppId, "cli", "art_asset", new string('b', 64), "/cli-releases"))).StatusCode);
        await using var deletionScope = _factory.Services.CreateAsyncScope();
        var deletionDb = deletionScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        deletionDb.Apps.Remove(await deletionDb.Apps.SingleAsync(x => x.Id == seed.AppId));
        await deletionDb.SaveChangesAsync();
        Assert.False(await deletionDb.AssetReleases.AnyAsync(x => x.AppId == seed.AppId));
    }

    [Fact]
    public async Task BranchEndpoints_Create_reuse_and_nest_child_apps()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var seed = await SeedAsync("org_app_branch_api", "app_branch_api", now);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);

        var firstResponse = await client.PostAsJsonAsync($"/api/environments/{seed.AppId}/branches", new { branchName = "feature/checkout" });
        var secondResponse = await client.PostAsJsonAsync($"/api/environments/{seed.AppId}/branches", new { branchName = "feature/checkout" });

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<EnvironmentDetailResponse>();
        var second = await secondResponse.Content.ReadFromJsonAsync<EnvironmentDetailResponse>();
        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(seed.AppId, first.ParentAppId);
        Assert.Equal("feature-checkout", first.BranchName);
        Assert.Equal("p0", first.Plan);

        var main = await client.GetFromJsonAsync<EnvironmentDetailResponse>($"/api/environments/{seed.AppId}");
        var branch = Assert.Single(main!.Branches);
        Assert.Equal(first.Id, branch.Id);

        var applications = await client.GetFromJsonAsync<ApplicationResponse[]>($"/api/apps?organizationId={seed.OrganizationId}");
        var application = Assert.Single(applications!);
        Assert.Equal(seed.ApplicationId, application.Id);
        Assert.Equal(2, application.EnvironmentCount);
        Assert.Contains(application.Environments, environment => environment.Id == first.Id && environment.Kind == EnvironmentKinds.Preview);
    }

    [Fact]
    public async Task PreviewBranchFromNamedEnvironment_CreatesAndDeletesOnlyItsTarget()
    {
        var seed = await SeedAsync("org_named_preview", "app_named_preview", DateTimeOffset.UtcNow);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var create = await client.PostAsJsonAsync($"/api/apps/{seed.ApplicationId}/environments", new CreateEnvironmentRequest("staging", "p0", "sqlite"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var staging = (await create.Content.ReadFromJsonAsync<ApplicationEnvironmentResponse>())!;
        var branchResponse = await client.PostAsJsonAsync($"/api/environments/{staging.Id}/branches", new EnsureAppBranchRequest("feature/from-staging"));
        Assert.True(branchResponse.StatusCode == HttpStatusCode.OK, await branchResponse.Content.ReadAsStringAsync());
        var branch = (await branchResponse.Content.ReadFromJsonAsync<EnvironmentDetailResponse>())!;
        Assert.Equal(staging.Id, branch.ParentAppId);
        Assert.Equal(seed.ApplicationId, branch.ApplicationId);
        var conflictingSource = await client.PostAsJsonAsync($"/api/environments/{seed.AppId}/branches", new EnsureAppBranchRequest("feature/from-staging"));
        Assert.Equal(HttpStatusCode.BadRequest, conflictingSource.StatusCode);
        Assert.Contains("branch_source_conflict", await conflictingSource.Content.ReadAsStringAsync());
        var nestedPreview = await client.PostAsJsonAsync($"/api/environments/{branch.Id}/branches", new EnsureAppBranchRequest("feature/nested"));
        Assert.Equal(HttpStatusCode.BadRequest, nestedPreview.StatusCode);
        Assert.Contains("branch_parent_invalid", await nestedPreview.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/environments/{staging.Id}/branches/{branch.Id}")).StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Equal("queued", (await db.Apps.SingleAsync(x => x.Id == branch.Id)).DeletionStatus);
        Assert.Null((await db.Apps.SingleAsync(x => x.Id == staging.Id)).DeletionStatus);
        Assert.Null((await db.Apps.SingleAsync(x => x.Id == seed.AppId)).DeletionStatus);
        Assert.Null((await db.Applications.SingleAsync(x => x.Id == seed.ApplicationId)).DeletionStatus);
    }

    [Fact]
    public async Task CompleteDeploymentCollection_ExceedsLegacyLimitAndRetainsTenantBoundary()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_complete", "app_complete", now);
        var other = await SeedAsync("org_other", "app_other", now);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            for (var i = 0; i < 105; i++)
            {
                db.Deployments.Add(new Deployment { Id = $"dep_complete_{i}", OrganizationId = seed.OrganizationId, AppId = seed.AppId, Database = DatabaseMode.Sqlite, Status = DeploymentStatus.Succeeded, CreatedAt = now.AddMinutes(-i), UpdatedAt = now });
            }
            db.Deployments.Add(new Deployment { Id = "dep_other", OrganizationId = other.OrganizationId, AppId = other.AppId, Database = DatabaseMode.Sqlite, Status = DeploymentStatus.Failed, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var legacy = await client.GetFromJsonAsync<DeploymentResponse[]>($"/api/deployments?organizationId={seed.OrganizationId}");
        var complete = await client.GetFromJsonAsync<DeploymentResponse[]>($"/api/deployments?organizationId={seed.OrganizationId}&all=true");
        Assert.Equal(100, legacy!.Length);
        Assert.Equal(105, complete!.Length);
        Assert.All(complete, deployment => Assert.Equal(seed.OrganizationId, deployment.OrganizationId));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/deployments?organizationId={other.OrganizationId}&all=true")).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentTiming_IncludesPreparationAndQueueButExcludesPreviousAttempts(bool retried)
    {
        var createdAt = DateTimeOffset.UtcNow.AddHours(-3);
        var requeuedAt = retried ? createdAt.AddHours(2) : (DateTimeOffset?)null;
        var attemptStart = requeuedAt ?? createdAt;
        var seed = await SeedAsync("org_timing", "app_timing", createdAt);
        const string id = "dep_timing";
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var deployment = new Deployment
            {
                Id = id, OrganizationId = seed.OrganizationId, AppId = seed.AppId,
                Database = DatabaseMode.Sqlite, Status = DeploymentStatus.Succeeded,
                CreatedAt = createdAt, UpdatedAt = attemptStart.AddSeconds(377),
                StartedAt = attemptStart.AddMinutes(5), CompletedAt = attemptStart.AddSeconds(377),
                Services = [new DeploymentService
                {
                    Id = "svc_timing", Name = "api", Image = "example/api:latest", Port = 8080,
                    Public = false, Path = "/", HealthPath = "/health"
                }],
                Events =
                [
                    new() { Id = "evt_build", Level = EventLevel.Info, CreatedAt = createdAt,
                        Message = "Waiting for the CLI to build and publish local images." },
                    new() { Id = "evt_queue", Level = EventLevel.Info, CreatedAt = createdAt.AddSeconds(279),
                        Message = "Deployment queued." }
                ]
            };
            if (retried)
            {
                // Deliberately insert retries out of chronological order.
                deployment.Events.Add(new() { Id = "evt_retry_latest", Level = EventLevel.Info,
                    CreatedAt = requeuedAt!.Value, Message = "Deployment manually queued." });
                deployment.Events.Add(new() { Id = "evt_retry_old", Level = EventLevel.Info,
                    CreatedAt = createdAt.AddHours(1), Message = "Deployment manually queued." });
            }
            db.Deployments.Add(deployment);
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);

        async Task<DeploymentResponse[]> ReadResponses()
        {
            var list = await client.GetFromJsonAsync<DeploymentResponse[]>($"/api/deployments?organizationId={seed.OrganizationId}&all=true");
            var detail = await client.GetFromJsonAsync<DeploymentResponse>($"/api/deployments/{id}");
            using var refresh = await client.PostAsync($"/api/deployments/{id}/refresh", null);
            refresh.EnsureSuccessStatusCode();
            return [Assert.Single(list!), detail!, (await refresh.Content.ReadFromJsonAsync<DeploymentResponse>())!];
        }

        foreach (var response in await ReadResponses())
        {
            Assert.Equal(createdAt, response.CreatedAt);
            Assert.Equal(requeuedAt, response.RequeuedAt);
            Assert.Equal(attemptStart.AddMinutes(5), response.StartedAt);
            Assert.Equal(TimeSpan.FromSeconds(377), response.CompletedAt!.Value - (response.RequeuedAt ?? response.CreatedAt));
        }

        var beforeDispatch = DateTimeOffset.UtcNow;
        using var dispatched = await client.PostAsync($"/api/deployments/{id}/dispatch", null);
        dispatched.EnsureSuccessStatusCode();
        var queued = (await dispatched.Content.ReadFromJsonAsync<DeploymentResponse>())!;
        Assert.InRange(queued.RequeuedAt!.Value, beforeDispatch, DateTimeOffset.UtcNow);
        foreach (var response in (await ReadResponses()).Append(queued))
        {
            Assert.Equal(createdAt, response.CreatedAt);
            Assert.Equal(queued.RequeuedAt, response.RequeuedAt);
            Assert.Equal("queued", response.Status);
            Assert.Null(response.StartedAt);
            Assert.Null(response.CompletedAt);
        }
    }

    [Fact]
    public async Task ApiModelBindingErrors_UseControlPlaneErrorEnvelope()
    {
        var now = new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);
        var seed = await SeedAsync("org_api_model_binding", "app_api_model_binding", now);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);

        var response = await client.PostAsJsonAsync("/api/deployments", new
        {
            appId = seed.AppId,
            database = "sqlite",
            services = new[]
            {
                new
                {
                    name = "web",
                    image = "ghcr.io/customer/app/web:abc123",
                    port = "not-a-number",
                    @public = true,
                    path = "/",
                    healthPath = "/health"
                }
            }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(error);
        Assert.Equal("validation_failed", error!.Error.Code);
        Assert.Equal("Request body is invalid.", error.Error.Message);
        Assert.NotEmpty(error.Error.Details);
    }

    [Fact]
    public async Task ArtifactDeployment_StoresLocalRuntimeImageTag()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_api_local_image", "app_api_local_image", now);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.DeploymentArtifacts.Add(new DeploymentArtifact
            {
                Id = "art_local_image",
                OrganizationId = seed.OrganizationId,
                AppId = seed.AppId,
                ServiceName = "web",
                FileName = "web.zip",
                ContentType = "application/zip",
                SizeBytes = 3,
                Sha256 = "abc123",
                ManifestJson = "{}",
                StoragePath = "artifacts/art_local_image.zip",
                Status = DeploymentArtifactStatus.Ready,
                CreatedAt = now,
                UpdatedAt = now,
                ExpiresAt = now.AddHours(1),
                UploadedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var createResponse = await client.PostAsJsonAsync("/api/deployments", new CreateDeploymentRequest(
            seed.AppId,
            "sqlite",
            "abcdef1234567890",
            [new DeploymentServiceRequest("web", null, 3000, true, "/", "/health", ArtifactId: "art_local_image")]));

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<DeploymentCreateResponse>();
        Assert.NotNull(created);
        var deployment = await client.GetFromJsonAsync<DeploymentResponse>($"/api/deployments/{created!.Id}");
        var service = Assert.Single(deployment!.Services);
        Assert.Equal($"minicloud/app-api-local-image-web:{created.Id}", service.Image);
        Assert.DoesNotContain("ghcr.io", service.Image);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }

    private async Task<SeedResult> SeedAsync(string organizationId, string appId, DateTimeOffset now)
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
        var applicationId = $"application_{appId}";
        db.Applications.Add(new MinicloudApplication
        {
            Id = applicationId, OrganizationId = organizationId, Name = appId,
            Slug = appId.Replace('_', '-'), NormalizedSlug = appId.Replace('_', '-'),
            DefaultEnvironmentId = appId, LegacyRootAppId = appId, CreatedAt = now, UpdatedAt = now
        });
        db.Apps.Add(new MinicloudApp
        {
            Id = appId,
            ApplicationId = applicationId,
            EnvironmentName = "production",
            NormalizedEnvironmentName = "production",
            OrganizationId = organizationId,
            Name = appId,
            Slug = appId.Replace('_', '-'),
            Plan = AppPlan.P0,
            Database = DatabaseMode.Sqlite,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.ApiKeys.Add(new ApiKey
        {
            Id = $"key_{Guid.NewGuid():N}",
            OrganizationId = organizationId,
            Name = "app-api",
            KeyPrefix = apiKeyToken.Prefix,
            KeyHash = apiKeyTokenService.Hash(apiKeyToken.Token),
            ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite, ApiKeyScopes.DeploymentsCreate, ApiKeyScopes.DeploymentsRead }),
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        // A migrated installation has organization-owned rows for its legacy plan names.
        var catalog = scope.ServiceProvider.GetRequiredService<ProviderCatalogService>();
        foreach (var type in Enum.GetValues<Minicloud.Common.Providers.CloudProvider>())
        {
            var account = new ProviderAccount { Id = $"provider_{organizationId}_{type}", OrganizationId = organizationId,
                Name = type.ToString(), ProviderType = type, CredentialPath = $"fixtures/{organizationId}/{type}", CredentialGeneration = "1" };
            db.ProviderAccounts.Add(account);
            await db.SaveChangesAsync();
            foreach (var plan in Enum.GetValues<AppPlan>())
            {
                var mapping = ProviderPlanMapping.FromAppPlan(plan, new ProviderPlanOptions());
                if (mapping.Provider != type) continue;
                await catalog.CreateAsync(new(organizationId, account.Id, mapping.Region, mapping.Sku,
                    Minicloud.Common.Validation.ControlPlaneValidation.ToWireValue(plan)), default);
            }
            await db.SaveChangesAsync();
        }

        return new SeedResult(organizationId, appId, apiKeyToken.Token, applicationId);
    }

    private sealed record SeedResult(string OrganizationId, string AppId, string ApiKeyToken, string ApplicationId);

    private sealed class AppApiFactory(SqliteConnection connection, string appDomain = "app.muni.dev", string? releaseMetadataPath = null) : WebApplicationFactory<Program>
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
                    ["ManagedDomains:Suffix"] = appDomain,
                    ["ServerRelease:ManifestPath"] = releaseMetadataPath,
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
