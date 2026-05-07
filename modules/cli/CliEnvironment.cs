namespace Municloud.Cli;

public sealed class CliEnvironment
{
    public const string TokenEnvironmentVariable = "MUNICLOUD_TOKEN";
    public const string ApiUrlEnvironmentVariable = "MUNICLOUD_API_URL";
    public const string RegistryHostEnvironmentVariable = "MUNICLOUD_REGISTRY_HOST";
    public const string RegistryGhcrOwnerEnvironmentVariable = "MUNICLOUD_REGISTRY_GHCR_OWNER";
    public const string RuntimeRegistryPrefixEnvironmentVariable = "MUNICLOUD_RUNTIME_REGISTRY_PREFIX";
    public const string LocalOrganizationSlugEnvironmentVariable = "MUNICLOUD_LOCAL_ORGANIZATION_SLUG";

    private CliEnvironment(string apiBaseUrl, string registryHost, string registryGhcrOwner, string runtimeRegistryPrefix, string localOrganizationSlug, string configHome)
    {
        ApiBaseUrl = apiBaseUrl.TrimEnd('/');
        RegistryHost = registryHost.Trim().TrimEnd('/');
        RegistryGhcrOwner = registryGhcrOwner.Trim().ToLowerInvariant();
        RuntimeRegistryPrefix = runtimeRegistryPrefix.Trim().TrimEnd('/');
        LocalOrganizationSlug = localOrganizationSlug.Trim().ToLowerInvariant();
        ConfigHome = configHome;
    }

    public string ApiBaseUrl { get; }
    public string RegistryHost { get; }
    public string RegistryGhcrOwner { get; }
    public string RuntimeRegistryPrefix { get; }
    public string LocalOrganizationSlug { get; }
    public string ConfigHome { get; }
    public string TokenFilePath => Path.Combine(ConfigHome, "token");

    public static CliEnvironment FromEnvironment()
    {
        var apiBaseUrl = Environment.GetEnvironmentVariable(ApiUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(apiBaseUrl))
        {
            apiBaseUrl = "https://cloud.muni.dev/api";
        }

        var registryHost = Environment.GetEnvironmentVariable(RegistryHostEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(registryHost))
        {
            registryHost = DefaultRegistryHostForApiBaseUrl(apiBaseUrl);
        }

        var registryGhcrOwner = Environment.GetEnvironmentVariable(RegistryGhcrOwnerEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(registryGhcrOwner))
        {
            registryGhcrOwner = "muniventures";
        }

        var runtimeRegistryPrefix = Environment.GetEnvironmentVariable(RuntimeRegistryPrefixEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(runtimeRegistryPrefix))
        {
            runtimeRegistryPrefix = $"ghcr.io/{registryGhcrOwner}";
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

        return new CliEnvironment(apiBaseUrl, registryHost, registryGhcrOwner, runtimeRegistryPrefix, localOrganizationSlug, Path.Combine(configHome, "municloud"));
    }

    public static CliEnvironment ForTests(string apiBaseUrl, string configHome) => new(apiBaseUrl, "registry.muni.dev", "municloud", "ghcr.io/municloud", "local", configHome);

    public static CliEnvironment ForTests(
        string apiBaseUrl,
        string configHome,
        string registryHost,
        string runtimeRegistryPrefix,
        string localOrganizationSlug) =>
        new(apiBaseUrl, registryHost, "municloud", runtimeRegistryPrefix, localOrganizationSlug, configHome);

    private static string DefaultRegistryHostForApiBaseUrl(string apiBaseUrl)
    {
        return Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("cloud-dev.muni.dev", StringComparison.OrdinalIgnoreCase)
            ? "registry-dev.muni.dev"
            : "registry.muni.dev";
    }
}
