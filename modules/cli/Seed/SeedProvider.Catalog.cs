// SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Minicloud.Cli.Seed;

internal sealed partial class SeedProvider
{
    private async Task<SeedQuote> HetznerQuoteAsync(SeedSelection s, CancellationToken ct)
    {
        // Hetzner exposes a project-scoped token, not an account/profile endpoint.
        await ListAsync("servers", "servers", ct);
        await ListAsync("ssh_keys", "ssh_keys", ct);
        var location = One(await ListAsync("locations", "locations", ct), x => Text(x, "name") == s.Region, "Unknown Hetzner location.");
        var size = One(await ListAsync("server_types", "server_types", ct), x => Text(x, "name") == s.Size, "Unknown Hetzner server type.");
        var architecture = Text(size, "architecture");
        SeedGuard.Require(architecture is "x86" or "arm", "Unsupported server architecture.");
        if (size.TryGetProperty("locations", out var locations))
            SeedGuard.Require(locations.EnumerateArray().Any(x => Text(x, "name") == s.Region && x.GetProperty("available").GetBoolean()),
                "Selected Hetzner size is not available in that location.");
        else
        {
            var datacenters = await ListAsync("datacenters", "datacenters", ct);
            SeedGuard.Require(datacenters.Any(x => Text(x.GetProperty("location"), "id") == Text(location, "id")
                && x.GetProperty("server_types").GetProperty("available").EnumerateArray().Any(y => y.ToString() == Text(size, "id"))),
                "Selected Hetzner size is not currently available in that location.");
        }
        var image = One(await ListAsync("images?type=system&architecture=" + architecture, "images", ct),
            x => Text(x, "os_flavor") == "ubuntu" && Text(x, "os_version") == "24.04" && Text(x, "architecture") == architecture
                && Text(x, "type") == "system" && Text(x, "status") == "available"
                && (!x.TryGetProperty("deprecated", out var deprecated) || deprecated.ValueKind == JsonValueKind.Null),
            "No unique supported Ubuntu 24.04 system image for this architecture.");
        SeedGuard.Require(Money(size, "disk") >= Money(image, "disk_size"), "Selected disk is smaller than the Ubuntu image minimum.");
        var pricing = (await SendAsync(HttpMethod.Get, "pricing", null, ct)).GetProperty("pricing");
        var serverPrices = One(pricing.GetProperty("server_types").EnumerateArray(), x => Text(x, "id") == Text(size, "id"), "Server pricing unavailable.");
        var serverPrice = One(serverPrices.GetProperty("prices").EnumerateArray(), x => Text(x, "location") == s.Region, "Region server pricing unavailable.");
        var ips = One(pricing.GetProperty("primary_ips").EnumerateArray(), x => Text(x, "type") == "ipv4", "IPv4 pricing unavailable.");
        var ipPrice = One(ips.GetProperty("prices").EnumerateArray(), x => Text(x, "location") == s.Region, "Region IPv4 pricing unavailable.");
        var currency = Text(pricing, "currency");
        SeedCost Cost(string name, JsonElement price, string exclusions) => new(name,
            Money(price.GetProperty("price_monthly"), "net"), Money(price.GetProperty("price_hourly"), "net"), currency,
            "hourly, monthly cap; net amounts", exclusions, "https://api.hetzner.cloud/v1/pricing");
        var costs = new[] {
            Cost("one dedicated VPS", serverPrice, "Tax/VAT excluded; included traffic bytes=" + Text(serverPrice, "included_traffic")
                + "; excess/TB net=" + Text(serverPrice.GetProperty("price_per_tb_traffic"), "net") + " " + currency + "; no backups/volumes/load balancer."),
            Cost("one auto-assigned public IPv4 (deleted with VPS)", ipPrice, "Tax/VAT excluded; IPv6 disabled.")
        };
        return new("Hetzner project scoped by the supplied credential (scope fingerprint binds approval)", "", Text(image, "id"),
            architecture == "arm" ? "linux/arm64" : "linux/amd64", "root", size.GetProperty("cores").GetInt32(),
            (int)(Money(size, "memory") * 1024), size.GetProperty("disk").GetInt32(), costs, default,
            "Project read/write API token required. Hetzner exposes no remaining server-quota preflight; availability is not a reservation. SSH key registration is nonbillable.");
    }

    private async Task<SeedQuote> DigitalOceanQuoteAsync(SeedSelection s, CancellationToken ct)
    {
        var account = (await SendAsync(HttpMethod.Get, "account", null, ct)).GetProperty("account");
        SeedGuard.Require(Text(account, "status") == "active" && account.GetProperty("email_verified").GetBoolean(), "DigitalOcean account must be active and email-verified.");
        var droplets = await ListAsync("droplets", "droplets", ct);
        SeedGuard.Require(droplets.Count < account.GetProperty("droplet_limit").GetInt32(), "DigitalOcean droplet quota exhausted.");
        await ListAsync("account/keys", "ssh_keys", ct);
        var network = One(await ListAsync("vpcs", "vpcs", ct), x => Text(x, "region") == s.Region
            && x.GetProperty("default").GetBoolean(), "An existing default DigitalOcean VPC is required in the selected region. Seed never implicitly creates a shared network; configure it separately in the provider console.");
        var region = One(await ListAsync("regions", "regions", ct), x => Text(x, "slug") == s.Region, "Unknown DigitalOcean region.");
        SeedGuard.Require(region.GetProperty("available").GetBoolean(), "DigitalOcean region unavailable.");
        var size = One(await ListAsync("sizes", "sizes", ct), x => Text(x, "slug") == s.Size, "Unknown DigitalOcean size.");
        SeedGuard.Require(size.GetProperty("available").GetBoolean()
            && size.GetProperty("regions").EnumerateArray().Any(x => x.GetString() == s.Region), "Size unavailable in selected DigitalOcean region.");
        var image = (await SendAsync(HttpMethod.Get, "images/ubuntu-24-04-x64", null, ct)).GetProperty("image");
        SeedGuard.Require(Text(image, "distribution") == "Ubuntu" && Text(image, "slug") == "ubuntu-24-04-x64"
            && image.GetProperty("public").GetBoolean() && image.GetProperty("regions").EnumerateArray().Any(x => x.GetString() == s.Region),
            "Official Ubuntu 24.04 x64 image unavailable in this region.");
        SeedGuard.Require(size.GetProperty("disk").GetInt32() >= image.GetProperty("min_disk_size").GetInt32(), "Selected disk is smaller than the image minimum.");
        var costs = new[] { new SeedCost("one dedicated Droplet with public IPv4", Money(size, "price_monthly"), Money(size, "price_hourly"),
            "USD", "hourly, monthly cap", "Taxes excluded; included outbound transfer TB=" + Text(size, "transfer")
                + "; excess bandwidth, backups, volumes and other optional services excluded; IPv6 disabled.",
            "https://api.digitalocean.com/v2/sizes") };
        return new("DigitalOcean account " + Text(account, "uuid"), "", Text(image, "id"), "linux/amd64", "root",
            size.GetProperty("vcpus").GetInt32(), size.GetProperty("memory").GetInt32(), size.GetProperty("disk").GetInt32(), costs, default,
            "Account/droplet/region/size/image/SSH-key/VPC read, droplet create/delete and SSH-key create/delete scopes required. Attach only the recorded existing default VPC; no network/project creation. Quota check is not a reservation.",
            Text(network, "id"));
    }

    private async Task<SeedQuote> VultrQuoteAsync(SeedSelection s, CancellationToken ct)
    {
        var account = (await SendAsync(HttpMethod.Get, "account", null, ct)).GetProperty("account");
        SeedGuard.Require(account.GetProperty("acls").EnumerateArray().Any(x => x.GetString() == "provision"), "Vultr key requires provision ACL and workstation IP access.");
        await ListAsync("instances", "instances", ct);
        await ListAsync("ssh-keys", "ssh_keys", ct);
        One(await ListAsync("regions", "regions", ct), x => Text(x, "id") == s.Region, "Unknown Vultr region.");
        var size = One(await ListAsync("plans", "plans", ct), x => Text(x, "id") == s.Size, "Unknown Vultr plan.");
        SeedGuard.Require(Text(size, "type") is "vc2" or "vhf" or "vhp" or "voc",
            "Select a documented x64 Vultr cloud-compute family (vc2/vhf/vhp/voc); bare metal, GPU and unknown architectures are not supported.");
        SeedGuard.Require(size.GetProperty("locations").EnumerateArray().Any(x => x.GetString() == s.Region), "Vultr plan is unavailable in this location.");
        var available = await SendAsync(HttpMethod.Get, "regions/" + Uri.EscapeDataString(s.Region) + "/availability", null, ct);
        SeedGuard.Require(available.GetProperty("available_plans").EnumerateArray().Any(x => x.GetString() == s.Size), "Vultr regional capacity unavailable.");
        var image = One(await ListAsync("os", "os", ct), x => Text(x, "family") == "ubuntu" && Text(x, "arch") == "x64"
            && System.Text.RegularExpressions.Regex.IsMatch(Text(x, "name"), @"^Ubuntu 24\.04(?:\s|$)"), "No unique supported Vultr Ubuntu 24.04 x64 image.");
        var costs = new[] { new SeedCost("one dedicated compute instance with public IPv4", Money(size, "monthly_cost"), null, "USD",
            "API monthly rate; hourly billing, 672-hour monthly cap, one-hour minimum (no hourly rate invented)",
            "Taxes excluded; included bandwidth GB=" + Text(size, "bandwidth")
                + "; regional bandwidth overage, backups, volumes and optional services excluded; IPv6 disabled.",
            "https://api.vultr.com/v2/plans") };
        return new("Vultr account " + SeedGuard.Hash(Text(account, "email")), "", Text(image, "id"), "linux/amd64", "root",
            size.GetProperty("vcpu_count").GetInt32(), size.GetProperty("ram").GetInt32(), size.GetProperty("disk").GetInt32(), costs, default,
            "Provision ACL, active funded account and allowed workstation IP required. Vultr exposes no remaining instance-quota preflight; availability is not a reservation. Billing terms: https://docs.vultr.com/support/platform/billing/how-am-i-billed-for-my-servers ; stopped resources remain billable.");
    }
}
