using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class SecretEncryptionFileTests
{
    [Fact]
    public void Encryption_key_can_be_loaded_from_a_protected_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-encryption-key-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "an independently generated secret key\r\n");
            var service = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions
            {
                EncryptionKeyFile = path
            }));

            var ciphertext = service.Encrypt("synthetic-value");
            Assert.Equal("synthetic-value", service.Decrypt(ciphertext));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Configured_missing_or_empty_encryption_key_file_fails_without_fallback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"minicloud-encryption-key-{Guid.NewGuid():N}");
        var missing = Options.Create(new SecretEncryptionOptions
        {
            EncryptionKey = "must-not-be-used",
            EncryptionKeyFile = path
        });
        Assert.Throws<FileNotFoundException>(() => new SecretEncryptionService(missing));
        File.WriteAllText(path, "\n");
        try
        {
            Assert.Throws<InvalidOperationException>(() => new SecretEncryptionService(missing));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
