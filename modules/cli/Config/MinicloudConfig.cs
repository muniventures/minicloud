namespace Minicloud.Cli.Config;

public sealed record MinicloudConfig(
    string App,
    string? Database,
    string? CommitSha,
    IReadOnlyDictionary<string, MinicloudServiceConfig> Services)
{
    public string? AppId { get; init; }
}

public sealed record MinicloudServiceConfig(
    string? SourcePath,
    string? Dockerfile,
    string? Image,
    int? Port,
    bool? Public,
    string? Path,
    string? HealthPath,
    IReadOnlyDictionary<string, string>? Env = null);

public sealed record ConfigDiagnostic(string Field, string Message);

public sealed record ConfigLoadResult(MinicloudConfig? Config, IReadOnlyList<ConfigDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0 && Config is not null;
}
