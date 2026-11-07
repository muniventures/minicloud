using System.ComponentModel.DataAnnotations;

namespace Minicloud.Common.Contracts;

public sealed record GrafanaLaunchRequest(
    [RegularExpression(@"^/(explore|d/[A-Za-z0-9_\-]{1,64})$")] string? Destination);

public sealed record GrafanaLaunchResponse(
    string LaunchCode,
    string Destination,
    DateTimeOffset ExpiresAt,
    string? ExchangeUrl = null,
    string? GrafanaBaseUrl = null);

public sealed record GrafanaExchangeRequest(string LaunchCode);

public sealed record GrafanaExchangeResponse(
    string Destination,
    DateTimeOffset ExpiresAt);

public sealed record GrafanaExploreUrlResponse(string ExploreUrl);
