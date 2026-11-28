namespace Selfrun.Common.Models;

public sealed class EnvironmentSleepSettings
{
    public string EnvironmentId { get; set; } = default!;
    public int? SleepAfterMinutes { get; set; }
    public bool HttpTrafficIsActivity { get; set; } = true;
    public string? AddressMode { get; set; }
    public long Revision { get; set; }
    public string PowerState { get; set; } = EnvironmentPowerStates.Awake;
    public DateTimeOffset StateChangedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public string? LastActivityKind { get; set; }
    public DateTimeOffset? LastHttpRequestAt { get; set; }
    public DateTimeOffset? KeepAwakeUntil { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}

public static class EnvironmentPowerStates
{
    public const string Awake = "awake";
    public const string Sleeping = "sleeping";
    public const string Asleep = "asleep";
    public const string Waking = "waking";
    public const string SleepFailed = "sleep_failed";
    public const string WakeFailed = "wake_failed";
}

public static class EnvironmentAddressModes
{
    public const string Dynamic = "dynamic";
    public const string Retained = "retained";
}

public sealed class EnvironmentPowerOperation
{
    public string Id { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string EnvironmentId { get; set; } = default!;
    public string Kind { get; set; } = default!;
    public string Trigger { get; set; } = default!;
    public string ActorJson { get; set; } = "{}";
    public string Status { get; set; } = EnvironmentPowerOperationStatuses.Queued;
    public string? Step { get; set; }
    public string? OwnershipToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? DeploymentId { get; set; }
}

public static class EnvironmentPowerOperationStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Cancelling = "cancelling";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public sealed class EnvironmentSnapshot
{
    public string Id { get; set; } = default!;
    public string EnvironmentId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Provider { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string? ProviderImageId { get; set; }
    public string Region { get; set; } = default!;
    public string? Datacenter { get; set; }
    public string Sku { get; set; } = default!;
    public string ServerName { get; set; } = default!;
    public string AddressMode { get; set; } = default!;
    public int? SizeGb { get; set; }
    public string Status { get; set; } = EnvironmentSnapshotStatuses.Creating;
    public string PlacementJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public static class EnvironmentSnapshotStatuses
{
    public const string Creating = "creating";
    public const string Pending = "pending";
    public const string Current = "current";
    public const string Superseded = "superseded";
    public const string Deleting = "deleting";
    public const string Deleted = "deleted";
    public const string Failed = "failed";
}

public sealed class EnvironmentRetainedAddress
{
    public string Id { get; set; } = default!;
    public string EnvironmentId { get; set; } = default!;
    public string OrganizationId { get; set; } = default!;
    public string Provider { get; set; } = default!;
    public string ProviderAccountId { get; set; } = default!;
    public string ProviderAddressId { get; set; } = default!;
    public string Ipv4 { get; set; } = default!;
    public string Region { get; set; } = default!;
    public string? Datacenter { get; set; }
    public string? PreviousIpv4 { get; set; }
    public string State { get; set; } = EnvironmentRetainedAddressStates.Unassigned;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

public static class EnvironmentRetainedAddressStates
{
    public const string Assigned = "assigned";
    public const string Unassigned = "unassigned";
    public const string Releasing = "releasing";
    public const string Released = "released";
    public const string Failed = "failed";
}
