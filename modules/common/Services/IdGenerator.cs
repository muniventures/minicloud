using System.Security.Cryptography;

namespace Minicloud.Common.Services;

public interface IIdGenerator
{
    string NewId(string prefix);
}

public sealed class IdGenerator : IIdGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public string NewId(string prefix)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        Span<char> chars = stackalloc char[24];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[bytes[i % bytes.Length] % Alphabet.Length];
        }

        return $"{prefix}_{new string(chars)}";
    }
}
