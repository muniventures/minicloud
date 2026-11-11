using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Minicloud.Api.Services;
using Minicloud.Common.Data;
using Npgsql;

namespace Minicloud.Tests;

// Exercises the packaged PostgreSQL initializer and one-shot API migration against a
// real disposable PostgreSQL volume with synthetic credentials.
public sealed class SelfHostingPostgresMigrationTests
{
    private static readonly string Repository = FindRepository();
    private const string AppPassword = "synthetic-app-password";

    [Fact]
    public async Task Fresh_concurrent_and_replayed_migrations_converge_and_existing_volume_is_preserved()
    {
        await using var postgres = await PostgresVolume.StartAsync();
        await postgres.InitializeDatabasesAsync();
        await postgres.InitializeDatabasesAsync();

        await Task.WhenAll(
            DatabaseMigrationCommand.RunAsync(postgres.Configuration("minicloud"), "Production"),
            DatabaseMigrationCommand.RunAsync(postgres.Configuration("minicloud"), "Production"));
        await DatabaseMigrationCommand.RunAsync(postgres.Configuration("minicloud"), "Production");

        await using (var db = postgres.Context("minicloud"))
        {
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO organizations(id,name,slug,created_at,updated_at) VALUES ('org_kept','Kept','kept',now(),now())");
        }

        await postgres.RecreateContainerAsync();
        await postgres.InitializeDatabasesAsync();
        await DatabaseMigrationCommand.RunAsync(postgres.Configuration("minicloud"), "Production");

        await using (var db = postgres.Context("minicloud"))
        {
            Assert.Equal("Kept", await db.Organizations.Where(x => x.Id == "org_kept").Select(x => x.Name).SingleAsync());
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        }
    }

    [Fact]
    public async Task Interrupted_upgrade_resumes_and_unknown_or_newer_state_is_rejected_without_mutation()
    {
        await using var postgres = await PostgresVolume.StartAsync();
        await postgres.InitializeDatabasesAsync();
        foreach (var database in new[] { "partial", "foreign_state", "newer" })
            await postgres.AdminSqlAsync($"CREATE DATABASE {database} OWNER minicloud");

        await using (var db = postgres.Context("partial"))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261109161756_DatabasePlatform");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO organizations(id,name,slug,created_at,updated_at) VALUES ('org_partial','Partial','partial',now(),now())");
        }
        await DatabaseMigrationCommand.RunAsync(postgres.Configuration("partial"), "Production");
        await using (var db = postgres.Context("partial"))
        {
            Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            Assert.True(await db.Organizations.AnyAsync(x => x.Id == "org_partial"));
        }

        await using (var db = postgres.Context("foreign_state"))
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE unrelated(id integer)");
        var foreign = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseMigrationCommand.RunAsync(postgres.Configuration("foreign_state"), "Production"));
        Assert.Contains("unknown populated state", foreign.Message);
        Assert.Equal("f", (await postgres.AdminSqlAsync(
            "SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL", "foreign_state")).Trim());

        await DatabaseMigrationCommand.RunAsync(postgres.Configuration("newer"), "Production");
        await using (var db = postgres.Context("newer"))
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"__EFMigrationsHistory\"(\"MigrationId\",\"ProductVersion\") VALUES ('99991231000000_FutureRelease','99.0.0')");
        var newer = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseMigrationCommand.RunAsync(postgres.Configuration("newer"), "Production"));
        Assert.Contains("99991231000000_FutureRelease", newer.Message);
    }

    [Fact]
    public async Task Initializer_refuses_existing_database_with_unexpected_owner()
    {
        await using var postgres = await PostgresVolume.StartAsync();
        await postgres.AdminSqlAsync("CREATE DATABASE minicloud;");
        var result = await postgres.TryInitializeDatabasesAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("unexpected owner", result.Error);
        Assert.DoesNotContain(AppPassword, result.Output + result.Error);
    }

    private sealed class PostgresVolume : IAsyncDisposable
    {
        private readonly string _name = "mc-selfhost-" + Guid.NewGuid().ToString("N")[..12];
        private readonly string _secrets = Path.Combine(Repository, ".local", "artifacts", "t41-postgres-tests", Guid.NewGuid().ToString("N"));
        private string _port = "";

        public static async Task<PostgresVolume> StartAsync()
        {
            var volume = new PostgresVolume();
            Directory.CreateDirectory(volume._secrets);
            await File.WriteAllTextAsync(Path.Combine(volume._secrets, "postgres-admin-password"), "synthetic-admin-password\n");
            await File.WriteAllTextAsync(Path.Combine(volume._secrets, "app-db-password"), AppPassword + "\n");
            await File.WriteAllTextAsync(Path.Combine(volume._secrets, "kratos-db-password"), "synthetic-kratos-password\n");
            await Docker("volume", "create", volume._name);
            await volume.StartContainerAsync();
            return volume;
        }

        public IConfiguration Configuration(string database) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Postgres",
            ["Database:PasswordFile"] = Path.Combine(_secrets, "app-db-password"),
            ["ConnectionStrings:Minicloud"] = $"Host=127.0.0.1;Port={_port};Database={database};Username=minicloud",
        }).Build();

        public MinicloudDbContext Context(string database) => new(new DbContextOptionsBuilder<MinicloudDbContext>()
            .UseNpgsql($"Host=127.0.0.1;Port={_port};Database={database};Username=minicloud;Password={AppPassword}").Options);

        public async Task InitializeDatabasesAsync()
        {
            var result = await TryInitializeDatabasesAsync();
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.DoesNotContain(AppPassword, result.Output + result.Error);
        }

        public async Task<ProcessResult> TryInitializeDatabasesAsync()
        {
            await Docker("exec", _name, "mkdir", "-p", "/run/secrets", "/opt/minicloud");
            foreach (var file in new[] { "postgres-admin-password", "app-db-password", "kratos-db-password" })
                await Docker("cp", Path.Combine(_secrets, file), $"{_name}:/run/secrets/{file}");
            await Docker("cp", Path.Combine(Repository, "packaging", "server", "bundle", "init-databases.sh"), $"{_name}:/opt/minicloud/init-databases.sh");
            return await Run("docker", "exec", "-e", "PGHOST=127.0.0.1", "-e", "PGUSER=postgres", "-e", "PGDATABASE=postgres",
                _name, "/bin/sh", "/opt/minicloud/init-databases.sh");
        }

        public async Task<string> AdminSqlAsync(string sql, string database = "postgres") =>
            await Docker("exec", _name, "psql", "-v", "ON_ERROR_STOP=1", "-tA", "-U", "postgres", "-d", database, "-c", sql);

        public async Task RecreateContainerAsync()
        {
            await Docker("rm", "-f", _name);
            await StartContainerAsync();
        }

        private async Task StartContainerAsync()
        {
            await Docker("run", "-d", "--name", _name, "-e", "POSTGRES_PASSWORD=synthetic-admin-password",
                "-v", $"{_name}:/var/lib/postgresql/data", "-p", "127.0.0.1::5432", "postgres:16-alpine");
            _port = (await Docker("port", _name, "5432/tcp")).Trim().Split('\n')[0].Split(':')[^1];
            for (var attempt = 0; attempt < 120; attempt++)
            {
                if ((await Run("docker", "exec", _name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres")).ExitCode == 0)
                {
                    try
                    {
                        await using var connection = new NpgsqlConnection(
                            $"Host=127.0.0.1;Port={_port};Database=postgres;Username=postgres;Password=synthetic-admin-password");
                        await connection.OpenAsync();
                        return;
                    }
                    catch (NpgsqlException) { }
                }
                await Task.Delay(250);
            }
            throw new TimeoutException("Synthetic PostgreSQL did not become ready.");
        }

        public async ValueTask DisposeAsync()
        {
            await Run("docker", "rm", "-f", _name);
            await Run("docker", "volume", "rm", "-f", _name);
            Directory.Delete(_secrets, recursive: true);
        }
    }

    public sealed record ProcessResult(int ExitCode, string Output, string Error);

    private static async Task<string> Docker(params string[] args)
    {
        var result = await Run("docker", args);
        Assert.True(result.ExitCode == 0, result.Error);
        return result.Output;
    }

    private static async Task<ProcessResult> Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Minicloud.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
