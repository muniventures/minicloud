using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class TelemetryControlPlaneTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public TelemetryControlPlaneTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task Issue_Revocation_And_Rotation_Enforce_Two_Active_Per_Service()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, tag, "shop");
        SeedSucceededDeployment(db, org, app, "backend", time.GetUtcNow());
        await db.SaveChangesAsync();

        var svc = CreateTelemetryService(db, time);
        var principal = ManagePrincipal($"usr_{tag}");
        var enabled = await svc.SetEnabledAsync(principal, app.Id, true, CancellationToken.None);
        Assert.NotNull(enabled);
        Assert.Equal("https://ingest.operator.test/otlp/v1/logs", enabled.Settings!.IngestionUrl);
        Assert.Matches(@"^mcl_[0-9a-f]{32}$", enabled.Settings!.TenantId);

        var first = await svc.IssueCredentialAsync(principal, app.Id, "backend", "edge-a", null, CancellationToken.None);
        var second = await svc.IssueCredentialAsync(principal, app.Id, "backend", "edge-b", null, CancellationToken.None);
        Assert.NotNull(first!.Success);
        Assert.NotNull(second!.Success);
        Assert.StartsWith("mcl_ingest_", first.Success.Response.Token);
        Assert.NotEqual(first.Success.Response.Token, second.Success.Response.Token);
        Assert.Equal(1, first.Success.Response.CredentialVersion);
        Assert.Equal(2, second.Success.Response.CredentialVersion);

        var third = await svc.IssueCredentialAsync(principal, app.Id, "backend", "edge-c", null, CancellationToken.None);
        Assert.Equal("credential_limit_exceeded", third!.Error!.Code);

        var auth = CreateAuthService(db, time);
        var ok = await auth.AuthenticateAsync($"Bearer {first.Success.Response.Token}", CancellationToken.None);
        Assert.NotNull(ok);
        Assert.Equal(app.Id, ok.AppId);

        Assert.True(await svc.RevokeCredentialAsync(principal, app.Id, first.Success.Response.Id, CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(31));
        var denied = await auth.AuthenticateAsync($"Bearer {first.Success.Response.Token}", CancellationToken.None);
        Assert.Null(denied);

        var rotated = await svc.IssueCredentialAsync(principal, app.Id, "backend", "edge-c", null, CancellationToken.None);
        Assert.NotNull(rotated!.Success);
        Assert.Equal(3, rotated.Success.Response.CredentialVersion);

        var listed = await svc.ListCredentialsAsync(principal, app.Id, CancellationToken.None);
        Assert.NotNull(listed);
        Assert.DoesNotContain(listed, x => x.Id == first.Success.Response.Id && x.RevokedAt == null);
        Assert.All(listed, x => Assert.False(string.IsNullOrWhiteSpace(x.KeyPrefix)));
    }

    [Fact]
    public async Task Credential_Cannot_Write_For_Another_App()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, appA) = await SeedOrgAppAsync(db, tag, "alpha");
        var (_, appB) = await SeedOrgAppAsync(db, $"{tag}b", "beta", org);
        SeedSucceededDeployment(db, org, appA, "backend", time.GetUtcNow());
        SeedSucceededDeployment(db, org, appB, "backend", time.GetUtcNow());
        await db.SaveChangesAsync();

        var svc = CreateTelemetryService(db, time);
        var principal = ManagePrincipal($"usr_{tag}");
        await svc.SetEnabledAsync(principal, appA.Id, true, CancellationToken.None);
        await svc.SetEnabledAsync(principal, appB.Id, true, CancellationToken.None);
        var issued = await svc.IssueCredentialAsync(principal, appA.Id, "backend", "edge", null, CancellationToken.None);

        var auth = CreateAuthService(db, time);
        var snapshot = await auth.AuthenticateAsync($"Bearer {issued!.Success!.Response.Token}", CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(appA.Id, snapshot.AppId);
        Assert.NotEqual(appB.Id, snapshot.AppId);
    }

    [Fact]
    public async Task Disabled_Blocks_Issuance_And_Auth_But_Allows_Settings_Read()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, tag, "shop");
        SeedSucceededDeployment(db, org, app, "backend", time.GetUtcNow());
        await db.SaveChangesAsync();

        var svc = CreateTelemetryService(db, time);
        var manager = ManagePrincipal($"usr_{tag}");
        var viewer = new ControlPlanePrincipal("browser_session", $"viewer_{tag}", null, null, new HashSet<string>(StringComparer.Ordinal));
        await EnsureUserAsync(db, $"viewer_{tag}", time.GetUtcNow());
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = $"mbr_{tag}",
            OrganizationId = org.Id,
            UserId = $"viewer_{tag}",
            Role = OrganizationRole.Viewer,
            CreatedAt = time.GetUtcNow(),
            UpdatedAt = time.GetUtcNow()
        });
        await db.SaveChangesAsync();

        await svc.SetEnabledAsync(manager, app.Id, true, CancellationToken.None);
        var issued = await svc.IssueCredentialAsync(manager, app.Id, "backend", "edge", null, CancellationToken.None);
        var auth = CreateAuthService(db, time);
        Assert.NotNull(await auth.AuthenticateAsync($"Bearer {issued!.Success!.Response.Token}", CancellationToken.None));

        await svc.SetEnabledAsync(manager, app.Id, false, CancellationToken.None);
        var blocked = await svc.IssueCredentialAsync(manager, app.Id, "backend", "edge-2", null, CancellationToken.None);
        Assert.Equal("telemetry_disabled", blocked!.Error!.Code);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await auth.AuthenticateAsync($"Bearer {issued.Success.Response.Token}", CancellationToken.None));

        var settings = await svc.GetSettingsAsync(viewer, app.Id, CancellationToken.None);
        Assert.NotNull(settings);
        Assert.False(settings.Enabled);
    }

    [Fact]
    public async Task Branch_Never_Inherits_Bindings_Or_Tokens()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var now = Now();
        var time = new MutableTimeProvider(now);
        var (org, parent) = await SeedOrgAppAsync(db, tag, "shop");
        await AttachPersistentApplicationAsync(db, parent, now);
        SeedSucceededDeployment(db, org, parent, "backend", now);
        await db.SaveChangesAsync();

        var svc = CreateTelemetryService(db, time);
        var principal = ManagePrincipal($"usr_{tag}");
        await svc.SetEnabledAsync(principal, parent.Id, true, CancellationToken.None);
        await svc.IssueCredentialAsync(principal, parent.Id, "backend", "edge", null, CancellationToken.None);

        var store = new InMemorySecretStore();
        var ids = new GuidIdGenerator();
        var secrets = new AppServiceSecretService(db, store, ids, time);
        var branches = new AppBranchService(db, secrets, ids, time);
        var branch = await branches.EnsureAsync(parent, "feature/checkout", CancellationToken.None);

        Assert.Empty(db.AppTelemetrySettings.Where(x => x.AppId == branch.Id));
        Assert.Empty(db.TelemetryIngestionCredentials.Where(x => x.AppId == branch.Id));
        Assert.Empty(db.GrafanaDataSourceBindings.Where(x => x.AppId == branch.Id));
    }

    [Fact]
    public async Task Tombstone_Revokes_Credentials_And_Enqueues_Cleanup_With_New_Tenant_On_Recreate()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, tag, "shop");
        SeedSucceededDeployment(db, org, app, "backend", time.GetUtcNow());
        await db.SaveChangesAsync();

        var svc = CreateTelemetryService(db, time);
        var principal = ManagePrincipal($"usr_{tag}");
        var firstSettings = (await svc.SetEnabledAsync(principal, app.Id, true, CancellationToken.None))!.Settings!;
        var issued = await svc.IssueCredentialAsync(principal, app.Id, "backend", "edge", null, CancellationToken.None);

        await svc.TombstoneAppAsync(app.Id, CancellationToken.None);
        var tombstoned = db.AppTelemetrySettings.Single(x => x.AppId == app.Id);
        Assert.False(tombstoned.Enabled);
        Assert.True(tombstoned.Deleting);
        var job = Assert.Single(db.TelemetryCleanupJobs.Where(x => x.AppId == app.Id));
        Assert.Equal(firstSettings.TenantId, job.TenantId);
        Assert.Equal("pending", job.Status);

        var auth = CreateAuthService(db, time);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await auth.AuthenticateAsync($"Bearer {issued!.Success!.Response.Token}", CancellationToken.None));

        var recreated = new MinicloudApp
        {
            Id = $"app_{tag}_v2",
            OrganizationId = org.Id,
            Name = "Shop",
            Slug = "shop",
            Plan = AppPlan.P0H0,
            Database = DatabaseMode.Postgres,
            CreatedAt = time.GetUtcNow(),
            UpdatedAt = time.GetUtcNow()
        };
        db.Apps.Remove(app);
        await db.SaveChangesAsync();
        db.Apps.Add(recreated);
        await db.SaveChangesAsync();
        var recreatedSettings = (await svc.SetEnabledAsync(principal, recreated.Id, true, CancellationToken.None))!.Settings!;
        Assert.NotEqual(firstSettings.TenantId, recreatedSettings.TenantId);
    }

    [Fact]
    public async Task Runtime_Tokens_Do_Not_Authenticate_As_Ingestion_Credentials()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        await SeedOrgAppAsync(db, tag, "shop");

        var auth = CreateAuthService(db, time);
        var runtimeTokens = new RuntimeTokenService(db, time);
        var runtime = runtimeTokens.Generate();
        Assert.Null(await auth.AuthenticateAsync($"Bearer {runtime.Token}", CancellationToken.None));
        Assert.Null(await auth.AuthenticateAsync("Bearer mrt_forged_token", CancellationToken.None));
        Assert.Null(await auth.AuthenticateAsync("Bearer not-a-telemetry-token", CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_Prefix_Flood_Remains_Bounded_And_Valid_Credential_Still_Authenticates()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, tag, "shop");
        SeedSucceededDeployment(db, org, app, "backend", time.GetUtcNow());
        await db.SaveChangesAsync();

        var svc = CreateTelemetryService(db, time);
        var principal = ManagePrincipal($"usr_{tag}");
        await svc.SetEnabledAsync(principal, app.Id, true, CancellationToken.None);
        var issued = await svc.IssueCredentialAsync(principal, app.Id, "backend", "edge", null, CancellationToken.None);
        var validToken = issued!.Success!.Response.Token;

        var auth = CreateAuthService(db, time);
        var ct = CancellationToken.None;

        foreach (var malformed in new[]
        {
            "Bearer not-a-telemetry-token",
            "Bearer mcl_ingest_short",
            "Bearer mrt_forged_token",
            "Basic abcdef",
        })
        {
            Assert.Null(await auth.AuthenticateAsync(malformed, ct));
        }

        for (var i = 0; i < 250; i++)
        {
            var flood = $"mcl_ingest_flood{i:x8}_{new string('a', 64)}";
            Assert.Null(await auth.AuthenticateAsync($"Bearer {flood}", ct));
        }

        var ok = await auth.AuthenticateAsync($"Bearer {validToken}", ct);
        Assert.NotNull(ok);
        Assert.Equal(app.Id, ok.AppId);
    }

    [Fact]
    public async Task Branch_Destroy_Tombstones_Telemetry_Blocks_Writes_And_Enqueues_Cleanup()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var now = Now();
        var time = new MutableTimeProvider(now);
        var (org, parent) = await SeedOrgAppAsync(db, tag, "shop");
        await AttachPersistentApplicationAsync(db, parent, now);
        SeedSucceededDeployment(db, org, parent, "backend", now);
        await db.SaveChangesAsync();

        var store = new InMemorySecretStore();
        var ids = new GuidIdGenerator();
        var secrets = new AppServiceSecretService(db, store, ids, time);
        var svc = CreateTelemetryService(db, time);
        var principal = ManagePrincipal($"usr_{tag}");

        var setupBranches = new AppBranchService(db, secrets, ids, time);
        var branch = await setupBranches.EnsureAsync(parent, "feature/telemetry", CancellationToken.None);
        SeedSucceededDeployment(db, org, branch, "backend", now);
        await db.SaveChangesAsync();

        var branchSettings = (await svc.SetEnabledAsync(principal, branch.Id, true, CancellationToken.None))!.Settings!;
        var issued = await svc.IssueCredentialAsync(principal, branch.Id, "backend", "edge", null, CancellationToken.None);
        Assert.NotNull(issued!.Success);

        // T90: GitHub branch-teardown dispatch decommissioned; DestroyAsync
        // removes local branch state and tombstones telemetry.
        var branches = new AppBranchService(db, secrets, ids, time, svc);

        var auth = CreateAuthService(db, time);
        Assert.NotNull(await auth.AuthenticateAsync($"Bearer {issued.Success.Response.Token}", CancellationToken.None));

        await branches.DestroyAsync(branch, CancellationToken.None);

        var settings = db.AppTelemetrySettings.SingleOrDefault(x => x.AppId == branch.Id);
        Assert.True(
            settings is null || (!settings.Enabled && settings.Deleting),
            "Branch telemetry must be tombstoned or removed with the app; it must never stay enabled.");
        Assert.All(
            db.TelemetryIngestionCredentials.Where(x => x.AppId == branch.Id).ToList(),
            x => Assert.NotNull(x.RevokedAt));
        var job = Assert.Single(db.TelemetryCleanupJobs.Where(x => x.AppId == branch.Id));
        Assert.Equal(branchSettings.TenantId, job.TenantId);
        Assert.Equal("pending", job.Status);

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await auth.AuthenticateAsync($"Bearer {issued.Success.Response.Token}", CancellationToken.None));
    }

    [Fact]
    public async Task Scope_Mapping_Matches_Telemetry_Contract()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var now = Now();
        var (org, _) = await SeedOrgAppAsync(db, tag, "shop");
        var authorization = new ControlPlaneAuthorization(db);
        var ct = CancellationToken.None;

        var logsReader = new ControlPlanePrincipal("api_key", null, $"key_{tag}", org.Id,
            new HashSet<string>(StringComparer.Ordinal) { ApiKeyScopes.LogsRead });
        var telemetryManager = new ControlPlanePrincipal("api_key", null, $"key_{tag}", org.Id,
            new HashSet<string>(StringComparer.Ordinal) { ApiKeyScopes.TelemetryManage });

        Assert.True(await authorization.CanAccessOrganizationAsync(logsReader.ToActorIdentity(), org.Id, OrganizationAction.ReadApplicationLogs, ct));
        Assert.False(await authorization.CanAccessOrganizationAsync(logsReader.ToActorIdentity(), org.Id, OrganizationAction.ManageTelemetry, ct));
        Assert.True(await authorization.CanAccessOrganizationAsync(telemetryManager.ToActorIdentity(), org.Id, OrganizationAction.ManageTelemetry, ct));

        var viewer = new ControlPlanePrincipal("browser_session", $"viewer_{tag}", null, null, new HashSet<string>(StringComparer.Ordinal));
        var developer = new ControlPlanePrincipal("browser_session", $"dev_{tag}", null, null, new HashSet<string>(StringComparer.Ordinal));
        await EnsureUserAsync(db, $"viewer_{tag}", now);
        await EnsureUserAsync(db, $"dev_{tag}", now);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = $"mbrv_{tag}", OrganizationId = org.Id, UserId = $"viewer_{tag}",
            Role = OrganizationRole.Viewer, CreatedAt = now, UpdatedAt = now
        });
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = $"mbrd_{tag}", OrganizationId = org.Id, UserId = $"dev_{tag}",
            Role = OrganizationRole.Developer, CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();

        Assert.True(await authorization.CanAccessOrganizationAsync(viewer.ToActorIdentity(), org.Id, OrganizationAction.ReadApplicationLogs, ct));
        Assert.False(await authorization.CanAccessOrganizationAsync(viewer.ToActorIdentity(), org.Id, OrganizationAction.ManageTelemetry, ct));
        Assert.True(await authorization.CanAccessOrganizationAsync(developer.ToActorIdentity(), org.Id, OrganizationAction.ManageTelemetry, ct));
    }

    private static string NewTag() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static DateTimeOffset Now() => new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static ControlPlanePrincipal ManagePrincipal(string userId) =>
        new("browser_session", userId, null, null, new HashSet<string>(StringComparer.Ordinal));

    private async Task<(Organization Org, MinicloudApp App)> SeedOrgAppAsync(
        MinicloudDbContext db, string tag, string slug, Organization? existingOrg = null)
    {
        var now = Now();
        var org = existingOrg ?? new Organization
        {
            Id = $"org_{tag}",
            Name = $"Org {tag}",
            Slug = $"org-{tag}",
            CreatedAt = now,
            UpdatedAt = now
        };
        if (existingOrg is null)
        {
            db.Organizations.Add(org);
        }

        var app = new MinicloudApp
        {
            Id = $"app_{tag}_{slug}",
            OrganizationId = org.Id,
            Name = slug,
            Slug = slug,
            Plan = AppPlan.P0H0,
            Database = DatabaseMode.Postgres,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Apps.Add(app);
        await EnsureUserAsync(db, $"usr_{tag}", now);
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = $"mbr_{tag}_{slug}",
            OrganizationId = org.Id,
            UserId = $"usr_{tag}",
            Role = OrganizationRole.Admin,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (org, app);
    }

    private static async Task AttachPersistentApplicationAsync(MinicloudDbContext db, MinicloudApp environment, DateTimeOffset now)
    {
        var applicationId = $"appl_{environment.Id}";
        db.Applications.Add(new MinicloudApplication
        {
            Id = applicationId, OrganizationId = environment.OrganizationId,
            Name = environment.Name, Slug = environment.Slug, NormalizedSlug = environment.Slug.ToLowerInvariant(),
            DefaultEnvironmentId = environment.Id, LegacyRootAppId = environment.Id,
            CreatedAt = now, UpdatedAt = now
        });
        environment.ApplicationId = applicationId;
        environment.EnvironmentKind = EnvironmentKinds.Persistent;
        environment.EnvironmentName = "production";
        environment.NormalizedEnvironmentName = "production";
        await db.SaveChangesAsync();
    }

    private static void SeedSucceededDeployment(
        MinicloudDbContext db, Organization org, MinicloudApp app, string serviceName, DateTimeOffset now)
    {
        var deployment = new Deployment
        {
            Id = $"dep_{app.Id}_{serviceName}",
            OrganizationId = org.Id,
            AppId = app.Id,
            Database = DatabaseMode.Postgres,
            Status = DeploymentStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            Services =
            [
                new DeploymentService
                {
                    Id = $"dsvc_{app.Id}_{serviceName}",
                    DeploymentId = $"dep_{app.Id}_{serviceName}",
                    Name = serviceName,
                    Image = $"ghcr.io/customer/{serviceName}:sha",
                    Port = 8080,
                    Public = true,
                    Path = "/",
                    HealthPath = "/health"
                }
            ]
        };
        db.Deployments.Add(deployment);
    }

    private static async Task EnsureUserAsync(MinicloudDbContext db, string userId, DateTimeOffset now)
    {
        if (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Users, x => x.Id == userId))
        {
            return;
        }

        db.Users.Add(new User
        {
            Id = userId,
            KratosIdentityId = $"kratos_{userId}",
            Email = $"{userId}@example.com",
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    private TelemetryService CreateTelemetryService(MinicloudDbContext db, TimeProvider time) =>
        new(db, new GuidIdGenerator(), time, new TelemetryTokenService(),
            new DeploymentServiceSetService(), new ControlPlaneAuthorization(db),
            new AuditEventService(db, new GuidIdGenerator(), time), new TestHostEnvironment(),
            Microsoft.Extensions.Options.Options.Create(new TelemetryClientOptions
            {
                IngestionUrl = "https://ingest.operator.test/otlp/v1/logs",
                AccessOrigin = "https://logs.operator.test"
            }));

    private static TelemetryCredentialAuthService CreateAuthService(MinicloudDbContext db, TimeProvider time) =>
        new(db, new TelemetryTokenService(), time);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class GuidIdGenerator : IIdGenerator
    {
        public string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Minicloud.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class InMemorySecretStore : ISecretStore
    {
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredSecretReference("memory", $"{request.AppId}/{request.Name}", "1"));
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.FromResult("value");
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

}
