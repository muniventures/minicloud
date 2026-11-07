using System.Text.Json;
using Microsoft.Extensions.Options;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class OpenBaoTokenRenewalService(
    IHttpClientFactory clients,
    IOptions<OpenBaoSecretStoreOptions> options,
    ILogger<OpenBaoTokenRenewalService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Address) || string.IsNullOrWhiteSpace(options.Value.Token))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = await RenewAsync(stoppingToken);
            await Task.Delay(delay, stoppingToken);
        }
    }

    public async Task<TimeSpan> RenewAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = clients.CreateClient(nameof(OpenBaoTokenRenewalService));
            using var request = new HttpRequestMessage(HttpMethod.Post,
                options.Value.Address.TrimEnd('/') + "/v1/auth/token/renew-self");
            request.Headers.Add("X-Vault-Token", options.Value.Token);
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.Timeout);
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("OpenBao API token renewal failed with HTTP {StatusCode}. Check token validity and renew-self permission; expired tokens require deployment recovery. Retrying in one minute.",
                    (int)response.StatusCode);
                return TimeSpan.FromMinutes(1);
            }

            // Renewal responses contain the token itself. Never log the response body.
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            if (!body.RootElement.TryGetProperty("auth", out var auth) || auth.ValueKind != JsonValueKind.Object ||
                !auth.TryGetProperty("renewable", out var renewable) || renewable.ValueKind != JsonValueKind.True ||
                !auth.TryGetProperty("lease_duration", out var lease) || !lease.TryGetInt32(out var seconds) || seconds <= 0)
            {
                logger.LogError("OpenBao API token renewal returned no renewable lease. Check periodic token configuration. Retrying in one minute.");
                return TimeSpan.FromMinutes(1);
            }

            logger.LogInformation("OpenBao API token renewed; lease duration {LeaseSeconds} seconds.", seconds);
            return TimeSpan.FromSeconds(Math.Min(TimeSpan.FromHours(12).TotalSeconds, seconds / 3.0));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Transport and parsing exceptions may contain sensitive remote content.
            logger.LogError("OpenBao API token renewal request failed. Retrying in one minute.");
            return TimeSpan.FromMinutes(1);
        }
    }
}
