using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public interface IRuntimeTokenService
{
    RuntimeTokenValue Generate();
    string Hash(string token);
    bool Verify(string token, string storedHash);
    string? ExtractPrefix(string token);
    Task<RuntimeTokenAuthenticationResult?> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken);
}

public sealed record RuntimeTokenValue(string Prefix, string Token);

public sealed record RuntimeTokenAuthenticationResult(RuntimeServer Server, RuntimeToken Token);

public sealed class RuntimeTokenService(MinicloudDbContext db, TimeProvider timeProvider) : IRuntimeTokenService
{
    private const string PrefixRoot = "mrt";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 210_000;

    public RuntimeTokenValue Generate()
    {
        var visible = TokenSegment(RandomNumberGenerator.GetBytes(6));
        var secret = TokenSegment(RandomNumberGenerator.GetBytes(32));
        var prefix = $"{PrefixRoot}_{visible}";
        return new RuntimeTokenValue(prefix, $"{prefix}_{secret}");
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

    public string? ExtractPrefix(string token)
    {
        var parts = token.Split('_', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0] == PrefixRoot ? $"{parts[0]}_{parts[1]}" : null;
    }

    public async Task<RuntimeTokenAuthenticationResult?> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        const string bearer = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rawToken = authorizationHeader[bearer.Length..].Trim();
        var prefix = ExtractPrefix(rawToken);
        if (prefix is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var candidates = await db.RuntimeTokens
            .Include(x => x.RuntimeServer)
            .ThenInclude(x => x.App)
            .Where(x => x.TokenPrefix == prefix && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var candidate in candidates)
        {
            if (candidate.ExpiresAt is not null && candidate.ExpiresAt <= now)
            {
                continue;
            }

            if (!Verify(rawToken, candidate.TokenHash))
            {
                continue;
            }

            candidate.LastUsedAt = now;
            candidate.UpdatedAt = now;
            candidate.RuntimeServer.LastSeenAt = now;
            candidate.RuntimeServer.UpdatedAt = now;
            return new RuntimeTokenAuthenticationResult(candidate.RuntimeServer, candidate);
        }

        return null;
    }

    private static string TokenSegment(byte[] bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();
}
