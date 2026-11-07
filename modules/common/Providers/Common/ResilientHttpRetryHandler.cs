using System.Net;
using Microsoft.Extensions.Logging;

namespace Minicloud.Common.Providers.Common;

public class ResilientHttpRetryHandler : DelegatingHandler
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly ILogger<ResilientHttpRetryHandler>? _logger;

    public ResilientHttpRetryHandler(
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        ILogger<ResilientHttpRetryHandler>? logger = null)
    {
        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _logger = logger;
    }

    public ResilientHttpRetryHandler(
        HttpMessageHandler innerHandler,
        int maxRetries = 3,
        TimeSpan? baseDelay = null,
        ILogger<ResilientHttpRetryHandler>? logger = null)
        : base(innerHandler)
    {
        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Options.TryGetValue(Hetzner.HetznerClient.NoRetry, out var noRetry) && noRetry)
        { return await base.SendAsync(request, cancellationToken); }
        var attempt = 0;

        while (true)
        {
            attempt++;
            HttpResponseMessage? response = null;
            Exception? caughtException = null;

            try
            {
                response = await base.SendAsync(request, cancellationToken);
                if (!IsTransientError(response.StatusCode) || attempt > _maxRetries)
                {
                    return response;
                }
            }
            catch (HttpRequestException ex) when (attempt <= _maxRetries)
            {
                caughtException = ex;
            }

            var delay = CalculateDelay(attempt, response);

            if (_logger != null && _logger.IsEnabled(LogLevel.Warning))
            {
                var method = request.Method.Method;
                var uri = TokenRedactor.Redact(request.RequestUri?.ToString());
                var status = response?.StatusCode.ToString() ?? caughtException?.GetType().Name;
                _logger.LogWarning(
                    "HTTP {Method} {Uri} failed with {Status}. Retry {Attempt}/{MaxRetries} after {DelayMs}ms.",
                    method,
                    uri,
                    status,
                    attempt,
                    _maxRetries,
                    delay.TotalMilliseconds);
            }

            response?.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsTransientError(HttpStatusCode statusCode)
    {
        return statusCode == (HttpStatusCode)429 // TooManyRequests
            || statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.InternalServerError
            || statusCode == HttpStatusCode.BadGateway
            || statusCode == HttpStatusCode.ServiceUnavailable
            || statusCode == HttpStatusCode.GatewayTimeout;
    }

    private TimeSpan CalculateDelay(int attempt, HttpResponseMessage? response)
    {
        if (response?.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                return response.Headers.RetryAfter.Delta.Value;
            }

            if (response.Headers.RetryAfter.Date.HasValue)
            {
                var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    return diff;
                }
            }
        }

        // Exponential backoff: baseDelay * 2^(attempt - 1)
        var factor = Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * factor);
    }
}
