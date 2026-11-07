namespace Minicloud.Common.Services;

public sealed class OpenBaoSecretStoreOptions
{
    public const string SectionName = "OpenBao";

    public string Address { get; set; } = "";
    public string Token { get; set; } = "";
    public string MountPath { get; set; } = "minicloud-apps";
    public string EnvironmentPrefix { get; set; } = "dev";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
