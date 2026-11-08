using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Vultr;
using Minicloud.Common.Services;

namespace Minicloud.Common.Providers;

// A new adapter instance captures one credential generation. Authorization is added
// per request by the adapters, never to shared transport default headers.
public class ProviderAccountClientFactory(
    MinicloudDbContext db, ISecretStore secrets, IHttpClientFactory http,
    IOptions<HetznerOptions> hetzner, IOptions<DigitalOceanOptions> digitalOcean,
    IOptions<VultrOptions> vultr, ILoggerFactory logs)
{
    public const string HttpClientName = "provider-accounts";

    public virtual async Task<IVpsProviderClient> CreateAsync(string organizationId, string providerAccountId,
        CancellationToken ct, bool allowDisabled = true)
    {
        var account = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == providerAccountId && x.OrganizationId == organizationId, ct)
            ?? throw new ProviderCatalogException("provider_not_found", "Provider account was not found in this organization.");
        if (!allowDisabled && !account.Enabled)
            throw new ProviderCatalogException("provider_disabled", "This provider account is disabled for new allocation.");
        if (account.CredentialStoreProvider != OpenBaoSecretStore.ProviderName || string.IsNullOrWhiteSpace(account.CredentialPath))
            throw new SecretStoreUnavailableException("Provider account credentials are unavailable.");
        var credential = await secrets.GetAsync(new StoredSecretReference(account.CredentialStoreProvider,
            account.CredentialPath, account.CredentialVersion), ct);
        if (string.IsNullOrWhiteSpace(credential)) throw new SecretStoreUnavailableException("Provider account credentials are empty.");
        return CreateCandidate(account, credential);
    }

    public virtual IVpsProviderClient CreateCandidate(ProviderAccount account, string credential)
    {
        var client = http.CreateClient(HttpClientName);
        return account.ProviderType switch
        {
            CloudProvider.Hetzner => new HetznerClient(client, new HetznerOptions
                { ApiToken = credential, BaseUrl = hetzner.Value.BaseUrl }, logs.CreateLogger<HetznerClient>()),
            CloudProvider.DigitalOcean => new DigitalOceanClient(client, new DigitalOceanOptions
                { ApiToken = credential, BaseUrl = digitalOcean.Value.BaseUrl, ProjectId = account.ProjectId }, logs.CreateLogger<DigitalOceanClient>()),
            CloudProvider.Vultr => new VultrClient(client, new VultrOptions
                { ApiKey = credential, BaseUrl = vultr.Value.BaseUrl, DefaultOsId = account.DefaultOsId }, logs.CreateLogger<VultrClient>()),
            _ => throw new ProviderCatalogException("provider_type_unsupported", "This VPS provider type is not supported.")
        };
    }
}
