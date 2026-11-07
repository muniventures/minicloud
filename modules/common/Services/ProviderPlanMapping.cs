using Minicloud.Common.Models;
using Minicloud.Common.Providers;

namespace Minicloud.Common.Services;

public class ProviderPlanOptions
{
    public CloudProvider DefaultProvider { get; set; } = CloudProvider.Hetzner;
    public string DefaultRegion { get; set; } = "nbg1";
    public string DefaultSku { get; set; } = "cx23";
    public string HetznerRegion { get; set; } = "nbg1";
    public string HetznerSku { get; set; } = "cx23";
    public string VultrRegion { get; set; } = "ewr";
    public string VultrSmallSku { get; set; } = "vc2-1c-0.5gb-v6";
    public string VultrMediumSku { get; set; } = "vc2-1c-0.5gb";
    public string VultrLargeSku { get; set; } = "vc2-1c-1gb";
    public string DigitalOceanRegion { get; set; } = "nyc1";
    public string DigitalOceanSmallSku { get; set; } = "s-1vcpu-512mb-10gb";
    public string DigitalOceanMediumSku { get; set; } = "s-1vcpu-1gb";
    public string DigitalOceanLargeSku { get; set; } = "s-1vcpu-2gb";
}

public sealed record ProviderPlanMapping(CloudProvider Provider, string Region, string Sku)
{
    public static ProviderPlanMapping FromAppPlan(AppPlan plan, ProviderPlanOptions options) => plan switch
    {
        AppPlan.P0H0 => new(CloudProvider.Hetzner, options.HetznerRegion, options.HetznerSku),
        AppPlan.P0V0 => new(CloudProvider.Vultr, options.VultrRegion, options.VultrSmallSku),
        AppPlan.P0V1 => new(CloudProvider.Vultr, options.VultrRegion, options.VultrMediumSku),
        AppPlan.P0V2 => new(CloudProvider.Vultr, options.VultrRegion, options.VultrLargeSku),
        AppPlan.P0D0 => new(CloudProvider.DigitalOcean, options.DigitalOceanRegion, options.DigitalOceanSmallSku),
        AppPlan.P0D1 => new(CloudProvider.DigitalOcean, options.DigitalOceanRegion, options.DigitalOceanMediumSku),
        AppPlan.P0D2 => new(CloudProvider.DigitalOcean, options.DigitalOceanRegion, options.DigitalOceanLargeSku),
        _ => new(options.DefaultProvider, options.DefaultRegion, options.DefaultSku)
    };
}
