using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class BootstrapPostgresTests
{
    [Fact]
    [Trait("Category", "Docker")]
    public async Task AdvisoryLockSurvivesPersistenceAndSerializesSeedAndRecoveryAcrossConnections()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        var container = "mc-bootstrap-" + Guid.NewGuid().ToString("N");
        var data = Path.Combine(files.Directory, "postgres-data");
        Directory.CreateDirectory(data);
        var passwordFile = Path.Combine(files.Directory, "postgres-password");
        var password = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(passwordFile, password);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var started = false;
        try
        {
            await Docker("run", "--rm", "--detach", "--pull", "never", "--name", container,
                "--log-driver", "none", "--mount", $"type=bind,source={data},target=/var/lib/postgresql/data",
                "--mount", $"type=bind,source={passwordFile},target=/run/test-password,readonly",
                "-e", "POSTGRES_PASSWORD_FILE=/run/test-password", "-p", "127.0.0.1::5432", "postgres:16-alpine");
            started = true;
            var port = (await Docker("port", container, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>().UseNpgsql(
                $"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={password};Pooling=False").Options;
            await using var firstDb = new MinicloudDbContext(options);
            await using var secondDb = new MinicloudDbContext(options);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!await firstDb.Database.CanConnectAsync(timeout.Token))
                await Task.Delay(100, timeout.Token);
            await firstDb.Database.EnsureCreatedAsync(timeout.Token);
            var identities = new BootstrapServiceTests.IdentityStore();
            var first = new BootstrapService(firstDb, identities, new IdGenerator(), TimeProvider.System);
            var second = new BootstrapService(secondDb, identities, new IdGenerator(), TimeProvider.System);
            var output = new BootstrapServiceTests.TokenOutput();
            await first.PrepareAsync("pg-installation", "pg-operation", output);
            identities.BeforeCreate = async () =>
            {
                await using var observer = new MinicloudDbContext(options);
                var persisted = await observer.BootstrapRecords.SingleAsync();
                Assert.Equal("in_progress", persisted.State);
                Assert.Equal("pg-operation", persisted.OperationId);
                await Task.Delay(200);
            };
            var request = new CompleteSetupRequest(output.Token!, "operator@example.test",
                "Synthetic-postgres-password!", "Operator", "Workspace", "workspace");
            var results = await Task.WhenAll(
                first.CompleteHostAsync("pg-installation", "pg-operation", request),
                second.CompleteHostAsync("pg-installation", "pg-operation", request));
            Assert.Equal(results[0], results[1]);
            Assert.Equal(1, identities.Creates);
            Assert.Equal(1, await firstDb.Users.CountAsync());
            Assert.Equal(1, await firstDb.Organizations.CountAsync());
            Assert.Equal(1, await firstDb.OrganizationMembers.CountAsync());
            var userId = results[0].UserId!;
            var now = DateTimeOffset.UtcNow;
            firstDb.BrowserSessions.Add(new BrowserSession
            {
                Id = "pg-session", UserId = userId, KratosIdentityId = identities.Identity!.Id,
                KratosSessionId = "pg-upstream", TokenHash = "synthetic-hash", CsrfTokenHash = "synthetic-csrf",
                CreatedAt = now, LastSeenAt = now, ExpiresAt = now.AddHours(1), IdleExpiresAt = now.AddHours(1)
            });
            await firstDb.SaveChangesAsync();
            await second.RecoverOperatorAsync("pg-installation", "pg-operation", userId,
                identities.Identity.Id, "Synthetic-recovery-password!");
            Assert.NotNull((await firstDb.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
            Assert.Equal(userId, (await firstDb.Users.SingleAsync()).Id);
            await firstDb.BootstrapRecords.ExecuteDeleteAsync();
            Assert.Equal("blocked", (await first.StatusAsync("pg-installation")).State);
            var conflict = await Assert.ThrowsAsync<BootstrapException>(() =>
                first.PrepareAsync("pg-installation", "new-operation", output));
            Assert.Equal("setup_state_conflict", conflict.Code);
        }
        finally
        {
            if (started)
            {
                // Only this test's named container and bind-mounted synthetic data.
                await Docker("exec", "--user", "0", container, "chmod", "-R", "a+rwX", "/var/lib/postgresql/data");
                await Docker("rm", "--force", container);
            }
        }
    }

    private static async Task<string> Docker(params string[] args)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
