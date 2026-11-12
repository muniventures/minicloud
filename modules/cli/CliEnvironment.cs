// SPDX-License-Identifier: MPL-2.0

using System.Security.Cryptography;
using System.Text;
using Minicloud.Cli.Config;

namespace Minicloud.Cli;

public sealed class CliEnvironment
{
    public const string TokenEnvironmentVariable = "MINICLOUD_TOKEN";
    public const string ApiUrlEnvironmentVariable = "MINICLOUD_API_URL";
    public const string LocalOrganizationSlugEnvironmentVariable = "MINICLOUD_LOCAL_ORGANIZATION_SLUG";

    private CliEnvironment(string apiBaseUrl, string localOrganizationSlug, string configHome, string appDomain = "")
    {
        ApiBaseUrl = apiBaseUrl.TrimEnd('/');
        LocalOrganizationSlug = localOrganizationSlug.Trim().ToLowerInvariant();
        ConfigHome = configHome;
        AppDomain = appDomain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    public string AppDomain { get; private set; }
    public string ApiBaseUrl { get; private set; }
    public string LocalOrganizationSlug { get; }
    public string ConfigHome { get; }
    public string? ActiveInstanceName { get; private set; }
    public string? InstanceUrl { get; private set; }
    public bool HasApiOverride { get; private set; }
    public InstanceStore Instances => new(ConfigHome);
    public string TokenFilePath => Path.Combine(ConfigHome, "tokens",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiBaseUrl))) + ".token");

    public void SetAppDomain(string appDomain) => AppDomain = appDomain;

    public void SelectInstance(InstanceProfile profile)
    {
        ActiveInstanceName = profile.Name;
        InstanceUrl = profile.Url;
        ApiBaseUrl = InstanceStore.DeriveApiUrl(profile.Url);
        AppDomain = profile.AppDomain;
        HasApiOverride = false;
    }

    public void LoadActiveInstance()
    {
        if (!string.IsNullOrWhiteSpace(ApiBaseUrl)) return;
        var state = Instances.Load();
        var active = state.Instances.SingleOrDefault(x => x.Name == state.Active);
        if (active is not null) SelectInstance(active);
    }

    public static CliEnvironment FromEnvironment()
    {
        var apiBaseUrl = Environment.GetEnvironmentVariable(ApiUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            apiBaseUrl = "";
        }

        var localOrganizationSlug = Environment.GetEnvironmentVariable(LocalOrganizationSlugEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(localOrganizationSlug))
        {
            localOrganizationSlug = "local";
        }

        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configHome))
        {
            configHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        var appDomain = Environment.GetEnvironmentVariable("MINICLOUD_APP_DOMAIN");
        if (string.IsNullOrWhiteSpace(appDomain)) appDomain = "";
        return new CliEnvironment(apiBaseUrl, localOrganizationSlug, Path.Combine(configHome, "minicloud"), appDomain)
        { HasApiOverride = !string.IsNullOrWhiteSpace(apiBaseUrl) };
    }

    public static CliEnvironment ForTests(string apiBaseUrl, string configHome) => new(apiBaseUrl, "local", configHome, "app.example.com");

    internal static CliEnvironment ForInstance(InstanceProfile profile, string configHome)
    {
        var result = new CliEnvironment("", "local", configHome);
        result.SelectInstance(profile);
        return result;
    }
}
