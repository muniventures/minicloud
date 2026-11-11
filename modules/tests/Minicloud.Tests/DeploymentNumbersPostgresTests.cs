using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Tests;

public sealed class DeploymentNumberFormatTests
{
    [Theory]
    [InlineData("20260521.1", 2026, 5, 21, 1L)]
    [InlineData("20240229.42", 2024, 2, 29, 42L)]
    [InlineData("20260521.9223372036854775807", 2026, 5, 21, long.MaxValue)]
    public void Parses_canonical_numbers(string value, int year, int month, int day, long counter)
    {
        Assert.True(DeploymentNumber.TryParse(value, out var date, out var parsed));
        Assert.Equal((new DateOnly(year, month, day), counter), (date, parsed));
        Assert.Equal(value, DeploymentNumber.Format(date, parsed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dep_0123456789abcdef")]
    [InlineData("20260521")]
    [InlineData("20260521.")]
    [InlineData("20260521.0")]
    [InlineData("20260521.01")]
    [InlineData("20260521.-1")]
    [InlineData("20260521.+1")]
    [InlineData(" 20260521.1")]
    [InlineData("20260521.1 ")]
    [InlineData("2026-05-21.1")]
    [InlineData("20260230.1")]
    [InlineData("20261301.1")]
    [InlineData("2026052.11")]
    [InlineData("20260521.9223372036854775808")]
    [InlineData("20260521.10000000000000000000")]
    [InlineData("20260521.\u0661")]
    [InlineData("\u0662\u0660\u0662\u0666\u0660\u0665\u0662\u0661.1")]
    public void Rejects_non_canonical_numbers(string? value)
    {
        Assert.False(DeploymentNumber.TryParse(value, out _, out _));
        Assert.False(DeploymentNumber.IsValid(value));
    }
}

// Real PostgreSQL is required: SQLite cannot exercise row-lock serialization or the migration backfill SQL.
public sealed class DeploymentNumbersPostgresTests
{
    private const string BeforeNumbers = "20261111134441_MaintenanceAuthority";

    [Fact]
    public async Task BackfillNumbersExistingRowsDeterministicallyAndSeedsCounters()
    {
        await WithPostgres(async options =>
        {
            await using (var db = new MinicloudDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync(BeforeNumbers);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO organizations(id,name,slug,created_at,updated_at) VALUES ('org_a','A','a',now(),now()), ('org_b','B','b',now(),now());
                    INSERT INTO apps(id,organization_id,name,slug,plan,database,environment_name,created_at,updated_at)
                    VALUES ('app_a','org_a','A','a','P0H0','None','production',now(),now()), ('app_b','org_b','B','b','P0H0','None','production',now(),now());
                    """);
                // Ties sort by ordinal id; the +02:00 instant is 23:30 UTC on May 21, so it sorts before the 23:59:59 ties.
                foreach (var (id, org, created) in new[]
                {
                    ("dep_b", "org_a", "2026-05-21 23:59:59+00"),
                    ("dep_a", "org_a", "2026-05-21 23:59:59+00"),
                    ("dep_Z", "org_a", "2026-05-21 23:59:59+00"),
                    ("dep_early", "org_a", "2026-05-21 00:00:00+00"),
                    ("dep_next", "org_a", "2026-05-22 01:30:00+02"),
                    ("dep_midnight", "org_a", "2026-05-22 00:00:00+00"),
                    ("dep_other", "org_b", "2026-05-21 12:00:00+00"),
                })
                {
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO deployments (id, organization_id, app_id, database, status, created_at, updated_at) VALUES ({0}, {1}, {2}, 'None', 'Queued', {3}::timestamptz, {3}::timestamptz)",
                        id, org, org == "org_a" ? "app_a" : "app_b", created);
                }

                await db.Database.MigrateAsync();
            }

            await using (var db = new MinicloudDbContext(options))
            {
                var numbers = await db.Deployments.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Number);
                Assert.Equal(new Dictionary<string, string>
                {
                    ["dep_early"] = "20260521.1",
                    ["dep_next"] = "20260521.2",
                    ["dep_Z"] = "20260521.3",
                    ["dep_a"] = "20260521.4",
                    ["dep_b"] = "20260521.5",
                    ["dep_midnight"] = "20260522.1",
                    ["dep_other"] = "20260521.1",
                }, numbers);
                var counters = await db.DeploymentNumberCounters.AsNoTracking()
                    .OrderBy(x => x.OrganizationId).ThenBy(x => x.UtcDate)
                    .Select(x => new { x.OrganizationId, x.UtcDate, x.LastValue }).ToListAsync();
                Assert.Equal(
                    [("org_a", new DateOnly(2026, 5, 21), 5L), ("org_a", new DateOnly(2026, 5, 22), 1L), ("org_b", new DateOnly(2026, 5, 21), 1L)],
                    counters.Select(x => (x.OrganizationId, x.UtcDate, x.LastValue)));

                db.Deployments.Add(NewDeployment("dep_after", "org_a", "app_a", new DateTimeOffset(2026, 5, 21, 18, 0, 0, TimeSpan.Zero)));
                await db.SaveChangesAsync();
                Assert.Equal("20260521.6", await db.Deployments.Where(x => x.Id == "dep_after").Select(x => x.Number).SingleAsync());
            }
        });
    }

    [Fact]
    public async Task ConcurrentCreatesAllocateContiguousNumbersWithoutReuseAfterRollbackOrDelete()
    {
        await WithPostgres(async options =>
        {
            await using (var db = new MinicloudDbContext(options))
            {
                await db.Database.MigrateAsync();
                Assert.Empty(await db.DeploymentNumberCounters.ToListAsync());
                db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
                db.Apps.Add(new MinicloudApp { Id = "app", OrganizationId = "org", Name = "App", Slug = "app" });
                await db.SaveChangesAsync();
            }

            var day = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
            const int writers = 24;
            using var gate = new SemaphoreSlim(0);
            var creates = Enumerable.Range(0, writers).Select(index => Task.Run(async () =>
            {
                await gate.WaitAsync();
                await using var db = new MinicloudDbContext(options);
                db.Deployments.Add(NewDeployment($"dep_{index:D2}", "org", "app", day));
                await db.SaveChangesAsync();
            })).ToList();
            gate.Release(writers);
            await Task.WhenAll(creates);

            await using (var db = new MinicloudDbContext(options))
            {
                var numbers = await db.Deployments.Select(x => x.Number).ToListAsync();
                Assert.Equal(Enumerable.Range(1, writers).Select(n => $"20260701.{n}").Order(StringComparer.Ordinal),
                    numbers.Order(StringComparer.Ordinal));

                await using (var transaction = await db.Database.BeginTransactionAsync())
                {
                    db.Deployments.Add(NewDeployment("dep_rolled_back", "org", "app", day));
                    await db.SaveChangesAsync();
                    await transaction.RollbackAsync();
                }
                db.ChangeTracker.Clear();

                await db.Deployments.Where(x => x.Number == $"20260701.{writers}").ExecuteDeleteAsync();
                db.Deployments.Add(NewDeployment("dep_after_delete", "org", "app", day));
                await db.SaveChangesAsync();
                Assert.Equal($"20260701.{writers + 1}", await db.Deployments.Where(x => x.Id == "dep_after_delete").Select(x => x.Number).SingleAsync());

                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE deployment_number_counters SET last_value = 9223372036854775807 WHERE organization_id = 'org'");
                db.Deployments.Add(NewDeployment("dep_exhausted", "org", "app", day));
                await Assert.ThrowsAsync<DeploymentNumberExhaustedException>(() => db.SaveChangesAsync());
                Assert.False(await db.Deployments.AnyAsync(x => x.Id == "dep_exhausted"));
            }
        });
    }

    private static Deployment NewDeployment(string id, string organizationId, string appId, DateTimeOffset createdAt) => new()
    {
        Id = id, OrganizationId = organizationId, AppId = appId, Status = DeploymentStatus.Queued,
        CreatedAt = createdAt, UpdatedAt = createdAt,
    };

    private static async Task WithPostgres(Func<DbContextOptions<MinicloudDbContext>, Task> test)
    {
        var container = "mc-numbers-" + Guid.NewGuid().ToString("N");
        try
        {
            await Docker("run", "--rm", "-d", "--name", container, "-e", "POSTGRES_PASSWORD=" + container,
                "-p", "127.0.0.1::5432", "postgres:16-alpine");
            var port = (await Docker("port", container, "5432/tcp")).Trim().Split(':')[^1];
            var options = new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseNpgsql($"Host=127.0.0.1;Port={port};Database=postgres;Username=postgres;Password={container}").Options;
            var ready = false;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await using var probe = new MinicloudDbContext(options);
                if (await probe.Database.CanConnectAsync()) { ready = true; break; }
                await Task.Delay(500);
            }
            Assert.True(ready, "Isolated PostgreSQL did not become ready.");
            await test(options);
        }
        finally { await Docker("rm", "-f", container); }
    }

    private static async Task<string> Docker(params string[] arguments)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }
}
