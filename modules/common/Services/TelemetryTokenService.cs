using System.Security.Cryptography;
using System.Text;

namespace Minicloud.Common.Services;

public interface ITelemetryTokenService
{
    TelemetryToken Generate();
    string Hash(string token);
    bool Verify(string token, string storedHash);
    string? ExtractPrefix(string? authorizationHeader);
}

public sealed record TelemetryToken(string Prefix, string Token);

public sealed class TelemetryTokenService : ITelemetryTokenService
{
    public const string PrefixRoot = "mcl_ingest";
    private const int VisibleBytes = 8;
    private const int SecretBytes = 32;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000;

    public TelemetryToken Generate()
    {
        var visible = TokenSegment(RandomNumberGenerator.GetBytes(VisibleBytes));
        var secret = TokenSegment(RandomNumberGenerator.GetBytes(SecretBytes));
        var prefix = $"{PrefixRoot}_{visible}";
        return new TelemetryToken(prefix, $"{prefix}_{secret}");
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

    public string? ExtractPrefix(string? authorizationHeader)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        const string bearer = "Bearer ";
        if (!authorizationHeader.StartsWith(bearer, StringComparison.Ordinal))
        {
            return null;
        }

        var token = authorizationHeader[bearer.Length..].Trim();
        var parts = token.Split('_');
        if (parts.Length != 4 || $"{parts[0]}_{parts[1]}" != PrefixRoot)
        {
            return null;
        }

        return $"{parts[0]}_{parts[1]}_{parts[2]}";
    }

    private static string TokenSegment(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
