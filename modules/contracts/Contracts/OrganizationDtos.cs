// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record OrganizationSummaryDto(string Id, string Name, string Slug, string Role);
public sealed record CreateOrganizationRequest(string Name, string Slug);
public sealed record OrganizationResponse(string Id, string Name, string Slug, DateTimeOffset CreatedAt);
