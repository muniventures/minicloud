using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Minicloud.Common.Services;

/// <summary>
/// Trusts a configured private OpenBao CA for OpenBao requests only. Process-wide
/// trust (for example SSL_CERT_FILE) would replace public roots used by provider APIs.
/// </summary>
public static class OpenBaoHttpHandler
{
    public static HttpMessageHandler Create(OpenBaoSecretStoreOptions options)
    {
        var handler = new SocketsHttpHandler();
        if (string.IsNullOrWhiteSpace(options.CaCertificateFile))
        {
            return handler;
        }

        var authority = LoadCertificateAuthority(options.CaCertificateFile);
        handler.SslOptions.RemoteCertificateValidationCallback =
            (_, certificate, _, errors) => IsTrusted(authority, certificate as X509Certificate2 ?? (certificate is null ? null : new X509Certificate2(certificate)), errors);
        return handler;
    }

    public static X509Certificate2 LoadCertificateAuthority(string path)
    {
        X509Certificate2Collection certificates;
        try
        {
            certificates = new X509Certificate2Collection();
            certificates.ImportFromPemFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidOperationException("The configured OpenBao CA certificate file could not be read.", ex);
        }

        if (certificates.Count != 1)
        {
            throw new InvalidOperationException("The configured OpenBao CA certificate file must contain exactly one certificate.");
        }

        return certificates[0];
    }

    public static bool IsTrusted(X509Certificate2 authority, X509Certificate2? certificate, SslPolicyErrors errors)
    {
        // Hostname mismatches and missing certificates are never accepted.
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(certificate);
    }
}
