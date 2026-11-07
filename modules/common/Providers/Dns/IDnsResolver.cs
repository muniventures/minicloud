namespace Minicloud.Common.Providers.Dns;

public interface IDnsResolver
{
    Task<IReadOnlyList<string>> ResolveIpAddressesAsync(string hostname, CancellationToken cancellationToken = default);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<string>> ResolveIpAddressesAsync(string hostname, CancellationToken cancellationToken = default)
    {
        var addresses = await System.Net.Dns.GetHostAddressesAsync(hostname, cancellationToken);
        return addresses.Select(a => a.ToString()).ToList();
    }
}
