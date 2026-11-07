using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Minicloud.Common.Registry;

public sealed class RegistryTokens(IOptions<RegistryOptions> options, TimeProvider clock)
{
    private readonly RegistryOptions _options = options.Value;
    public static string Hash(string secret) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight((text.Length + 3) / 4 * 4, '='));

    public string Issue(string session, string repository, bool push, DateTimeOffset expires, bool includeAccess = true, bool delete = false, bool pull = true)
    {
        using var cert = X509Certificate2.CreateFromPemFile(_options.CertificatePath, _options.KeyPath);
        var now = clock.GetUtcNow();
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { typ = "JWT", alg = "RS256", x5c = new[] { Convert.ToBase64String(cert.RawData) } }));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = _options.Issuer,
            aud = _options.Audience,
            sub = session,
            iat = now.ToUnixTimeSeconds(),
            nbf = now.AddSeconds(-10).ToUnixTimeSeconds(),
            exp = (expires < now.AddMinutes(5) ? expires : now.AddMinutes(5)).ToUnixTimeSeconds(),
            jti = Guid.NewGuid().ToString("N"),
            access = (includeAccess ? new[] { new { type = "repository", name = repository,
                actions = new[] { pull ? "pull" : null, push ? "push" : null, delete ? "delete" : null }.Where(x => x is not null).ToArray() } } : [])
        }));
        var unsigned = header + "." + payload;
        using var rsa = cert.GetRSAPrivateKey()!;
        return unsigned + "." + Encode(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public string? Verify(string token)
    {
        if (token.Length > 16384) { return null; }
        try
        {
            var split = token.Split('.');
            if (split.Length != 3) { return null; }
            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(_options.CertificatePath));
            using var rsa = cert.GetRSAPublicKey()!;
            if (!rsa.VerifyData(Encoding.ASCII.GetBytes(split[0] + "." + split[1]), Decode(split[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) { return null; }
            using var header = JsonDocument.Parse(Decode(split[0]));
            using var payload = JsonDocument.Parse(Decode(split[1]));
            var claims = payload.RootElement;
            var now = clock.GetUtcNow().ToUnixTimeSeconds();
            if (header.RootElement.GetProperty("alg").GetString() != "RS256" || claims.GetProperty("iss").GetString() != _options.Issuer ||
                claims.GetProperty("aud").GetString() != _options.Audience || claims.GetProperty("exp").GetInt64() <= now || claims.GetProperty("nbf").GetInt64() > now) { return null; }
            return claims.GetProperty("sub").GetString();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or CryptographicException) { return null; }
    }
}
