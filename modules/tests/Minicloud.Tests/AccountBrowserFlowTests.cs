using System.Net;
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
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Tests;

public sealed class AccountBrowserFlowTests : IAsyncLifetime
{
    private const string FlowId = "11111111-2222-4333-8444-555555555555";
    private const string IdentityId = "22222222-2222-4333-8444-555555555555";
    private const string SessionId = "33333333-2222-4333-8444-555555555555";
    private const string Dashboard = "https://dashboard.example.test";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private FlowFactory _factory = default!;
    private HttpClient _client = default!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = new FlowFactory(_connection);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://api.example.test"), HandleCookies = false, AllowAutoRedirect = false
        });
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "user-1", KratosIdentityId = IdentityId, Email = "operator@example.test" });
        db.Organizations.Add(new Organization { Id = "org-1", Name = "Example", Slug = "example" });
        db.OrganizationMembers.Add(new OrganizationMember { Id = "member-1", UserId = "user-1", OrganizationId = "org-1", Role = OrganizationRole.Owner });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task StartAndReadRewriteOnlyTheFixedFlowActionAndAllowlistedCookies()
    {
        var response = await _client.GetAsync("/api/account/flows/login/browser?returnTo=%2Fcli%2Flogin%3Fsession%3Drequest-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var body = (await response.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        Assert.Null(body.Session);
        Assert.Equal($"/api/account/flows/login?flow={FlowId}", body.Flow!.Ui.Action);
        Assert.DoesNotContain("private upstream diagnostic", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(body.Flow.Ui.Nodes, node => node.Type == "script");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("csrf_token_test=", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains("samesite=lax", cookie);
        Assert.DoesNotContain("domain=", cookie);
        var upstream = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("/self-service/login/browser", upstream.Uri.AbsolutePath);
        Assert.Contains(Uri.EscapeDataString(Dashboard + "/cli/login?session=request-1"), upstream.Uri.Query);
        Assert.Equal(Dashboard, upstream.Origin);

        var read = await _client.GetAsync($"/api/account/flows/login?id={FlowId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal($"/self-service/login/flows?id={FlowId}", _factory.Upstream.Requests.Last().Uri.PathAndQuery);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/%2f%2fevil.example")]
    [InlineData("/%0aevil")]
    public async Task UnsafeReturnTargetsNeverReachKratos(string path)
    {
        var response = await _client.GetAsync("/api/account/flows/login/browser?returnTo=" + Uri.EscapeDataString(path));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Theory]
    [InlineData("/api/account/flows/registration/browser")]
    [InlineData("/api/account/flows/admin/browser")]
    [InlineData("/api/account/flows/recovery/browser")]
    [InlineData("/api/account/flows/login?id=../../admin")]
    public async Task UnapprovedKindsAndIdsAreRejected(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task SubmitRequiresExactOriginAndUpstreamCsrfBeforeCallingKratos()
    {
        var response = await _client.PostAsJsonAsync($"/api/account/flows/login?flow={FlowId}", LoginFields());
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var request = Submit("login", new { identifier = "operator@example.test", password = "synthetic-password" });
        response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task LoginVerifiesWhoamiAndIssuesOnlyALocalOpaqueSessionForTheBoundIdentity()
    {
        var (cookie, csrf) = await Login();
        Assert.NotEmpty(csrf);
        Assert.DoesNotContain(SessionId, cookie);
        Assert.DoesNotContain(IdentityId, cookie);
        Assert.Equal(2, _factory.Upstream.Requests.Count);
        Assert.Equal("/sessions/whoami", _factory.Upstream.Requests[1].Uri.AbsolutePath);
        Assert.Contains("ory_kratos_session=synthetic-ory-session", _factory.Upstream.Requests[1].Cookie);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        request.Headers.Add("Cookie", cookie);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(csrf, (await response.Content.ReadFromJsonAsync<AccountSessionResponse>())!.CsrfToken);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var stored = await db.BrowserSessions.SingleAsync();
        Assert.DoesNotContain(cookie.Split('=')[1], stored.TokenHash);
        Assert.Equal("user-1", stored.UserId);
        Assert.Single(await db.Users.ToListAsync());
        Assert.Single(await db.OrganizationMembers.ToListAsync());
    }

    [Fact]
    public async Task LoginCannotAdoptExistingOperatorByEmail()
    {
        _factory.Upstream.Identity = "44444444-2222-4333-8444-555555555555";
        using var request = Submit("login", LoginFields());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.Empty(await db.BrowserSessions.ToListAsync());
        Assert.Equal(IdentityId, (await db.Users.SingleAsync()).KratosIdentityId);
    }

    [Fact]
    public async Task SettingsRequiresBrowserSessionCsrfAndRotatesAllLocalSessions()
    {
        var (cookie, csrf) = await Login();
        using (var invalid = Submit("settings", LoginFields(), cookie))
        {
            var response = await _client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var request = Submit("settings", LoginFields(), cookie);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        var changed = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(cookie, LocalCookie(changed));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var sessions = await db.BrowserSessions.AsNoTracking().ToListAsync();
        Assert.Single(sessions);
        Assert.Single(sessions, session => session.RevokedAt is null);
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        stale.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task LogoutUsesTheReconstructedBrowserSessionAndRevokesIt()
    {
        var (cookie, csrf) = await Login();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/logout");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", Dashboard);
        request.Headers.Add("X-Minicloud-CSRF", csrf);
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-minicloud-session=;"));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        Assert.NotNull((await db.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
    }

    [Fact]
    public async Task EnabledRecoveryRequiresAVerifiedBoundSessionAndRevokesPreviousLocalAccess()
    {
        var (cookie, _) = await Login();
        _factory.Services.GetRequiredService<IOptions<KratosOptions>>().Value.EmailRecoveryEnabled = true;
        var start = await _client.GetAsync("/api/account/flows/recovery/browser");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        using var request = Submit("recovery", new { csrf_token = "flow-csrf", code = "123456", method = "code" });
        var recovered = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.NotEqual(cookie, LocalCookie(recovered));
        using var stale = new HttpRequestMessage(HttpMethod.Get, "/api/account/session");
        stale.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task ExplicitInvalidBearerCannotFallBackToTheBrowserCookieForSettings()
    {
        var (cookie, _) = await Login();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account/flows/settings/browser");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Authorization", "Bearer invalid-synthetic");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request)).StatusCode);
        Assert.Equal(2, _factory.Upstream.Requests.Count);
    }

    [Fact]
    public async Task OversizedAndUnexpectedFieldsFailWithoutUpstreamMutation()
    {
        using var oversized = Submit("login", new { csrf_token = "csrf", password = new string('x', 65536) });
        var response = await _client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var unexpected = Submit("login", new { csrf_token = "csrf", url = "http://admin.example" });
        response = await _client.SendAsync(unexpected);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Theory]
    [InlineData(400, 400, "auth_flow_invalid")]
    [InlineData(401, 401, "unauthenticated")]
    [InlineData(403, 403, "csrf_invalid")]
    [InlineData(410, 410, "auth_flow_expired")]
    [InlineData(503, 503, "auth_unavailable")]
    [InlineData(302, 503, "auth_unavailable")]
    public async Task UpstreamErrorsAndRedirectsAreSanitizedAndNeverFollowed(int upstreamStatus, int expected, string code)
    {
        _factory.Upstream.Status = upstreamStatus;
        using var request = Submit("login", LoginFields());
        var response = await _client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(code, body);
        Assert.DoesNotContain("private upstream diagnostic", body);
        Assert.Single(_factory.Upstream.Requests);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    private async Task<(string Cookie, string Csrf)> Login()
    {
        using var request = Submit("login", LoginFields());
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<AccountFlowResponse>())!;
        Assert.Null(body.Flow);
        return (LocalCookie(response), body.Session!.CsrfToken);
    }

    private static string LocalCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("__Host-minicloud-session=")).Split(';')[0];

    private static object LoginFields() =>
        new { csrf_token = "synthetic-flow-csrf", identifier = "operator@example.test", password = "synthetic-password", method = "password" };

    private static HttpRequestMessage Submit(string kind, object fields, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/account/flows/{kind}?flow={FlowId}")
        { Content = JsonContent.Create(fields) };
        request.Headers.Add("Origin", Dashboard);
        if (cookie is not null) request.Headers.Add("Cookie", cookie + "; ory_kratos_session=synthetic-ory-session");
        return request;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class FlowFactory(SqliteConnection connection) : WebApplicationFactory<Program>
    {
        public UpstreamHandler Upstream { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite", ["Database:RunMigrationsOnStartup"] = "false",
                ["ConnectionStrings:Minicloud"] = "Data Source=:memory:",
                ["Cors:AllowedOrigins:0"] = Dashboard,
                ["Kratos:DashboardOrigin"] = Dashboard, ["Kratos:PublicBaseUrl"] = "http://kratos:4433"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options => options.UseSqlite(connection));
                services.RemoveAll<IKratosSessionValidator>();
                services.AddSingleton<IKratosSessionValidator>(new SessionValidator());
                services.AddHttpClient<KratosBrowserFlowClient>().ConfigurePrimaryHttpMessageHandler(() => Upstream);
            });
        }
    }

    private sealed class SessionValidator : IKratosSessionValidator
    {
        public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class UpstreamHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Cookie, string Origin)> Requests { get; } = [];
        public string Identity { get; set; } = IdentityId;
        public int Status { get; set; } = 200;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : "",
                request.Headers.TryGetValues("Origin", out var origin) ? origin.Single() : ""));
            if (Status != 200)
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)Status)
                { Content = new StringContent("private upstream diagnostic") });
            if (request.RequestUri!.AbsolutePath == "/sessions/whoami")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = JsonContent.Create(new { id = SessionId, active = true, identity = new { id = Identity } }) });
            if (request.Method == HttpMethod.Post)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = request.RequestUri.AbsolutePath.EndsWith("settings") || request.RequestUri.AbsolutePath.EndsWith("recovery")
                        ? JsonContent.Create(new { state = request.RequestUri.AbsolutePath.EndsWith("recovery") ? "passed_challenge" : "success" })
                        : JsonContent.Create(new { session = new { id = SessionId } })
                };
                response.Headers.Add("Set-Cookie", "ory_kratos_session=synthetic-ory-session; Path=/; HttpOnly");
                return Task.FromResult(response);
            }
            var kind = request.RequestUri.AbsolutePath.Split('/')[2];
            var flowResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    id = FlowId, type = "browser", expires_at = DateTimeOffset.UtcNow.AddMinutes(10),
                    ui = new
                    {
                        action = $"http://kratos:4433/self-service/{kind}?flow={FlowId}", method = "POST",
                        nodes = new[]
                        {
                            new { group = "default", attributes = new { name = "csrf_token", type = "hidden", value = "synthetic-csrf", required = true } },
                            new { group = "default", attributes = new { name = "evil", type = "script", value = "private upstream diagnostic", required = false } }
                        },
                        messages = new[] { new { id = 1, type = "error", text = "private upstream diagnostic" } }
                    }
                }), Encoding.UTF8, "application/json")
            };
            flowResponse.Headers.Add("Set-Cookie", "csrf_token_test=synthetic-cookie; Domain=kratos; Path=/self-service; SameSite=None");
            flowResponse.Headers.Add("Set-Cookie", "not_allowed=private; Path=/");
            return Task.FromResult(flowResponse);
        }
    }
}
