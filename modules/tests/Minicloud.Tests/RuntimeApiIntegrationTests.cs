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
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.Dns;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class RuntimeApiIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private RuntimeApiFactory? _factory;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new RuntimeApiFactory(_connection);
        _factory.CreateClient().Dispose();

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    [Fact]
    public async Task RuntimeToken_CanIngestAndAppApiCanReadRedactedLogs()
    {
        var now = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);
        var seed = await SeedAsync("org_runtime_a", "app_runtime_a", now);
        using var client = _factory!.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.RuntimeToken);
        var ingest = await client.PostAsJsonAsync("/v1/runtime/ingest/logs", new
        {
            records = new[]
            {
                new
                {
                    source = "service:api",
                    service = (string?)"api",
                    stream = "stderr",
                    observedAt = now,
                    cursor = "service:api:000000000001",
                    message = "failed password=hunter2"
                },
                new
                {
                    source = "caddy",
                    service = (string?)null,
                    stream = "stdout",
                    observedAt = now.AddSeconds(1),
                    cursor = "caddy:000000000001",
                    message = "reverse_proxy 502"
                }
            }
        });

        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        using var ingestDocument = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync());
        Assert.Equal(2, ingestDocument.RootElement.GetProperty("accepted").GetInt32());

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var logs = await client.GetFromJsonAsync<RuntimeLogDto[]>($"/v1/runtime/logs?appId={seed.AppId}&tail=10");

        Assert.NotNull(logs);
        Assert.Equal(2, logs!.Length);
        Assert.Contains(logs, log => log.Source == "service:api" && log.Content.Contains("[REDACTED]", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, log => log.Content.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeToken_CannotSpoofAnotherAppThroughPayload()
    {
        var now = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);
        var seedA = await SeedAsync("org_runtime_scope_a", "app_runtime_scope_a", now);
        var seedB = await SeedAsync("org_runtime_scope_b", "app_runtime_scope_b", now);
        using var client = _factory!.CreateClient();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seedA.RuntimeToken);
        var ingest = await client.PostAsJsonAsync("/v1/runtime/ingest/logs", new
        {
            organizationId = seedB.OrganizationId,
            appId = seedB.AppId,
            records = new[]
            {
                new
                {
                    source = "service:api",
                    service = "api",
                    stream = "stdout",
                    observedAt = now,
                    cursor = "service:api:000000000002",
                    message = "hello from scoped token"
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seedB.ApiKeyToken);
        var logsForB = await client.GetFromJsonAsync<RuntimeLogDto[]>($"/v1/runtime/logs?appId={seedB.AppId}&tail=10");
        Assert.Empty(logsForB!);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seedA.ApiKeyToken);
        var logsForA = await client.GetFromJsonAsync<RuntimeLogDto[]>($"/v1/runtime/logs?appId={seedA.AppId}&tail=10");
        Assert.Single(logsForA!);
    }

    [Fact]
    public async Task RevokedRuntimeToken_IsRejected()
    {
        var now = new DateTimeOffset(2026, 5, 4, 10, 0, 0, TimeSpan.Zero);
        var seed = await SeedAsync("org_runtime_revoked", "app_runtime_revoked", now, revoked: true);
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.RuntimeToken);

        var response = await client.PostAsJsonAsync("/v1/runtime/heartbeat", new { agentVersion = "0.1.0", observedAt = now });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RuntimeRouting_ReturnsOnlyTokenScopedAppRoutesAndAcceptsApplyResults()
    {
        var now = new DateTimeOffset(2026, 5, 17, 10, 0, 0, TimeSpan.Zero);
        var seedA = await SeedAsync("org_runtime_route_a", "app_runtime_route_a", now);
        var seedB = await SeedAsync("org_runtime_route_b", "app_runtime_route_b", now);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.Domains.Add(new Domain
            {
                Id = "dom_route_a",
                OrganizationId = seedA.OrganizationId,
                AppId = seedA.AppId,
                ServiceName = "api",
                Hostname = "org-a-app-a-api.app.muni.dev",
                Kind = DomainBindingService.KindMinicloudSubdomain,
                PathPrefix = "/",
                DnsStatus = DomainBindingService.DnsVerified,
                ApplyStatus = DomainBindingService.ApplyPending,
                SslStatus = DomainBindingService.SslPending,
                Status = DomainBindingService.StatusPendingApply,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Domains.Add(new Domain
            {
                Id = "dom_route_b",
                OrganizationId = seedB.OrganizationId,
                AppId = seedB.AppId,
                ServiceName = "api",
                Hostname = "org-b-app-b-api.app.muni.dev",
                Kind = DomainBindingService.KindMinicloudSubdomain,
                PathPrefix = "/",
                DnsStatus = DomainBindingService.DnsVerified,
                ApplyStatus = DomainBindingService.ApplyPending,
                SslStatus = DomainBindingService.SslPending,
                Status = DomainBindingService.StatusPendingApply,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seedA.RuntimeToken);
        var routing = await client.GetFromJsonAsync<RuntimeRoutingDto>("/v1/runtime/routing");

        Assert.NotNull(routing);
        var route = Assert.Single(routing!.Routes);
        Assert.Equal("dom_route_a", route.BindingId);
        Assert.Equal("api", route.ServiceName);
        Assert.Equal(8080, route.ServicePort);

        var apply = await client.PostAsJsonAsync("/v1/runtime/routing/apply-results", new
        {
            version = routing.Version,
            results = new[]
            {
                new
                {
                    bindingId = "dom_route_a",
                    applyStatus = "applied",
                    sslStatus = "ready",
                    failureCode = (string?)null,
                    failureMessage = (string?)null,
                    observedAt = now.AddSeconds(5)
                },
                new
                {
                    bindingId = "dom_route_b",
                    applyStatus = "applied",
                    sslStatus = "ready",
                    failureCode = (string?)null,
                    failureMessage = (string?)null,
                    observedAt = now.AddSeconds(5)
                }
            }
        });
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var bindingA = await verifyDb.Domains.SingleAsync(x => x.Id == "dom_route_a");
        var bindingB = await verifyDb.Domains.SingleAsync(x => x.Id == "dom_route_b");
        Assert.Equal(DomainBindingService.StatusActive, bindingA.Status);
        Assert.Equal(DomainBindingService.StatusPendingApply, bindingB.Status);

        bindingA.Status = DomainBindingService.StatusDisabled;
        bindingA.UpdatedAt = DateTimeOffset.UtcNow;
        await verifyDb.SaveChangesAsync();
        var staleApply = await client.PostAsJsonAsync("/v1/runtime/routing/apply-results", new
        {
            version = routing.Version,
            results = new[] { new { bindingId = bindingA.Id, applyStatus = "applied", sslStatus = "ready", observedAt = now.AddMinutes(1) } }
        });
        Assert.Equal(HttpStatusCode.OK, staleApply.StatusCode);
        await verifyDb.Entry(bindingA).ReloadAsync();
        Assert.Equal(DomainBindingService.StatusDisabled, bindingA.Status);
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
    public async Task RuntimeRoutingDoesNotActivateBindingBeforeDnsVerification()
    {
        var now = DateTimeOffset.UtcNow;
        var seed = await SeedAsync("org_runtime_dns", "app_runtime_dns", now);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.Domains.Add(new Domain
            {
                Id = "dom_runtime_dns", OrganizationId = seed.OrganizationId, AppId = seed.AppId,
                ServiceName = "api", Hostname = "runtime-dns.app.muni.dev", Kind = DomainBindingService.KindMinicloudSubdomain,
                PathPrefix = "/", DnsStatus = DomainBindingService.DnsPending, ApplyStatus = DomainBindingService.ApplyPending,
                SslStatus = DomainBindingService.SslPending, Status = DomainBindingService.StatusPendingApply,
                CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.RuntimeToken);
        var routing = await client.GetFromJsonAsync<RuntimeRoutingDto>("/v1/runtime/routing");
        var response = await client.PostAsJsonAsync("/v1/runtime/routing/apply-results", new
        {
            version = routing!.Version,
            results = new[] { new { bindingId = "dom_runtime_dns", applyStatus = "applied", sslStatus = "ready", observedAt = now } }
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var resultScope = _factory.Services.CreateAsyncScope();
        var resultDb = resultScope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Equal(DomainBindingService.StatusPendingApply, (await resultDb.Domains.FindAsync("dom_runtime_dns"))!.Status);
    }

    [Fact]
    public async Task RuntimeRoutingWaitsForDeploymentOwnershipToBeReleased()
    {
        var seed = await SeedAsync("org_runtime_busy", "app_runtime_busy", DateTimeOffset.UtcNow);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var deployment = await db.Deployments.SingleAsync(d => d.AppId == seed.AppId);
            deployment.Status = DeploymentStatus.Deploying;
            deployment.OwnershipToken = "active-owner";
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.RuntimeToken);
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/v1/runtime/routing")).StatusCode);
        var heartbeat = await client.PostAsJsonAsync("/v1/runtime/heartbeat", new { agentVersion = "0.2.0", observedAt = DateTimeOffset.UtcNow });
        Assert.Equal(HttpStatusCode.OK, heartbeat.StatusCode);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var deployment = await db.Deployments.SingleAsync(d => d.AppId == seed.AppId);
            deployment.Status = DeploymentStatus.Succeeded;
            deployment.OwnershipToken = null;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/runtime/routing")).StatusCode);
    }

    [Fact]
    public async Task AppDomainRemovalRequiresWritePermissionAndIsAppScoped()
    {
        var a = await SeedAsync("org_remove_a", "app_remove_a", DateTimeOffset.UtcNow);
        var b = await SeedAsync("org_remove_b", "app_remove_b", DateTimeOffset.UtcNow);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            foreach (var key in await db.ApiKeys.ToListAsync())
            {
                key.ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite });
            }
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.ApiKeyToken);
        var registered = await client.PostAsJsonAsync($"/v1/custom-domains?appId={a.AppId}", new { hostname = "remove.example.com" });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var row = (await registered.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.CustomDomainRegistrationResponse>())!;
        var path = $"/v1/custom-domains/{row.Id}?appId={a.AppId}";
        client.DefaultRequestHeaders.Authorization = new("Bearer", b.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/v1/custom-domains/{row.Id}?appId={b.AppId}")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.ApiKeyToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(path)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<Minicloud.Common.Contracts.CustomDomainRegistrationResponse[]>($"/v1/custom-domains?appId={a.AppId}"))!);
    }

    [Fact]
    public async Task CustomDomainRequiresProofAndIsScopedToAppAndRuntime()
    {
        var now = DateTimeOffset.UtcNow;
        var a = await SeedAsync("org_custom_a", "app_custom_a", now);
        var b = await SeedAsync("org_custom_b", "app_custom_b", now);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var runtime = await db.RuntimeServers.SingleAsync(x => x.AppId == a.AppId);
            db.ServerInstances.Add(new ServerInstance { Id = "custom-server", AppId = a.AppId, OrganizationId = a.OrganizationId,
                Provider = runtime.Provider, ProviderServerId = runtime.ProviderServerId, IpAddress = "192.0.2.10", Region = "test", Status = "ready", BootstrapStatus = "ready" });
            foreach (var key in await db.ApiKeys.Where(x => x.OrganizationId == a.OrganizationId || x.OrganizationId == b.OrganizationId).ToListAsync())
            {
                key.ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite });
            }
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.ApiKeyToken);
        var rejected = await client.PostAsJsonAsync($"/v1/domains?appId={a.AppId}", new { serviceName = "api", hostname = "EXAMPLE.COM" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var registrationResponse = await client.PostAsJsonAsync($"/v1/custom-domains?appId={a.AppId}", new { hostname = "EXAMPLE.COM" });
        Assert.Equal(HttpStatusCode.OK, registrationResponse.StatusCode);
        var registration = (await registrationResponse.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.CustomDomainRegistrationResponse>())!;
        Assert.Equal(a.AppId, registration.AppId);
        const string siblingId = "app_custom_sibling";
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            db.Apps.Add(new MinicloudApp { Id = siblingId, OrganizationId = a.OrganizationId, Name = "Sibling", Slug = "sibling" });
            await db.SaveChangesAsync();
        }
        var siblingDomains = await client.GetFromJsonAsync<Minicloud.Common.Contracts.CustomDomainRegistrationResponse[]>($"/v1/custom-domains?appId={siblingId}");
        Assert.Empty(siblingDomains!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/v1/custom-domains/{registration.Id}/verify?appId={siblingId}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/v1/custom-domains?appId={siblingId}", new { hostname = "example.com" })).StatusCode);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().Domains.ToListAsync());
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", b.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/v1/custom-domains/{registration.Id}/verify?appId={a.AppId}", null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.ApiKeyToken);
        var notVerified = await client.PostAsync($"/v1/custom-domains/{registration.Id}/verify?appId={a.AppId}", null);
        Assert.Equal("pending", (await notVerified.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.CustomDomainRegistrationResponse>())!.Status);
        _factory.Dns.Token = registration.VerificationToken;
        var stillUnverified = await client.PostAsJsonAsync($"/v1/domains?appId={a.AppId}", new { serviceName = "api", hostname = "example.com" });
        Assert.Equal(HttpStatusCode.BadRequest, stillUnverified.StatusCode);
        var proof = await client.PostAsync($"/v1/custom-domains/{registration.Id}/verify?appId={a.AppId}", null);
        Assert.Equal("verified", (await proof.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.CustomDomainRegistrationResponse>())!.Status);
        var created = await client.PostAsJsonAsync($"/v1/domains?appId={a.AppId}", new { serviceName = "api", hostname = "EXAMPLE.COM" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var binding = (await created.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.DomainBindingResponse>())!;
        Assert.Equal("example.com", binding.Hostname);
        Assert.Equal("192.0.2.10", binding.TargetIp);
        Assert.Equal("pending_dns", binding.Status);
        Assert.NotNull(binding.VerificationToken);
        var verifyUrl = $"/v1/domains/{binding.Id}/verify?appId={a.AppId}";
        client.DefaultRequestHeaders.Authorization = new("Bearer", b.ApiKeyToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(verifyUrl, null)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.RuntimeToken);
        var pending = await client.GetFromJsonAsync<Minicloud.Common.Contracts.RuntimeDesiredRoutingResponse>("/v1/runtime/routing");
        Assert.Empty(pending!.Routes);
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.ApiKeyToken);
        _factory.Dns.Token = "wrong";
        var mismatch = await client.PostAsync(verifyUrl, null);
        Assert.Equal("pending", (await mismatch.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.DomainBindingResponse>())!.DnsStatus);
        _factory.Dns.Token = binding.VerificationToken;
        var verified = await client.PostAsync(verifyUrl, null);
        Assert.Equal("verified", (await verified.Content.ReadFromJsonAsync<Minicloud.Common.Contracts.DomainBindingResponse>())!.DnsStatus);
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.RuntimeToken);
        var active = await client.GetFromJsonAsync<Minicloud.Common.Contracts.RuntimeDesiredRoutingResponse>("/v1/runtime/routing");
        Assert.Equal("example.com", Assert.Single(active!.Routes).Hostname);
        client.DefaultRequestHeaders.Authorization = new("Bearer", b.RuntimeToken);
        Assert.Empty((await client.GetFromJsonAsync<Minicloud.Common.Contracts.RuntimeDesiredRoutingResponse>("/v1/runtime/routing"))!.Routes);
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.ApiKeyToken);
        await client.PatchAsJsonAsync($"/v1/domains/{binding.Id}?appId={a.AppId}", new { disabled = true });
        await client.PostAsync(verifyUrl, null);
        client.DefaultRequestHeaders.Authorization = new("Bearer", a.RuntimeToken);
        Assert.Empty((await client.GetFromJsonAsync<Minicloud.Common.Contracts.RuntimeDesiredRoutingResponse>("/v1/runtime/routing"))!.Routes);
    }

    [Fact]
    public async Task CatalogRuntimeRegistrationRequiresTheSavedManagedServerIdentity()
    {
        var seed = await SeedAsync("org_runtime_catalog", "app_runtime_catalog", DateTimeOffset.UtcNow);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            var account = new ProviderAccount { Id = "runtime-catalog-account", OrganizationId = seed.OrganizationId,
                Name = "Original", ProviderType = Minicloud.Common.Providers.CloudProvider.Hetzner,
                CredentialPath = "synthetic/path", CredentialGeneration = "test" };
            var plan = new ProviderPlan { Id = "runtime-catalog-plan", OrganizationId = seed.OrganizationId,
                ProviderAccountId = account.Id, ProviderAccount = account, Name = "Runtime small", NormalizedName = "RUNTIME SMALL",
                Region = "nbg1", Sku = "cx23" };
            db.ProviderPlans.Add(plan);
            (await db.Apps.SingleAsync(x => x.Id == seed.AppId)).PlanId = plan.Id;
            db.ServerInstances.Add(new ServerInstance { Id = "runtime-saved-server", OrganizationId = seed.OrganizationId,
                AppId = seed.AppId, ProviderAccountId = account.Id, PlanId = plan.Id, Provider = "hetzner",
                ProviderServerId = "owned-resource", Region = "nbg1", Status = "ready", BootstrapStatus = "ready" });
            var key = await db.ApiKeys.SingleAsync(x => x.OrganizationId == seed.OrganizationId);
            key.ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.DeploymentsRead, ApiKeyScopes.DeploymentsCreate });
            await db.SaveChangesAsync();
        }
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ApiKeyToken);
        var forged = await client.PostAsJsonAsync($"/v1/runtime/servers?appId={seed.AppId}",
            new { provider = "hetzner", providerAccountId = "runtime-catalog-account", providerServerId = "another-account-resource" });
        Assert.Equal(HttpStatusCode.Conflict, forged.StatusCode);
        Assert.Contains("runtime_server_conflict", await forged.Content.ReadAsStringAsync());
        var valid = await client.PostAsJsonAsync($"/v1/runtime/servers?appId={seed.AppId}",
            new { provider = "hetzner", providerAccountId = "runtime-catalog-account", providerServerId = "owned-resource" });
        Assert.Equal(HttpStatusCode.Created, valid.StatusCode);
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            (await db.Apps.SingleAsync(x => x.Id == seed.AppId)).PlanId = null;
            await db.SaveChangesAsync();
        }
        var legacyAccountInjection = await client.PostAsJsonAsync($"/v1/runtime/servers?appId={seed.AppId}",
            new { provider = "hetzner", providerAccountId = "runtime-catalog-account", providerServerId = "owned-resource" });
        Assert.Equal(HttpStatusCode.Conflict, legacyAccountInjection.StatusCode);
        Assert.Contains("runtime_account_conflict", await legacyAccountInjection.Content.ReadAsStringAsync());
    }

    private async Task<SeedResult> SeedAsync(string organizationId, string appId, DateTimeOffset now, bool revoked = false)
    {
        await using var scope = _factory!.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var apiKeyTokenService = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
        var runtimeTokenService = scope.ServiceProvider.GetRequiredService<IRuntimeTokenService>();

        var apiKeyToken = apiKeyTokenService.Generate();
        var runtimeToken = runtimeTokenService.Generate();
        var organization = new Organization
        {
            Id = organizationId,
            Name = organizationId,
            Slug = organizationId.Replace('_', '-'),
            CreatedAt = now,
            UpdatedAt = now
        };
        var app = new MinicloudApp
        {
            Id = appId,
            OrganizationId = organization.Id,
            Name = appId,
            Slug = appId.Replace('_', '-'),
            Plan = AppPlan.P0,
            Database = DatabaseMode.Sqlite,
            CreatedAt = now,
            UpdatedAt = now
        };
        var deployment = new Deployment
        {
            Id = $"dep_{Guid.NewGuid():N}",
            OrganizationId = organization.Id,
            AppId = app.Id,
            Database = DatabaseMode.Sqlite,
            Status = DeploymentStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now
        };
        deployment.Services.Add(new DeploymentService
        {
            Id = $"svc_{Guid.NewGuid():N}",
            DeploymentId = deployment.Id,
            Name = "api",
            Image = "ghcr.io/example/api:latest",
            Port = 8080,
            Public = true,
            Path = "/",
            HealthPath = "/health"
        });
        var server = new RuntimeServer
        {
            Id = $"rtsrv_{Guid.NewGuid():N}",
            OrganizationId = organization.Id,
            AppId = app.Id,
            Provider = "hetzner",
            ProviderServerId = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Organizations.Add(organization);
        db.Apps.Add(app);
        db.Deployments.Add(deployment);
        db.RuntimeServers.Add(server);
        db.ApiKeys.Add(new ApiKey
        {
            Id = $"key_{Guid.NewGuid():N}",
            OrganizationId = organization.Id,
            Name = "runtime-read",
            KeyPrefix = apiKeyToken.Prefix,
            KeyHash = apiKeyTokenService.Hash(apiKeyToken.Token),
            ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.DeploymentsRead }),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.RuntimeTokens.Add(new RuntimeToken
        {
            Id = $"rttok_{Guid.NewGuid():N}",
            RuntimeServerId = server.Id,
            TokenPrefix = runtimeToken.Prefix,
            TokenHash = runtimeTokenService.Hash(runtimeToken.Token),
            RevokedAt = revoked ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        return new SeedResult(organization.Id, app.Id, apiKeyToken.Token, runtimeToken.Token);
    }

    private sealed record SeedResult(string OrganizationId, string AppId, string ApiKeyToken, string RuntimeToken);
    private sealed record RuntimeLogDto(string Source, string Content);
    private sealed record RuntimeRoutingDto(string Version, RuntimeRouteDto[] Routes);
    private sealed record RuntimeRouteDto(string BindingId, string ServiceName, int ServicePort);

    public sealed class TestCustomDns : ICustomDomainDnsResolver
    {
        public string? Token { get; set; }
        public Task<IReadOnlyList<string>> QueryAsync(string hostname, int type, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(type == 16 ? ["\"" + Token + "\""] : type == 1 ? ["192.0.2.10"] : []);
    }

    private sealed class RuntimeApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public TestCustomDns Dns { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Firebase:ProjectId"] = "minicloud-test",
                    ["Database:RunMigrationsOnStartup"] = "false",
                    ["ConnectionStrings:Minicloud"] = "Data Source=:memory:"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ICustomDomainDnsResolver>();
                services.AddSingleton<ICustomDomainDnsResolver>(Dns);
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection));
            });
        }
    }
}
