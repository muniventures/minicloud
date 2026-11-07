using Microsoft.EntityFrameworkCore;

namespace Minicloud.Telemetry.Data;

public class TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : DbContext(options)
{
    public DbSet<TelemetryAuthorityCheckpoint> AuthorityCheckpoints => Set<TelemetryAuthorityCheckpoint>();
    public DbSet<TelemetryTenantBinding> TenantBindings => Set<TelemetryTenantBinding>();
    public DbSet<TelemetryCredentialReplica> CredentialReplicas => Set<TelemetryCredentialReplica>();
    public DbSet<TelemetryDailyUsage> DailyUsages => Set<TelemetryDailyUsage>();
    public DbSet<TelemetryGrafanaSession> GrafanaSessions => Set<TelemetryGrafanaSession>();
    public DbSet<TelemetryLaunchCode> LaunchCodes => Set<TelemetryLaunchCode>();
    public DbSet<TelemetryCleanupRecord> CleanupRecords => Set<TelemetryCleanupRecord>();
    public DbSet<TelemetryStatusEvent> StatusEvents => Set<TelemetryStatusEvent>();
    public DbSet<TelemetryDeploymentReplica> DeploymentReplicas => Set<TelemetryDeploymentReplica>();
    public DbSet<TelemetryDataSourceReplica> DataSourceReplicas => Set<TelemetryDataSourceReplica>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<TelemetryAuthorityCheckpoint>(b =>
        {
            b.ToTable("telemetry_authority_checkpoints");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Sequence);
        });

        modelBuilder.Entity<TelemetryTenantBinding>(b =>
        {
            b.ToTable("telemetry_tenant_bindings");
            b.HasKey(x => x.AppId);
            b.HasIndex(x => x.TenantId).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.AppId });
        });

        modelBuilder.Entity<TelemetryCredentialReplica>(b =>
        {
            b.ToTable("telemetry_credential_replicas");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.KeyPrefix);
            b.HasIndex(x => new { x.AppId, x.ServiceName });
        });

        modelBuilder.Entity<TelemetryDailyUsage>(b =>
        {
            b.ToTable("telemetry_daily_usages");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.AppId, x.Day }).IsUnique();
            b.HasIndex(x => new { x.OrganizationId, x.Day });
        });

        modelBuilder.Entity<TelemetryGrafanaSession>(b =>
        {
            b.ToTable("telemetry_grafana_sessions");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.SessionHash).IsUnique();
            b.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<TelemetryLaunchCode>(b =>
        {
            b.ToTable("telemetry_launch_codes");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.CodeHash).IsUnique();
            b.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<TelemetryCleanupRecord>(b =>
        {
            b.ToTable("telemetry_cleanup_records");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Status);
            b.HasIndex(x => x.AppId);
        });

        modelBuilder.Entity<TelemetryStatusEvent>(b =>
        {
            b.ToTable("telemetry_status_events");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Kind, x.TargetId });
        });

        modelBuilder.Entity<TelemetryDeploymentReplica>(b =>
        {
            b.ToTable("telemetry_deployment_replicas");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.AppId);
        });

        modelBuilder.Entity<TelemetryDataSourceReplica>(b =>
        {
            b.ToTable("telemetry_data_source_replicas");
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.AppId);
            b.HasIndex(x => x.KeyPrefix);
        });
    }
}
