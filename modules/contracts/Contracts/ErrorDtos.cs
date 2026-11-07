// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Common.Contracts;

public sealed record ErrorResponse(ErrorBody Error);
public sealed record ErrorBody(string Code, string Message, IReadOnlyList<ErrorDetail> Details);
public sealed record ErrorDetail(string Field, string Message);
