// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record CreateApiKeyRequest(
    string OrganizationId,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);

public sealed record ApiKeyCreateResponse(
    string Id,
    string Name,
    string Prefix,
    string Token,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public sealed record ApiKeyResponse(
    string Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset CreatedAt);
