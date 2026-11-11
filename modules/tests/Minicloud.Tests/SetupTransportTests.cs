using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Api.Controllers;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class SetupTransportTests
{
    private const string Installation = "installation-test";
    private const string Operation = "operation-test";
    private const string Dashboard = "https://console.example.test";
    private static CompleteSetupRequest Form(string token) =>
        new(token, "operator@example.test", "Synthetic-password-123!", "Operator", "Workspace", "workspace");

    [Fact]
    public async Task SharedCompletionReturnsResultNotSessionAndRejectsBrowserReplayWithoutRecreatingAnything()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        var token = new BootstrapServiceTests.TokenOutput();
        await files.Service.PrepareAsync(Installation, Operation, token);
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        using (var pending = await client.GetAsync("/api/account/setup"))
        {
            AssertNoStore(pending);
            var status = (await pending.Content.ReadFromJsonAsync<BootstrapStatusResponse>())!;
            Assert.Equal("required", status.State);
            Assert.Equal(files.Clock.Now.AddMinutes(30), status.ExpiresAt);
        }
        using (var completed = await Post(client, Form(token.Token!)))
        {
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            AssertNoStore(completed);
            var result = (await completed.Content.ReadFromJsonAsync<BootstrapResult>())!;
            Assert.Equal(Installation, result.InstallationId);
            Assert.Equal(Operation, result.OperationId);
            Assert.Equal("ready", result.State);
            Assert.NotNull(result.UserId);
            Assert.NotNull(result.OrganizationId);
            var body = await completed.Content.ReadAsStringAsync();
            Assert.DoesNotContain(token.Token!, body);
            Assert.DoesNotContain("Synthetic-password", body);
            Assert.DoesNotContain("csrfToken", body);
        }
        files.Identities.Unavailable = true;
        using var ready = await client.GetAsync("/api/account/setup/");
        AssertNoStore(ready);
        Assert.Equal("ready", (await ready.Content.ReadFromJsonAsync<BootstrapStatusResponse>())!.State);
        using var replay = await Post(client, Form("incorrect-but-ready"));
        await Error(replay, 409, "setup_already_completed");
        Assert.Equal(1, files.Identities.Creates);
        Assert.Equal(0, files.Identities.PasswordChanges);
        Assert.Single(await files.Db.Users.ToListAsync());
        Assert.Single(await files.Db.Organizations.ToListAsync());
        Assert.Empty(await files.Db.ProviderAccounts.ToListAsync());
        Assert.Empty(await files.Db.Apps.ToListAsync());
        Assert.Empty(await files.Db.BrowserSessions.ToListAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("https://foreign.example.test")]
    [InlineData("https://console.example.test/")]
    [InlineData("https://console.example.test.evil.test")]
    [InlineData("https://other-cors.example.test")]
    [InlineData("https://CONSOLE.example.test")]
    [InlineData("https://console.example.test, https://console.example.test")]
    public async Task CompletionRequiresExactlyConfiguredDashboardOrigin(string? origin)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        using var response = await Post(client, Form("synthetic-token"), origin);
        await Error(response, 403, "csrf_invalid");
        Assert.Empty(await files.Db.BootstrapRecords.ToListAsync());
        Assert.Equal(0, files.Identities.Creates);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("""{"setupToken":"synthetic-token"}""")]
    [InlineData("""{"setupToken":"a","email":"a","password":"a","displayName":"a","organizationName":"a","organizationSlug":null}""")]
    [InlineData("""{"setupToken":"a","email":"a","password":"a","displayName":"a","organizationName":"a","organizationName":"b"}""")]
    [InlineData("""{"setupToken":"a","email":"a","password":"a","displayName":"a","organizationName":"a","organizationSlug":"a","operationId":"foreign"}""")]
    public async Task InvalidJsonIsBoundedSanitizedAndNeverReachesAdmission(string json)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/setup");
        request.Headers.Add("Origin", Dashboard);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        await Error(response, 400, "setup_request_invalid");
        Assert.Empty(await files.Db.BootstrapRecords.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownAndUnknownLengthBodiesAreLimitedBeforeParsing(bool chunked)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/setup");
        request.Headers.Add("Origin", Dashboard);
        var body = new string('x', SetupController.BodyLimit + 1);
        request.Content = chunked ? new UnknownLengthContent(body) : new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        await Error(response, 413, "setup_request_invalid");
        Assert.Empty(await files.Db.BootstrapRecords.ToListAsync());
    }

    [Fact]
    public async Task HttpQueriesWrongMediaTypeAndBearerCannotBecomeSetupAdmission()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        using (var insecure = await Post(client, Form("synthetic-token"), path: "http://api.example.test/api/account/setup"))
        {
            await Error(insecure, 400, "setup_https_required");
            Assert.Null(insecure.Headers.Location);
        }
        using (var query = await Post(client, Form("synthetic-token"), path: "/api/account/setup?setupToken=synthetic-token"))
            await Error(query, 400, "setup_request_invalid");
        using (var query = await client.GetAsync("/api/account/setup?setupToken=synthetic-token"))
            await Error(query, 400, "setup_request_invalid");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/setup"))
        {
            request.Headers.Add("Origin", Dashboard);
            request.Content = new StringContent(JsonSerializer.Serialize(Form("synthetic-token")));
            using var media = await client.SendAsync(request);
            await Error(media, 400, "setup_request_invalid");
        }
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/setup"))
        {
            request.Headers.TryAddWithoutValidation("Origin", new[] { Dashboard, Dashboard });
            request.Content = JsonContent.Create(Form("synthetic-token"));
            using var duplicateOrigin = await client.SendAsync(request);
            await Error(duplicateOrigin, 403, "csrf_invalid");
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-machine-credential");
        using var bearer = await Post(client, Form("synthetic-token"));
        await Error(bearer, 401, "unauthenticated");
        Assert.Empty(await files.Db.BootstrapRecords.ToListAsync());
    }

    [Fact]
    public async Task SharedInvalidExpiredAndRotatedAdmissionErrorsRemainTypedAndNoStore()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        var token = new BootstrapServiceTests.TokenOutput();
        await files.Service.PrepareAsync(Installation, Operation, token);
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        using (var invalid = await Post(client, Form("incorrect")))
            await Error(invalid, 401, "setup_token_invalid");
        files.Clock.Now = files.Clock.Now.AddMinutes(30);
        using (var expired = await Post(client, Form(token.Token!)))
            await Error(expired, 410, "setup_token_expired");
        var old = token.Token!;
        await files.Service.RotateTokenAsync(Installation, Operation, token);
        using (var rotated = await Post(client, Form(old)))
            await Error(rotated, 401, "setup_token_invalid");
        Assert.Equal(0, files.Identities.Creates);
        Assert.Equal("required", (await files.Service.StatusAsync(Installation)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSharedOperationIsReportedWithoutInventingAnotherSeed(bool uncertain)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        var token = new BootstrapServiceTests.TokenOutput();
        await files.Service.PrepareAsync(Installation, Operation, token);
        files.Identities.FailAfterCreate = !uncertain;
        files.Identities.FailBeforeCreate = uncertain;
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        using (var interrupted = await Post(client, Form(token.Token!)))
            await Error(interrupted, 503, "auth_unavailable");
        using (var status = await client.GetAsync("/api/account/setup"))
            Assert.Equal("in_progress", (await status.Content.ReadFromJsonAsync<BootstrapStatusResponse>())!.State);
        files.Identities.FailBeforeCreate = false;
        using var retried = await Post(client, Form(token.Token!));
        if (uncertain) await Error(retried, 409, "setup_in_progress");
        else Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(1, files.Identities.Creates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid/installation")]
    [InlineData("foreign-installation")]
    public async Task MissingInvalidOrForeignInstallationIsBlockedAndCannotBeClaimed(string? installation)
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await files.Service.PrepareAsync(Installation, Operation, new BootstrapServiceTests.TokenOutput());
        await using var factory = new SetupFactory(files, installation);
        using var client = Client(factory);
        using var status = await client.GetAsync("/api/account/setup");
        AssertNoStore(status);
        Assert.Equal("blocked", (await status.Content.ReadFromJsonAsync<BootstrapStatusResponse>())!.State);
        using var complete = await Post(client, Form("synthetic-token"));
        await Error(complete, 409, "setup_state_conflict");
        Assert.Equal(0, files.Identities.Creates);
    }

    [Fact]
    public async Task PopulatedStateIsBlockedAndDependencyOutageIsSanitizedNotRequired()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        factory.Identities.Outage = true;
        using (var outage = await client.GetAsync("/api/account/setup"))
            await Error(outage, 503, "auth_unavailable");
        factory.Identities.Outage = false;
        files.Db.Users.Add(new User { Id = "existing-user", Email = "existing@example.test" });
        await files.Db.SaveChangesAsync();
        using var blocked = await client.GetAsync("/api/account/setup");
        Assert.Equal("blocked", (await blocked.Content.ReadFromJsonAsync<BootstrapStatusResponse>())!.State);
        using var complete = await Post(client, Form("synthetic-token"));
        await Error(complete, 409, "setup_state_conflict");
        Assert.Single(await files.Db.Users.ToListAsync());
        Assert.Equal(0, files.Identities.Creates);
    }

    [Fact]
    public async Task AdmissionHasBoundedGlobalRateBudgetAndStatusRemainsReadable()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var factory = new SetupFactory(files);
        using var client = Client(factory);
        for (var i = 0; i < 10; i++)
        {
            using var attempt = await Post(client, Form("synthetic-token"));
            await Error(attempt, 409, "setup_state_conflict");
        }
        using var limited = await Post(client, Form("synthetic-token"));
        await Error(limited, 429, "setup_rate_limited");
        Assert.NotNull(limited.Headers.RetryAfter);
        using var status = await client.GetAsync("/api/account/setup");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        AssertNoStore(status);
    }

    private static HttpClient Client(SetupFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://api.example.test"), HandleCookies = false, AllowAutoRedirect = false
    });

    private static Task<HttpResponseMessage> Post(HttpClient client, CompleteSetupRequest body,
        string? origin = Dashboard, string path = "/api/account/setup")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        return Send();
        async Task<HttpResponseMessage> Send()
        {
            using (request) return await client.SendAsync(request);
        }
    }

    private static async Task Error(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        AssertNoStore(response);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ErrorResponse>())!.Error.Code);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-token", body);
        Assert.DoesNotContain("Synthetic-password", body);
        Assert.DoesNotContain("private-upstream", body);
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly byte[] body;
        public UnknownLengthContent(string value)
        {
            body = Encoding.UTF8.GetBytes(value);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();
    }

    private sealed class SetupFactory(BootstrapServiceTests.Fixture files, string? installation = Installation)
        : WebApplicationFactory<Program>
    {
        public IdentityStore Identities { get; } = new(files.Identities);
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Provider"] = "Sqlite",
                    ["Database:RunMigrationsOnStartup"] = "false",
                    ["Database:PasswordFile"] = "",
                    ["ConnectionStrings:Minicloud"] = $"Data Source={Path.Combine(files.Directory, "state.db")};Pooling=False",
                    ["Bootstrap:InstallationId"] = installation,
                    ["Kratos:AdminBaseUrl"] = "http://synthetic-identity.invalid",
                    ["Kratos:DashboardOrigin"] = Dashboard,
                    ["Cors:AllowedOrigins:0"] = Dashboard,
                    ["Cors:AllowedOrigins:1"] = "https://other-cors.example.test",
                    ["Secrets:EncryptionKey"] = "synthetic-setup-encryption-key",
                    ["Secrets:EncryptionKeyFile"] = ""
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<DbContextOptions<MinicloudDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<MinicloudDbContext>>();
                services.AddDbContext<MinicloudDbContext>(options =>
                    options.UseSqlite($"Data Source={Path.Combine(files.Directory, "state.db")};Pooling=False"));
                services.RemoveAll<IBootstrapIdentityStore>();
                services.AddSingleton<IBootstrapIdentityStore>(Identities);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(files.Clock);
                services.PostConfigure<SecretEncryptionOptions>(options =>
                {
                    options.EncryptionKey = "synthetic-setup-encryption-key";
                    options.EncryptionKeyFile = null;
                });
            });
        }
    }

    private sealed class IdentityStore(BootstrapServiceTests.IdentityStore inner) : IBootstrapIdentityStore
    {
        public bool Outage { get; set; }
        public Task<bool> HasIdentitiesAsync(CancellationToken ct) => Outage
            ? throw new BootstrapException("auth_unavailable", 503, "private-upstream synthetic-token Synthetic-password")
            : inner.HasIdentitiesAsync(ct);
        public Task<BootstrapIdentity?> FindOperationAsync(string installationId, string operationId, CancellationToken ct) =>
            inner.FindOperationAsync(installationId, operationId, ct);
        public Task<BootstrapIdentity> CreateAsync(string installationId, string operationId, string email, string password, string displayName, CancellationToken ct) =>
            inner.CreateAsync(installationId, operationId, email, password, displayName, ct);
        public Task<BootstrapIdentity> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task SetPasswordAsync(string id, string password, CancellationToken ct) => inner.SetPasswordAsync(id, password, ct);
        public Task RevokeSessionsAsync(string id, CancellationToken ct) => inner.RevokeSessionsAsync(id, ct);
    }
}
