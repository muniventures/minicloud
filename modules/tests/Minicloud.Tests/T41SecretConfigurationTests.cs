using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Minicloud.Api.Extensions;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class T41SecretConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    public void Api_registration_does_not_replace_empty_configured_keys(string key)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:EncryptionKey"] = key,
            ["Secrets:EncryptionKeyFile"] = null
        });
        builder.Services.AddMinicloudApi(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() =>
            provider.GetRequiredService<SecretEncryptionService>());
    }

    [Fact]
    public void Api_registration_binds_explicit_fixture_key_without_substitution()
    {
        const string key = "synthetic-explicit-t41-fixture-key";
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Secrets:EncryptionKey"] = key,
            ["Secrets:EncryptionKeyFile"] = null
        });
        builder.Services.AddMinicloudApi(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        var encryption = provider.GetRequiredService<SecretEncryptionService>();
        var independent = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions
        {
            EncryptionKey = key
        }));

        Assert.Equal("synthetic-value", independent.Decrypt(encryption.Encrypt("synthetic-value")));
    }
}
