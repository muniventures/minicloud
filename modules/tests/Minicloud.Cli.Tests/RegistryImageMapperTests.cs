using Minicloud.Cli;

namespace Minicloud.Tests;

public sealed class RegistryImageMapperTests
{
    [Theory]
    [InlineData("https://api.cloud-dev.muni.dev", "registry-dev.muni.dev")]
    [InlineData("https://api.cloud.muni.dev", "registry.muni.dev")]
    public void FromEnvironment_defaults_registry_host_from_api_environment(string apiBaseUrl, string expectedRegistryHost)
    {
        var originalApiUrl = Environment.GetEnvironmentVariable(CliEnvironment.ApiUrlEnvironmentVariable);
        var originalRegistryHost = Environment.GetEnvironmentVariable(CliEnvironment.RegistryHostEnvironmentVariable);
        var originalRegistryGhcrOwner = Environment.GetEnvironmentVariable(CliEnvironment.RegistryGhcrOwnerEnvironmentVariable);
        var originalRuntimeRegistryPrefix = Environment.GetEnvironmentVariable(CliEnvironment.RuntimeRegistryPrefixEnvironmentVariable);
        var originalConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable(CliEnvironment.ApiUrlEnvironmentVariable, apiBaseUrl);
            Environment.SetEnvironmentVariable(CliEnvironment.RegistryHostEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(CliEnvironment.RegistryGhcrOwnerEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(CliEnvironment.RuntimeRegistryPrefixEnvironmentVariable, null);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", "/tmp/minicloud-tests");

            var environment = CliEnvironment.FromEnvironment();

            Assert.Equal(expectedRegistryHost, environment.RegistryHost);
            Assert.Equal("muniventures", environment.RegistryGhcrOwner);
            Assert.Equal("ghcr.io/muniventures", environment.RuntimeRegistryPrefix);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CliEnvironment.ApiUrlEnvironmentVariable, originalApiUrl);
            Environment.SetEnvironmentVariable(CliEnvironment.RegistryHostEnvironmentVariable, originalRegistryHost);
            Environment.SetEnvironmentVariable(CliEnvironment.RegistryGhcrOwnerEnvironmentVariable, originalRegistryGhcrOwner);
            Environment.SetEnvironmentVariable(CliEnvironment.RuntimeRegistryPrefixEnvironmentVariable, originalRuntimeRegistryPrefix);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", originalConfigHome);
        }
    }

    [Fact]
    public void RuntimeImageForDeployment_maps_minicloud_registry_ref_to_runtime_registry()
    {
        var environment = CliEnvironment.ForTests(
            "https://api.cloud.muni.dev",
            "/tmp/minicloud-tests",
            "localhost:5050",
            "localhost:5051/minicloud-local",
            "local");
        var mapper = new RegistryImageMapper(environment);

        var image = mapper.RuntimeImageForDeployment("localhost:5050/teamcore/backend:latest", "Acme-Corp");

        Assert.Equal("localhost:5051/minicloud-local/acme-corp-teamcore-backend:latest", image);
    }

    [Fact]
    public void RuntimeImageForDeployment_leaves_external_refs_unchanged()
    {
        var mapper = new RegistryImageMapper(CliEnvironment.ForTests("https://api.cloud.muni.dev", "/tmp/minicloud-tests"));

        var image = mapper.RuntimeImageForDeployment("ghcr.io/customer/teamcore/backend:abc123", "acme");

        Assert.Equal("ghcr.io/customer/teamcore/backend:abc123", image);
    }

    [Theory]
    [InlineData("localhost:5050/teamcore/backend:latest", true)]
    [InlineData("registry.muni.dev/teamcore/backend:latest", false)]
    [InlineData("ghcr.io/customer/teamcore/backend:latest", false)]
    [InlineData("", false)]
    public void UsesMinicloudRegistry_matches_configured_host(string image, bool expected)
    {
        var environment = CliEnvironment.ForTests(
            "https://api.cloud.muni.dev",
            "/tmp/minicloud-tests",
            "localhost:5050",
            "localhost:5051/minicloud-local",
            "local");
        var mapper = new RegistryImageMapper(environment);

        Assert.Equal(expected, mapper.UsesMinicloudRegistry(image));
    }
}
