namespace Minicloud.Common.Contracts;

public sealed record DatabaseConnectionResponse(string DatabaseId, string Engine, string AccessMode, string DatabaseName, string Username, string Password, string? Host, int Port, string? SslMode, bool EndpointAvailable, string? CaCertificatePem = null)
{
    public override string ToString() => "[redacted database connection response]";
}
