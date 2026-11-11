using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class MaintenanceAuthorityPostgresTests
{
    private const string Installation = "inst-pg-maintenance";

    [Fact]
    [Trait("Category", "Docker")]
    public async Task FenceIsOrderedAgainstConcurrentAdmissionAndTransferRevokesOldEpoch()
    {
        await using var files = await BootstrapServiceTests.Fixture.CreateAsync();
        var container = "mc-maintenance-" + Guid.NewGuid().ToString("N");
        var passwordFile = Path.Combine(files.Directory, "postgres-password");
        var password = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(passwordFile, password);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var started = false;
        try
        {
            await Docker("run", "--rm", "--detach", "--pull", "never", "--name", container, "--log-driver", "none",
                "--mount", $"type=bind,source={passwordFile},target=/run/test-password,readonly",
                "-e", "POSTGRES_PASSWORD_FILE=/run/test-password", "-p", "127.0.0.1::5432", "postgres:16-alpine");
            started = true;
            var port = (await Docker("port", container, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>().UseNpgsql(
                $"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={password};Pooling=False").Options;
            await using (var db = new MinicloudDbContext(options))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    try { if (await db.Database.CanConnectAsync(timeout.Token)) break; }
                    catch (Npgsql.NpgsqlException) { }
                    await Task.Delay(200, timeout.Token);
                }
                await db.Database.MigrateAsync(timeout.Token);
                var now = DateTimeOffset.UtcNow;
                db.BootstrapRecords.Add(new BootstrapRecord { InstallationId = Installation, OperationId = "op-b", State = "ready", CreatedAt = now, UpdatedAt = now });
                await db.SaveChangesAsync();
            }
            var scopes = new ServiceCollection().AddScoped(_ => new MinicloudDbContext(options)).BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>();
            var participant = new MaintenanceParticipant(scopes, "api", null, TimeProvider.System);

            // Admission attempts race the fence; every admitted activity must be visible to drain-status
            // after the fence returns, and nothing can be admitted afterwards.
            using var stop = new CancellationTokenSource();
            var leases = new System.Collections.Concurrent.ConcurrentBag<MaintenanceActivityLease>();
            var refusals = 0;
            var contenders = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var (decision, lease) = await participant.TryBeginActivityAsync("api_write", null, requireOpen: true, CancellationToken.None);
                    if (lease is not null) leases.Add(lease);
                    else if (decision.Outcome == MaintenanceAdmissionOutcome.Fenced) { Interlocked.Increment(ref refusals); return; }
                    else Assert.Fail($"Unexpected admission outcome {decision.Outcome}");
                }
            })).ToArray();
            await Task.Delay(300);
            await using var operatorDb = new MinicloudDbContext(options);
            var service = new MaintenanceAuthorityService(operatorDb, TimeProvider.System, "host-a");
            var fenced = await service.FenceAsync(Installation, "op-race");
            var status = await service.DrainStatusAsync(Installation);
            await stop.CancelAsync();
            await Task.WhenAll(contenders);
            Assert.Equal("fenced", fenced.State);
            Assert.NotEmpty(leases);
            Assert.Equal(6, refusals);
            var active = status.Work.Single(x => x.Category == "admitted_activities" && x.State == "active");
            Assert.Equal(leases.Count, active.Count);
            // Drain status lists at most MaxListedIds ids (ordinal order); Count above covers the rest.
            Assert.Equal(
                leases.Select(x => x.Id).Order(StringComparer.Ordinal).Take(MaintenanceAuthorityService.MaxListedIds),
                active.Ids);
            Assert.False(status.Drained);
            foreach (var lease in leases) await lease.DisposeAsync();
            Assert.True((await service.DrainStatusAsync(Installation)).Drained);

            // Concurrent fences with different operation ids: exactly one holder wins.
            await service.ReleaseAsync(Installation, "op-race");
            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
            {
                await using var db = new MinicloudDbContext(options);
                try { await new MaintenanceAuthorityService(db, TimeProvider.System, "host-a").FenceAsync(Installation, $"op-c{i}"); return true; }
                catch (MaintenanceException ex) when (ex.Code == "maintenance_fence_held") { return false; }
            }));
            Assert.Single(attempts, x => x);
            var holder = (await service.StatusAsync(Installation)).HolderOperationId!;

            // Transfer bumps the durable epoch; the old process stays revoked even after the new authority releases.
            await using (var db = new MinicloudDbContext(options))
                Assert.True(await participant.CanClaimAsync(db, default));
            var transferred = await new MaintenanceAuthorityService(operatorDb, TimeProvider.System, "host-b").TransferAsync(Installation, "op-move", null);
            Assert.Equal(2L, transferred.Epoch);
            Assert.NotEqual("op-move", holder);
            await new MaintenanceAuthorityService(operatorDb, TimeProvider.System, "host-b").ReleaseAsync(Installation, "op-move");
            await using (var db = new MinicloudDbContext(options))
            {
                Assert.False(await participant.CanClaimAsync(db, default));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => participant.EnsureCommitAuthorityAsync(db, default));
            }
            var (revoked, none) = await participant.TryBeginActivityAsync("api_write", null, requireOpen: true, default);
            Assert.True(revoked.IsRevoked);
            Assert.Null(none);
        }
        finally
        {
            if (started)
                await Docker("rm", "--force", container);
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
