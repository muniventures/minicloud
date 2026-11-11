using Microsoft.Extensions.Configuration;
using Minicloud.Api.Services;
using Minicloud.Common.Data;

namespace Minicloud.Tests;

public sealed class DatabaseMigrationCommandTests
{
    [Fact]
    public void Migration_command_uses_configured_postgres_password_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-migration-password-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "synthetic-password\n");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Postgres",
                ["Database:PasswordFile"] = path,
                ["ConnectionStrings:Minicloud"] = "Host=postgres;Database=minicloud;Username=minicloud;Password=old",
                ["Database:RunMigrationsOnStartup"] = "false"
            }).Build();

            var options = DatabaseMigrationCommand.ResolveOptions(configuration, "Production");
            var connectionString = MinicloudDatabase.ResolveConnectionString(options);
            var parsed = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            Assert.Equal("postgres", parsed.Host);
            Assert.Equal("minicloud", parsed.Database);
            Assert.Equal("minicloud", parsed.Username);
            Assert.Equal("synthetic-password", parsed.Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Database_password_file_overrides_only_the_password_and_preserves_connection_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-db-password-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "p@ss;word\r\n");
            var result = MinicloudDatabase.ApplyPasswordFile(
                "Host=postgres;Database=minicloud;Username=minicloud;Password=old;Pooling=true",
                path);
            var parsed = new Npgsql.NpgsqlConnectionStringBuilder(result);
            Assert.Equal("postgres", parsed.Host);
            Assert.Equal("minicloud", parsed.Database);
            Assert.Equal("minicloud", parsed.Username);
            Assert.Equal("p@ss;word", parsed.Password);
            Assert.True(parsed.Pooling);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Configured_missing_or_empty_database_password_file_fails_explicitly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-db-password-{Guid.NewGuid():N}");
        Assert.Throws<FileNotFoundException>(() =>
            MinicloudDatabase.ApplyPasswordFile("Host=postgres;Database=minicloud", path));
        File.WriteAllText(path, "\r\n");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                MinicloudDatabase.ApplyPasswordFile("Host=postgres;Database=minicloud", path));
        }
        finally
        {
            File.Delete(path);
        }
    }

}
