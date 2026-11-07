using Microsoft.Extensions.DependencyInjection;

namespace Minicloud.Common.Extensions;

/// <summary>
/// Opt-in Common registrations. Referencing Common registers nothing by
/// itself: hosts call these explicitly per capability. None of these start
/// background services or require backend credentials (PostgreSQL, cloud
/// providers, OpenBao) unless the host also configures that capability.
/// </summary>
public static class MinicloudCommonExtensions
{
    public static IServiceCollection AddMinicloudCommon(this IServiceCollection services)
    {
        services.AddSingleton<Services.IIdGenerator, Services.IdGenerator>();
        return services;
    }

    public static IServiceCollection AddMinicloudSecrets(
        this IServiceCollection services,
        Action<Services.SecretEncryptionOptions>? configureEncryption = null,
        Action<Services.OpenBaoSecretStoreOptions>? configureStore = null)
    {
        if (configureEncryption is not null)
        {
            services.Configure(configureEncryption);
        }

        if (configureStore is not null)
        {
            services.Configure(configureStore);
        }

        services.AddSingleton<Services.SecretEncryptionService>();
        return services;
    }
}
