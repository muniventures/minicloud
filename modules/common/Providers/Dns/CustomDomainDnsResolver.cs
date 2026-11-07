using System.Net.Http.Json;

namespace Minicloud.Common.Providers.Dns;

public interface ICustomDomainDnsResolver
{
    Task<IReadOnlyList<string>> QueryAsync(string hostname, int type, CancellationToken cancellationToken);
}

public sealed class CustomDomainDnsResolver(HttpClient http) : ICustomDomainDnsResolver
{
    public async Task<IReadOnlyList<string>> QueryAsync(string hostname, int type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(hostname)}&type={type}");
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<DnsResponse>(cancellationToken);
        if (result is null || result.Status is not (0 or 3))
        {
            throw new InvalidOperationException("DNS resolver unavailable.");
        }
        return result.Answer?.Where(x => x.Type == type).Select(x => x.Data).ToArray() ?? [];
    }

    private sealed record DnsResponse(int Status, DnsAnswer[]? Answer);
    private sealed record DnsAnswer(int Type, string Data);
}
