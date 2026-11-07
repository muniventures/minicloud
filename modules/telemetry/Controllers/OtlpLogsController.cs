using System.IO.Compression;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Minicloud.Telemetry.Otlp;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Services;

namespace Minicloud.Telemetry.Controllers;

[Route("otlp/v1/logs")]
public sealed class OtlpLogsController(
    TelemetryCredentialAuthService auth,
    OtlpNormalizationService normalization,
    TelemetryQuotaService quotas,
    OtlpLokiForwarder forwarder,
    OtlpIngestionLimiter limiter,
    TelemetryStatusReporter reporter,
    TimeProvider timeProvider,
    ILogger<OtlpLogsController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var authorizationValues = Request.Headers["Authorization"];
        if (authorizationValues.Count > 1)
        {
            return OtlpError(
                StatusCodes.Status401Unauthorized,
                "Ambiguous credentials: exactly one Authorization header is required.",
                null);
        }

        TelemetryAuthSnapshot identity;
        try
        {
            var snapshot = await auth.AuthenticateAsync(
                authorizationValues.Count == 1 ? authorizationValues[0] : null,
                cancellationToken);
            if (snapshot is null)
            {
                return OtlpError(
                    StatusCodes.Status401Unauthorized,
                    "Unknown, revoked, or expired ingestion credential.",
                    null);
            }

            identity = snapshot;
        }
        catch (TelemetryAuthUnavailableException)
        {
            logger.LogWarning("OTLP auth authority unavailable; denying as retryable.");
            return OtlpError(
                StatusCodes.Status503ServiceUnavailable,
                "Telemetry authority is temporarily unavailable; retry with backoff.",
                TimeSpan.FromSeconds(5));
        }

        var mediaType = (Request.ContentType?.Split(';', 2)[0] ?? string.Empty).Trim();
        if (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return OtlpError(
                StatusCodes.Status415UnsupportedMediaType,
                "HTTP/JSON is unsupported; send application/x-protobuf ExportLogsServiceRequest.",
                null);
        }

        if (mediaType.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
        {
            return OtlpError(
                StatusCodes.Status415UnsupportedMediaType,
                "gRPC is unsupported; send OTLP/HTTP protobuf to this endpoint.",
                null);
        }

        if (!string.Equals(mediaType, OtlpProtobuf.ProtobufContentType, StringComparison.OrdinalIgnoreCase))
        {
            return OtlpError(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported media type; send application/x-protobuf ExportLogsServiceRequest.",
                null);
        }

        var encodings = Request.Headers["Content-Encoding"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useGzip = false;
        if (encodings.Length == 1 && string.Equals(encodings[0], "gzip", StringComparison.OrdinalIgnoreCase))
        {
            useGzip = true;
        }
        else if (encodings.Length == 1 && (encodings[0].Length == 0 || string.Equals(encodings[0], "identity", StringComparison.OrdinalIgnoreCase)))
        {
            useGzip = false;
        }
        else if (encodings.Length > 0)
        {
            return OtlpError(
                StatusCodes.Status415UnsupportedMediaType,
                "Unsupported content encoding; use identity or gzip.",
                null);
        }

        _ = Request.Headers.Remove("X-Scope-OrgID");

        if (!limiter.TryEnter())
        {
            return OtlpError(
                StatusCodes.Status503ServiceUnavailable,
                "Ingestion is temporarily overloaded; retry with backoff.",
                TimeSpan.FromSeconds(5));
        }

        try
        {
            return await ExportCoreAsync(identity, useGzip, cancellationToken);
        }
        finally
        {
            limiter.Release();
        }
    }

    private async Task<IActionResult> ExportCoreAsync(
        TelemetryAuthSnapshot identity,
        bool useGzip,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength > TelemetryLimits.MaxEncodedBytes)
        {
            return OtlpError(
                StatusCodes.Status413PayloadTooLarge,
                $"Encoded request exceeds {TelemetryLimits.MaxEncodedBytes} bytes.",
                null);
        }

        byte[] encoded;
        try
        {
            encoded = await ReadBoundedAsync(Request.Body, TelemetryLimits.MaxEncodedBytes, cancellationToken);
        }
        catch (OtlpRequestValidationException ex)
        {
            return OtlpError(StatusCodes.Status413PayloadTooLarge, ex.Message, null);
        }

        byte[] payload;
        if (useGzip)
        {
            try
            {
                payload = await DecompressBoundedAsync(encoded, cancellationToken);
            }
            catch (OtlpRequestValidationException ex)
            {
                return OtlpError(StatusCodes.Status413PayloadTooLarge, ex.Message, null);
            }
            catch (InvalidDataException)
            {
                return OtlpError(StatusCodes.Status400BadRequest, "Content-Encoding gzip body is malformed.", null);
            }
        }
        else
        {
            payload = encoded;
        }

        OtlpExportRequest request;
        try
        {
            request = OtlpProtobuf.ParseExportRequest(payload);
        }
        catch (OtlpDecodeException ex)
        {
            return OtlpError(StatusCodes.Status400BadRequest, ex.Message, null);
        }

        var receivedAt = timeProvider.GetUtcNow();
        OtlpNormalizationResult normalized;
        try
        {
            normalized = await normalization.NormalizeAsync(request, identity, receivedAt, cancellationToken);
        }
        catch (OtlpRequestValidationException ex)
        {
            return OtlpError(StatusCodes.Status400BadRequest, ex.Message, null);
        }

        if (normalized.TotalRecords == 0)
        {
            return OtlpResult(StatusCodes.Status200OK, [], null);
        }

        if (normalized.AcceptedRecords == 0)
        {
            return OtlpResult(
                StatusCodes.Status400BadRequest,
                OtlpProtobuf.EncodeExportResponse(
                    normalized.RejectedRecords,
                    normalized.Notice ?? "All log records were rejected by gateway validation."),
                null);
        }

        var quota = await quotas.ReserveAsync(
            identity.AppId, identity.OrganizationId, normalized.NormalizedBytes, cancellationToken);
        if (!quota.Admitted)
        {
            logger.LogWarning(
                "OTLP quota denied: reason={Reason} bytes={Bytes} rejected={Rejected}",
                quota.Reason, normalized.NormalizedBytes, normalized.RejectedRecords);
            return OtlpResult(
                StatusCodes.Status429TooManyRequests,
                OtlpProtobuf.EncodeExportResponse(
                    normalized.RejectedRecords,
                    quota.Reason ?? "Admission budget exceeded; retry with backoff."),
                quota.RetryAfter);
        }

        var forwardBody = normalized.ForwardRequest.ToByteArray();
        var outcome = await forwarder.ForwardAsync(
            identity.TenantId, forwardBody, normalized.RejectedRecords, normalized.Notice, cancellationToken);

        if (outcome.StatusCode is >= 200 and <= 299)
        {
            reporter.RecordLastAccepted(identity.AppId, receivedAt);
        }

        return OtlpResult(outcome.StatusCode, outcome.ResponseBody, outcome.RetryAfter);
    }

    private IActionResult OtlpError(int statusCode, string message, TimeSpan? retryAfter) =>
        OtlpResult(statusCode, OtlpProtobuf.EncodeExportResponse(0, message), retryAfter);

    private IActionResult OtlpResult(int statusCode, byte[] body, TimeSpan? retryAfter)
    {
        if (retryAfter is not null)
        {
            var seconds = Math.Clamp((int)Math.Ceiling(retryAfter.Value.TotalSeconds), 1, 300);
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Response.StatusCode = statusCode;
        return File(body, OtlpProtobuf.ProtobufContentType);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0L;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new OtlpRequestValidationException($"Encoded request exceeds {maxBytes} bytes.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }

    private static async Task<byte[]> DecompressBoundedAsync(byte[] encoded, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(encoded, writable: false);
        await using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0L;
        int read;
        while ((read = await gzip.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > TelemetryLimits.MaxDecompressedBytes)
            {
                throw new OtlpRequestValidationException(
                    $"Decompressed request exceeds {TelemetryLimits.MaxDecompressedBytes} bytes.");
            }

            destination.Write(buffer, 0, read);
        }

        return destination.ToArray();
    }
}

public sealed class OtlpIngestionLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public OtlpIngestionLimiter(IOptions<TelemetryOptions> options)
    {
        _semaphore = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentIngestions), Math.Max(1, options.Value.MaxConcurrentIngestions));
    }

    public bool TryEnter() => _semaphore.Wait(0);

    public void Release() => _semaphore.Release();

    public void Dispose() => _semaphore.Dispose();
}
