namespace Minicloud.Common.Registry;

public sealed class RegistryOptions
{
    public bool Enabled { get; set; }
    public bool AllowInsecureLoopback { get; set; }
    public string PublicUrl { get; set; } = "";
    public string BackendUrl { get; set; } = "http://registry:5000";
    public string CertificatePath { get; set; } = "/var/lib/minicloud/artifacts/registry-auth/cert.pem";
    public string KeyPath { get; set; } = "/var/lib/minicloud/artifacts/registry-auth/key.pem";
    public string StoragePath { get; set; } = "/var/lib/minicloud/artifacts/registry";
    public string Issuer { get; set; } = "minicloud-registry";
    public string Audience { get; set; } = "minicloud-registry";
    public long MinFreeBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public Dictionary<string, string> PlannedPlatforms { get; set; } = new(StringComparer.Ordinal);
}
