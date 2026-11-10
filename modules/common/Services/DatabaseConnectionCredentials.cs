namespace Minicloud.Common.Services;

public sealed record DatabaseConnectionCredentials(string DatabaseName, string Username, string Password, string? Host = null, int Port = 5432, string? SslMode = null, string? CaCertificatePem = null)
{
    public override string ToString() => "[redacted database connection credentials]";
}
