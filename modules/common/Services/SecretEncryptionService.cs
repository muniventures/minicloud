using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Minicloud.Common.Services;

public sealed class SecretEncryptionService(IOptions<SecretEncryptionOptions> options)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key = SHA256.HashData(Encoding.UTF8.GetBytes(options.Value.EncryptionKey));

    public string Encrypt(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return string.Join(".", "v1", Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(ciphertext));
    }

    public string Decrypt(string encryptedValue)
    {
        var parts = encryptedValue.Split('.');
        if (parts.Length != 4 || parts[0] != "v1")
        {
            throw new InvalidOperationException("Secret value is not in a supported format.");
        }

        var nonce = Convert.FromBase64String(parts[1]);
        var tag = Convert.FromBase64String(parts[2]);
        var ciphertext = Convert.FromBase64String(parts[3]);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
