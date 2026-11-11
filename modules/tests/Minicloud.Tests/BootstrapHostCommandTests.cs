using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Services;

namespace Minicloud.Tests;

public sealed class BootstrapHostCommandTests
{
    [Fact]
    public async Task IndependentHostProcessesSerializeCompletionAndReplayWithoutSecrets()
    {
        await using var fixture = await BootstrapServiceTests.Fixture.CreateAsync();
        await using var upstream = new IdentityServer();
        fixture.Clock.Now = DateTimeOffset.UtcNow;
        var token = new BootstrapServiceTests.TokenOutput();
        await fixture.Service.PrepareAsync("installation-test", "operation-test", token);
        var input = new
        {
            installationId = "installation-test", operationId = "operation-test", setupToken = token.Token,
            email = "operator@example.test", password = "Synthetic-host-password-123!",
            displayName = "Operator", organizationName = "Workspace", organizationSlug = "workspace"
        };
        var results = await Task.WhenAll(
            RunAsync(fixture, upstream.Origin, ["complete", "--stdin"], input),
            RunAsync(fixture, upstream.Origin, ["complete", "--stdin"], input));
        Assert.All(results, result =>
        {
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.DoesNotContain(token.Token!, result.Output + result.Error);
            Assert.DoesNotContain("Synthetic-host-password", result.Output + result.Error);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal("ready", json.RootElement.GetProperty("state").GetString());
        });
        Assert.Equal(1, upstream.Creates);
        Assert.Equal(1, await fixture.Db.Users.CountAsync());
        Assert.Equal(1, await fixture.Db.Organizations.CountAsync());
        var calls = upstream.Calls;
        var replay = await RunAsync(fixture, upstream.Origin, ["complete", "--stdin"],
            new { installationId = "installation-test", operationId = "operation-test" });
        Assert.True(replay.ExitCode == 0, replay.Error);
        Assert.Equal(calls, upstream.Calls);
        Assert.Equal(results[0].Output, replay.Output);
    }

    [Fact]
    public async Task HostRejectsSecretArgumentsAndRedirectionWithoutProtectedOutput()
    {
        await using var fixture = await BootstrapServiceTests.Fixture.CreateAsync();
        var invalid = await RunAsync(fixture, "http://127.0.0.1:1",
            ["complete", "--password", "do-not-echo-this-secret"], new { });
        Assert.NotEqual(0, invalid.ExitCode);
        Assert.Empty(invalid.Output);
        Assert.DoesNotContain("do-not-echo", invalid.Error);
        Assert.Contains("setup_state_conflict", invalid.Error);
        var noOutput = await RunAsync(fixture, "http://127.0.0.1:1", ["prepare", "--stdin"],
            new { installationId = "installation-test", operationId = "operation-test" });
        Assert.NotEqual(0, noOutput.ExitCode);
        Assert.Empty(noOutput.Output);
        Assert.Empty(await fixture.Db.BootstrapRecords.ToListAsync());
        var malformed = await RunAsync(fixture, "http://127.0.0.1:1", ["complete", "--stdin"], "secret-invalid-json", raw: true);
        Assert.NotEqual(0, malformed.ExitCode);
        Assert.DoesNotContain("secret-invalid-json", malformed.Error);
        Assert.Contains("bootstrap_failed", malformed.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        BootstrapServiceTests.Fixture fixture, string origin, string[] args, object input, bool raw = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = fixture.Directory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(BootstrapCommand).Assembly.Location);
        start.ArgumentList.Add("bootstrap");
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["Database__Provider"] = "Sqlite";
        start.Environment["ConnectionStrings__Minicloud"] = $"Data Source={Path.Combine(fixture.Directory, "state.db")}";
        start.Environment["Kratos__AdminBaseUrl"] = origin;
        start.Environment.Remove("Database__PasswordFile");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(raw ? (string)input : JsonSerializer.Serialize(input));
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return (process.ExitCode, await output, await error);
    }

    private sealed class IdentityServer : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        private readonly Task loop;
        private object? identity;
        public int Creates { get; private set; }
        public int Calls { get; private set; }
        public string Origin { get; }
        public IdentityServer()
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Origin = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Origin + "/");
            listener.Start();
            loop = ServeAsync();
        }
        private async Task ServeAsync()
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    Calls++;
                    if (context.Request.HttpMethod == "POST")
                    {
                        using var request = await JsonDocument.ParseAsync(context.Request.InputStream);
                        var root = request.RootElement;
                        var marker = root.GetProperty("metadata_admin").Clone();
                        identity = new
                        {
                            id = Guid.NewGuid().ToString(), schema_id = "default", state = "active",
                            traits = root.GetProperty("traits").Clone(), metadata_admin = marker
                        };
                        Creates++;
                        await Task.Delay(500);
                        await RespondAsync(context, identity);
                    }
                    else
                        await RespondAsync(context, identity is null ? Array.Empty<object>() : new[] { identity });
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
        private static async Task RespondAsync(HttpListenerContext context, object body)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        public async ValueTask DisposeAsync()
        {
            listener.Close();
            await loop;
        }
    }
}
