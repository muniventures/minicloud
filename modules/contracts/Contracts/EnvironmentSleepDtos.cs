using System.Text.Json;

namespace Selfrun.Common.Contracts;

public sealed record EnvironmentSleepPolicyPatch(
    bool? Enabled = null,
    JsonElement SleepAfterMinutes = default,
    bool? HttpTrafficIsActivity = null,
    long? Revision = null);

public sealed record EnvironmentSleepPatchRequest(EnvironmentSleepPolicyPatch? Policy);

public sealed record WakeEnvironmentRequest(int? KeepAwakeMinutes = null);

public sealed record EnvironmentSleepPolicyResponse(
    int? SleepAfterMinutes,
    bool HttpTrafficIsActivity,
    long Revision);

public sealed record EnvironmentRetainedAddressResponse(
    string Ipv4,
    string State,
    string? PreviousIpv4);

public sealed record EnvironmentSnapshotResponse(
    DateTimeOffset CreatedAt,
    int? SizeGb,
    bool PendingPurge);

public sealed record EnvironmentPowerOperationResponse(
    string Id,
    string Kind,
    string Status,
    string? Step,
    string Trigger);

public sealed record EnvironmentSleepErrorResponse(string Code, string Message);

public sealed record EnvironmentSleepResponse(
    EnvironmentSleepPolicyResponse Policy,
    string State,
    DateTimeOffset StateChangedAt,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? LastHttpRequestAt,
    DateTimeOffset? KeepAwakeUntil,
    DateTimeOffset? SleepsAt,
    bool Eligible,
    string? IneligibleReason,
    string AddressMode,
    EnvironmentRetainedAddressResponse? RetainedAddress,
    EnvironmentSnapshotResponse? Snapshot,
    EnvironmentPowerOperationResponse? Operation,
    EnvironmentSleepErrorResponse? LastError);

public sealed record EnvironmentPowerOperationAcceptedResponse(EnvironmentPowerOperationResponse Operation);
