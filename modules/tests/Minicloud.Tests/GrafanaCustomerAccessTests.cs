using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class GrafanaCustomerAccessTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public GrafanaCustomerAccessTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task TwoOrgs_WithSameAppSlug_StayIsolated_ThroughExplore_DataSource_AndDirectGateway()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (orgA, appA) = await SeedOrgAppAsync(db, $"{tag}a", "shop");
        var (orgB, appB) = await SeedOrgAppAsync(db, $"{tag}b", "shop");
        await db.SaveChangesAsync();

        var provisioning = CreateProvisioning(db, time, fake);
        var gatewayBase = "https://api.test/api/grafana/query-gateway";
        var principalA = FirebasePrincipal($"usr_{tag}a");
        var principalB = FirebasePrincipal($"usr_{tag}b");

        var readyA = await provisioning.EnsureProvisionedAsync(principalA, appA.Id, gatewayBase, CancellationToken.None);
        var readyB = await provisioning.EnsureProvisionedAsync(principalB, appB.Id, gatewayBase, CancellationToken.None);
        Assert.True(readyA.Ready);
        Assert.True(readyB.Ready);
        Assert.NotEqual(readyA.GrafanaOrgId, readyB.GrafanaOrgId);
        Assert.NotEqual(readyA.DataSourceUid, readyB.DataSourceUid);

        var tokenA = ReadToken(fake, readyA.GrafanaOrgId, readyA.DataSourceUid);
        var tokenB = ReadToken(fake, readyB.GrafanaOrgId, readyB.DataSourceUid);
        Assert.NotEqual(tokenA, tokenB);

        var loki = new FakeLokiHandler(_ => Task.FromResult(LokiOk("""{"status":"success","data":{"result":[]}}""")));
        var gateway = CreateGateway(db, time, loki);

        var window = QueryWindow(time.GetUtcNow());
        var exploreA = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bservice_name%3D%22backend%22%7D", null, null, $"Bearer {tokenA}", 1, CancellationToken.None);
        var exploreB = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bservice_name%3D%22backend%22%7D", null, null, $"Bearer {tokenB}", 1, CancellationToken.None);
        Assert.Equal(200, exploreA.StatusCode);
        Assert.Equal(200, exploreB.StatusCode);
        Assert.Equal($"mcl_tenant_{tag}a", loki.Requests[0].Tenant);
        Assert.Equal($"mcl_tenant_{tag}b", loki.Requests[1].Tenant);

        // Caller-supplied tenant signals are overridden, never trusted.
        var forged = await gateway.ExecuteAsync(
            "GET", "/loki/api/v1/query_range",
            $"{window}&query=%7Bservice_name%3D%22backend%22%7D&X-Scope-OrgID={orgB.Id}",
            null, null, $"Bearer {tokenA}", 1, CancellationToken.None);
        Assert.Equal(200, forged.StatusCode);
        Assert.Equal($"mcl_tenant_{tag}a", loki.Requests[^1].Tenant);

        var resolution = await gateway.ResolveAsync($"Bearer {tokenA}", CancellationToken.None);
        Assert.NotNull(resolution);
        Assert.Equal(appA.Id, resolution.AppId);
        Assert.NotEqual(appB.Id, resolution.AppId);

        // Direct gateway calls to non-allowlisted ops are denied, even authenticated.
        var push = await gateway.ExecuteAsync("POST", "/loki/api/v1/push", string.Empty, null, null, $"Bearer {tokenA}", 1, CancellationToken.None);
        Assert.Equal(404, push.StatusCode);
        var rules = await gateway.ExecuteAsync("GET", "/api/v1/rules", string.Empty, null, null, $"Bearer {tokenA}", 1, CancellationToken.None);
        Assert.Equal(404, rules.StatusCode);
        var config = await gateway.ExecuteAsync("GET", "/config", string.Empty, null, null, $"Bearer {tokenA}", 1, CancellationToken.None);
        Assert.Equal(404, config.StatusCode);
        Assert.Equal(3, loki.Requests.Count);
    }

    [Fact]
    public async Task Viewer_CanLaunch_ButCannotAlter_DataSource_Membership_OrAuth()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (org, app) = await SeedOrgAppAsync(db, tag, "shop");
        await EnsureMemberAsync(db, org.Id, $"viewer_{tag}", OrganizationRole.Viewer, time.GetUtcNow());
        await db.SaveChangesAsync();

        var provisioning = CreateProvisioning(db, time, fake);
        var ready = await provisioning.EnsureProvisionedAsync(
            FirebasePrincipal($"viewer_{tag}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(ready.Ready);

        // Guard denies every datasource/auth/membership mutation for Viewers.
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("PUT", "/api/datasources/uid/mcl-x"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("DELETE", "/api/datasources/uid/mcl-x"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("POST", "/api/datasources"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("POST", "/api/auth/keys"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("POST", "/api/org/users"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.AllowForward, GrafanaProxyGuard.DecideRoute("GET", "/api/datasources/uid/mcl-x"));

        // Privilege escalation to Admin is repaired back to Viewer; default-org
        // membership is removed. Identity converges before any session exists.
        fake.SetRole(ready.GrafanaOrgId, ready.GrafanaUserId, "Admin");
        fake.AddToOrg(1, ready.GrafanaUserId, "Viewer");
        var repaired = await provisioning.EnsureProvisionedAsync(
            FirebasePrincipal($"viewer_{tag}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(repaired.Ready);
        Assert.Equal("Viewer", fake.GetRole(ready.GrafanaOrgId, ready.GrafanaUserId));
        Assert.Equal([ready.GrafanaOrgId], fake.GetUserOrgIds(ready.GrafanaUserId));
    }

    [Fact]
    public async Task LaunchCode_IsSingleUse_Expires_AndBoundToAudience()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (_, app) = await SeedOrgAppAsync(db, tag, "shop");
        await db.SaveChangesAsync();

        var sessions = CreateSessions(db, time, fake, audience: "production");
        var launch = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, "/explore", "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(launch.Ok);

        var first = await sessions.ExchangeLaunchCodeAsync(launch.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(first.Ok);
        Assert.NotNull(first.SessionToken);
        Assert.Equal("/explore", first.Destination);

        var replay = await sessions.ExchangeLaunchCodeAsync(launch.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(replay.Ok);

        var launch2 = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(launch2.Ok);
        time.Advance(TimeSpan.FromSeconds(61));
        var expired = await sessions.ExchangeLaunchCodeAsync(launch2.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(expired.Ok);

        var launch3 = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(launch3.Ok);
        var staging = CreateSessions(db, time, fake, audience: "staging");
        var crossEnv = await staging.ExchangeLaunchCodeAsync(launch3.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(crossEnv.Ok);
    }

    [Fact]
    public async Task Revocation_BlocksOpenSession_Within30s_And_Logout_Invalidates()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (org, app) = await SeedOrgAppAsync(db, tag, "shop");
        await db.SaveChangesAsync();

        var sessions = CreateSessions(db, time, fake, audience: "production");
        var launch = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        var exchanged = await sessions.ExchangeLaunchCodeAsync(launch.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(exchanged.Ok);
        Assert.NotNull(await sessions.ValidateProxySessionAsync(exchanged.SessionToken!, CancellationToken.None));

        var membership = await db.OrganizationMembers.SingleAsync(x => x.OrganizationId == org.Id && x.UserId == $"usr_{tag}");
        db.OrganizationMembers.Remove(membership);
        await db.SaveChangesAsync();

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await sessions.ValidateProxySessionAsync(exchanged.SessionToken!, CancellationToken.None));
        var row = await db.GrafanaProxySessions.SingleAsync(x => x.SessionHash == GrafanaSessionService.HashSessionToken(exchanged.SessionToken!));
        Assert.NotNull(row.RevokedAt);

        // A logged-out session never validates again, even with a live membership.
        var launch2 = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(launch2.Ok);
    }

    [Fact]
    public async Task Idle_AndAbsolute_SessionExpiry_AreEnforced()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (_, app) = await SeedOrgAppAsync(db, tag, "shop");
        await db.SaveChangesAsync();

        var sessions = CreateSessions(db, time, fake, audience: "production");
        var launch = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        var exchanged = await sessions.ExchangeLaunchCodeAsync(launch.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(exchanged.Ok);

        time.Advance(TimeSpan.FromMinutes(14).Add(TimeSpan.FromSeconds(59)));
        Assert.NotNull(await sessions.ValidateProxySessionAsync(exchanged.SessionToken!, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.Null(await sessions.ValidateProxySessionAsync(exchanged.SessionToken!, CancellationToken.None));

        var launch2 = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        var exchanged2 = await sessions.ExchangeLaunchCodeAsync(launch2.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(exchanged2.Ok);
        for (var i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromMinutes(14));
            Assert.NotNull(await sessions.ValidateProxySessionAsync(exchanged2.SessionToken!, CancellationToken.None));
        }

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(await sessions.ValidateProxySessionAsync(exchanged2.SessionToken!, CancellationToken.None));
    }

    [Fact]
    public async Task Provisioning_FailsClosed_WithRecoverableError_AndNoSession()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient { CredentialAvailable = false };
        var (_, app) = await SeedOrgAppAsync(db, tag, "shop");
        await db.SaveChangesAsync();

        var provisioning = CreateProvisioning(db, time, fake);
        var result = await provisioning.EnsureProvisionedAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(result.Ready);
        Assert.NotNull(result.UnavailableReason);

        var sessions = CreateSessions(db, time, fake, audience: "production");
        var launch = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(launch.Ok);
        Assert.Empty(db.GrafanaLaunchCodes.Where(x => x.AppId == app.Id && x.ConsumedAt != null));
        Assert.Empty(db.GrafanaProxySessions.Where(x => x.AppId == app.Id));

        // Partial identities (pending bindings) cannot obtain sessions either.
        var fake2 = new FakeGrafanaApiClient { FailOperations = new HashSet<string>(StringComparer.Ordinal) { "user" } };
        var provisioning2 = CreateProvisioning(db, time, fake2);
        var partial = await provisioning2.EnsureProvisionedAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(partial.Ready);
        var userBinding = await db.GrafanaUserBindings
            .SingleOrDefaultAsync(x => x.OrganizationId == app.OrganizationId && x.MinicloudUserId == $"usr_{tag}");
        Assert.True(userBinding is null || userBinding.ReconciliationStatus != "ready");
    }

    [Fact]
    public async Task OrgSwitch_StaleIds_AndForgedHeaders_DoNotRevealData()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (orgA, appA) = await SeedOrgAppAsync(db, $"{tag}a", "shop");
        var (orgB, appB) = await SeedOrgAppAsync(db, $"{tag}b", "vault");
        await db.SaveChangesAsync();

        var sessions = CreateSessions(db, time, fake, audience: "production");
        var launch = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}a"), appA.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(launch.Ok);
        var exchanged = await sessions.ExchangeLaunchCodeAsync(launch.Code!, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        var snapshot = await sessions.ValidateProxySessionAsync(exchanged.SessionToken!, CancellationToken.None);
        Assert.NotNull(snapshot);
        Assert.Equal(orgA.Id, snapshot.OrganizationId);
        Assert.Equal(appA.Id, snapshot.AppId);

        // Cross-org launch payload: member of A cannot launch into B's app.
        var crossOrg = await sessions.IssueLaunchCodeAsync(
            FirebasePrincipal($"usr_{tag}a"), appB.Id, null, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.False(crossOrg.Ok);

        // Forged caller headers are stripped; only the trusted identity survives.
        var incoming = new List<KeyValuePair<string, string>>
        {
            new("X-Minicloud-Grafana-Identity", "mc-evil-orgB"),
            new("X-Grafana-Org-Id", "999"),
            new("Authorization", "Basic YWRtaW46YWRtaW4="),
            new("Cookie", "grafana_session=stolen; mc_grafana_proxy=stolen"),
            new("X-Scope-OrgID", "other-tenant"),
            new("X-Forwarded-User", "evil"),
            new("Content-Type", "application/json")
        };
        var sanitized = GrafanaProxyGuard.StripUntrustedHeaders(incoming);
        Assert.DoesNotContain(sanitized, x => x.Key.StartsWith("X-Minicloud-Grafana-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sanitized, x => string.Equals(x.Key, "Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sanitized, x => string.Equals(x.Key, "Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sanitized, x => string.Equals(x.Key, "X-Grafana-Org-Id", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(sanitized, x => string.Equals(x.Key, "X-Scope-OrgID", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("application/json", sanitized["Content-Type"]);

        // Public login/basic-auth/admin routes are denied even with a valid session.
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("GET", "/login"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("POST", "/api/login"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("GET", "/api/admin/settings"));
        Assert.Equal(GrafanaProxyGuard.RouteDecision.Deny, GrafanaProxyGuard.DecideRoute("GET", "/api/datasources/proxy/uid/mcl-x/loki/api/v1/query_range"));

        // Unified-query envelope: foreign UID, oversized expr, and 8-day range denied.
        var uid = snapshot.DataSourceUid;
        Assert.True(GrafanaProxyGuard.TryValidateDsQueryEnvelope(
            $$"""{"queries":[{"datasource":{"type":"loki","uid":"{{uid}}"},"expr":"{service_name=\"backend\"}"}],"from":"2026-09-07T12:00:00Z","to":"2026-09-08T12:00:00Z"}""",
            uid, out _));
        Assert.False(GrafanaProxyGuard.TryValidateDsQueryEnvelope(
            """{"queries":[{"datasource":{"type":"loki","uid":"mcl-other"},"expr":"{x=\"y\"}"}]}""",
            uid, out _));
        Assert.False(GrafanaProxyGuard.TryValidateDsQueryEnvelope(
            $$"""{"queries":[{"datasource":{"type":"loki","uid":"{{uid}}"},"expr":"{{new string('x', 5000)}}"}],"from":"2026-09-07T12:00:00Z","to":"2026-09-08T12:00:00Z"}""",
            uid, out _));
        Assert.False(GrafanaProxyGuard.TryValidateDsQueryEnvelope(
            $$"""{"queries":[{"datasource":{"type":"loki","uid":"{{uid}}"},"expr":"{a=\"b\"}"}],"from":"2026-08-01T00:00:00Z","to":"2026-09-08T12:00:00Z"}""",
            uid, out _));
    }

    [Fact]
    public async Task QueryGateway_Enforces_Result_Time_Text_AndConcurrencyBounds()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (_, app) = await SeedOrgAppAsync(db, tag, "shop");
        await db.SaveChangesAsync();

        var provisioning = CreateProvisioning(db, time, fake);
        var ready = await provisioning.EnsureProvisionedAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(ready.Ready);
        var token = $"Bearer {ReadToken(fake, ready.GrafanaOrgId, ready.DataSourceUid)}";

        var loki = new FakeLokiHandler(_ => Task.FromResult(LokiOk("{}")));
        var gateway = CreateGateway(db, time, loki);
        var window = QueryWindow(time.GetUtcNow());

        var tooMany = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D&limit=2000", null, null, token, 1, CancellationToken.None);
        Assert.Equal(400, tooMany.StatusCode);

        var wide = QueryWindowHours(time.GetUtcNow(), 192);
        var tooWide = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{wide}&query=%7Bx%3D%22y%22%7D", null, null, token, 1, CancellationToken.None);
        Assert.Equal(400, tooWide.StatusCode);

        var longQuery = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query={new string('a', 5000)}", null, null, token, 1, CancellationToken.None);
        Assert.Equal(400, longQuery.StatusCode);

        var noWindow = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", "query=%7Bx%3D%22y%22%7D", null, null, token, 1, CancellationToken.None);
        Assert.Equal(400, noWindow.StatusCode);

        var labelsNoWindow = await gateway.ExecuteAsync("GET", "/loki/api/v1/labels", string.Empty, null, null, token, 1, CancellationToken.None);
        Assert.Equal(400, labelsNoWindow.StatusCode);

        // Metadata discovery is bounded and tenant-scoped exactly like Explore.
        var labels = await gateway.ExecuteAsync("GET", "/loki/api/v1/labels", window, null, null, token, 1, CancellationToken.None);
        Assert.Equal(200, labels.StatusCode);
        Assert.Equal($"mcl_tenant_{tag}", loki.Requests[^1].Tenant);

        var series = await gateway.ExecuteAsync("GET", "/loki/api/v1/series", $"{window}&match%5B%5D=%7Bservice_name%3D%22backend%22%7D", null, null, token, 1, CancellationToken.None);
        Assert.Equal(200, series.StatusCode);

        var federated = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D&shards=8", null, null, token, 1, CancellationToken.None);
        Assert.Equal(400, federated.StatusCode);

        var ambiguous = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D", null, null, token, 2, CancellationToken.None);
        Assert.Equal(401, ambiguous.StatusCode);

        // Oversized upstream responses fail closed instead of truncating.
        var bigLoki = new FakeLokiHandler(_ => Task.FromResult(LokiOk(new string('x', (int)TelemetryLimits.MaxQueryResponseBytes + 1024))));
        var bigGateway = CreateGateway(db, time, bigLoki);
        var oversized = await bigGateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D", null, null, token, 1, CancellationToken.None);
        Assert.Equal(502, oversized.StatusCode);
    }

    [Fact]
    public async Task QueryGateway_Enforces_PerApp_AndTotal_Concurrency()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var tokens = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var (_, app) = await SeedOrgAppAsync(db, $"{tag}{i}", $"app{i}");
            var ready = await CreateProvisioning(db, time, fake).EnsureProvisionedAsync(
                FirebasePrincipal($"usr_{tag}{i}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
            Assert.True(ready.Ready);
            tokens.Add($"Bearer {ReadToken(fake, ready.GrafanaOrgId, ready.DataSourceUid)}");
        }

        await db.SaveChangesAsync();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var loki = new FakeLokiHandler(async _ =>
        {
            Interlocked.Increment(ref entered);
            await gate.Task;
            return LokiOk("{}");
        });
        var gateway = CreateGateway(db, time, loki);
        var window = QueryWindow(time.GetUtcNow());
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var running = new List<Task<GrafanaGatewayOutcome>>();
        for (var i = 0; i < 2; i++)
        {
            running.Add(gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D", null, null, tokens[0], 1, cts.Token));
        }

        await SpinUntilAsync(() => Volatile.Read(ref entered) == 2, cts.Token);
        var third = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D", null, null, tokens[0], 1, cts.Token);
        Assert.Equal(429, third.StatusCode);

        for (var app = 1; app < 4; app++)
        {
            for (var i = 0; i < 2; i++)
            {
                running.Add(gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D", null, null, tokens[app], 1, cts.Token));
            }
        }

        await SpinUntilAsync(() => Volatile.Read(ref entered) == 8, cts.Token);
        var overTotal = await gateway.ExecuteAsync("GET", "/loki/api/v1/query_range", $"{window}&query=%7Bx%3D%22y%22%7D", null, null, tokens[4], 1, cts.Token);
        Assert.Equal(429, overTotal.StatusCode);

        gate.TrySetResult(true);
        var outcomes = await Task.WhenAll(running);
        Assert.All(outcomes, x => Assert.Equal(200, x.StatusCode));
    }

    [Fact]
    public async Task OperatorCredential_FailsClosed_WhenMissing_AndResolves_FromStore()
    {
        var throwing = new ThrowingSecretStore();
        var provider = new GrafanaOperatorCredentialProvider(
            throwing, Options.Create(new GrafanaOptions()), new EmptyConfiguration(), NullLogger<GrafanaOperatorCredentialProvider>.Instance);
        Assert.Null(await provider.GetServerAdminPasswordAsync(CancellationToken.None));

        var memory = new MemorySecretStore("op-secret");
        var resolved = new GrafanaOperatorCredentialProvider(
            memory, Options.Create(new GrafanaOptions()), new EmptyConfiguration(), NullLogger<GrafanaOperatorCredentialProvider>.Instance);
        Assert.Equal("op-secret", await resolved.GetServerAdminPasswordAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Provisioning_IsIdempotent_AcrossRetries()
    {
        var tag = NewTag();
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var fake = new FakeGrafanaApiClient();
        var (_, app) = await SeedOrgAppAsync(db, tag, "shop");
        await db.SaveChangesAsync();

        var provisioning = CreateProvisioning(db, time, fake);
        var first = await provisioning.EnsureProvisionedAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        var second = await provisioning.EnsureProvisionedAsync(
            FirebasePrincipal($"usr_{tag}"), app.Id, "https://api.test/api/grafana/query-gateway", CancellationToken.None);
        Assert.True(first.Ready);
        Assert.True(second.Ready);
        Assert.Equal(first.GrafanaOrgId, second.GrafanaOrgId);
        Assert.Equal(first.GrafanaUserId, second.GrafanaUserId);
        Assert.Equal(first.DataSourceUid, second.DataSourceUid);
        Assert.Equal(1, fake.OrgCreates);
        Assert.Equal(1, fake.UserCreates);
    }

    private static string ReadToken(FakeGrafanaApiClient fake, long orgId, string uid)
    {
        var header = fake.DataSourceSecrets[(orgId, uid)];
        Assert.StartsWith("Bearer ", header);
        return header["Bearer ".Length..].Trim();
    }

    private static string QueryWindow(DateTimeOffset now)
    {
        var end = now.ToUnixTimeMilliseconds() * 1_000_000L;
        var start = end - 15 * 60 * 1_000_000_000L;
        return $"start={start}&end={end}";
    }

    private static string QueryWindowHours(DateTimeOffset now, int hours)
    {
        var end = now.ToUnixTimeMilliseconds() * 1_000_000L;
        var start = end - hours * 3_600L * 1_000_000_000L;
        return $"start={start}&end={end}";
    }

    private static async Task SpinUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.True(condition());
    }

    private static string NewTag() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();
    private static DateTimeOffset Now() => new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private static ControlPlanePrincipal FirebasePrincipal(string userId) =>
        new("browser_session", userId, null, null, new HashSet<string>(StringComparer.Ordinal));

    private async Task<(Organization Org, MinicloudApp App)> SeedOrgAppAsync(MinicloudDbContext db, string tag, string slug)
    {
        var now = Now();
        var org = new Organization { Id = $"org_{tag}", Name = $"Org {tag}", Slug = $"org-{tag}", CreatedAt = now, UpdatedAt = now };
        db.Organizations.Add(org);
        var app = new MinicloudApp
        {
            Id = $"app_{tag}_{slug}", OrganizationId = org.Id, Name = slug, Slug = slug,
            Plan = AppPlan.P0H0, Database = DatabaseMode.Postgres, CreatedAt = now, UpdatedAt = now
        };
        db.Apps.Add(app);
        if (!await db.Users.AnyAsync(x => x.Id == $"usr_{tag}"))
        {
            db.Users.Add(new User { Id = $"usr_{tag}", KratosIdentityId = $"kratos_usr_{tag}", Email = $"usr_{tag}@example.com", CreatedAt = now, UpdatedAt = now });
        }

        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = $"mbr_{tag}", OrganizationId = org.Id, UserId = $"usr_{tag}",
            Role = OrganizationRole.Admin, CreatedAt = now, UpdatedAt = now
        });
        db.AppTelemetrySettings.Add(new AppTelemetrySettings
        {
            Id = $"tset_{tag}", OrganizationId = org.Id, AppId = app.Id, TenantId = $"mcl_tenant_{tag}",
            Enabled = true, Deleting = false, RetentionPolicy = "7d", ProvisioningStatus = "ready",
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return (org, app);
    }

    private static async Task EnsureMemberAsync(MinicloudDbContext db, string orgId, string userId, OrganizationRole role, DateTimeOffset now)
    {
        if (!await db.Users.AnyAsync(x => x.Id == userId))
        {
            db.Users.Add(new User { Id = userId, KratosIdentityId = $"kratos_{userId}", Email = $"{userId}@example.com", CreatedAt = now, UpdatedAt = now });
        }

        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = $"mbr_{userId}", OrganizationId = orgId, UserId = userId, Role = role, CreatedAt = now, UpdatedAt = now
        });
    }

    private GrafanaProvisioningService CreateProvisioning(MinicloudDbContext db, TimeProvider time, IGrafanaApiClient client) =>
        new(db, client, new GuidIdGenerator(), time, new ControlPlaneAuthorization(db),
            new AuditEventService(db, new GuidIdGenerator(), time),
            Options.Create(new GrafanaOptions { ExpectedVersion = "11.6.5" }),
            NullLogger<GrafanaProvisioningService>.Instance);

    private GrafanaSessionService CreateSessions(MinicloudDbContext db, TimeProvider time, IGrafanaApiClient client, string audience)
    {
        var provisioning = CreateProvisioning(db, time, client);
        return new GrafanaSessionService(
            db, provisioning, new ControlPlaneAuthorization(db), new GuidIdGenerator(), time,
            Options.Create(new GrafanaOptions { Audience = audience, AllowedOrigins = ["https://console.test"] }),
            new TestHostEnvironment(), NullLogger<GrafanaSessionService>.Instance);
    }

    private static GrafanaQueryGateway CreateGateway(MinicloudDbContext db, TimeProvider time, FakeLokiHandler loki)
    {
        var http = new HttpClient(loki) { BaseAddress = new Uri("http://loki:3100/") };
        var factory = new StubHttpClientFactory(http);
        return new GrafanaQueryGateway(
            db, time, factory,
            Options.Create(new TelemetryIngestionOptions { LokiBaseAddress = "http://loki:3100" }),
            NullLogger<GrafanaQueryGateway>.Instance);
    }

    private static HttpResponseMessage LokiOk(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

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

    private sealed class EmptyConfiguration : Microsoft.Extensions.Configuration.IConfiguration
    {
        public string? this[string key] { get => null; set { } }
        public IEnumerable<Microsoft.Extensions.Configuration.IConfigurationSection> GetChildren() => [];
        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => new Microsoft.Extensions.Primitives.CancellationChangeToken(CancellationToken.None);
        public Microsoft.Extensions.Configuration.IConfigurationSection GetSection(string key) =>
            new Microsoft.Extensions.Configuration.ConfigurationSection(new Microsoft.Extensions.Configuration.ConfigurationRoot([]), key);
    }

    private sealed class ThrowingSecretStore : ISecretStore
    {
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken) =>
            throw new SecretStoreUnavailableException("unavailable");
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            throw new SecretStoreUnavailableException("unavailable");
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            throw new SecretStoreUnavailableException("unavailable");
    }

    private sealed class MemorySecretStore(string value) : ISecretStore
    {
        public Task<StoredSecretReference> PutAsync(SecretStoreWriteRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredSecretReference("memory", "p", "1"));
        public Task<string> GetAsync(StoredSecretReference reference, CancellationToken cancellationToken) =>
            Task.FromResult(value);
        public Task DeleteAsync(StoredSecretReference reference, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed record LokiRequest(string? Tenant, string Path, string Query);

    private sealed class FakeLokiHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public readonly List<LokiRequest> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? tenant = null;
            if (request.Headers.TryGetValues("X-Scope-OrgID", out var values))
            {
                tenant = string.Join(",", values);
            }

            lock (Requests)
            {
                Requests.Add(new LokiRequest(tenant, request.RequestUri?.AbsolutePath ?? string.Empty, request.RequestUri?.Query ?? string.Empty));
            }

            return await responder(request);
        }
    }

    private sealed class FakeGrafanaApiClient : IGrafanaApiClient
    {
        public string Version = "11.6.5";
        public bool CredentialAvailable = true;
        public HashSet<string> FailOperations = new(StringComparer.Ordinal);
        public int OrgCreates;
        public int UserCreates;
        public readonly Dictionary<(long OrgId, string Uid), string> DataSourceSecrets = [];

        private long _nextOrg = Interlocked.Increment(ref s_orgSequence) * 1000 + 10;
        private long _nextUser = Interlocked.Increment(ref s_userSequence) * 1000 + 100;
        private static long s_orgSequence;
        private static long s_userSequence;
        private readonly Dictionary<string, long> _orgs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (long Id, string Email)> _users = new(StringComparer.Ordinal);
        private readonly Dictionary<long, List<(long UserId, string Role)>> _members = new();
        private readonly Dictionary<(long OrgId, string Uid), GrafanaDataSourceState> _dataSources = new();

        public void SetRole(long orgId, long userId, string role)
        {
            var members = _members[orgId];
            var index = members.FindIndex(x => x.UserId == userId);
            members[index] = (userId, role);
        }

        public string GetRole(long orgId, long userId) =>
            _members[orgId].Single(x => x.UserId == userId).Role;

        public void AddToOrg(long orgId, long userId, string role)
        {
            _members.TryAdd(orgId, []);
            _members[orgId].Add((userId, role));
        }

        public IReadOnlyList<long> GetUserOrgIds(long userId) =>
            _members.Where(x => x.Value.Any(m => m.UserId == userId)).Select(x => x.Key).OrderBy(x => x).ToList();

        private void Check(string operation)
        {
            if (!CredentialAvailable)
            {
                throw new GrafanaProvisioningUnavailableException("Grafana server-admin credential is unavailable.");
            }

            if (FailOperations.Contains(operation))
            {
                throw new GrafanaProvisioningUnavailableException($"Injected failure at {operation}.");
            }
        }

        public Task<string> GetVersionAsync(CancellationToken cancellationToken)
        {
            Check("version");
            return Task.FromResult(Version);
        }

        public Task<GrafanaOrgState> EnsureOrganizationAsync(string name, CancellationToken cancellationToken)
        {
            Check("org");
            if (_orgs.TryGetValue(name, out var id))
            {
                return Task.FromResult(new GrafanaOrgState(id, name));
            }

            OrgCreates++;
            id = _nextOrg++;
            _orgs[name] = id;
            _members.TryAdd(id, []);
            return Task.FromResult(new GrafanaOrgState(id, name));
        }

        public Task<GrafanaUserState> EnsureUserAsync(string login, string email, string name, CancellationToken cancellationToken)
        {
            Check("user");
            if (_users.TryGetValue(login, out var existing))
            {
                return Task.FromResult(new GrafanaUserState(existing.Id, login, existing.Email));
            }

            UserCreates++;
            var id = _nextUser++;
            _users[login] = (id, email);
            return Task.FromResult(new GrafanaUserState(id, login, email));
        }

        public Task<IReadOnlyList<GrafanaOrgMembership>> GetOrgUsersAsync(long grafanaOrgId, CancellationToken cancellationToken)
        {
            Check("members");
            var loginById = _users.ToDictionary(x => x.Value.Id, x => x.Key);
            var result = _members.TryGetValue(grafanaOrgId, out var members)
                ? members.Select(x => new GrafanaOrgMembership(x.UserId, loginById.GetValueOrDefault(x.UserId, "?"), x.Role)).ToList()
                : new List<GrafanaOrgMembership>();
            return Task.FromResult<IReadOnlyList<GrafanaOrgMembership>>(result);
        }

        public Task<IReadOnlyList<long>> GetUserOrganizationsAsync(long grafanaUserId, CancellationToken cancellationToken)
        {
            Check("userorgs");
            return Task.FromResult<IReadOnlyList<long>>(GetUserOrgIds(grafanaUserId));
        }

        public Task AddOrgUserAsync(long grafanaOrgId, long grafanaUserId, string role, CancellationToken cancellationToken)
        {
            Check("adduser");
            _members.TryAdd(grafanaOrgId, []);
            if (!_members[grafanaOrgId].Any(x => x.UserId == grafanaUserId))
            {
                _members[grafanaOrgId].Add((grafanaUserId, role));
            }

            return Task.CompletedTask;
        }

        public Task UpdateOrgUserRoleAsync(long grafanaOrgId, long grafanaUserId, string role, CancellationToken cancellationToken)
        {
            Check("roleuser");
            SetRole(grafanaOrgId, grafanaUserId, role);
            return Task.CompletedTask;
        }

        public Task RemoveOrgUserAsync(long grafanaOrgId, long grafanaUserId, CancellationToken cancellationToken)
        {
            Check("removeuser");
            if (_members.TryGetValue(grafanaOrgId, out var members))
            {
                members.RemoveAll(x => x.UserId == grafanaUserId);
            }

            return Task.CompletedTask;
        }

        public Task<GrafanaDataSourceState?> GetDataSourceAsync(long grafanaOrgId, string uid, CancellationToken cancellationToken)
        {
            Check("readds");
            return Task.FromResult(_dataSources.GetValueOrDefault((grafanaOrgId, uid)));
        }

        public Task<GrafanaDataSourceState> EnsureDataSourceAsync(
            long grafanaOrgId, string uid, string name, string url, string? authorizationHeaderValue, CancellationToken cancellationToken)
        {
            Check("writeds");
            if (authorizationHeaderValue is not null)
            {
                DataSourceSecrets[(grafanaOrgId, uid)] = authorizationHeaderValue;
            }

            var state = new GrafanaDataSourceState(uid, name, url, "proxy", false);
            _dataSources[(grafanaOrgId, uid)] = state;
            return Task.FromResult(state);
        }
    }
}
