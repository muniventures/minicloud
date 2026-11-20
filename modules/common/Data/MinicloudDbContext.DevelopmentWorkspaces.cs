using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Minicloud.Common.Models;

namespace Minicloud.Common.Data;

public sealed partial class MinicloudDbContext
{
    public DbSet<DevelopmentWorkspace> DevelopmentWorkspaces => Set<DevelopmentWorkspace>();
    public DbSet<DevelopmentWorkspaceVm> DevelopmentWorkspaceVms => Set<DevelopmentWorkspaceVm>();
    public DbSet<DevelopmentWorkspaceOperation> DevelopmentWorkspaceOperations => Set<DevelopmentWorkspaceOperation>();
    public DbSet<DevelopmentWorkspaceTaskLease> DevelopmentWorkspaceTaskLeases => Set<DevelopmentWorkspaceTaskLease>();
    public DbSet<DevelopmentWorkspaceFilesystemJob> DevelopmentWorkspaceFilesystemJobs => Set<DevelopmentWorkspaceFilesystemJob>();
    public DbSet<DevelopmentWorkspaceVmResourceSample> DevelopmentWorkspaceVmResourceSamples => Set<DevelopmentWorkspaceVmResourceSample>();

    private static void ConfigureDevelopmentWorkspaces(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DevelopmentWorkspace>(entity =>
        {
            entity.ToTable("development_workspaces");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique().HasFilter("volume_status <> 'deleted'");
            entity.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey }).IsUnique().HasFilter("volume_status <> 'deleted'");
            entity.HasIndex(x => new { x.ProviderAccountId, x.ProviderVolumeId }).IsUnique();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.PrimaryVmId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(x => x.ProviderAccountId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(x => x.Region).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        });
        modelBuilder.Entity<DevelopmentWorkspaceVm>(entity =>
        {
            entity.ToTable("development_workspace_vms", table =>
                table.HasCheckConstraint("ck_development_vm_kind", "kind IN ('primary', 'ephemeral')"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
            entity.HasIndex(x => new { x.WorkspaceId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => x.WorkspaceId).IsUnique().HasFilter("kind = 'primary'");
            entity.HasIndex(x => new { x.ProviderAccountId, x.ProviderServerId }).IsUnique();
            entity.HasIndex(x => new { x.Repository, x.WorkBranch }).IsUnique().HasFilter("kind = 'ephemeral'");
            entity.HasIndex(x => new { x.DesiredState, x.Phase, x.ShutdownDeadline });
            entity.Property(x => x.Kind).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<DevelopmentWorkspace>().WithMany(x => x.Vms)
                .HasForeignKey(x => new { x.OrganizationId, x.WorkspaceId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderPlan>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PlanId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DevelopmentWorkspaceOperation>(entity =>
        {
            entity.ToTable("development_workspace_operations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
            entity.HasIndex(x => new { x.VmId, x.Generation, x.Kind });
            entity.Property(x => x.Revision).IsConcurrencyToken();
            foreach (var name in new[] { nameof(DevelopmentWorkspaceOperation.WorkspaceId), nameof(DevelopmentWorkspaceOperation.VmId),
                nameof(DevelopmentWorkspaceOperation.Generation), nameof(DevelopmentWorkspaceOperation.Kind),
                nameof(DevelopmentWorkspaceOperation.PlanId), nameof(DevelopmentWorkspaceOperation.ProviderAccountId),
                nameof(DevelopmentWorkspaceOperation.Region), nameof(DevelopmentWorkspaceOperation.Sku),
                nameof(DevelopmentWorkspaceOperation.ImageId), nameof(DevelopmentWorkspaceOperation.ProviderImage),
                nameof(DevelopmentWorkspaceOperation.ImageManifestSha256), nameof(DevelopmentWorkspaceOperation.ComputeHourlyRate) })
                entity.Property(name).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.HasOne<DevelopmentWorkspaceVm>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.VmId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DevelopmentWorkspaceTaskLease>(entity =>
        {
            entity.ToTable("development_workspace_task_leases");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.VmId, x.Generation, x.TaskId }).IsUnique();
            entity.HasIndex(x => new { x.VmId, x.Generation, x.Status });
            entity.HasOne<DevelopmentWorkspaceVm>().WithMany(x => x.TaskLeases).HasForeignKey(x => new { x.WorkspaceId, x.VmId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DevelopmentWorkspaceFilesystemJob>(entity =>
        {
            entity.ToTable("development_workspace_filesystem_jobs", table =>
                table.HasCheckConstraint("ck_development_filesystem_job_kind", "kind IN ('disk_usage', 'directory_deletion')"));
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.VmId, x.Kind, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.State, x.CreatedAt });
            entity.HasIndex(x => new { x.VmId, x.State });
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128);
            foreach (var name in new[] { nameof(DevelopmentWorkspaceFilesystemJob.OrganizationId),
                nameof(DevelopmentWorkspaceFilesystemJob.WorkspaceId), nameof(DevelopmentWorkspaceFilesystemJob.VmId),
                nameof(DevelopmentWorkspaceFilesystemJob.Generation), nameof(DevelopmentWorkspaceFilesystemJob.Kind),
                nameof(DevelopmentWorkspaceFilesystemJob.PathJson), nameof(DevelopmentWorkspaceFilesystemJob.TargetDevice),
                nameof(DevelopmentWorkspaceFilesystemJob.TargetInode) })
                entity.Property(name).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
            entity.HasOne<DevelopmentWorkspaceVm>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.VmId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DevelopmentWorkspaceVmResourceSample>(entity =>
        {
            entity.ToTable("development_workspace_vm_resource_samples");
            entity.HasKey(x => new { x.VmId, x.Generation, x.Minute });
            entity.HasIndex(x => x.ExpiresAt);
            entity.HasIndex(x => new { x.WorkspaceId, x.VmId });
            entity.HasOne<DevelopmentWorkspaceVm>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.VmId })
                .HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
