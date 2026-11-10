namespace Minicloud.Common.Contracts;

public sealed record DatabaseBackupRecoveryMaterialResponse(string DatabaseId, string Engine, string KeyVersion, string EncryptionKey)
{
    public override string ToString() => "[redacted backup recovery material]";
}
