// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record RegisterCustomDomainRequest(string Hostname);

public sealed record CustomDomainRegistrationResponse(
    string Id, string? AppId, string Hostname, string VerificationToken, string Status,
    string? FailureMessage, DateTimeOffset? LastCheckedAt);
