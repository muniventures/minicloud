using System.Net;
using Microsoft.Extensions.Options;
using Minicloud.Telemetry.Otlp;

namespace Minicloud.Telemetry.Services;

public sealed record OtlpForwardOutcome(
    int StatusCode,
    byte[] ResponseBody,
    TimeSpan? RetryAfter,
    long UpstreamRejected,
    bool IsRetryable);

public sealed class OtlpLokiForwarder(
    IHttpClientFactory httpClients,
    IOptions<TelemetryOptions> options,
    ILogger<OtlpLokiForwarder> logger)
{
    private const long MaxUpstreamBodyBytes = 256 * 1024;

    public async Task<OtlpForwardOutcome> ForwardAsync(
        string tenantId,
        byte[] protobufBody,
        long gatewayRejected,
        string? gatewayNotice,
        CancellationToken cancellationToken)
    {
        var config = options.Value;
        Uri endpoint;
        try
        {
            endpoint = new Uri(new Uri(config.LokiBaseAddress.TrimEnd('/') + "/"), "otlp/v1/logs");
        }
        catch (UriFormatException ex)
        {
            logger.LogError(ex, "Telemetry Loki base address is misconfigured; denying ingestion as retryable.");
            return RetryableOutcome(
                StatusCodes.Status503ServiceUnavailable,
                "Log storage is temporarily unavailable.",
                null, gatewayRejected, gatewayNotice);
        }

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Clamp(config.UpstreamTimeoutSeconds, 1, 60)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            var client = httpClients.CreateClient();
            using var content = new ByteArrayContent(protobufBody);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(OtlpProtobuf.ProtobufContentType);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            request.Headers.TryAddWithoutValidation("X-Scope-OrgID", tenantId);

            var forwardedBytes = protobufBody.Length;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            var upstreamBody = await ReadBoundedAsync(response.Content, linked.Token);
            var retryAfter = ParseRetryAfter(response.Headers.RetryAfter);
            var status = (int)response.StatusCode;

            if (status is >= 200 and <= 299)
            {
                var (upstreamRejected, upstreamError) = OtlpProtobuf.ParseExportResponse(upstreamBody);
                var totalRejected = gatewayRejected + upstreamRejected;
                var message = CombineNotices(gatewayNotice, upstreamError);
                logger.LogInformation(
                    "OTLP forward accepted: bytes={Bytes} gatewayRejected={GatewayRejected} upstreamRejected={UpstreamRejected} status={Status}",
                    forwardedBytes, gatewayRejected, upstreamRejected, status);
                return new OtlpForwardOutcome(
                    StatusCodes.Status200OK,
                    OtlpProtobuf.EncodeExportResponse(totalRejected, message ?? string.Empty),
                    null, totalRejected, false);
            }

            if (status is 429 or 502 or 503 or 504 or 408)
            {
                var message = !string.IsNullOrWhiteSpace(gatewayNotice)
                    ? gatewayNotice
                    : $"Log storage is temporarily unavailable (upstream {(int)response.StatusCode}).";
                logger.LogWarning(
                    "OTLP forward retryable: status={Status} bytes={Bytes} gatewayRejected={GatewayRejected}",
                    status, forwardedBytes, gatewayRejected);
                return RetryableOutcome(status, message, retryAfter, gatewayRejected, gatewayNotice);
            }

            var detail = ExtractUpstreamDetail(upstreamBody);
            var clientMessage = CombineNotices(gatewayNotice, detail)
                ?? $"Log storage rejected the request (upstream {status}).";
            logger.LogWarning(
                "OTLP forward rejected: status={Status} bytes={Bytes} gatewayRejected={GatewayRejected}",
                status, forwardedBytes, gatewayRejected);
            return new OtlpForwardOutcome(
                MapUpstreamClientError(status),
                OtlpProtobuf.EncodeExportResponse(gatewayRejected, clientMessage),
                null, gatewayRejected, false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "OTLP forward timed out; denying as retryable.");
            return RetryableOutcome(
                StatusCodes.Status503ServiceUnavailable,
                "Log storage timed out; retry with backoff. Duplicates are possible.",
                null, gatewayRejected, gatewayNotice);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "OTLP forward transport failure; denying as retryable.");
            return RetryableOutcome(
                StatusCodes.Status503ServiceUnavailable,
                "Log storage is unreachable; retry with backoff. Duplicates are possible.",
                null, gatewayRejected, gatewayNotice);
        }
    }

    private static OtlpForwardOutcome RetryableOutcome(
        int statusCode,
        string message,
        TimeSpan? retryAfter,
        long gatewayRejected,
        string? gatewayNotice) =>
        new(statusCode,
            OtlpProtobuf.EncodeExportResponse(gatewayRejected, CombineNotices(gatewayNotice, message) ?? message),
            retryAfter, gatewayRejected, true);

    private static string? CombineNotices(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : OtlpProtobuf.BoundError(second);
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return OtlpProtobuf.BoundError(first);
        }

        return OtlpProtobuf.BoundError($"{first} {second}");
    }

    private static string? ExtractUpstreamDetail(byte[] body)
    {
        if (body.Length == 0)
        {
            return null;
        }

        var (_, error) = OtlpProtobuf.ParseExportResponse(body);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return error;
        }

        try
        {
            var text = System.Text.Encoding.UTF8.GetString(body);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static int MapUpstreamClientError(int status) =>
        status switch
        {
            400 => StatusCodes.Status400BadRequest,
            404 => StatusCodes.Status404NotFound,
            413 => StatusCodes.Status413PayloadTooLarge,
            415 => StatusCodes.Status415UnsupportedMediaType,
            _ when status is >= 400 and < 500 => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status502BadGateway,
        };

    private static TimeSpan? ParseRetryAfter(System.Net.Http.Headers.RetryConditionHeaderValue? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value.Delta is not null)
        {
            var seconds = Math.Clamp(value.Delta.Value.TotalSeconds, 1, 300);
            return TimeSpan.FromSeconds(seconds);
        }

        if (value.Date is not null)
        {
            var delay = value.Date.Value - DateTimeOffset.UtcNow;
            if (delay < TimeSpan.FromSeconds(1))
            {
                return TimeSpan.FromSeconds(1);
            }

            return delay > TimeSpan.FromSeconds(300) ? TimeSpan.FromSeconds(300) : delay;
        }

        return null;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream();
        var buffer = new byte[16 * 1024];
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxUpstreamBodyBytes)
            {
                break;
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }
}
