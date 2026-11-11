namespace Minicloud.Common.Models;

/// <summary>
/// Installation-wide maintenance authority. Exactly one row (id 1) exists; its epoch is the
/// fencing token every API admission and worker claim validates against.
/// </summary>
public sealed class MaintenanceAuthorityRecord
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public string? InstallationId { get; set; }
    public long Epoch { get; set; } = 1;
    public string State { get; set; } = MaintenanceAuthorityStates.Open;
    public string? HolderOperationId { get; set; }
    public string? HostId { get; set; }
    public string? TransferOperationId { get; set; }
    public DateTimeOffset? FencedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset? TransferredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class MaintenanceAuthorityStates
{
    public const string Open = "open";
    public const string Fenced = "fenced";
}

/// <summary>
/// A durable record of admitted in-flight activity (an API management write or a scheduler pass)
/// that drain status must observe until it completes or its renewed lease expires.
/// </summary>
public sealed class MaintenanceActivity
{
    public string Id { get; set; } = default!;
    public string Kind { get; set; } = default!;
    public string ParticipantId { get; set; } = default!;
    public long Epoch { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>A registered API or worker process and the authority epoch it validated at startup.</summary>
public sealed class MaintenanceParticipantRecord
{
    public string Id { get; set; } = default!;
    public string Role { get; set; } = default!;
    public string? HostId { get; set; }
    public long Epoch { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
