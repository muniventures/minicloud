using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Minicloud.Common.Data;

public sealed class MinicloudDesignTimeDbContextFactory : IDesignTimeDbContextFactory<MinicloudDbContext>
{
    public MinicloudDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MinicloudDbContext>();
        var provider = Environment.GetEnvironmentVariable("Database__Provider") ?? "Postgres";
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Minicloud") ?? "Host=localhost;Port=55433;Database=minicloud_dev;Username=minicloud;Password=minicloud";

        if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            options.UseNpgsql(connectionString);
        }
        else
        {
            options.UseSqlite(connectionString);
        }

        return new MinicloudDbContext(options.Options);
    }
}
