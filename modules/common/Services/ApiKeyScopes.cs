namespace Minicloud.Common.Services;

public static class ApiKeyScopes
{
    public const string AppsRead = "apps:read";
    public const string AppsWrite = "apps:write";
    public const string DeploymentsCreate = "deployments:create";
    public const string DeploymentsRead = "deployments:read";
    public const string ApiKeysRead = "api_keys:read";
    public const string ApiKeysWrite = "api_keys:write";
    public const string SecretsRead = "secrets:read";
    public const string SecretsWrite = "secrets:write";
    public const string LogsRead = "logs:read";
    public const string TelemetryManage = "telemetry:manage";
    public const string ProvidersRead = "providers:read";
    public const string ProvidersWrite = "providers:write";
    public const string PlansRead = "plans:read";
    public const string PlansWrite = "plans:write";

    public static readonly IReadOnlyList<string> CliLoginDefault =
    [
        AppsRead,
        AppsWrite,
        DeploymentsCreate,
        DeploymentsRead,
        SecretsRead,
        SecretsWrite,
        LogsRead,
        PlansRead
    ];

    public static readonly ISet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        AppsRead,
        AppsWrite,
        DeploymentsCreate,
        DeploymentsRead,
        ApiKeysRead,
        ApiKeysWrite,
        SecretsRead,
        SecretsWrite,
        LogsRead,
        TelemetryManage,
        ProvidersRead,
        ProvidersWrite,
        PlansRead,
        PlansWrite
    };
}
