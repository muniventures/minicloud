using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

/// <summary>
/// Resolves the Grafana server-admin credential used ONLY by the private
/// provisioning client. Reads the operator secret from OpenBao first and falls
/// back to the GRAFANA_ADMIN_PASSWORD environment variable (operator-injected,
/// never committed). Returns null when unavailable so provisioning fails closed.
/// </summary>
public sealed class GrafanaOperatorCredentialProvider(
    ISecretStore secrets,
    IOptions<GrafanaOptions> options,
    IConfiguration configuration,
    ILogger<GrafanaOperatorCredentialProvider> logger)
{
    public async Task<string?> GetServerAdminPasswordAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reference = new StoredSecretReference(
                OpenBaoSecretStore.ProviderName, options.Value.OperatorSecretPath, Version: null);
            var value = await secrets.GetAsync(reference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }
        catch (SecretStoreUnavailableException ex)
        {
            logger.LogDebug(ex, "Grafana operator secret not found in OpenBao; trying environment fallback.");
        }

        var env = configuration["GRAFANA_ADMIN_PASSWORD"];
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }
}
