using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Providers;
using Minicloud.Common.Providers.Dns;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Providers;

public sealed class ProviderRegistrationTests
{
    [Fact]
    public void ProductionRegistrationsResolveCloudflareClient()
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<CloudflareDnsClient>(scope.ServiceProvider.GetRequiredService<ICloudflareDnsClient>());
    }

    [Fact]
    public void ProductionSshClientActivatesWithoutConnecting()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<SshDeploymentOptions>(_ => { });
        services.AddScoped<ISshDeploymentClient, SshDeploymentClient>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.IsType<SshDeploymentClient>(scope.ServiceProvider.GetRequiredService<ISshDeploymentClient>());
    }

    [Theory]
    [InlineData(CloudProvider.Hetzner)]
    [InlineData(CloudProvider.DigitalOcean)]
    [InlineData(CloudProvider.Vultr)]
    public void ProductionRegistrationsResolveVpsClients(CloudProvider cloud)
    {
        using var provider = CreateProvider();
        using var scope = provider.CreateScope();
        var resolve = scope.ServiceProvider.GetRequiredService<ProviderServiceCollectionExtensions.VpsProviderClientResolver>();
        Assert.NotNull(resolve(cloud));
        Assert.NotNull(scope.ServiceProvider.GetRequiredKeyedService<IVpsProviderClient>(cloud));
    }

    private static ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMinicloudProviders(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
