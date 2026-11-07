namespace Minicloud.Common.Providers.Hetzner;

public class HetznerOptions
{
    public const string SectionName = "Hetzner";

    public string ApiToken { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.hetzner.cloud/v1/";
}
