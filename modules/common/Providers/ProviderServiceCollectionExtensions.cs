using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Minicloud.Common.Providers.Common;
using Minicloud.Common.Providers.DigitalOcean;
using Minicloud.Common.Providers.Dns;
using Minicloud.Common.Providers.Hetzner;
using Minicloud.Common.Providers.Vultr;
using System.Net.Http;

namespace Minicloud.Common.Providers;

public static class ProviderServiceCollectionExtensions
{
    public delegate IVpsProviderClient VpsProviderClientResolver(CloudProvider provider);

    public static IServiceCollection AddMinicloudProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<HetznerOptions>(configuration.GetSection(HetznerOptions.SectionName));
        services.Configure<DigitalOceanOptions>(configuration.GetSection(DigitalOceanOptions.SectionName));
        services.Configure<VultrOptions>(configuration.GetSection(VultrOptions.SectionName));
        services.Configure<CloudflareOptions>(configuration.GetSection(CloudflareOptions.SectionName));

        services.TryAddTransient<ResilientHttpRetryHandler>();
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.AddHttpClient(ProviderAccountClientFactory.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<ProviderAccountClientFactory>();

        // Hetzner
        services.AddHttpClient<HetznerClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, HetznerClient>(CloudProvider.Hetzner);

        // DigitalOcean
        services.AddHttpClient<DigitalOceanClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, DigitalOceanClient>(CloudProvider.DigitalOcean);

        // Vultr
        services.AddHttpClient<VultrClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();
        services.AddKeyedScoped<IVpsProviderClient, VultrClient>(CloudProvider.Vultr);

        services.AddHttpClient<ICustomDomainDnsResolver, CustomDomainDnsResolver>(http => http.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<Minicloud.Common.Services.CustomDomainVerificationService>();
        services.AddScoped<Minicloud.Common.Services.CustomDomainRegistrationService>();

        // Cloudflare DNS
        services.AddHttpClient<ICloudflareDnsClient, CloudflareDnsClient>()
            .AddHttpMessageHandler<ResilientHttpRetryHandler>();

        // Resolver delegate
        services.AddScoped<VpsProviderClientResolver>(sp => provider => provider switch
        {
            CloudProvider.Hetzner => sp.GetRequiredService<HetznerClient>(),
            CloudProvider.DigitalOcean => sp.GetRequiredService<DigitalOceanClient>(),
            CloudProvider.Vultr => sp.GetRequiredService<VultrClient>(),
            _ => throw new NotSupportedException($"VPS provider {provider} is not supported.")
        });

        return services;
    }
}
