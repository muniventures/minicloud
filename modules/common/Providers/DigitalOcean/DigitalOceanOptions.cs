namespace Minicloud.Common.Providers.DigitalOcean;

public class DigitalOceanOptions
{
    public const string SectionName = "DigitalOcean";

    public string ApiToken { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string BaseUrl { get; set; } = "https://api.digitalocean.com/v2/";
}
