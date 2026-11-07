using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Minicloud.Common.Services;

public sealed record TelemetryReadClaim(
    string Sub,
    string OrganizationId,
    string AppId,
    string TenantId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public static class TelemetryReadToken
{
    private const string TokenPrefix = "mcl_rt_";

    public static string Create(
        string sub,
        string organizationId,
        string appId,
        string tenantId,
        TimeSpan lifetime,
        string signingKey,
        TimeProvider timeProvider) =>
        Create(sub, organizationId, appId, tenantId, lifetime, Encoding.UTF8.GetBytes(signingKey), timeProvider);

    public static string Create(
        string sub,
        string organizationId,
        string appId,
        string tenantId,
        TimeSpan lifetime,
        byte[] signingKey,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        var expires = now.Add(lifetime);
        var claim = new TelemetryReadClaim(sub, organizationId, appId, tenantId, now, expires);
        var json = JsonSerializer.Serialize(claim);
        var payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(json));

        using var hmac = new HMACSHA256(signingKey);
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        var signatureBase64 = Base64UrlEncode(signatureBytes);

        return $"{TokenPrefix}{payloadBase64}.{signatureBase64}";
    }

    public static bool TryValidate(
        string token,
        string signingKey,
        TimeProvider timeProvider,
        out TelemetryReadClaim? claim,
        out string? error) =>
        TryValidate(token, Encoding.UTF8.GetBytes(signingKey), timeProvider, out claim, out error);

    public static bool TryValidate(
        string token,
        byte[] signingKey,
        TimeProvider timeProvider,
        out TelemetryReadClaim? claim,
        out string? error)
    {
        claim = null;
        error = null;

        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            error = "malformed_token";
            return false;
        }

        var tokenBody = token[TokenPrefix.Length..];
        var dotIndex = tokenBody.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= tokenBody.Length - 1)
        {
            error = "malformed_token";
            return false;
        }

        var payloadBase64 = tokenBody[..dotIndex];
        var signatureBase64 = tokenBody[(dotIndex + 1)..];

        byte[] expectedSig;
        using (var hmac = new HMACSHA256(signingKey))
        {
            expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadBase64));
        }

        byte[] actualSig;
        try
        {
            actualSig = Base64UrlDecode(signatureBase64);
        }
        catch
        {
            error = "invalid_signature";
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSig, actualSig))
        {
            error = "invalid_signature";
            return false;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(payloadBase64);
            var parsed = JsonSerializer.Deserialize<TelemetryReadClaim>(payloadBytes);
            if (parsed is null)
            {
                error = "invalid_payload";
                return false;
            }

            var now = timeProvider.GetUtcNow();
            if (now > parsed.ExpiresAt)
            {
                error = "expired_token";
                return false;
            }

            claim = parsed;
            return true;
        }
        catch
        {
            error = "invalid_payload";
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        return Convert.FromBase64String(base64);
    }
}
