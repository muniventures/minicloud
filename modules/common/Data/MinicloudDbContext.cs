using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;
using System.Text;

namespace Minicloud.Common.Data;

public sealed class MinicloudDbContext(DbContextOptions<MinicloudDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<BrowserSession> BrowserSessions => Set<BrowserSession>();
    public DbSet<BootstrapRecord> BootstrapRecords => Set<BootstrapRecord>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<MinicloudApplication> Applications => Set<MinicloudApplication>();
    public DbSet<MinicloudApp> Apps => Set<MinicloudApp>();
    public DbSet<AppSecret> AppSecrets => Set<AppSecret>();
    public DbSet<AppServiceSecret> AppServiceSecrets => Set<AppServiceSecret>();
    public DbSet<AppServiceSecretServiceDefinition> AppServiceSecretServices => Set<AppServiceSecretServiceDefinition>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<CliLoginSession> CliLoginSessions => Set<CliLoginSession>();
    public DbSet<AssetRelease> AssetReleases => Set<AssetRelease>();
    public DbSet<ImagePublication> ImagePublications => Set<ImagePublication>();
    public DbSet<RegistrySession> RegistrySessions => Set<RegistrySession>();
    public DbSet<RegistryUpload> RegistryUploads => Set<RegistryUpload>();
    public DbSet<RegistryGarbageCandidate> RegistryGarbageCandidates => Set<RegistryGarbageCandidate>();
    public DbSet<RegistryMaintenanceState> RegistryMaintenanceStates => Set<RegistryMaintenanceState>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<DeploymentService> DeploymentServices => Set<DeploymentService>();
    public DbSet<DeploymentEvent> DeploymentEvents => Set<DeploymentEvent>();
    public DbSet<DeploymentLog> DeploymentLogs => Set<DeploymentLog>();
    public DbSet<DeploymentArtifact> DeploymentArtifacts => Set<DeploymentArtifact>();
    public DbSet<ServerInstance> ServerInstances => Set<ServerInstance>();
    public DbSet<CustomDomainRegistration> CustomDomainRegistrations => Set<CustomDomainRegistration>();
    public DbSet<Domain> Domains => Set<Domain>();
    public DbSet<RuntimeServer> RuntimeServers => Set<RuntimeServer>();
    public DbSet<RuntimeToken> RuntimeTokens => Set<RuntimeToken>();
    public DbSet<RuntimeLogRecord> RuntimeLogRecords => Set<RuntimeLogRecord>();
    public DbSet<RuntimeStatusSnapshot> RuntimeStatusSnapshots => Set<RuntimeStatusSnapshot>();
    public DbSet<AppTelemetrySettings> AppTelemetrySettings => Set<AppTelemetrySettings>();
    public DbSet<TelemetryIngestionCredential> TelemetryIngestionCredentials => Set<TelemetryIngestionCredential>();
    public DbSet<GrafanaOrganizationBinding> GrafanaOrganizationBindings => Set<GrafanaOrganizationBinding>();
    public DbSet<GrafanaUserBinding> GrafanaUserBindings => Set<GrafanaUserBinding>();
    public DbSet<GrafanaDataSourceBinding> GrafanaDataSourceBindings => Set<GrafanaDataSourceBinding>();
    public DbSet<GrafanaLaunchCode> GrafanaLaunchCodes => Set<GrafanaLaunchCode>();
    public DbSet<GrafanaProxySession> GrafanaProxySessions => Set<GrafanaProxySession>();
    public DbSet<TelemetryCleanupJob> TelemetryCleanupJobs => Set<TelemetryCleanupJob>();
    public DbSet<TelemetryDailyUsage> TelemetryDailyUsages => Set<TelemetryDailyUsage>();
    public DbSet<TelemetryAuthorityOutbox> TelemetryAuthorityOutboxes => Set<TelemetryAuthorityOutbox>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<AppStorageVolume> AppStorageVolumes => Set<AppStorageVolume>();
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
    public DbSet<ProviderSshKey> ProviderSshKeys => Set<ProviderSshKey>();
    public DbSet<ProviderPlan> ProviderPlans => Set<ProviderPlan>();
    public DbSet<ProviderPlanName> ProviderPlanNames => Set<ProviderPlanName>();
    public DbSet<ProviderCredentialCleanup> ProviderCredentialCleanups => Set<ProviderCredentialCleanup>();

    public DbSet<ManagedDatabaseServer> ManagedDatabaseServers => Set<ManagedDatabaseServer>();
    public DbSet<ManagedDatabase> ManagedDatabases => Set<ManagedDatabase>();
    public DbSet<DatabaseBackupPolicy> DatabaseBackupPolicies => Set<DatabaseBackupPolicy>();
    public DbSet<DatabaseBackupArtifact> DatabaseBackupArtifacts => Set<DatabaseBackupArtifact>();
    public DbSet<DatabaseOperation> DatabaseOperations => Set<DatabaseOperation>();

    public DbSet<ManagedDatabaseBinding> ManagedDatabaseBindings => Set<ManagedDatabaseBinding>();
    public DbSet<ManagedDatabaseNetwork> ManagedDatabaseNetworks => Set<ManagedDatabaseNetwork>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagedDatabaseNetwork>(entity =>
        {
            entity.ToTable("managed_database_networks");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.ProviderAccountId, x.ProviderNetworkId }).IsUnique();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProviderAccount>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDatabaseServer>(entity =>
        {
            entity.ToTable("managed_database_servers");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.OrganizationId, x.Name }).IsUnique();
            entity.HasOne<ProviderPlan>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.PlanId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<ManagedDatabase>(entity =>
        {
            entity.ToTable("managed_databases");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.AppId).IsUnique();
            entity.HasIndex(x => new { x.ServerId, x.Name }).IsUnique();
            entity.HasOne<ManagedDatabaseNetwork>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.NetworkId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ManagedDatabaseServer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ServerId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<DatabaseBackupPolicy>(entity =>
        {
            entity.ToTable("database_backup_policies");
            entity.Property(x => x.Revision).HasDefaultValue(1L).IsConcurrencyToken();
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.DatabaseId).IsUnique();
        });
        modelBuilder.Entity<DatabaseBackupArtifact>(entity =>
        {
            entity.ToTable("database_backup_artifacts");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DatabaseOperation>(entity =>
        {
            entity.ToTable("database_operations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.OrganizationId);
            entity.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManagedDatabaseBinding>(entity =>
        {
            entity.ToTable("managed_database_bindings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasIndex(x => new { x.AppId, x.ServiceName }).IsUnique();
            entity.HasOne<ManagedDatabase>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.DatabaseId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<MinicloudApp>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.AppId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderAccount>(entity =>
        {
            entity.ToTable("provider_accounts");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.Property(x => x.ProviderType).HasConversion<string>();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.HasOne(x => x.Organization).WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderPlan>(entity =>
        {
            entity.ToTable("provider_plans");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.HasIndex(x => new { x.OrganizationId, x.NormalizedName }).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(128);
            entity.Property(x => x.NormalizedName).HasMaxLength(256);
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.ProviderAccount).WithMany(x => x.Plans)
                .HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderPlanName>(entity =>
        {
            entity.ToTable("provider_plan_names");
            entity.HasKey(x => new { x.OrganizationId, x.NormalizedName });
            entity.Property(x => x.NormalizedName).HasMaxLength(256);
            // No plan FK: claims intentionally outlive deleted plans.
        });
        modelBuilder.Entity<ProviderCredentialCleanup>(entity =>
        {
            entity.ToTable("provider_credential_cleanups");
            entity.HasKey(x => x.Id);
        });
        modelBuilder.Entity<ProviderSshKey>(entity =>
        {
            entity.ToTable("provider_ssh_keys");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.ProviderAccountId, x.Id });
            entity.HasIndex(x => new { x.OrganizationId, x.ProviderAccountId, x.ProviderKeyId }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.ProviderAccountId, x.Fingerprint }).IsUnique();
            entity.HasOne<ProviderAccount>().WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<MinicloudApp>().HasOne(x => x.SelectedPlan).WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.PlanId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MinicloudApp>().HasOne<ProviderAccount>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.SshProviderAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<MinicloudApp>().HasOne<ProviderSshKey>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.SshProviderAccountId, x.SshKeyId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.ProviderAccountId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Deployment>().HasOne<ProviderAccount>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ServerInstance>().HasOne<ProviderAccount>().WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AppStorageVolume>(entity =>
        {
            entity.ToTable("app_storage_volumes");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.AppId, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.ProviderAccountId, x.ProviderVolumeId }).IsUnique();
            entity.HasOne<ProviderAccount>().WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ImagePublication>(entity =>
        {
            entity.ToTable("image_publications");
            entity.HasIndex(x => new { x.AppId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.Repository, x.CandidateTag }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.AppId, x.ServiceName });
            entity.HasIndex(x => new { x.Status, x.ExpiresAt });
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RegistrySession>(entity =>
        {
            entity.ToTable("registry_sessions");
            entity.HasIndex(x => new { x.OrganizationId, x.AppId, x.ExpiresAt });
            entity.HasIndex(x => x.CredentialHash).IsUnique();
            entity.Property(x => x.Kind).HasConversion<string>();
            entity.Property(x => x.Revision).IsConcurrencyToken();
            entity.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ImagePublication).WithMany().HasForeignKey(x => x.ImagePublicationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Deployment).WithMany().HasForeignKey(x => x.DeploymentId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RegistryMaintenanceState>(entity =>
        {
            entity.ToTable("registry_maintenance_states");
            entity.Property(x => x.Revision).IsConcurrencyToken();
        });
        modelBuilder.Entity<RegistryUpload>(entity =>
        {
            entity.ToTable("registry_uploads");
            entity.HasOne(x => x.Session).WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<RegistryGarbageCandidate>(entity =>
        {
            entity.ToTable("registry_garbage_candidates");
            entity.HasIndex(x => new { x.Repository, x.ManifestDigest });
        });
        modelBuilder.Entity<DeploymentService>().HasOne(x => x.ImagePublication).WithMany()
            .HasForeignKey(x => x.ImagePublicationId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AssetRelease>(entity =>
        {
            entity.ToTable("asset_releases");
            entity.HasIndex(x => new { x.AppId, x.Name, x.InputSha256 });
            entity.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Artifact).WithMany().HasForeignKey(x => x.ArtifactId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Deployment>().HasOne(x => x.AssetRelease).WithMany().HasForeignKey(x => x.AssetReleaseId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasIndex(x => x.KratosIdentityId).IsUnique();
            entity.HasIndex(x => x.Email).IsUnique();
            entity.Property(x => x.KratosIdentityId).HasColumnName("kratos_identity_id");
            entity.Property(x => x.DisplayName).HasColumnName("display_name");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<BrowserSession>(entity =>
        {
            entity.ToTable("browser_sessions");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.UserId);
            entity.HasIndex(x => x.KratosSessionId).IsUnique();
            entity.HasIndex(x => new { x.UserId, x.RevokedAt, x.IdleExpiresAt });
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BootstrapRecord>(entity =>
        {
            entity.ToTable("bootstrap_records");
            entity.HasKey(x => x.InstallationId);
            entity.HasIndex(x => x.OperationId).IsUnique();
            entity.Property(x => x.State).HasMaxLength(32);
        });

        modelBuilder.Entity<Organization>(entity =>
        {
            entity.ToTable("organizations");
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<OrganizationMember>(entity =>
        {
            entity.ToTable("organization_members");
            entity.HasIndex(x => new { x.OrganizationId, x.UserId }).IsUnique();
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.UserId).HasColumnName("user_id");
            entity.Property(x => x.Role).HasConversion<string>().HasColumnName("role");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<MinicloudApplication>(entity =>
        {
            entity.ToTable("applications");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OrganizationId, x.Id });
            entity.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.NormalizedSlug }).IsUnique();
            entity.HasIndex(x => x.LegacyRootAppId).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(120);
            entity.Property(x => x.Slug).HasMaxLength(63);
            entity.Property(x => x.NormalizedSlug).HasMaxLength(63);
            entity.HasOne(x => x.Organization)
                .WithMany(x => x.Applications)
                .HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MinicloudApp>(entity =>
        {
            entity.ToTable("apps");
            entity.Property(x => x.SshAccessMode).HasDefaultValue("none");
            entity.Property(x => x.SshRegistrationState).HasDefaultValue("not_registered");
            entity.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique();
            entity.HasIndex(x => new { x.ParentAppId, x.BranchName }).IsUnique();
            entity.HasIndex(x => new { x.ApplicationId, x.NormalizedEnvironmentName }).IsUnique();
            entity.HasOne(x => x.Application)
                .WithMany(x => x.Environments)
                .HasForeignKey(x => new { x.OrganizationId, x.ApplicationId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ParentApp)
                .WithMany(x => x.Branches)
                .HasForeignKey(x => x.ParentAppId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.ApplicationId).HasColumnName("application_id");
            entity.Property(x => x.ParentAppId).HasColumnName("parent_app_id");
            entity.Property(x => x.BranchName).HasColumnName("branch_name");
            entity.Property(x => x.EnvironmentName).HasColumnName("environment_name").HasMaxLength(63);
            entity.Property(x => x.NormalizedEnvironmentName).HasColumnName("normalized_environment_name").HasMaxLength(63);
            entity.Property(x => x.EnvironmentKind).HasColumnName("environment_kind").HasMaxLength(32).HasDefaultValue(EnvironmentKinds.Persistent);
            entity.Property(x => x.RuntimeParentSlug).HasColumnName("runtime_parent_slug").HasMaxLength(63);
            entity.Property(x => x.SourceBranch).HasColumnName("source_branch").HasMaxLength(63);
            entity.Property(x => x.SourceEnvironmentId).HasColumnName("source_environment_id");
            entity.Property(x => x.EnvironmentKey).HasColumnName("environment_key");
            entity.HasIndex(x => x.EnvironmentKey).IsUnique();
            entity.Property(x => x.Plan).HasConversion<string>().HasColumnName("plan");
            entity.Property(x => x.Database).HasConversion<string>().HasColumnName("database");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<AppSecret>(entity =>
        {
            entity.ToTable("app_secrets");
            entity.HasIndex(x => new { x.AppId, x.Name }).IsUnique();
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.EncryptedValue).HasColumnName("encrypted_value");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<AppServiceSecret>(entity =>
        {
            entity.ToTable("app_service_secrets");
            entity.HasIndex(x => new { x.AppId, x.ServiceName, x.Name }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.AppId });
            entity.HasIndex(x => new { x.AppId, x.ServiceName });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.ServiceName).HasColumnName("service_name");
            entity.Property(x => x.StoreProvider).HasColumnName("store_provider");
            entity.Property(x => x.StorePath).HasColumnName("store_path");
            entity.Property(x => x.StoreVersion).HasColumnName("store_version");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

        modelBuilder.Entity<AppServiceSecretServiceDefinition>(entity =>
        {
            entity.ToTable("app_service_secret_services");
            entity.HasIndex(x => new { x.AppId, x.ServiceName }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.AppId });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.ServiceName).HasColumnName("service_name");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.HasIndex(x => x.KeyPrefix);
            entity.HasIndex(x => new { x.OrganizationId, x.RevokedAt });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.KeyPrefix).HasColumnName("key_prefix");
            entity.Property(x => x.KeyHash).HasColumnName("key_hash");
            entity.Property(x => x.ScopesJson).HasColumnName("scopes_json");
            entity.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<CliLoginSession>(entity =>
        {
            entity.ToTable("cli_login_sessions");
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.ExpiresAt);
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
            entity.Property(x => x.ScopesJson).HasColumnName("scopes_json");
            entity.Property(x => x.ApiKeyId).HasColumnName("api_key_id");
            entity.Property(x => x.RawApiKeyToken).HasColumnName("raw_api_key_token");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            entity.Property(x => x.ExchangedAt).HasColumnName("exchanged_at");
        });

        modelBuilder.Entity<Deployment>(entity =>
        {
            entity.ToTable("deployments");
            entity.Property(x => x.CancellationRequestedAt).HasColumnName("cancellation_requested_at").IsConcurrencyToken();
            entity.Property(x => x.CancellationRequestedBy).HasColumnName("cancellation_requested_by");
            entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            entity.HasIndex(x => new { x.AppId, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.HasIndex(x => x.OwnershipToken).IsUnique();
            entity.HasIndex(x => x.AppId)
                .IsUnique()
                .HasFilter("ownership_token IS NOT NULL");
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.Database).HasConversion<string>().HasColumnName("database");
            entity.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            entity.Property(x => x.WebsiteUrl).HasColumnName("website_url");
            entity.Property(x => x.GithubRunId).HasColumnName("github_run_id");
            entity.Property(x => x.GithubRunUrl).HasColumnName("github_run_url");
            entity.Property(x => x.CommitSha).HasColumnName("commit_sha");
            entity.Property(x => x.FailureCode).HasColumnName("failure_code");
            entity.Property(x => x.FailureMessage).HasColumnName("failure_message");
            entity.Property(x => x.WorkerId).HasColumnName("worker_id");
            entity.Property(x => x.OwnershipToken).HasColumnName("ownership_token");
            entity.Property(x => x.OwnershipAcquiredAt).HasColumnName("ownership_acquired_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<DeploymentService>(entity =>
        {
            entity.ToTable("deployment_services");
            entity.HasIndex(x => new { x.DeploymentId, x.Name }).IsUnique();
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.ArtifactId).HasColumnName("artifact_id");
            entity.Property(x => x.HealthPath).HasColumnName("health_path");
            entity.Property(x => x.EnvJson).HasColumnName("env_json");
            entity.Property(x => x.SecretEnvJson).HasColumnName("secret_env_json");
            entity.Property(x => x.Sha256).HasColumnName("sha256");
        });

        modelBuilder.Entity<DeploymentEvent>(entity =>
        {
            entity.ToTable("deployment_events");
            entity.HasIndex(x => new { x.DeploymentId, x.CreatedAt });
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.Level).HasConversion<string>().HasColumnName("level");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata_json");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<DeploymentLog>(entity =>
        {
            entity.ToTable("deployment_logs");
            entity.HasIndex(x => new { x.DeploymentId, x.CreatedAt });
            entity.Property(x => x.DeploymentId).HasColumnName("deployment_id");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<DeploymentArtifact>(entity =>
        {
            entity.ToTable("deployment_artifacts");
            entity.HasIndex(x => new { x.OrganizationId, x.AppId, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.ExpiresAt });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.ServiceName).HasColumnName("service_name");
            entity.Property(x => x.FileName).HasColumnName("file_name");
            entity.Property(x => x.ContentType).HasColumnName("content_type");
            entity.Property(x => x.ExpectedSizeBytes).HasColumnName("expected_size_bytes");
            entity.Property(x => x.ExpectedSha256).HasColumnName("expected_sha256");
            entity.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            entity.Property(x => x.Sha256).HasColumnName("sha256");
            entity.Property(x => x.ManifestJson).HasColumnName("manifest_json");
            entity.Property(x => x.StoragePath).HasColumnName("storage_path");
            entity.Property(x => x.Status).HasConversion<string>().HasColumnName("status");
            entity.Property(x => x.FailureCode).HasColumnName("failure_code");
            entity.Property(x => x.FailureMessage).HasColumnName("failure_message");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
            entity.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        });

        modelBuilder.Entity<ServerInstance>(entity =>
        {
            entity.ToTable("server_instances");
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.ProviderServerId).HasColumnName("provider_server_id");
            entity.Property(x => x.IpAddress).HasColumnName("ip_address");
            entity.Property(x => x.BootstrapStatus).HasColumnName("bootstrap_status");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<CustomDomainRegistration>(entity =>
        {
            entity.ToTable("custom_domain_registrations");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.HasOne(x => x.App).WithMany().HasForeignKey(x => x.AppId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.Hostname).IsUnique();
            entity.HasIndex(x => x.OrganizationId);
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.VerificationToken).HasColumnName("verification_token");
            entity.Property(x => x.LastCheckedAt).HasColumnName("last_checked_at");
            entity.Property(x => x.FailureMessage).HasColumnName("failure_message");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<Domain>(entity =>
        {
            entity.ToTable("domains");
            entity.Property(x => x.CustomDomainRegistrationId).HasColumnName("custom_domain_registration_id");
            entity.HasOne<CustomDomainRegistration>().WithMany().HasForeignKey(x => x.CustomDomainRegistrationId).OnDelete(DeleteBehavior.NoAction);
            entity.HasIndex(x => x.Hostname).IsUnique();
            entity.HasIndex(x => new { x.AppId, x.ServiceName });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.ServiceName).HasColumnName("service_name");
            entity.Property(x => x.PathPrefix).HasColumnName("path_prefix");
            entity.Property(x => x.VerificationToken).HasColumnName("verification_token");
            entity.Property(x => x.LastCheckedAt).HasColumnName("last_checked_at");
            entity.Property(x => x.TargetIp).HasColumnName("target_ip");
            entity.Property(x => x.DnsStatus).HasColumnName("dns_status");
            entity.Property(x => x.ApplyStatus).HasColumnName("apply_status");
            entity.Property(x => x.SslStatus).HasColumnName("ssl_status");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.IsPrimary).HasColumnName("is_primary");
            entity.Property(x => x.FailureCode).HasColumnName("failure_code");
            entity.Property(x => x.FailureMessage).HasColumnName("failure_message");
            entity.Property(x => x.LastAppliedAt).HasColumnName("last_applied_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<RuntimeServer>(entity =>
        {
            entity.ToTable("runtime_servers");
            entity.HasIndex(x => x.AppId);
            entity.HasIndex(x => new { x.ProviderAccountId, x.Provider, x.ProviderServerId });
            entity.HasOne<ProviderAccount>().WithMany()
                .HasForeignKey(x => new { x.OrganizationId, x.ProviderAccountId })
                .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.ProviderServerId).HasColumnName("provider_server_id");
            entity.Property(x => x.AgentVersion).HasColumnName("agent_version");
            entity.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<RuntimeToken>(entity =>
        {
            entity.ToTable("runtime_tokens");
            entity.HasIndex(x => x.TokenPrefix);
            entity.HasIndex(x => new { x.RuntimeServerId, x.RevokedAt });
            entity.Property(x => x.RuntimeServerId).HasColumnName("runtime_server_id");
            entity.Property(x => x.TokenPrefix).HasColumnName("token_prefix");
            entity.Property(x => x.TokenHash).HasColumnName("token_hash");
            entity.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<RuntimeLogRecord>(entity =>
        {
            entity.ToTable("runtime_log_records");
            entity.HasIndex(x => new { x.AppId, x.ObservedAt });
            entity.HasIndex(x => new { x.RuntimeServerId, x.Source, x.Cursor, x.ObservedAt }).IsUnique();
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.RuntimeServerId).HasColumnName("runtime_server_id");
            entity.Property(x => x.ObservedAt).HasColumnName("observed_at");
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
        });

        modelBuilder.Entity<RuntimeStatusSnapshot>(entity =>
        {
            entity.ToTable("runtime_status_snapshots");
            entity.HasIndex(x => new { x.AppId, x.ReceivedAt });
            entity.Property(x => x.OrganizationId).HasColumnName("organization_id");
            entity.Property(x => x.AppId).HasColumnName("app_id");
            entity.Property(x => x.RuntimeServerId).HasColumnName("runtime_server_id");
            entity.Property(x => x.AgentVersion).HasColumnName("agent_version");
            entity.Property(x => x.StatusJson).HasColumnName("status_json");
            entity.Property(x => x.ObservedAt).HasColumnName("observed_at");
            entity.Property(x => x.ReceivedAt).HasColumnName("received_at");
        });

        modelBuilder.Entity<AppTelemetrySettings>(entity =>
        {
            entity.ToTable("app_telemetry_settings");
            entity.HasIndex(x => x.TenantId).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.AppId }).IsUnique();
            entity.HasIndex(x => x.AppId).IsUnique();
        });

        modelBuilder.Entity<TelemetryIngestionCredential>(entity =>
        {
            entity.ToTable("telemetry_ingestion_credentials");
            entity.HasIndex(x => x.KeyPrefix);
            entity.HasIndex(x => new { x.AppId, x.ServiceName, x.RevokedAt });
            entity.HasIndex(x => new { x.OrganizationId, x.AppId });
        });

        modelBuilder.Entity<GrafanaOrganizationBinding>(entity =>
        {
            entity.ToTable("grafana_organization_bindings");
            entity.HasIndex(x => x.OrganizationId).IsUnique();
            entity.HasIndex(x => x.GrafanaOrgId).IsUnique();
        });

        modelBuilder.Entity<GrafanaUserBinding>(entity =>
        {
            entity.ToTable("grafana_user_bindings");
            entity.HasIndex(x => new { x.OrganizationId, x.MinicloudUserId }).IsUnique();
        });

        modelBuilder.Entity<GrafanaDataSourceBinding>(entity =>
        {
            entity.ToTable("grafana_data_source_bindings");
            entity.HasIndex(x => x.GrafanaUid).IsUnique();
            entity.HasIndex(x => new { x.AppId, x.DeletedAt });
        });

        modelBuilder.Entity<GrafanaLaunchCode>(entity =>
        {
            entity.ToTable("grafana_launch_codes");
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<GrafanaProxySession>(entity =>
        {
            entity.ToTable("grafana_proxy_sessions");
            entity.HasIndex(x => x.SessionHash).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.MinicloudUserId });
        });

        modelBuilder.Entity<TelemetryCleanupJob>(entity =>
        {
            entity.ToTable("telemetry_cleanup_jobs");
            entity.HasIndex(x => new { x.AppId, x.Status });
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<TelemetryDailyUsage>(entity =>
        {
            entity.ToTable("telemetry_daily_usages");
            entity.HasIndex(x => new { x.AppId, x.Day }).IsUnique();
            entity.HasIndex(x => new { x.OrganizationId, x.Day });
        });

        modelBuilder.Entity<TelemetryAuthorityOutbox>(entity =>
        {
            entity.ToTable("telemetry_authority_outboxes");
            entity.HasKey(x => x.Sequence);
            entity.HasIndex(x => x.ProcessedAt);
            entity.HasIndex(x => new { x.EventType, x.EntityId });
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
            entity.HasIndex(x => new { x.AppId, x.CreatedAt });
        });

        UseSnakeCaseColumns(modelBuilder);
    }

    private static void UseSnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (char.IsUpper(character))
            {
                if (i > 0)
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
