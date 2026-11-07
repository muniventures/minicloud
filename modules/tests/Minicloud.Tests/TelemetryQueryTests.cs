using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class TelemetryQueryTests : IClassFixture<SqliteTestHarness>
{
    private readonly SqliteTestHarness _harness;

    public TelemetryQueryTests(SqliteTestHarness harness)
    {
        _harness = harness;
    }

    [Fact]
    public async Task Unknown_Or_Foreign_App_Returns_Null_For_Non_Disclosing_404()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, _) = await SeedOrgAppAsync(db, NewTag(), "shop");
        var (_, foreignApp) = await SeedOrgAppAsync(db, NewTag(), "shop");
        var queries = CreateService(db, time, LokiOk());

        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());
        Assert.Null(await queries.QueryAsync(viewer, "app_missing", Filters(), CancellationToken.None));
        Assert.Null(await queries.QueryAsync(viewer, foreignApp.Id, Filters(), CancellationToken.None));
    }

    [Fact]
    public async Task Api_Key_Without_Logs_Read_Cannot_Query()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var queries = CreateService(db, time, LokiOk());

        var key = new ControlPlanePrincipal("api_key", null, "key_123", org.Id,
            new HashSet<string>(StringComparer.Ordinal) { ApiKeyScopes.DeploymentsRead });
        Assert.Null(await queries.QueryAsync(key, app.Id, Filters(), CancellationToken.None));

        var reader = new ControlPlanePrincipal("api_key", null, "key_123", org.Id,
            new HashSet<string>(StringComparer.Ordinal) { ApiKeyScopes.LogsRead });
        Assert.IsType<TelemetryQueryOk>(await queries.QueryAsync(reader, app.Id, Filters(), CancellationToken.None));
    }

    [Theory]
    [InlineData("2026-09-08T12:00:00Z", "2026-09-08T11:00:00Z", null, null, null, null, null, "validation_failed")]
    [InlineData(null, null, null, "verbose", null, null, null, "validation_failed")]
    [InlineData(null, null, null, null, null, "not-hex!!", null, "validation_failed")]
    [InlineData(null, null, null, null, null, null, 0, "validation_failed")]
    [InlineData(null, null, null, null, null, null, 1001, "validation_failed")]
    public async Task Invalid_Filters_Return_Typed_Errors(
        string? start, string? end, string? service, string? severity, string? text, string? traceId, int? limit, string code)
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var queries = CreateService(db, time, LokiOk());
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var outcome = await queries.QueryAsync(viewer, app.Id, new TelemetryLogFilters(
            start is null ? null : DateTimeOffset.Parse(start),
            end is null ? null : DateTimeOffset.Parse(end),
            service, severity, text, traceId, limit), CancellationToken.None);
        var invalid = Assert.IsType<TelemetryQueryInvalid>(outcome);
        Assert.Equal(code, invalid.Code);
    }

    [Fact]
    public async Task Window_Older_Than_Retention_Is_Rejected()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var queries = CreateService(db, time, LokiOk());
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var outcome = await queries.QueryAsync(viewer, app.Id, Filters(
            start: Now().AddDays(-8), end: Now().AddDays(-7).AddMinutes(-1)), CancellationToken.None);
        Assert.Equal("start_before_retention", Assert.IsType<TelemetryQueryInvalid>(outcome).Code);
    }

    [Fact]
    public async Task Window_Larger_Than_Seven_Days_Is_Rejected()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var queries = CreateService(db, time, LokiOk());
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var outcome = await queries.QueryAsync(viewer, app.Id, Filters(
            start: Now().AddDays(-7).AddMinutes(-1), end: Now()), CancellationToken.None);
        Assert.Equal("query_range_too_large", Assert.IsType<TelemetryQueryInvalid>(outcome).Code);
    }

    [Fact]
    public async Task Missing_Settings_Returns_Not_Configured_State()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        var queries = CreateService(db, time, LokiOk());
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var ok = Assert.IsType<TelemetryQueryOk>(await queries.QueryAsync(viewer, app.Id, Filters(), CancellationToken.None));
        Assert.Equal("not_configured", ok.Response.State);
        Assert.Empty(ok.Response.Entries);
    }

    [Fact]
    public async Task Disabled_Settings_Return_Disabled_State_Without_Querying_Loki()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time, enabled: false);
        var handler = LokiOk();
        var queries = CreateService(db, time, handler);
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var ok = Assert.IsType<TelemetryQueryOk>(await queries.QueryAsync(viewer, app.Id, Filters(), CancellationToken.None));
        Assert.Equal("disabled", ok.Response.State);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public void Log_Query_Escapes_Hostile_Literals()
    {
        var query = TelemetryQueryService.BuildLogQuery("web\"} |~ \"evil", "a\"b\nc\\d");
        Assert.Equal("{minicloud_source=\"otlp\", service_name=\"web\\\"} |~ \\\"evil\"} |= \"a\\\"b\\nc\\\\d\"", query);
        Assert.DoesNotContain("\"} |~ \"", query.Replace("\\\"", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_Header_Comes_From_Storage_Not_Caller()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        var settings = await EnableAsync(db, app, time);
        var handler = LokiOk();
        var queries = CreateService(db, time, handler);
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var ok = Assert.IsType<TelemetryQueryOk>(await queries.QueryAsync(
            viewer, app.Id, Filters(service: "backend"), CancellationToken.None));
        Assert.Equal("ok", ok.Response.State);
        Assert.NotNull(handler.LastRequest);
        var tenant = Assert.Single(handler.LastRequest!.Headers.GetValues("X-Scope-OrgID"));
        Assert.Equal(settings.TenantId, tenant);
        Assert.Contains("service_name%3D%22backend%22", handler.LastRequest.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetching_More_Than_Limit_Marks_Truncation()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var handler = LokiOk(LokiBody(Enumerable.Range(0, 6).Select(i => ($"line-{i}", "INFO", (string?)null)).ToArray()));
        var queries = CreateService(db, time, handler);
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var ok = Assert.IsType<TelemetryQueryOk>(await queries.QueryAsync(
            viewer, app.Id, Filters(limit: 5), CancellationToken.None));
        Assert.True(ok.Response.Truncated);
        Assert.Equal("entry_limit", ok.Response.TruncationReason);
        Assert.Equal(5, ok.Response.Entries.Count);
    }

    [Fact]
    public async Task Severity_And_Trace_Filters_Apply_As_Typed_Post_Filters()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var handler = LokiOk(LokiBody(
            ("boom", "ERROR", "abcdef1234567890abcdef1234567890"),
            ("all good", "INFO", "abcdef1234567890abcdef1234567890"),
            ("other trace", "ERROR", "ffffffffffffffffffffffffffffffff")));
        var queries = CreateService(db, time, handler);
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var ok = Assert.IsType<TelemetryQueryOk>(await queries.QueryAsync(viewer, app.Id,
            Filters(severity: "error", traceId: "ABCDEF1234567890abcdef1234567890"), CancellationToken.None));
        var entry = Assert.Single(ok.Response.Entries);
        Assert.Equal("boom", entry.Body);
        Assert.Equal("ERROR", entry.Severity);
        Assert.False(ok.Response.Truncated);
    }

    [Fact]
    public async Task Loki_Failure_Maps_To_Retryable_Unavailable()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var queries = CreateService(db, time, new CaptureHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var unavailable = Assert.IsType<TelemetryQueryUnavailable>(
            await queries.QueryAsync(viewer, app.Id, Filters(), CancellationToken.None));
        Assert.Equal("telemetry_unavailable", unavailable.Code);
        Assert.NotNull(unavailable.RetryAfter);
    }

    [Fact]
    public async Task Exhausted_Query_Budget_Returns_Rate_Limited()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var limiter = new TelemetryQueryLimiter();
        Assert.True(limiter.TryEnter(app.Id));
        Assert.True(limiter.TryEnter(app.Id));
        var queries = CreateService(db, time, LokiOk(), limiter);
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        try
        {
            var unavailable = Assert.IsType<TelemetryQueryUnavailable>(
                await queries.QueryAsync(viewer, app.Id, Filters(), CancellationToken.None));
            Assert.Equal("query_rate_limited", unavailable.Code);
        }
        finally
        {
            limiter.Release(app.Id);
            limiter.Release(app.Id);
        }
    }

    [Fact]
    public async Task Audit_Excludes_Search_Text_And_Trace_Id()
    {
        await using var db = _harness.CreateDbContext();
        var time = new MutableTimeProvider(Now());
        var (org, app) = await SeedOrgAppAsync(db, NewTag(), "shop");
        await EnableAsync(db, app, time);
        var queries = CreateService(db, time, LokiOk());
        var viewer = await ViewerPrincipalAsync(db, org.Id, NewTag(), time.GetUtcNow());

        var secretText = $"needle-{NewTag()}";
        var ok = Assert.IsType<TelemetryQueryOk>(await queries.QueryAsync(viewer, app.Id,
            Filters(text: secretText, traceId: "abcdef1234567890abcdef1234567890"), CancellationToken.None));
        Assert.Equal("ok", ok.Response.State);
        var audit = Assert.Single(db.AuditEvents.Where(x => x.Action == "telemetry.logs_queried" && x.AppId == app.Id));
        Assert.DoesNotContain(secretText, audit.Details ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef1234567890abcdef1234567890", audit.Details ?? string.Empty, StringComparison.Ordinal);
    }

    private static TelemetryLogFilters Filters(
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        string? service = null,
        string? severity = null,
        string? text = null,
        string? traceId = null,
        int? limit = null) =>
        new(start ?? Now().AddMinutes(-15), end ?? Now(), service, severity, text, traceId, limit);

    private static string LokiBody(params (string Line, string Severity, string? TraceId)[] entries)
    {
        var streams = string.Join(",", entries.Select(e =>
            "{\"stream\":{\"service_name\":\"backend\",\"minicloud_source\":\"otlp\"},"
            + "\"structuredMetadata\":{\"severity\":\"" + e.Severity + "\""
            + (e.TraceId is null ? string.Empty : ",\"trace_id\":\"" + e.TraceId + "\"") + "},"
            + "\"values\":[[\"" + DateTimeOffsetToNanos(Now()) + "\"," + JsonEncoded(e.Line) + "]]}"));
        return "{\"status\":\"success\",\"data\":{\"resultType\":\"streams\",\"result\":[" + streams + "]}}";
    }

    private static string JsonEncoded(string value) =>
        JsonSerializer.Serialize(value);

    private static string DateTimeOffsetToNanos(DateTimeOffset value) =>
        (value.ToUnixTimeMilliseconds() * 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static CaptureHandler LokiOk(string? body = null) =>
        new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body ?? LokiBody(), System.Text.Encoding.UTF8, "application/json")
        });

    private TelemetryQueryService CreateService(
        MinicloudDbContext db, TimeProvider time, CaptureHandler handler, TelemetryQueryLimiter? limiter = null) =>
        new(db, new ControlPlaneAuthorization(db), new StubClientFactory(handler),
            Options.Create(new TelemetryIngestionOptions { LokiBaseAddress = "http://loki:3100" }),
            time, limiter ?? new TelemetryQueryLimiter(),
            new AuditEventService(db, new GuidIdGenerator(), time), NullLogger<TelemetryQueryService>.Instance);

    private static async Task<Minicloud.Common.Models.AppTelemetrySettings> EnableAsync(
        MinicloudDbContext db, MinicloudApp app, TimeProvider time, bool enabled = true)
    {
        var now = time.GetUtcNow();
        var settings = new Minicloud.Common.Models.AppTelemetrySettings
        {
            Id = $"tset_{Guid.NewGuid():N}",
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            TenantId = $"mcl_{Guid.NewGuid():N}",
            Enabled = enabled,
            Deleting = false,
            RetentionPolicy = "7d",
            ProvisioningStatus = "ready",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AppTelemetrySettings.Add(settings);
        await db.SaveChangesAsync();
        return settings;
    }

    private static string NewTag() => Guid.NewGuid().ToString("N")[..12].ToLowerInvariant();

    private static DateTimeOffset Now() => new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private async Task<(Organization Org, MinicloudApp App)> SeedOrgAppAsync(
        MinicloudDbContext db, string tag, string slug)
    {
        var now = Now();
        var org = new Organization
        {
            Id = $"org_{tag}",
            Name = $"Org {tag}",
            Slug = $"org-{tag}",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Organizations.Add(org);
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
        await db.SaveChangesAsync();
        return (org, app);
    }

    private static async Task<ControlPlanePrincipal> ViewerPrincipalAsync(
        MinicloudDbContext db, string organizationId, string tag, DateTimeOffset now)
    {
        var userId = $"viewer_{tag}";
        db.Users.Add(new User
        {
            Id = userId,
            FirebaseUid = $"firebase_{userId}",
            Email = $"{userId}@example.com",
            CreatedAt = now,
            UpdatedAt = now
        });
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Id = $"mbr_{tag}",
            OrganizationId = organizationId,
            UserId = userId,
            Role = OrganizationRole.Viewer,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        return new ControlPlanePrincipal("firebase", userId, null, null, new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class GuidIdGenerator : IIdGenerator
    {
        public string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }
}
