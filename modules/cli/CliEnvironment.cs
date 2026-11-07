// SPDX-License-Identifier: MPL-2.0

namespace Minicloud.Cli;

public sealed class CliEnvironment
{
    public const string TokenEnvironmentVariable = "MINICLOUD_TOKEN";
    public const string ApiUrlEnvironmentVariable = "MINICLOUD_API_URL";
    public const string LocalOrganizationSlugEnvironmentVariable = "MINICLOUD_LOCAL_ORGANIZATION_SLUG";

    private CliEnvironment(string apiBaseUrl, string localOrganizationSlug, string configHome, string appDomain = "app.muni.dev")
    {
        ApiBaseUrl = apiBaseUrl.TrimEnd('/');
        LocalOrganizationSlug = localOrganizationSlug.Trim().ToLowerInvariant();
        ConfigHome = configHome;
        AppDomain = appDomain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    public string AppDomain { get; }
    public string ApiBaseUrl { get; }
    public string LocalOrganizationSlug { get; }
    public string ConfigHome { get; }
    public string TokenFilePath => Path.Combine(ConfigHome, "token");

    public static CliEnvironment FromEnvironment()
    {
        var apiBaseUrl = Environment.GetEnvironmentVariable(ApiUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            apiBaseUrl = "https://api.cloud.muni.dev";
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
        if (string.IsNullOrWhiteSpace(appDomain)) appDomain = "app.muni.dev";
        return new CliEnvironment(apiBaseUrl, localOrganizationSlug, Path.Combine(configHome, "minicloud"), appDomain);
    }

    public static CliEnvironment ForTests(string apiBaseUrl, string configHome) => new(apiBaseUrl, "local", configHome);
}
