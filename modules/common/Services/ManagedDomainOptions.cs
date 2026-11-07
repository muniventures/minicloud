namespace Minicloud.Common.Services;

public sealed class ManagedDomainOptions
{
    public const string SectionName = "ManagedDomains";
    public string Suffix { get; set; } = DomainBindingService.MinicloudSuffix;

    public bool Contains(string hostname) =>
        hostname.Equals(Suffix, StringComparison.OrdinalIgnoreCase) ||
        hostname.EndsWith("." + Suffix, StringComparison.OrdinalIgnoreCase);
}
