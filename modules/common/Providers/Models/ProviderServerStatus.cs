namespace Minicloud.Common.Providers.Models;

public record ProviderServerStatus(
    string Id,
    string Status,
    string? Ipv4Address = null,
    string? Ipv6Address = null,
    bool IsRunning = false,
    string? RawStatus = null);
