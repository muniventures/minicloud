namespace Minicloud.Common.Services;

public sealed class ManagedDomainOptions
{
    public const string SectionName = "ManagedDomains";
    public string Suffix { get; set; } = DomainBindingService.MinicloudSuffix;
    public bool ManageDnsRecords { get; set; } = true;
    public string[] ReservedHostnames { get; set; } = [];

    public bool IsReserved(string hostname) => Contains(hostname) || ReservedHostnames.Any(reserved =>
        hostname.Equals(reserved, StringComparison.OrdinalIgnoreCase) ||
        hostname.EndsWith("." + reserved, StringComparison.OrdinalIgnoreCase));

    public bool Contains(string hostname) =>
        hostname.Equals(Suffix, StringComparison.OrdinalIgnoreCase) ||
        hostname.EndsWith("." + Suffix, StringComparison.OrdinalIgnoreCase);
}
