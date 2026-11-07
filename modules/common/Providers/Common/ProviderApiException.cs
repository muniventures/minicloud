using System.Net;

namespace Minicloud.Common.Providers.Common;

public class ProviderApiException : HttpRequestException
{
    public CloudProvider? Provider { get; }
    public string? ResponseBody { get; }

    public ProviderApiException(
        string message,
        HttpStatusCode? statusCode = null,
        CloudProvider? provider = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(TokenRedactor.Redact(message), innerException, statusCode)
    {
        Provider = provider;
        ResponseBody = TokenRedactor.Redact(responseBody);
    }
}
