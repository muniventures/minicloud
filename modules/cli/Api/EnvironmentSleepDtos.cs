// SPDX-License-Identifier: MPL-2.0

using System.Text.Json.Serialization;
using Selfrun.Common.Contracts;

namespace Selfrun.Cli.Api;

internal sealed record EnvironmentSleepPolicyPatch(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SleepAfterMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? HttpTrafficIsActivity = null,
    long Revision = 0);

internal sealed record EnvironmentSleepPatchRequest(EnvironmentSleepPolicyPatch? Policy);

internal sealed record EnvironmentSleepUpdateRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Plan = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Database = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? SetAsDefault = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SshAccessRequest? SshAccess = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EnvironmentSleepPatchRequest? Sleep = null);

internal sealed record WakeEnvironmentRequest(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? KeepAwakeMinutes = null);

internal sealed record EnvironmentSleepPolicyResponse(
    int? SleepAfterMinutes,
    bool HttpTrafficIsActivity,
    long Revision)
{
    public bool Enabled => SleepAfterMinutes is not null;
}

internal sealed record EnvironmentRetainedAddressResponse(
    string Ipv4,
    string State,
    string? PreviousIpv4);

internal sealed record EnvironmentSnapshotResponse(
    DateTimeOffset CreatedAt,
    int? SizeGb,
    bool PendingPurge);

internal sealed record EnvironmentPowerOperationResponse(
    string Id,
    string Kind,
    string Status,
    string? Step,
    string Trigger);

internal sealed record EnvironmentSleepErrorResponse(string Code, string Message);

internal sealed record EnvironmentSleepResponse(
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

internal sealed record ApplicationEnvironmentSleepResponse(
    string Id,
    string ApplicationId,
    string Name,
    EnvironmentSleepResponse? Sleep,
    EnvironmentSshAccessResponse? SshAccess);

internal sealed record EnvironmentPowerActionResponse(
    EnvironmentPowerOperationResponse? Operation,
    string? State,
    EnvironmentSleepErrorResponse? LastError);
