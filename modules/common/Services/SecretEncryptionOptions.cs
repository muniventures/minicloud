namespace Minicloud.Common.Services;

public sealed class SecretEncryptionOptions
{
    public const string SectionName = "Secrets";

    public string EncryptionKey { get; set; } = "minicloud-development-secret-key";

    public string? EncryptionKeyFile { get; set; }
}
