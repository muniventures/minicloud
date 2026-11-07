using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Minicloud.Common.Data;

/// <summary>
/// Explicit database options. Each host supplies its intentional defaults;
/// Common never guesses a provider or connection string on its own.
/// Inventoried host differences preserved here:
/// - API defaults to Sqlite in Development, Postgres otherwise; the worker
///   always defaults to Postgres.
/// - API reads Minicloud, DefaultConnection, then DATABASE_URL. The worker
///   additionally honors the legacy Database key between DefaultConnection
///   and DATABASE_URL.
/// - Only the API understood sqlite:// URLs; only the worker defaulted an
///   unspecified postgres port to 5432. The canonical normalizer does both;
///   hosts opt in by using it.
/// </summary>
public sealed class MinicloudDatabaseOptions
{
    public string Provider { get; set; } = "Postgres";

    public string? MinicloudConnectionString { get; set; }

    public string? DefaultConnectionString { get; set; }

    public string? DatabaseConnectionString { get; set; }

    public string? DatabaseUrl { get; set; }

    public string FallbackConnectionString { get; set; } =
        "Host=localhost;Database=minicloud;Username=minicloud;Password=minicloud";
}

public static class MinicloudDatabase
{
    public static string ResolveConnectionString(MinicloudDatabaseOptions options) =>
        options.MinicloudConnectionString
        ?? options.DefaultConnectionString
        ?? options.DatabaseConnectionString
        ?? NormalizeDatabaseUrl(options.DatabaseUrl)
        ?? options.FallbackConnectionString;

    public static string? NormalizeDatabaseUrl(string? databaseUrl)
    {
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            return null;
        }

        if (databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) ||
            databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(databaseUrl);
            var userInfo = uri.UserInfo.Split(':', 2);
            var username = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(0) ?? "");
            var password = Uri.UnescapeDataString(userInfo.ElementAtOrDefault(1) ?? "");
            var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            var port = uri.Port > 0 ? uri.Port : 5432;
            return $"Host={uri.Host};Port={port};Database={database};Username={username};Password={password}";
        }

        if (databaseUrl.StartsWith("sqlite://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(databaseUrl);
            return $"Data Source={uri.AbsolutePath}";
        }

        return databaseUrl;
    }

    public static void ConfigureDbContext(DbContextOptionsBuilder options, string provider, string connectionString)
    {
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(SqliteDatabaseConfigurator.ConfigureConnectionString(connectionString));
        }
        else
        {
            options.UseNpgsql(connectionString);
        }
    }

    public static IServiceCollection AddMinicloudDbContext(this IServiceCollection services, MinicloudDatabaseOptions options)
    {
        var provider = options.Provider;
        var connectionString = ResolveConnectionString(options);
        services.AddDbContext<MinicloudDbContext>(
            (_, builder) => ConfigureDbContext(builder, provider, connectionString));
        return services;
    }
}
