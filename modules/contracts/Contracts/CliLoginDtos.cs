// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record CliLoginSessionCreateResponse(
    string SessionId,
    string LoginUrl,
    string Status,
    DateTimeOffset ExpiresAt);

public sealed record CliLoginSessionResponse(
    string SessionId,
    string Status,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? ExchangedAt);

public sealed record ApproveCliLoginSessionRequest(string OrganizationId);

public sealed record CliLoginSessionExchangeResponse(
    string Token,
    string Email,
    OrganizationSummaryDto Organization,
    IReadOnlyList<string> Scopes);
