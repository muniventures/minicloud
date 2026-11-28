using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Selfrun.Common.Data;

namespace Selfrun.Tests;

public sealed class MigrationConsistencyTests
{
    private static SelfrunDbContext CreateContext() => new(
        new DbContextOptionsBuilder<SelfrunDbContext>()
            .UseNpgsql("Host=localhost;Database=migration_validation")
            .Options);

    [Fact]
    public void PostgresModelMatchesMigrationSnapshot()
    {
        using var db = CreateContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void FreshPostgresMigrationIncludesPortalSessionsAndExcludesWorkspaceTables()
    {
        using var db = CreateContext();
        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Contains("ALTER TABLE browser_sessions ADD renewed_at", script);
        Assert.DoesNotContain("development_workspace", script);
    }

    [Fact]
    public void EnvironmentPowerPostgresUpgradeIsAdditiveAndBackfillsDisabledAwakeSettings()
    {
        using var db = CreateContext();
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261123163018_ServerHostKeyEnrollment";
        const string migration = "20261128131911_EnvironmentPowerPersistence";
        var upgrade = migrator.GenerateScript(previous, migration);
        Assert.Contains("sleep_after_minutes integer,", upgrade);
        Assert.Contains("http_traffic_is_activity boolean NOT NULL DEFAULT TRUE", upgrade);
        Assert.Contains("power_state text NOT NULL DEFAULT 'awake'", upgrade);
        Assert.Contains("SELECT id, 0, updated_at FROM apps;", upgrade);
        Assert.Contains("WHERE status NOT IN ('completed', 'failed', 'cancelled')", upgrade);
        Assert.Contains("WHERE status = 'current'", upgrade);
        Assert.Contains("ON environment_retained_addresses (provider_account_id, ipv4) WHERE state <> 'released'", upgrade);
        Assert.DoesNotContain("ALTER TABLE", upgrade);
        Assert.DoesNotContain("DROP TABLE", upgrade);
        Assert.Equal(4, migrator.GenerateScript(migration, previous).Split("DROP TABLE environment_").Length - 1);
    }

    [Fact]
    public void WakeQueueMigrationAllowsOneActiveOperationPerKind()
    {
        using var db = CreateContext();
        var upgrade = db.GetService<IMigrator>().GenerateScript(
            "20261128131911_EnvironmentPowerPersistence",
            "20261128144912_EnvironmentPowerWakeQueue");

        Assert.Contains("DROP INDEX ux_environment_power_operations_active", upgrade);
        Assert.Contains("CREATE UNIQUE INDEX ux_environment_power_operations_active_kind", upgrade);
        Assert.Contains("(environment_id, kind)", upgrade);
        Assert.Contains("WHERE status NOT IN ('completed', 'failed', 'cancelled')", upgrade);
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
