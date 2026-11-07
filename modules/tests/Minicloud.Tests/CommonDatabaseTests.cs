using Minicloud.Common.Data;

namespace Minicloud.Tests;

public sealed class CommonDatabaseTests
{
    [Fact]
    public void NormalizePostgresUrlDefaultsPort()
    {
        Assert.Equal(
            "Host=db.example.com;Port=5432;Database=minicloud;Username=app;Password=secret",
            MinicloudDatabase.NormalizeDatabaseUrl("postgresql://app:secret@db.example.com/minicloud"));
    }

    [Fact]
    public void NormalizePostgresUrlKeepsExplicitPort()
    {
        Assert.Equal(
            "Host=db.example.com;Port=5433;Database=minicloud;Username=app;Password=secret",
            MinicloudDatabase.NormalizeDatabaseUrl("postgres://app:secret@db.example.com:5433/minicloud"));
    }

    [Fact]
    public void NormalizeSqliteUrl()
    {
        Assert.Equal(
            "Data Source=/tmp/minicloud.db",
            MinicloudDatabase.NormalizeDatabaseUrl("sqlite:///tmp/minicloud.db"));
    }

    [Fact]
    public void NormalizePassthroughAndNull()
    {
        Assert.Equal("Host=localhost;Database=x", MinicloudDatabase.NormalizeDatabaseUrl("Host=localhost;Database=x"));
        Assert.Null(MinicloudDatabase.NormalizeDatabaseUrl(null));
        Assert.Null(MinicloudDatabase.NormalizeDatabaseUrl("  "));
    }

    [Fact]
    public void ResolveConnectionStringPrefersExplicitKeysOverUrl()
    {
        var options = new MinicloudDatabaseOptions
        {
            MinicloudConnectionString = "Host=a",
            DefaultConnectionString = "Host=b",
            DatabaseConnectionString = "Host=c",
            DatabaseUrl = "postgresql://u:p@h/d"
        };
        Assert.Equal("Host=a", MinicloudDatabase.ResolveConnectionString(options));

        options.MinicloudConnectionString = null;
        Assert.Equal("Host=b", MinicloudDatabase.ResolveConnectionString(options));

        options.DefaultConnectionString = null;
        Assert.Equal("Host=c", MinicloudDatabase.ResolveConnectionString(options));

        options.DatabaseConnectionString = null;
        Assert.Equal(
            "Host=h;Port=5432;Database=d;Username=u;Password=p",
            MinicloudDatabase.ResolveConnectionString(options));
    }

    [Fact]
    public void ResolveConnectionStringFallsBackToLocalhost()
    {
        Assert.Equal(
            "Host=localhost;Database=minicloud;Username=minicloud;Password=minicloud",
            MinicloudDatabase.ResolveConnectionString(new MinicloudDatabaseOptions()));
    }
}
