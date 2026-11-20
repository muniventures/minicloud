namespace Minicloud.Common.Models;

public sealed class DevelopmentWorkspace
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string CreatedBy { get; set; } = default!;
    public string Name { get; set; } = default!;
    public int? IdleTimeoutMinutes { get; set; }
    public bool KeepRunning { get; set; }
    public string PrimaryVmId { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string Region { get; set; } = default!;
    public int VolumeSizeGb { get; set; }
    public string? ProviderVolumeId { get; set; }
    public string VolumeStatus { get; set; } = "pending";
    public string VolumeFilesystem { get; set; } = "ext4";
    public string? VolumeFilesystemUuid { get; set; }
    public string? VolumeAttachedVmId { get; set; }
    public DateTimeOffset? LastBackupAt { get; set; }
    public string? BackupStatus { get; set; }
    public decimal StorageMonthlyRate { get; set; }
    public string Currency { get; set; } = default!;
    public DateTimeOffset? PricingUpdatedAt { get; set; }
    public string IdempotencyKey { get; set; } = default!;
    public string RequestDigest { get; set; } = default!;
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<DevelopmentWorkspaceVm> Vms { get; set; } = [];
}

public static class DevelopmentWorkspaceVmKinds
{
    public const string Primary = "primary";
    public const string Ephemeral = "ephemeral";
}

public sealed class DevelopmentWorkspaceVm
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Kind { get; init; } = default!;
    public string IdempotencyKey { get; set; } = default!;
    public string RequestDigest { get; set; } = default!;
    public long Generation { get; set; }
    public string DesiredState { get; set; } = "stopped";
    public string Phase { get; set; } = "stopped";
    public string ImageId { get; set; } = default!;
    public string Profile { get; set; } = default!;
    public string PlanId { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string Region { get; set; } = default!;
    public string Sku { get; set; } = default!;
    public string ProviderImage { get; set; } = default!;
    public string ImageManifestSha256 { get; set; } = default!;
    public string Architecture { get; set; } = "linux/amd64";
    public int CpuCount { get; set; }
    public int MemoryGiB { get; set; }
    public decimal ComputeHourlyRate { get; set; }
    public bool ComputeReserved { get; set; }
    public bool StartAfterStop { get; set; }
    public DateTimeOffset? StartRequestedAt { get; set; }
    // Latest runtime resource sample only (no history); telemetry never changes Revision on its own.
    public string? ResourceSampleJson { get; set; }
    public long? ResourceSampleGeneration { get; set; }
    public DateTimeOffset? ResourceSampleReceivedAt { get; set; }
    public string? RuntimeCapabilities { get; set; }
    public string? NextImageId { get; set; }
    public string? NextProfile { get; set; }
    public string? NextPlanId { get; set; }
    public string? OperationId { get; set; }
    public string? AllocationOperationId { get; set; }
    public string? ProviderServerId { get; set; }
    public string? ProviderIpId { get; set; }
    public string? ProviderFirewallId { get; set; }
    public string? ProviderActionId { get; set; }
    public string? IpAddress { get; set; }
    public string? EnrollmentTokenDigest { get; set; }
    public DateTimeOffset? EnrollmentTokenExpiresAt { get; set; }
    public string? RuntimeTokenHash { get; set; }
    public DateTimeOffset? RuntimeTokenExpiresAt { get; set; }
    public string? SshBootstrapSecretRef { get; set; }
    public string? RuntimeCredentialRef { get; set; }
    public bool GatewayReady { get; set; }
    public string? TaskSecretReferencesJson { get; set; }
    public DateTimeOffset? EnrolledAt { get; set; }
    public string? SshHostKey { get; set; }
    public string? SshHostKeyFingerprint { get; set; }
    public long? SshHostKeyGeneration { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? ShutdownDeadline { get; set; }
    public string? ShutdownReason { get; set; }
    public DateTimeOffset? LastReadyAt { get; set; }
    public DateTimeOffset? QuiescedAt { get; set; }
    public long? QuiescedGeneration { get; set; }
    public string? QuiescedOperationId { get; set; }
    public string? QuiescenceReceipt { get; set; }
    public string? Repository { get; set; }
    public string? BaseRef { get; set; }
    public string? BaseCommit { get; set; }
    public string? WorkBranch { get; set; }
    public bool PushAuthorized { get; set; }
    public string? RepositoryCredentialRef { get; set; }
    public DateTimeOffset? IgnoredDataAcknowledgedAt { get; set; }
    public string GitStatus { get; set; } = "not_applicable";
    public DateTimeOffset? BaseVerifiedAt { get; set; }
    public long? BaseVerifiedGeneration { get; set; }
    public long? GitSavedGeneration { get; set; }
    public string? GitSavedOperationId { get; set; }
    public DateTimeOffset? GitSavedAt { get; set; }
    public string? GitSaveReceipt { get; set; }
    public string? FinalHead { get; set; }
    public string? VerifiedRemoteRef { get; set; }
    public string? VerifiedRemoteCommit { get; set; }
    public long? DiscardGeneration { get; set; }
    public string? DiscardOperationId { get; set; }
    public string? DiscardRequestedBy { get; set; }
    public DateTimeOffset? DiscardRequestedAt { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<DevelopmentWorkspaceTaskLease> TaskLeases { get; set; } = [];
}

// Each operation pins its generation's placement and pricing; options changes never rewrite it.
public sealed class DevelopmentWorkspaceOperation
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string VmId { get; set; } = default!;
    public long Generation { get; set; }
    public string Kind { get; set; } = default!;
    public string Status { get; set; } = "pending";
    public string Step { get; set; } = "pending";
    public string? LeaseOwner { get; set; }
    public string? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int Attempts { get; set; }
    public string PlanId { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string Region { get; set; } = default!;
    public string Sku { get; set; } = default!;
    public string ImageId { get; set; } = default!;
    public string ProviderImage { get; set; } = default!;
    public string ImageManifestSha256 { get; set; } = default!;
    public decimal ComputeHourlyRate { get; set; }
    public string? ProviderResourceId { get; set; }
    public string? ProviderActionId { get; set; }
    public string? ProviderEvidenceJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DevelopmentWorkspaceTaskLease
{
    public string Id { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string VmId { get; set; } = default!;
    public string TaskId { get; set; } = default!;
    public long Generation { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class DevelopmentWorkspaceFilesystemJobKinds
{
    public const string DiskUsage = "disk_usage";
    public const string DirectoryDeletion = "directory_deletion";
}

public sealed class DevelopmentWorkspaceFilesystemJob
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string WorkspaceId { get; set; } = default!;
    public string VmId { get; set; } = default!;
    public long Generation { get; set; }
    public string Kind { get; init; } = default!;
    public string State { get; set; } = "queued";
    public string PathJson { get; set; } = "[]";
    public long? TargetDevice { get; set; }
    public long? TargetInode { get; set; }
    public string? SourceJobId { get; set; }
    public string IdempotencyKey { get; set; } = default!;
    public string RequestDigest { get; set; } = default!;
    public string ActorId { get; set; } = default!;
    public bool CancelRequested { get; set; }
    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Revision { get; set; } = 1;
}

/// <summary>Short-lived per-minute resource history for one VM generation (#205).</summary>
public sealed class DevelopmentWorkspaceVmResourceSample
{
    public string WorkspaceId { get; set; } = default!;
    public string VmId { get; set; } = default!;
    public long Generation { get; set; }
    public DateTimeOffset Minute { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public double? CpuUtilizationPercent { get; set; }
    public long? MemoryTotalBytes { get; set; }
    public long? MemoryAvailableBytes { get; set; }
    public long? WorkspaceDiskTotalBytes { get; set; }
    public long? WorkspaceDiskAvailableBytes { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
