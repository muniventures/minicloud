namespace Selfrun.Common.Providers.Models;

public sealed record ProviderRetainedAddress(
    string Id,
    string Ipv4Address,
    string? Region = null,
    string? Datacenter = null);
