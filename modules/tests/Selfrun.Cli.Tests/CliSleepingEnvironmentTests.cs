// SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Selfrun.Cli;
using Selfrun.Cli.Api;
using Selfrun.Cli.Auth;
using Selfrun.Cli.Commands;

namespace Selfrun.Tests;

public sealed class CliSleepingEnvironmentTests
{
    [Theory]
    [InlineData("30m", 30)]
    [InlineData("72h", 4_320)]
    [InlineData("30d", 43_200)]
    public void Sleep_duration_parser_accepts_inclusive_bounds_and_units(string duration, int expectedMinutes) =>
        Assert.Equal(expectedMinutes, CliApplication.ParseSleepDurationMinutes(duration, 30, 43_200, "--sleep-after"));

    [Theory]
    [InlineData("29m")]
    [InlineData("31d")]
    [InlineData("0h")]
    [InlineData("2w")]
    [InlineData("1.5h")]
    public void Sleep_duration_parser_rejects_out_of_range_or_unknown_units(string duration) =>
        Assert.Throws<CliCommandException>(() =>
            CliApplication.ParseSleepDurationMinutes(duration, 30, 43_200, "--sleep-after"));

    [Theory]
    [InlineData("30m", 30)]
    [InlineData("24h", 1_440)]
    public void Keep_awake_duration_parser_accepts_inclusive_bounds(string duration, int expectedMinutes) =>
        Assert.Equal(expectedMinutes, CliApplication.ParseSleepDurationMinutes(duration, 30, 1_440, "--keep-awake"));

    [Theory]
    [InlineData("29m")]
    [InlineData("25h")]
    public void Keep_awake_duration_parser_rejects_values_outside_the_api_range(string duration) =>
        Assert.Throws<CliCommandException>(() =>
            CliApplication.ParseSleepDurationMinutes(duration, 30, 1_440, "--keep-awake"));

    [Fact]
    public async Task Sleep_only_update_enables_default_without_ssh_options()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "update", "staging", "--sleep-after", "on", "--config", fixture.Config],
            default);

        Assert.True(exitCode == CliExitCodes.Success, fixture.Console.Output + fixture.Console.Errors);
        Assert.Equal(HttpMethod.Patch, fixture.Handler.PatchMethod);
        Assert.Equal("/api/apps/application_demo/environments/environment_staging", fixture.Handler.PatchPath);
        var policy = fixture.Handler.PatchBody!.RootElement.GetProperty("sleep").GetProperty("policy");
        Assert.True(policy.GetProperty("enabled").GetBoolean());
        Assert.False(policy.TryGetProperty("sleepAfterMinutes", out _));
        Assert.Equal(5, policy.GetProperty("revision").GetInt64());
        Assert.Contains("Address mode: dynamic", fixture.Console.Output);
    }

    [Fact]
    public async Task Combined_sleep_and_ssh_update_preserves_both_revisions_and_settings()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "update", "staging", "--sleep-after", "4h", "--traffic-activity", "off", "--ssh-key", "key_new", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.Success, exitCode);
        var body = fixture.Handler.PatchBody!.RootElement;
        var policy = body.GetProperty("sleep").GetProperty("policy");
        Assert.True(policy.GetProperty("enabled").GetBoolean());
        Assert.Equal(240, policy.GetProperty("sleepAfterMinutes").GetInt32());
        Assert.False(policy.GetProperty("httpTrafficIsActivity").GetBoolean());
        Assert.Equal(5, policy.GetProperty("revision").GetInt64());
        Assert.Equal("key_new", body.GetProperty("sshAccess").GetProperty("keyId").GetString());
        Assert.Equal(7, body.GetProperty("sshAccess").GetProperty("expectedRevision").GetInt64());
    }

    [Fact]
    public async Task Manual_sleep_waits_and_reports_progress()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "sleep", "staging", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Sleep operation queued: operation-1", fixture.Console.Output);
        Assert.Contains("Sleep: Stop Services", fixture.Console.Output);
        Assert.Contains("Sleep: Create Snapshot", fixture.Console.Output);
        Assert.Contains("Environment 'staging' is asleep", fixture.Console.Output);
        Assert.Equal(3, fixture.Handler.PowerPollCount);
    }

    [Fact]
    public async Task Manual_wake_no_wait_returns_operation_and_keep_awake_minutes()
    {
        await using var fixture = await Fixture.CreateAsync();

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "wake", "staging", "--keep-awake", "4h", "--no-wait", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Wake operation queued: operation-1", fixture.Console.Output);
        Assert.Equal(0, fixture.Handler.PowerPollCount);
        Assert.Equal(240, fixture.Handler.WakeBody!.RootElement.GetProperty("keepAwakeMinutes").GetInt32());
    }

    [Fact]
    public async Task Manual_wake_waits_until_the_environment_is_awake()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.ListState = "awake";

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "wake", "staging", "--keep-awake", "30m", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Wake: Restore Server", fixture.Console.Output);
        Assert.Contains("Environment 'staging' is awake", fixture.Console.Output);
        Assert.Equal(3, fixture.Handler.PowerPollCount);
    }

    [Fact]
    public async Task Failed_sleep_reports_api_error_and_returns_deployment_failure()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.FailPowerOperation = true;

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "sleep", "staging", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.DeploymentFailed, exitCode);
        Assert.Contains("snapshot_failed", fixture.Console.Errors);
        Assert.Contains("snapshot creation failed", fixture.Console.Errors);
    }

    [Fact]
    public async Task Cancelled_sleep_reports_the_terminal_operation_instead_of_polling_forever()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.CancelPowerOperation = true;

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "sleep", "staging", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.DeploymentFailed, exitCode);
        Assert.Contains("operation_cancelled", fixture.Console.Errors);
        Assert.Equal(1, fixture.Handler.PowerPollCount);
    }

    [Fact]
    public async Task Retained_address_change_warns_to_repoint_custom_domains()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.AddressMode = "retained";
        fixture.Handler.PreviousIpv4 = "203.0.113.10";
        fixture.Handler.Ipv4 = "203.0.113.20";

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "update", "staging", "--sleep-after", "2h", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("Address mode: retained", fixture.Console.Output);
        Assert.Contains("203.0.113.10 to 203.0.113.20", fixture.Console.Errors);
        Assert.Contains("custom.example.com", fixture.Console.Errors);
        Assert.Contains("follow the server automatically", fixture.Console.Errors);
    }

    [Fact]
    public async Task Environment_list_includes_state_and_scheduled_sleep_time()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Handler.ListState = "awake";

        var exitCode = await fixture.Cli.RunAsync(
            ["environments", "list", "--config", fixture.Config],
            default);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Contains("STATE\tSLEEPS AT", fixture.Console.Output);
        Assert.Contains("staging\tp0\tsqlite\tawake\t2026-11-28 18:00 UTC", fixture.Console.Output);
    }

    private sealed class Fixture(DirectoryInfo directory, string config, Handler handler, TestConsole console, CliApplication cli) : IAsyncDisposable
    {
        public string Config { get; } = config;
        public Handler Handler { get; } = handler;
        public TestConsole Console { get; } = console;
        public CliApplication Cli { get; } = cli;

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Directory.CreateTempSubdirectory("sleeping-environment-");
            var config = Path.Combine(directory.FullName, "selfrun.yml");
            await File.WriteAllTextAsync(config, "app: demo\nappId: application_demo\ndatabase: sqlite\nservices:\n  web:\n    image: example/web:latest\n    port: 8080\n    public: true\n    path: /\n    healthPath: /health\n");
            var handler = new Handler();
            var console = new TestConsole();
            var environment = CliEnvironment.ForTests("https://api.example", directory.FullName);
            var tokens = new TokenStore(environment);
            tokens.SaveToken("test-token");
            return new Fixture(directory, config, handler, console,
                new CliApplication(console, environment, tokens, new SelfrunApiClient(environment, tokens, new HttpClient(handler))));
        }

        public ValueTask DisposeAsync()
        {
            directory.Delete(true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly object _sshAccess = new
        {
            mode = "existing",
            providerAccountId = "provider_1",
            keyId = "key_old",
            name = "Old key",
            fingerprint = "SHA256:fixture",
            keyType = "ed25519",
            revision = 7,
            registrationState = "ready",
            selectionLocked = false
        };
        private readonly DateTimeOffset _sleepTime = new(2026, 11, 28, 18, 0, 0, TimeSpan.Zero);
        private int _powerPollCount;
        private string? _powerKind;

        public JsonDocument? PatchBody { get; private set; }
        public string? PatchPath { get; private set; }
        public HttpMethod? PatchMethod { get; private set; }
        public JsonDocument? WakeBody { get; private set; }
        public int PowerPollCount => _powerPollCount;
        public bool FailPowerOperation { get; set; }
        public bool CancelPowerOperation { get; set; }
        public string AddressMode { get; set; } = "dynamic";
        public string Ipv4 { get; set; } = "203.0.113.10";
        public string? PreviousIpv4 { get; set; }
        public string ListState { get; set; } = "asleep";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            object response;
            if (path == "/api/me")
            {
                response = CliApiFixtures.Me("org");
            }
            else if (path == "/api/apps/application_demo")
            {
                response = new
                {
                    id = "application_demo",
                    organizationId = "org",
                    name = "Demo",
                    slug = "demo",
                    environments = new[] { CliApiFixtures.EnvironmentSummary("environment_staging", "staging") }
                };
            }
            else if (path == "/api/environments/environment_staging" && request.Method == HttpMethod.Get)
            {
                response = new
                {
                    id = "environment_staging",
                    applicationId = "application_demo",
                    organizationId = "org",
                    name = "staging",
                    slug = "staging",
                    plan = "p0",
                    database = "sqlite",
                    branches = Array.Empty<object>(),
                    domains = Array.Empty<object>(),
                    sshAccess = _sshAccess
                };
            }
            else if (path == "/api/apps/application_demo/environments/environment_staging" && request.Method == HttpMethod.Patch)
            {
                PatchMethod = request.Method;
                PatchPath = path;
                PatchBody = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                response = EnvironmentSleepResponse("awake", retainedAddress: AddressMode == "retained");
            }
            else if (path == "/api/apps/application_demo/environments/environment_staging" && request.Method == HttpMethod.Get)
            {
                _powerPollCount++;
                if (FailPowerOperation)
                    return Json(HttpStatusCode.OK, EnvironmentSleepResponse("sleep_failed", error: new { code = "snapshot_failed", message = "snapshot creation failed" }));
                if (CancelPowerOperation)
                    return Json(HttpStatusCode.OK, EnvironmentSleepResponse("awake", operationStatus: "cancelled"));

                var listState = ListState;
                if (_powerKind == "sleep" && _powerPollCount <= 2)
                    listState = "sleeping";
                else if (_powerKind == "sleep" && _powerPollCount > 2)
                    listState = "asleep";
                else if (_powerKind == "wake" && _powerPollCount <= 2)
                    listState = "waking";
                else if (_powerKind == "wake" && _powerPollCount > 2)
                    listState = "awake";
                response = EnvironmentSleepResponse(listState);
            }
            else if (path == "/api/apps/application_demo/environments/environment_staging/sleep" && request.Method == HttpMethod.Post)
            {
                _powerKind = "sleep";
                return Json(HttpStatusCode.Accepted, new { operation = Operation("sleep", "queued", "stop_services") });
            }
            else if (path == "/api/apps/application_demo/environments/environment_staging/wake" && request.Method == HttpMethod.Post)
            {
                _powerKind = "wake";
                if (request.Content is not null)
                    WakeBody = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                return Json(HttpStatusCode.Accepted, new { operation = Operation("wake", "queued", "restore_server") });
            }
            else if (path == "/api/domains" && request.Method == HttpMethod.Get)
            {
                response = new[]
                {
                    new
                    {
                        id = "domain_custom",
                        appId = "environment_staging",
                        serviceName = "web",
                        hostname = "custom.example.com",
                        kind = "custom_domain",
                        pathPrefix = "/",
                        dnsStatus = "verified",
                        applyStatus = "applied",
                        sslStatus = "ready",
                        status = "active",
                        isPrimary = false,
                        failureCode = (string?)null,
                        failureMessage = (string?)null,
                        lastAppliedAt = _sleepTime,
                        createdAt = _sleepTime,
                        updatedAt = _sleepTime
                    }
                };
            }
            else
            {
                throw new InvalidOperationException(request.Method + " " + path);
            }

            return Json(HttpStatusCode.OK, response);
        }

        private object EnvironmentSleepResponse(
            string state,
            bool retainedAddress = false,
            object? error = null,
            string? operationStatus = null)
        {
            var isPolling = _powerPollCount > 0;
            if (isPolling && state == "sleeping" && _powerPollCount > 2)
                state = "asleep";
            var operation = operationStatus is not null
                ? Operation(_powerKind ?? "sleep", operationStatus, "completed")
                : isPolling && state is "sleeping" or "waking"
                ? Operation(_powerKind ?? "sleep", "running", _powerKind == "wake" ? "restore_server" : _powerPollCount == 1 ? "stop_services" : "create_snapshot")
                : null;
            return new
            {
                id = "environment_staging",
                applicationId = "application_demo",
                name = "staging",
                sleep = new
                {
                    policy = new { sleepAfterMinutes = 120, httpTrafficIsActivity = true, revision = 5 },
                    state,
                    stateChangedAt = _sleepTime,
                    lastActivityAt = _sleepTime,
                    lastHttpRequestAt = _sleepTime,
                    keepAwakeUntil = (DateTimeOffset?)null,
                    sleepsAt = state == "awake" ? _sleepTime : (DateTimeOffset?)null,
                    eligible = true,
                    ineligibleReason = (string?)null,
                    addressMode = AddressMode,
                    retainedAddress = retainedAddress
                        ? new { ipv4 = Ipv4, state = "assigned", previousIpv4 = PreviousIpv4 }
                        : null,
                    snapshot = (object?)null,
                    operation,
                    lastError = error
                },
                sshAccess = _sshAccess
            };
        }

        private static object Operation(string kind, string status, string step) =>
            new { id = "operation-1", kind, status, step, trigger = "manual_cli" };

        private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
            new(status) { Content = JsonContent.Create(body) };
    }

    private sealed class TestConsole : IConsole
    {
        public bool SupportsAnsi => false;
        public string Output { get; private set; } = "";
        public string Errors { get; private set; } = "";
        public void Write(string message) => Output += message;
        public void WriteLine(string message = "") => Output += message + Environment.NewLine;
        public void WriteError(string message) => Errors += message + Environment.NewLine;
        public string? ReadLine() => null;
        public ConsoleKeyInfo ReadKey(bool intercept) => throw new InvalidOperationException();
    }
}
