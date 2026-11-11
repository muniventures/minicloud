using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class OpenBaoCertificateTrustTests
{
    [Fact]
    public async Task Configured_private_ca_is_trusted_only_for_matching_openbao_hostname()
    {
        var directory = Path.Combine(Path.GetTempPath(), "minicloud-openbao-ca-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var authority = CreateAuthority("Synthetic OpenBao CA");
            using var otherAuthority = CreateAuthority("Unrelated CA");
            var caFile = Path.Combine(directory, "openbao-ca.crt");
            var otherFile = Path.Combine(directory, "other-ca.crt");
            await File.WriteAllTextAsync(caFile, authority.ExportCertificatePem());
            await File.WriteAllTextAsync(otherFile, otherAuthority.ExportCertificatePem());

            await using (var server = await TlsServer.StartAsync(CreateLeaf(authority, "localhost")))
            {
                Assert.Equal(HttpStatusCode.OK, await GetAsync(caFile, server.Uri));
                await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(otherFile, server.Uri));
                await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(null, server.Uri));
            }

            await using (var mismatched = await TlsServer.StartAsync(CreateLeaf(authority, "openbao")))
                await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(caFile, mismatched.Uri));

            var empty = Path.Combine(directory, "empty.crt");
            await File.WriteAllTextAsync(empty, "");
            Assert.Throws<InvalidOperationException>(() => OpenBaoHttpHandler.Create(new OpenBaoSecretStoreOptions { CaCertificateFile = empty }));
            Assert.Throws<InvalidOperationException>(() => OpenBaoHttpHandler.Create(
                new OpenBaoSecretStoreOptions { CaCertificateFile = Path.Combine(directory, "missing.crt") }));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<HttpStatusCode> GetAsync(string? caFile, Uri uri)
    {
        using var client = new HttpClient(OpenBaoHttpHandler.Create(new OpenBaoSecretStoreOptions { CaCertificateFile = caFile ?? "" }));
        using var response = await client.GetAsync(uri);
        return response.StatusCode;
    }

    private static X509Certificate2 CreateAuthority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 authority, string hostname)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostname}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostname);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var signed = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30),
            RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
    }

    private sealed class TlsServer(WebApplication app, Uri uri) : IAsyncDisposable
    {
        public Uri Uri { get; } = uri;

        public static async Task<TlsServer> StartAsync(X509Certificate2 certificate)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
                options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
            var app = builder.Build();
            app.MapGet("/v1/sys/health", () => Results.Ok());
            await app.StartAsync();
            return new TlsServer(app, new Uri($"https://localhost:{port}/v1/sys/health"));
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }
}
