using Minicloud.Common.Data;

namespace Minicloud.Api.Providers;

public sealed class DatabaseConnectionStringProvider(IConfiguration configuration, IWebHostEnvironment environment) : IDatabaseConnectionStringProvider
{
    public string GetProviderName() =>
        configuration.GetValue("Database:Provider", environment.IsDevelopment() ? "Sqlite" : "Postgres");

    public string? GetConnectionString() =>
        configuration.GetConnectionString("Minicloud")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? MinicloudDatabase.NormalizeDatabaseUrl(Environment.GetEnvironmentVariable("DATABASE_URL"));
}
