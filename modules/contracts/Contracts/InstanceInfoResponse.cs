// SPDX-License-Identifier: MPL-2.0
namespace Minicloud.Common.Contracts;

public sealed record InstanceInfoResponse(
    string Product,
    int ProtocolVersion,
    string AppDomain,
    string? ServerVersion = null,
    string? SourceRevision = null,
    CliCompatibilityInfo? CliCompatibility = null);

public sealed record CliCompatibilityInfo(
    int ProtocolVersion,
    string? MinimumVersion,
    string? MaximumExclusiveVersion,
    string[] SourceRevisions);
