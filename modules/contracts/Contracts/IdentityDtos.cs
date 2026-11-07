// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record MeResponse(
    string Id,
    string Email,
    string? DisplayName,
    IReadOnlyList<OrganizationSummaryDto> Organizations);
