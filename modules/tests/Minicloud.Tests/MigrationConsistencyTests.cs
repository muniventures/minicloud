using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Minicloud.Common.Data;

namespace Minicloud.Tests;

public sealed class MigrationConsistencyTests
{
    private static MinicloudDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MinicloudDbContext>()
            .UseNpgsql("Host=localhost;Database=migration_validation")
            .Options);

    [Fact]
    public void PostgresModelMatchesMigrationSnapshot()
    {
        using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void DomainMigrationCreatesColumnOnceAndReconciliationPreservesIt()
    {
        using var db = CreateContext();
        var migrator = db.GetService<IMigrator>();
        const string original = "20260914143000_AddDomainIsPrimary";
        const string repair = "20260918084933_ReconcileDomainModelSnapshot";
        var upgrade = migrator.GenerateScript("20260914121500_AddDeploymentServiceSha256", repair);
        Assert.Equal(1, upgrade.Split("ADD is_primary").Length - 1);
        var reconciliation = migrator.GenerateScript(original, repair);
        Assert.DoesNotContain("ALTER TABLE", reconciliation);
        var rollback = migrator.GenerateScript(repair, original);
        Assert.DoesNotContain("DROP COLUMN", rollback);
    }
}
