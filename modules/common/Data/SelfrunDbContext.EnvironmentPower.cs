using Microsoft.EntityFrameworkCore;
using Selfrun.Common.Models;

namespace Selfrun.Common.Data;

public sealed partial class SelfrunDbContext
{
    public DbSet<EnvironmentSleepSettings> EnvironmentSleepSettings => Set<EnvironmentSleepSettings>();
    public DbSet<EnvironmentPowerOperation> EnvironmentPowerOperations => Set<EnvironmentPowerOperation>();
    public DbSet<EnvironmentSnapshot> EnvironmentSnapshots => Set<EnvironmentSnapshot>();
    public DbSet<EnvironmentRetainedAddress> EnvironmentRetainedAddresses => Set<EnvironmentRetainedAddress>();

    private static void ConfigureEnvironmentPower(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnvironmentSleepSettings>(entity =>
        {
            entity.ToTable("environment_sleep_settings", table =>
            {
                table.HasCheckConstraint("ck_environment_sleep_timeout",
                    "sleep_after_minutes BETWEEN 30 AND 43200");
                table.HasCheckConstraint("ck_environment_sleep_address_mode",
                    $"address_mode IN ('{EnvironmentAddressModes.Dynamic}', '{EnvironmentAddressModes.Retained}')");
                table.HasCheckConstraint("ck_environment_sleep_power_state",
                    $"power_state IN ('{EnvironmentPowerStates.Awake}', '{EnvironmentPowerStates.Sleeping}', '{EnvironmentPowerStates.Asleep}', '{EnvironmentPowerStates.Waking}', '{EnvironmentPowerStates.SleepFailed}', '{EnvironmentPowerStates.WakeFailed}')");
            });
            entity.HasKey(x => x.EnvironmentId);
            entity.HasOne<SelfrunApp>().WithOne().HasForeignKey<EnvironmentSleepSettings>(x => x.EnvironmentId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(x => x.HttpTrafficIsActivity).HasDefaultValue(true);
            entity.Property(x => x.PowerState).HasDefaultValue(EnvironmentPowerStates.Awake);
            entity.Property(x => x.Revision).IsConcurrencyToken();
        });

        modelBuilder.Entity<EnvironmentPowerOperation>(entity =>
        {
            const string terminal = $"('{EnvironmentPowerOperationStatuses.Completed}', '{EnvironmentPowerOperationStatuses.Failed}', '{EnvironmentPowerOperationStatuses.Cancelled}')";
            entity.ToTable("environment_power_operations", table =>
            {
                table.HasCheckConstraint("ck_environment_power_operation_kind", "kind IN ('sleep', 'wake')");
                table.HasCheckConstraint("ck_environment_power_operation_status",
                    $"status IN ('{EnvironmentPowerOperationStatuses.Queued}', '{EnvironmentPowerOperationStatuses.Running}', '{EnvironmentPowerOperationStatuses.Cancelling}') OR status IN {terminal}");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OwnershipToken).IsConcurrencyToken();
            entity.HasOne<SelfrunApp>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EnvironmentId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.EnvironmentId, x.Kind }).IsUnique().HasFilter($"status NOT IN {terminal}")
                .HasDatabaseName("ux_environment_power_operations_active_kind");
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAt, x.UpdatedAt });
            entity.HasIndex(x => x.DeploymentId);
        });

        // Provider resources must remain discoverable for cleanup even after the environment is gone.
        modelBuilder.Entity<EnvironmentSnapshot>(entity =>
        {
            entity.ToTable("environment_snapshots", table =>
            {
                table.HasCheckConstraint("ck_environment_snapshot_status",
                    $"status IN ('{EnvironmentSnapshotStatuses.Creating}', '{EnvironmentSnapshotStatuses.Pending}', '{EnvironmentSnapshotStatuses.Current}', '{EnvironmentSnapshotStatuses.Superseded}', '{EnvironmentSnapshotStatuses.Deleting}', '{EnvironmentSnapshotStatuses.Deleted}', '{EnvironmentSnapshotStatuses.Failed}')");
                table.HasCheckConstraint("ck_environment_snapshot_current_image",
                    $"status <> '{EnvironmentSnapshotStatuses.Current}' OR provider_image_id IS NOT NULL");
                table.HasCheckConstraint("ck_environment_snapshot_address_mode",
                    $"address_mode IN ('{EnvironmentAddressModes.Dynamic}', '{EnvironmentAddressModes.Retained}')");
            });
            entity.HasKey(x => x.Id);
            entity.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.EnvironmentId, "CurrentSnapshot").IsUnique()
                .HasFilter($"status = '{EnvironmentSnapshotStatuses.Current}'")
                .HasDatabaseName("ux_environment_snapshots_current");
            entity.HasIndex(x => x.EnvironmentId);
        });

        modelBuilder.Entity<EnvironmentRetainedAddress>(entity =>
        {
            const string live = $"state <> '{EnvironmentRetainedAddressStates.Released}'";
            entity.ToTable("environment_retained_addresses", table => table.HasCheckConstraint("ck_environment_retained_address_state",
                $"state IN ('{EnvironmentRetainedAddressStates.Assigned}', '{EnvironmentRetainedAddressStates.Unassigned}', '{EnvironmentRetainedAddressStates.Releasing}', '{EnvironmentRetainedAddressStates.Released}', '{EnvironmentRetainedAddressStates.Failed}')"));
            entity.HasKey(x => x.Id);
            entity.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.EnvironmentId, "LiveAddress").IsUnique().HasFilter(live)
                .HasDatabaseName("ux_environment_retained_addresses_live");
            entity.HasIndex(x => x.EnvironmentId);
            entity.HasIndex(x => new { x.ProviderAccountId, x.ProviderAddressId }).IsUnique()
                .HasDatabaseName("ux_environment_retained_addresses_provider_id");
            entity.HasIndex(x => new { x.ProviderAccountId, x.Ipv4 }).IsUnique().HasFilter(live)
                .HasDatabaseName("ux_environment_retained_addresses_live_ipv4");
        });
    }
}
