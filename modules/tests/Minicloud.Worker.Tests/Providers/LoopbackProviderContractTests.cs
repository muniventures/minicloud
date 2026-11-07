using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minicloud.Common.Providers;

namespace Minicloud.Worker.Tests.Providers;

public sealed class LoopbackProviderContractTests
{
    [Theory]
    [InlineData(CloudProvider.Hetzner)]
    [InlineData(CloudProvider.DigitalOcean)]
    [InlineData(CloudProvider.Vultr)]
    public async Task ProductionRegistrationUsesConfiguredLocalHttpEndpoint(CloudProvider cloud)
    {
        using var portPicker = new TcpListener(IPAddress.Loopback, 0);
        portPicker.Start();
        var port = ((IPEndPoint)portPicker.LocalEndpoint).Port;
        portPicker.Stop();

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var response = cloud switch
        {
            CloudProvider.Hetzner => """{"servers":[]}""",
            CloudProvider.DigitalOcean => """{"droplets":[]}""",
            CloudProvider.Vultr => """{"instances":[]}""",
            _ => throw new ArgumentOutOfRangeException(nameof(cloud))
        };
        var expectedPath = cloud switch
        {
            CloudProvider.Hetzner => "/v1/servers?name=integration-probe",
            CloudProvider.DigitalOcean => "/v2/droplets?name=integration-probe",
            CloudProvider.Vultr => "/v2/instances?label=integration-probe",
            _ => throw new ArgumentOutOfRangeException(nameof(cloud))
        };

        var received = listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hetzner:BaseUrl"] = $"http://127.0.0.1:{port}/v1/",
            ["Hetzner:ApiToken"] = "test-only",
            ["DigitalOcean:BaseUrl"] = $"http://127.0.0.1:{port}/v2/",
            ["DigitalOcean:ApiToken"] = "test-only",
            ["Vultr:BaseUrl"] = $"http://127.0.0.1:{port}/v2/",
            ["Vultr:ApiKey"] = "test-only"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMinicloudProviders(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ProviderServiceCollectionExtensions.VpsProviderClientResolver>();

        var findTask = resolver(cloud).FindServerAsync("integration-probe");
        var request = await received;
        Assert.Equal("GET", request.Request.HttpMethod);
        Assert.Equal(expectedPath, request.Request.RawUrl);
        Assert.Equal("Bearer test-only", request.Request.Headers["Authorization"]);
        var bytes = Encoding.UTF8.GetBytes(response);
        request.Response.ContentType = "application/json";
        request.Response.ContentLength64 = bytes.Length;
        await request.Response.OutputStream.WriteAsync(bytes);
        request.Response.Close();
        Assert.Null(await findTask.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
