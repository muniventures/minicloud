namespace Minicloud.Common.Providers.Vultr;

public class VultrOptions
{
    public const string SectionName = "Vultr";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.vultr.com/v2/";
    public int? DefaultOsId { get; set; } = 2284;
}
