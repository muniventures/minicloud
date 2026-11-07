namespace Minicloud.Common.Providers.Models;

public record ProviderServerInfo(
    string Id,
    string Name,
    string Ipv4Address,
    string Status,
    string? Ipv6Address = null,
    IReadOnlyDictionary<string, string>? Labels = null);
