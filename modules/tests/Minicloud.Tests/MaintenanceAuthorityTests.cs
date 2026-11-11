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
using Minicloud.Api.Services;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class MaintenanceAuthorityTests : IAsyncLifetime
{
    private const string Installation = "inst-maintenance";
    private readonly SqliteTestHarness _harness = new();

    public Task InitializeAsync() => _harness.InitializeAsync();
    public Task DisposeAsync() => _harness.DisposeAsync();

    private async Task SeedInstallationAsync()
    {
        await using var db = _harness.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        db.BootstrapRecords.Add(new BootstrapRecord { InstallationId = Installation, OperationId = "op-bootstrap", State = "ready", CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    private MaintenanceAuthorityService Service(MinicloudDbContext db, string? host = null) => new(db, TimeProvider.System, host);

    private IServiceScopeFactory Scopes() => new ServiceCollection()
        .AddScoped(_ => _harness.CreateDbContext())
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task FenceReleaseAndTransferEnforceHolderInstallationAndHost()
    {
        await SeedInstallationAsync();
        await using var db = _harness.CreateDbContext();
        var service = Service(db, "host-a");

        var status = await service.StatusAsync(Installation);
        Assert.Equal((1L, "open", (string?)null), (status.Epoch, status.State, status.InstallationId));

        var fenced = await service.FenceAsync(Installation, "op-upgrade-1");
        Assert.Equal(("fenced", "op-upgrade-1", Installation), (fenced.State, fenced.HolderOperationId, fenced.InstallationId));
        Assert.Equal(fenced.FencedAt, (await service.FenceAsync(Installation, "op-upgrade-1")).FencedAt);
        Assert.Equal("maintenance_fence_held", (await Assert.ThrowsAsync<MaintenanceException>(() => service.FenceAsync(Installation, "op-other"))).Code);
        var wrongHolder = await Assert.ThrowsAsync<MaintenanceException>(() => service.ReleaseAsync(Installation, "op-other"));
        Assert.True(wrongHolder.Refused);
        Assert.Equal("maintenance_holder_mismatch", wrongHolder.Code);
        Assert.Equal("fenced", (await service.StatusAsync(Installation)).State);

        Assert.Equal("open", (await service.ReleaseAsync(Installation, "op-upgrade-1")).State);
        Assert.Equal("open", (await service.ReleaseAsync(Installation, "op-upgrade-1")).State);
        Assert.Equal("maintenance_operation_completed", (await Assert.ThrowsAsync<MaintenanceException>(() => service.FenceAsync(Installation, "op-upgrade-1"))).Code);

        Assert.Equal("installation_mismatch", (await Assert.ThrowsAsync<MaintenanceException>(() => service.FenceAsync("inst-other", "op-x"))).Code);
        var invalid = await Assert.ThrowsAsync<MaintenanceException>(() => service.FenceAsync(Installation, "bad id"));
        Assert.False(invalid.Refused);

        var transferred = await service.TransferAsync(Installation, "op-transfer-1", null);
        Assert.Equal((2L, "host-a", "fenced", "op-transfer-1"), (transferred.Epoch, transferred.HostId, transferred.State, transferred.HolderOperationId));
        Assert.Equal(2L, (await service.TransferAsync(Installation, "op-transfer-1", "host-a")).Epoch);
        Assert.Equal("maintenance_host_mismatch", (await Assert.ThrowsAsync<MaintenanceException>(() => service.TransferAsync(Installation, "op-transfer-2", "host-b"))).Code);

        await using var otherDb = _harness.CreateDbContext();
        var otherHost = Service(otherDb, "host-b");
        Assert.False((await otherHost.StatusAsync(Installation)).LocalHostAuthorized);
        Assert.Equal("maintenance_host_not_authorized", (await Assert.ThrowsAsync<MaintenanceException>(() => otherHost.ReleaseAsync(Installation, "op-transfer-1"))).Code);
        Assert.Equal("maintenance_host_not_authorized", (await Assert.ThrowsAsync<MaintenanceException>(() => Service(otherDb).FenceAsync(Installation, "op-y"))).Code);
        var moved = await otherHost.TransferAsync(Installation, "op-transfer-3", null);
        Assert.Equal((3L, "host-b"), (moved.Epoch, moved.HostId));
        Assert.Equal("open", (await otherHost.ReleaseAsync(Installation, "op-transfer-3")).State);
    }

    [Fact]
    public async Task TransferRevokesStaleEpochsForeignHostsAndRestoredCopies()
    {
        await SeedInstallationAsync();
        var scopes = Scopes();
        var unbound = new MaintenanceParticipant(scopes, "worker", null, TimeProvider.System);
        var oldHost = new MaintenanceParticipant(scopes, "worker", "host-a", TimeProvider.System);
        var newHost = new MaintenanceParticipant(scopes, "api", "host-b", TimeProvider.System);
        await using var db = _harness.CreateDbContext();
        Assert.True(await unbound.CanClaimAsync(db, default));
        Assert.True(await oldHost.CanClaimAsync(db, default));
        Assert.True(await newHost.CanClaimAsync(db, default));
        Assert.Equal(1L, (await unbound.RegisterAsync(default)).Epoch);

        await Service(db, "host-b").TransferAsync(Installation, "op-move", null);
        db.ChangeTracker.Clear();

        Assert.Equal(MaintenanceAdmissionOutcome.HostMismatch, (await unbound.CheckAsync(db, false, default)).Outcome);
        Assert.Equal(MaintenanceAdmissionOutcome.HostMismatch, (await oldHost.CheckAsync(db, false, default)).Outcome);
        Assert.False(await oldHost.CanClaimAsync(db, default));
        // The transfer target's own processes adopt the new epoch; they are fenced until release.
        Assert.True(await newHost.CanClaimAsync(db, default));
        Assert.Equal(2L, newHost.RegisteredEpoch);
        Assert.Equal(MaintenanceAdmissionOutcome.Fenced, (await newHost.CheckAsync(db, true, default)).Outcome);
        // A restarted process on the old host is refused even though it registers fresh.
        var restartedOld = new MaintenanceParticipant(scopes, "worker", "host-a", TimeProvider.System);
        Assert.Equal(MaintenanceAdmissionOutcome.HostMismatch, (await restartedOld.CheckAsync(db, false, default)).Outcome);
        Assert.True((await restartedOld.RegisterAsync(default)).IsRevoked);

        // A database restored from an earlier backup (older epoch) cannot be adopted by a running process.
        await db.MaintenanceAuthorities.ExecuteUpdateAsync(x => x.SetProperty(r => r.Epoch, 1L));
        Assert.Equal(MaintenanceAdmissionOutcome.Revoked, (await newHost.CheckAsync(db, false, default)).Outcome);

        await db.MaintenanceAuthorities.ExecuteDeleteAsync();
        Assert.Equal(MaintenanceAdmissionOutcome.Unavailable, (await newHost.CheckAsync(db, false, default)).Outcome);
    }

    [Fact]
    public async Task ActivitiesAreRefusedWhileFencedAndKeepDrainOpenUntilCompleted()
    {
        await SeedInstallationAsync();
        var participant = new MaintenanceParticipant(Scopes(), "api", null, TimeProvider.System);
        var (admitted, lease) = await participant.TryBeginActivityAsync("api_write", "POST Apps.Create", requireOpen: true, default);
        Assert.True(admitted.IsAdmitted);

        await using var db = _harness.CreateDbContext();
        var service = Service(db);
        await service.FenceAsync(Installation, "op-drain");
        var draining = await service.DrainStatusAsync(Installation);
        Assert.False(draining.Drained);
        Assert.Equal("draining", draining.Phase);
        var active = Assert.Single(draining.Work, x => x.Category == "admitted_activities");
        Assert.Equal((1, true, lease!.Id), (active.Count, active.BlocksDrain, active.Ids[0]));

        var (refused, none) = await participant.TryBeginActivityAsync("api_write", null, requireOpen: true, default);
        Assert.Equal(MaintenanceAdmissionOutcome.Fenced, refused.Outcome);
        Assert.Equal("maintenance_in_progress", refused.Code);
        Assert.Null(none);
        var ran = false;
        Assert.False(await participant.RunActivityAsync("managed_dns_reconcile", true, _ => { ran = true; return Task.CompletedTask; }, default));
        Assert.False(ran);
        Assert.True(await participant.RunActivityAsync("provider_credential_cleanup", false, _ => { ran = true; return Task.CompletedTask; }, default));
        Assert.True(ran);

        await lease.DisposeAsync();
        var drained = await service.DrainStatusAsync(Installation);
        Assert.True(drained.Drained);
        Assert.Equal("drained", drained.Phase);
        Assert.Equal(0, drained.BlockingCount);
    }

    [Theory]
    [InlineData("api", "api_write")]
    [InlineData("worker", "managed_dns_reconcile")]
    public async Task ExpiredActivitiesKeepDrainBlockedUntilAcceptedWorkCompletes(string role, string kind)
    {
        await SeedInstallationAsync();
        var started = DateTimeOffset.UtcNow;
        var participant = new MaintenanceParticipant(Scopes(), role, null, new FixedClock(started));
        await using var db = _harness.CreateDbContext();
        var service = new MaintenanceAuthorityService(db, new FixedClock(started + MaintenanceParticipant.ActivityLeaseDuration), null);
        using var caller = new CancellationTokenSource();

        Assert.True(await participant.RunActivityAsync(kind, true, async token =>
        {
            await service.FenceAsync(Installation, "op-expired");
            var status = await service.DrainStatusAsync(Installation);
            Assert.False(status.Drained);
            Assert.Equal(("draining", 1), (status.Phase, status.BlockingCount));
            var uncertain = Assert.Single(status.Work, x => x.Category == "admitted_activities");
            Assert.Equal(("uncertain", true, 1), (uncertain.State, uncertain.BlocksDrain, uncertain.Count));
            Assert.False(token.IsCancellationRequested);
            var output = new StringWriter();
            Assert.Equal(MaintenanceCommand.ExitNotDrained, await MaintenanceCommand.ExecuteAsync("drain-status",
                new MaintenanceCommand.Input { InstallationId = Installation, OperationId = "op-expired" }, service, output));
        }, caller.Token));

        Assert.True((await service.DrainStatusAsync(Installation)).Drained);
        Assert.Empty(await db.MaintenanceActivities.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task OrphanedActivitiesRemainUncertainAcrossRestartReleaseAndTransfer()
    {
        await SeedInstallationAsync();
        var now = DateTimeOffset.UtcNow;
        await using var db = _harness.CreateDbContext();
        db.MaintenanceActivities.Add(new MaintenanceActivity
        {
            Id = "ma_orphan", Kind = "api_write", ParticipantId = "mp_gone", Epoch = 1,
            StartedAt = now.AddDays(-31), ExpiresAt = now.AddDays(-30)
        });
        await db.SaveChangesAsync();
        var service = Service(db, "host-new");
        await service.FenceAsync(Installation, "op-first");
        Assert.False((await service.DrainStatusAsync(Installation)).Drained);
        await service.ReleaseAsync(Installation, "op-first");
        await service.TransferAsync(Installation, "op-transfer", null);
        var restarted = new MaintenanceParticipant(Scopes(), "api", "host-new", TimeProvider.System);
        Assert.True((await restarted.RegisterAsync(default)).IsAdmitted);

        var status = await service.DrainStatusAsync(Installation);
        Assert.Equal(("draining", false, 1), (status.Phase, status.Drained, status.BlockingCount));
        var uncertain = Assert.Single(status.Work, x => x.Category == "admitted_activities");
        Assert.Equal(("uncertain", true), (uncertain.State, uncertain.BlocksDrain));
        Assert.Equal("ma_orphan", Assert.Single(uncertain.Ids));
        Assert.Equal(1L, (await db.MaintenanceActivities.AsNoTracking().SingleAsync()).Epoch);
    }

    [Fact]
    public async Task FailedCompletionRecordingStillBlocksDrainAfterExpiry()
    {
        await SeedInstallationAsync();
        var started = DateTimeOffset.UtcNow;
        var participant = new MaintenanceParticipant(Scopes(), "api", null, new FixedClock(started));
        var (decision, lease) = await participant.TryBeginActivityAsync("api_write", "POST Apps.Create", true, default);
        Assert.True(decision.IsAdmitted);
        await using var db = _harness.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER reject_activity_completion BEFORE DELETE ON maintenance_activities
            BEGIN SELECT RAISE(FAIL, 'synthetic completion failure'); END;
            """);
        await lease!.DisposeAsync();

        var service = new MaintenanceAuthorityService(db, new FixedClock(started.AddDays(1)), null);
        await service.FenceAsync(Installation, "op-completion-failed");
        var status = await service.DrainStatusAsync(Installation);
        Assert.False(status.Drained);
        Assert.Equal(1, status.BlockingCount);
        var uncertain = Assert.Single(status.Work, x => x.Category == "admitted_activities");
        Assert.Equal(("uncertain", true, lease.Id), (uncertain.State, uncertain.BlocksDrain, uncertain.Ids[0]));
    }

    [Fact]
    public async Task DrainStatusReportsInFlightAndUncertainWorkAndIgnoresInformationalQueues()
    {
        await SeedInstallationAsync();
        var now = DateTimeOffset.UtcNow;
        await using var db = _harness.CreateDbContext();
        db.Organizations.Add(new Organization { Id = "org_m", Name = "Org", Slug = "org-m", CreatedAt = now, UpdatedAt = now });
        db.Apps.Add(new MinicloudApp { Id = "app_m", OrganizationId = "org_m", Name = "App", Slug = "app-m", CreatedAt = now, UpdatedAt = now });
        db.Apps.Add(new MinicloudApp { Id = "app_del", OrganizationId = "org_m", Name = "Del", Slug = "app-del", DeletionStatus = "queued", CreatedAt = now, UpdatedAt = now });
        foreach (var app in new[] { "dep-queued", "dep-running", "dep-uncertain", "dep-done" })
            db.Apps.Add(new MinicloudApp { Id = "app_" + app, OrganizationId = "org_m", Name = app, Slug = app, CreatedAt = now, UpdatedAt = now });
        Deployment Dep(string id, DeploymentStatus status, string? owner = null) => new()
        { Id = id, OrganizationId = "org_m", AppId = "app_" + id, Status = status, OwnershipToken = owner, CreatedAt = now, UpdatedAt = now };
        db.Deployments.AddRange(Dep("dep-queued", DeploymentStatus.Queued), Dep("dep-running", DeploymentStatus.Deploying, "owner-1"),
            Dep("dep-uncertain", DeploymentStatus.Failed, "owner-2"), Dep("dep-done", DeploymentStatus.Succeeded));
        DatabaseOperation Op(string id, string status, string? error = null, int attempts = 0, DateTimeOffset? lease = null, DateTimeOffset? at = null) => new()
        { Id = id, OrganizationId = "org_m", DatabaseId = "db-" + id, Kind = "migrate", Status = status, ErrorCode = error, AttemptCount = attempts,
          LeaseExpiresAt = lease, OwnershipToken = lease is null ? null : "lease", CreatedAt = at ?? now, UpdatedAt = at ?? now };
        db.DatabaseOperations.AddRange(Op("op-q", "queued"), Op("op-retry", "queued", attempts: 2), Op("op-leased", "running", lease: now.AddMinutes(5)),
            Op("op-expired", "running", lease: now.AddMinutes(-5)), Op("op-recon", "failed", "database_migration_reconciliation_required", at: now.AddMinutes(-10)),
            Op("op-plainfail", "failed", "database_operation_failed"));
        db.ProviderCredentialCleanups.Add(new ProviderCredentialCleanup { Id = "cleanup-1", OrganizationId = "org_m", ProviderAccountId = "pa", Path = "p", NotBefore = now });
        await db.SaveChangesAsync();

        var service = Service(db);
        await service.FenceAsync(Installation, "op-drain");
        var status = await service.DrainStatusAsync(Installation);
        string[] Ids(string category, string state) => status.Work.SingleOrDefault(x => x.Category == category && x.State == state)?.Ids.ToArray() ?? [];
        Assert.False(status.Drained);
        Assert.Equal(["dep-queued"], Ids("deployments", "queued"));
        Assert.Equal(["dep-running"], Ids("deployments", "running"));
        Assert.Equal(["dep-uncertain"], Ids("deployments", "uncertain"));
        Assert.Equal(["op-q"], Ids("database_operations", "queued"));
        Assert.Equal(["op-retry"], Ids("database_operations", "retrying"));
        Assert.Equal(["op-leased"], Ids("database_operations", "leased"));
        Assert.Equal(["op-expired", "op-recon"], Ids("database_operations", "uncertain"));
        Assert.Equal(["app_del"], Ids("app_deletions", "queued"));
        var cleanup = Assert.Single(status.Work, x => x.Category == "provider_credential_cleanup");
        Assert.False(cleanup.BlocksDrain);
        Assert.Equal(9, status.BlockingCount);

        // Finish every accepted unit except the uncertain migration: drain must stay false for it alone.
        await db.Deployments.Where(x => x.Id != "dep-done").ExecuteUpdateAsync(x => x
            .SetProperty(d => d.Status, DeploymentStatus.Succeeded).SetProperty(d => d.OwnershipToken, (string?)null));
        await db.DatabaseOperations.Where(x => x.Id != "op-recon" && x.Id != "op-plainfail").ExecuteUpdateAsync(x => x
            .SetProperty(o => o.Status, "completed").SetProperty(o => o.LeaseExpiresAt, (DateTimeOffset?)null));
        await db.Apps.Where(x => x.Id == "app_del").ExecuteDeleteAsync();
        status = await service.DrainStatusAsync(Installation);
        Assert.False(status.Drained);
        Assert.Equal(["op-recon"], Ids("database_operations", "uncertain"));

        // A later successful operation on the same target reconciles the uncertain outcome.
        var reconciled = Op("op-reconciled", "completed", at: now);
        reconciled.DatabaseId = "db-op-recon";
        db.DatabaseOperations.Add(reconciled);
        await db.SaveChangesAsync();
        status = await service.DrainStatusAsync(Installation);
        Assert.True(status.Drained);
        Assert.Equal("drained", status.Phase);
    }

    [Fact]
    public async Task HostCommandParsesProtectedInputAndMapsExitCodes()
    {
        Assert.Equal(("fence", (string?)null), MaintenanceCommand.Parse(["fence", "--stdin"]));
        Assert.Equal(("status", "/run/in.json"), MaintenanceCommand.Parse(["status", "--input-file", "/run/in.json"]));
        foreach (var args in new[] { new[] { "fence" }, ["unfence", "--stdin"], ["fence", "--stdin", "--stdin"], ["fence", "--stdin", "--output-file", "x"], ["fence", "--input-file"] })
            Assert.False(Assert.Throws<MaintenanceException>(() => MaintenanceCommand.Parse(args)).Refused);
        await Assert.ThrowsAsync<MaintenanceException>(() => MaintenanceCommand.ReadInputAsync(null, new StringReader("""{"installationId":"a","InstallationId":"b"}""")));
        await Assert.ThrowsAsync<MaintenanceException>(() => MaintenanceCommand.ReadInputAsync(null, new StringReader("""{"installationId":"a","password":"x"}""")));
        await Assert.ThrowsAsync<MaintenanceException>(() => MaintenanceCommand.ReadInputAsync(null, new StringReader("[]")));

        await SeedInstallationAsync();
        await using var db = _harness.CreateDbContext();
        var service = Service(db, "host-a");
        async Task<(int Code, JsonElement Json)> Run(string operation, string json)
        {
            var output = new StringWriter();
            var input = await MaintenanceCommand.ReadInputAsync(null, new StringReader(json));
            var code = await MaintenanceCommand.ExecuteAsync(operation, input, service, output);
            return (code, JsonDocument.Parse(output.ToString()).RootElement.Clone());
        }

        var notFenced = await Run("drain-status", $$"""{"installationId":"{{Installation}}"}""");
        Assert.Equal(MaintenanceCommand.ExitNotDrained, notFenced.Code);
        Assert.Equal("open", notFenced.Json.GetProperty("status").GetProperty("phase").GetString());
        var fence = await Run("fence", $$"""{"installationId":"{{Installation}}","operationId":"op-cmd"}""");
        Assert.Equal(MaintenanceCommand.ExitSuccess, fence.Code);
        Assert.Equal("fenced", fence.Json.GetProperty("authority").GetProperty("state").GetString());
        var drained = await Run("drain-status", $$"""{"installationId":"{{Installation}}","operationId":"op-cmd"}""");
        Assert.Equal(MaintenanceCommand.ExitSuccess, drained.Code);
        Assert.True(drained.Json.GetProperty("status").GetProperty("drained").GetBoolean());
        Assert.Equal(MaintenanceCommand.ExitNotDrained, (await Run("drain-status", $$"""{"installationId":"{{Installation}}","operationId":"op-not-holder"}""")).Code);
        var refused = await Run("release", $$"""{"installationId":"{{Installation}}","operationId":"op-wrong"}""");
        Assert.Equal(MaintenanceCommand.ExitRefused, refused.Code);
        Assert.Equal("maintenance_holder_mismatch", refused.Json.GetProperty("code").GetString());
        Assert.Equal(MaintenanceCommand.ExitSuccess, (await Run("release", $$"""{"installationId":"{{Installation}}","operationId":"op-cmd"}""")).Code);
        await Assert.ThrowsAsync<MaintenanceException>(() => Run("fence", $$"""{"installationId":"{{Installation}}"}"""));
    }

    [Theory]
    [InlineData("Account", "Login", MaintenanceAdmissionClass.Exempt)]
    [InlineData("Runtime", "IngestLogs", MaintenanceAdmissionClass.Exempt)]
    [InlineData("Runtime", "CreateRuntimeServer", MaintenanceAdmissionClass.Governed)]
    [InlineData("Deployments", "CreateDeployment", MaintenanceAdmissionClass.Governed)]
    [InlineData("Deployments", "CancelDeployment", MaintenanceAdmissionClass.Continuation)]
    [InlineData("DatabaseOperationCancellation", "Cancel", MaintenanceAdmissionClass.Continuation)]
    [InlineData("Setup", "Complete", MaintenanceAdmissionClass.Governed)]
    public void AdmissionPolicyClassifiesControllerActions(string controller, string action, MaintenanceAdmissionClass expected) =>
        Assert.Equal(expected, MaintenanceAdmissionPolicy.Classify(controller, action));

    [Fact]
    public async Task FencedApiRefusesManagementWritesWithRetryableErrorWhileReadsWork()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var factory = new MaintenanceApiFactory(connection);
        factory.CreateClient().Dispose();
        string token;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
            await db.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            db.BootstrapRecords.Add(new BootstrapRecord { InstallationId = Installation, OperationId = "op-b", State = "ready", CreatedAt = now, UpdatedAt = now });
            db.Organizations.Add(new Organization { Id = "org_api", Name = "Org", Slug = "org-api", CreatedAt = now, UpdatedAt = now });
            db.Applications.Add(new MinicloudApplication { Id = "application_api", OrganizationId = "org_api", Name = "api", Slug = "api", NormalizedSlug = "api",
                DefaultEnvironmentId = "app_api", LegacyRootAppId = "app_api", CreatedAt = now, UpdatedAt = now });
            db.Apps.Add(new MinicloudApp { Id = "app_api", ApplicationId = "application_api", EnvironmentName = "production", NormalizedEnvironmentName = "production",
                OrganizationId = "org_api", Name = "api", Slug = "api", Plan = AppPlan.P0, Database = DatabaseMode.Sqlite, CreatedAt = now, UpdatedAt = now });
            var tokens = scope.ServiceProvider.GetRequiredService<IApiKeyTokenService>();
            var generated = tokens.Generate();
            token = generated.Token;
            db.ApiKeys.Add(new ApiKey { Id = "key_api", OrganizationId = "org_api", Name = "k", KeyPrefix = generated.Prefix, KeyHash = tokens.Hash(generated.Token),
                ScopesJson = JsonSerializer.Serialize(new[] { ApiKeyScopes.AppsRead, ApiKeyScopes.AppsWrite }), CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
            var catalog = scope.ServiceProvider.GetRequiredService<ProviderCatalogService>();
            var mapping = ProviderPlanMapping.FromAppPlan(AppPlan.P0V0, new ProviderPlanOptions());
            db.ProviderAccounts.Add(new ProviderAccount { Id = "provider_api", OrganizationId = "org_api", Name = "p", ProviderType = mapping.Provider,
                CredentialPath = "fixtures/org_api", CredentialGeneration = "1" });
            await db.SaveChangesAsync();
            await catalog.CreateAsync(new("org_api", "provider_api", mapping.Region, mapping.Sku,
                Minicloud.Common.Validation.ControlPlaneValidation.ToWireValue(AppPlan.P0V0)), default);
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClient();
        const string url = "/api/apps/application_api/environments";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("staging", "p0-v-0"))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await using (var scope = factory.Services.CreateAsyncScope())
            await Service(scope.ServiceProvider.GetRequiredService<MinicloudDbContext>()).FenceAsync(Installation, "op-api");
        var refused = await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("staging", "p0-v-0"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), refused.Headers.RetryAfter?.Delta);
        Assert.Equal("maintenance_in_progress", (await refused.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/environments/app_api")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().MaintenanceActivities.ToListAsync());

        await using (var scope = factory.Services.CreateAsyncScope())
            await Service(scope.ServiceProvider.GetRequiredService<MinicloudDbContext>()).ReleaseAsync(Installation, "op-api");
        var created = await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("staging", "p0-v-0"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<MinicloudDbContext>().MaintenanceActivities.ToListAsync());

        // After transfer to another host this API process is revoked durably, not just paused.
        await using (var scope = factory.Services.CreateAsyncScope())
            await Service(scope.ServiceProvider.GetRequiredService<MinicloudDbContext>(), "host-new").TransferAsync(Installation, "op-move", null);
        var revoked = await client.PostAsJsonAsync(url, new CreateEnvironmentRequest("qa", "p0-v-0"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, revoked.StatusCode);
        Assert.Equal("maintenance_authority_revoked", (await revoked.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/environments/app_api")).StatusCode);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MaintenanceApiFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Secrets:EncryptionKey"] = "synthetic-maintenance-test-key",
                ["Providers:AllowLegacyConfiguration"] = "true",
                ["ManagedDomains:Suffix"] = "app.muni.dev",
                ["Firebase:ProjectId"] = "minicloud-test",
                ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection));
            });
        }
    }
}
