namespace Minicloud.Common.Providers.Dns;

public class CloudflareOptions
{
    public const string SectionName = "Cloudflare";

    public string ApiToken { get; set; } = string.Empty;
    public string ZoneId { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.cloudflare.com/client/v4/";
    public int Ttl { get; set; } = 120;
    public bool Proxied { get; set; } = false;
    public TimeSpan PropagationPollInterval { get; set; } = TimeSpan.FromSeconds(5);
}
