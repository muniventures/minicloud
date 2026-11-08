namespace Minicloud.Common.Services;

public sealed class ProviderCatalogOptions
{
    // Temporary explicit upgrade bridge. New installations and completed cutovers
    // never provision through installation-global provider credentials.
    public bool AllowLegacyConfiguration { get; set; }
}
