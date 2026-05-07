namespace Municloud.Cli.Config;

public sealed record MunicloudConfig(
    string App,
    string? Environment,
    string? DeploymentType,
    string? Database,
    string? CommitSha,
    IReadOnlyDictionary<string, MunicloudServiceConfig> Services);

public sealed record MunicloudServiceConfig(
    string? SourcePath,
    string? Dockerfile,
    string? Image,
    int? Port,
    bool? Public,
    string? Path,
    string? HealthPath,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record ConfigDiagnostic(string Field, string Message);

public sealed record ConfigLoadResult(MunicloudConfig? Config, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0 && Config is not null;
}
