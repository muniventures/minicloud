using System.Security.Cryptography;
using System.Text;

namespace Minicloud.Common.Services;

public interface IApiKeyTokenService
{
    ApiKeyToken Generate();
    string Hash(string token);
    bool Verify(string token, string storedHash);
}

public sealed record ApiKeyToken(string Prefix, string Token);

public sealed class ApiKeyTokenService : IApiKeyTokenService
{
    private const string PrefixRoot = "mc_live";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000;

    public ApiKeyToken Generate()
    {
        var visible = TokenSegment(RandomNumberGenerator.GetBytes(6));
        var secret = TokenSegment(RandomNumberGenerator.GetBytes(32));
        var prefix = $"{PrefixRoot}_{visible}";
        return new ApiKeyToken(prefix, $"{prefix}_{secret}");
    }

    public string Hash(string token)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(token),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

        return $"pbkdf2_sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string token, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2_sha256" || !int.TryParse(parts[1], out var iterations))
        {
            return false;
        }

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(token),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string TokenSegment(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
