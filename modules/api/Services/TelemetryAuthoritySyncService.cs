using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed class TelemetryAuthoritySyncService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IOptions<TelemetryClientOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetryAuthoritySyncService> logger) : BackgroundService
{
    private static long _sequenceCounter = 1;

    public async Task<bool> SyncNowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteSyncCycleAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry authority sync cycle encountered an error.");
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Telemetry authority sync background service started.");

        // Initial sync on startup
        try
        {
            await ExecuteSyncCycleAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Initial telemetry authority sync failed; will retry in background loop.");
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(options.Value.SyncIntervalSeconds, 1, 60));
        using var timer = new PeriodicTimer(interval, timeProvider);

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ExecuteSyncCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error during periodic telemetry authority sync; will retry.");
            }
        }
    }

    private async Task ExecuteSyncCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MinicloudDbContext>();
        var now = timeProvider.GetUtcNow();

        // 1. Gather outbox items and build batch
        var pendingOutbox = await db.TelemetryAuthorityOutboxes
            .Where(x => x.ProcessedAt == null)
            .OrderBy(x => x.Sequence)
            .Take(100)
            .ToListAsync(cancellationToken);

        var maxOutboxSeq = await db.TelemetryAuthorityOutboxes.MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
        var currentSeq = Interlocked.Increment(ref _sequenceCounter);
        var checkpointSeq = Math.Max(currentSeq, maxOutboxSeq + 1);

        // Collect all active tenants
        var tenants = await db.AppTelemetrySettings.AsNoTracking()
            .Select(x => new TelemetryTenantSyncItem
            {
                AppId = x.AppId,
                OrganizationId = x.OrganizationId,
                TenantId = x.TenantId,
                Enabled = x.Enabled,
                Deleting = x.Deleting,
                RetentionPolicy = x.RetentionPolicy,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        // Collect all active and recently revoked credentials
        var credentials = await db.TelemetryIngestionCredentials.AsNoTracking()
            .Select(x => new TelemetryCredentialSyncItem
            {
                Id = x.Id,
                OrganizationId = x.OrganizationId,
                AppId = x.AppId,
                ServiceName = x.ServiceName,
                TenantId = db.AppTelemetrySettings.Where(s => s.AppId == x.AppId).Select(s => s.TenantId).FirstOrDefault() ?? string.Empty,
                KeyPrefix = x.KeyPrefix,
                KeyHash = x.KeyHash,
                CredentialVersion = x.CredentialVersion,
                Status = x.RevokedAt == null ? "active" : "revoked",
                ExpiresAt = x.ExpiresAt,
                RevokedAt = x.RevokedAt,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        // Collect recent deployments
        var deployments = await db.Deployments.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .Take(500)
            .Select(x => new TelemetryDeploymentSyncItem
            {
                Id = x.Id,
                AppId = x.AppId,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        // Collect data sources
        var dataSources = await db.GrafanaDataSourceBindings.AsNoTracking()
            .Where(x => x.DeletedAt == null)
            .Select(x => new TelemetryDataSourceSyncItem
            {
                Id = x.Id,
                AppId = x.AppId,
                OrganizationId = x.OrganizationId,
                TenantId = db.AppTelemetrySettings.Where(s => s.AppId == x.AppId).Select(s => s.TenantId).FirstOrDefault() ?? string.Empty,
                GrafanaUid = x.GrafanaUid,
                KeyPrefix = x.CredentialReference.Contains('|') ? x.CredentialReference.Split('|', 2)[0] : x.CredentialReference,
                KeyHash = x.CredentialReference.Contains('|') ? x.CredentialReference.Split('|', 2)[1] : string.Empty,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        // Collect tombstones
        var tombstones = await db.TelemetryCleanupJobs.AsNoTracking()
            .Select(x => new TelemetryTombstoneSyncItem
            {
                Id = x.Id,
                OrganizationId = x.OrganizationId,
                AppId = x.AppId,
                TenantId = x.TenantId,
                CutoffAt = x.CutoffAt,
                CreatedAt = x.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var batchRequest = new TelemetrySyncBatchRequest
        {
            CheckpointSequence = checkpointSeq,
            CheckpointTimestamp = now,
            Tenants = tenants,
            Credentials = credentials,
            Deployments = deployments,
            DataSources = dataSources,
            Tombstones = tombstones
        };

        var client = httpClients.CreateClient();
        var baseUrl = options.Value.TelemetryServiceUrl.TrimEnd('/');
        var syncUrl = $"{baseUrl}/internal/v1/telemetry/sync";

        using var syncMsg = new HttpRequestMessage(HttpMethod.Post, syncUrl)
        {
            Content = JsonContent.Create(batchRequest)
        };
        syncMsg.Headers.TryAddWithoutValidation("X-Telemetry-Management-Key", options.Value.ManagementApiKey);

        using var syncResp = await client.SendAsync(syncMsg, cancellationToken);
        if (!syncResp.IsSuccessStatusCode)
        {
            logger.LogWarning("Telemetry authority sync failed with status {StatusCode}", syncResp.StatusCode);
            return;
        }

        // Mark outbox records as processed
        foreach (var item in pendingOutbox)
        {
            item.ProcessedAt = now;
        }

        // 2. Poll reverse status
        var statusUrl = $"{baseUrl}/internal/v1/telemetry/status";
        using var statusMsg = new HttpRequestMessage(HttpMethod.Get, statusUrl);
        statusMsg.Headers.TryAddWithoutValidation("X-Telemetry-Management-Key", options.Value.ManagementApiKey);

        using var statusResp = await client.SendAsync(statusMsg, cancellationToken);
        if (statusResp.IsSuccessStatusCode)
        {
            var statusReport = await statusResp.Content.ReadFromJsonAsync<TelemetryStatusReport>(cancellationToken);
            if (statusReport is not null)
            {
                foreach (var appStatus in statusReport.AppStatuses)
                {
                    var setting = await db.AppTelemetrySettings.FirstOrDefaultAsync(x => x.AppId == appStatus.AppId, cancellationToken);
                    if (setting is not null)
                    {
                        if (setting.LastAcceptedAt is null || appStatus.LastAcceptedAt > setting.LastAcceptedAt)
                        {
                            setting.LastAcceptedAt = appStatus.LastAcceptedAt;
                            setting.UpdatedAt = now;
                        }
                    }
                }

                foreach (var credStatus in statusReport.CredentialStatuses)
                {
                    var cred = await db.TelemetryIngestionCredentials.FirstOrDefaultAsync(x => x.Id == credStatus.CredentialId, cancellationToken);
                    if (cred is not null)
                    {
                        if (cred.LastUsedAt is null || credStatus.LastUsedAt > cred.LastUsedAt)
                        {
                            cred.LastUsedAt = credStatus.LastUsedAt;
                            cred.UpdatedAt = now;
                        }
                    }
                }

                foreach (var cleanStatus in statusReport.CleanupStatuses)
                {
                    var job = await db.TelemetryCleanupJobs.FirstOrDefaultAsync(x => x.Id == cleanStatus.JobId, cancellationToken);
                    if (job is not null)
                    {
                        job.Status = cleanStatus.Status;
                        job.LastError = cleanStatus.LastError;
                        job.CompletedAt = cleanStatus.CompletedAt;
                        job.UpdatedAt = now;
                    }
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
