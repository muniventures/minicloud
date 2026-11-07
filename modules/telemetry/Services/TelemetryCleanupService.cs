using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Telemetry.Data;

namespace Minicloud.Telemetry.Services;

public sealed class TelemetryCleanupService(
    TelemetryDbContext db,
    TelemetryStatusReporter reporter,
    IHttpClientFactory httpClients,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetryCleanupService> logger)
{
    public async Task ProcessPendingCleanupsAsync(CancellationToken cancellationToken)
    {
        var pending = await db.CleanupRecords
            .Where(x => x.Status == "pending" || (x.Status == "failed" && x.Attempts < 5))
            .OrderBy(x => x.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        foreach (var record in pending)
        {
            await ProcessSingleCleanupAsync(record, cancellationToken);
        }
    }

    private async Task ProcessSingleCleanupAsync(TelemetryCleanupRecord record, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        record.Status = "in_progress";
        record.Attempts++;
        record.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            // 1. Send delete request to Loki for tenant
            await DeleteLokiDataAsync(record.TenantId, record.AppId, record.CutoffAt ?? now, cancellationToken);

            // 2. Remove app-related local replicas
            var creds = await db.CredentialReplicas.Where(x => x.AppId == record.AppId).ToListAsync(cancellationToken);
            db.CredentialReplicas.RemoveRange(creds);

            var ds = await db.DataSourceReplicas.Where(x => x.AppId == record.AppId).ToListAsync(cancellationToken);
            db.DataSourceReplicas.RemoveRange(ds);

            var sessions = await db.GrafanaSessions.Where(x => x.AppId == record.AppId).ToListAsync(cancellationToken);
            db.GrafanaSessions.RemoveRange(sessions);

            var launches = await db.LaunchCodes.Where(x => x.AppId == record.AppId).ToListAsync(cancellationToken);
            db.LaunchCodes.RemoveRange(launches);

            record.Status = "completed";
            record.CompletedAt = now;
            record.UpdatedAt = now;
            record.LastError = null;
            await db.SaveChangesAsync(cancellationToken);

            reporter.RecordCleanupStatus(record.Id, record.AppId, record.TenantId, "completed", null, now);
            logger.LogInformation("Telemetry cleanup completed for app {AppId} (tenant {TenantId})", record.AppId, record.TenantId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telemetry cleanup failed for app {AppId} (tenant {TenantId}) attempt {Attempt}", record.AppId, record.TenantId, record.Attempts);
            record.Status = record.Attempts >= 5 ? "failed" : "pending";
            record.UpdatedAt = now;
            record.LastError = ex.Message;
            await db.SaveChangesAsync(cancellationToken);

            reporter.RecordCleanupStatus(record.Id, record.AppId, record.TenantId, record.Status, ex.Message, null);
        }
    }

    private async Task DeleteLokiDataAsync(string tenantId, string appId, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var endpoint = options.Value.LokiBaseAddress.TrimEnd('/') + "/";
        var client = httpClients.CreateClient();

        // Loki delete API: POST /loki/api/v1/delete?query={minicloud_app_id="<appId>"}&end=<cutoff>
        var deleteUrl = $"{endpoint}loki/api/v1/delete?query=%7Bminicloud_source%3D%22otlp%22%7D&end={cutoff.ToUnixTimeMilliseconds() * 1_000_000}";
        using var request = new HttpRequestMessage(HttpMethod.Post, deleteUrl);
        request.Headers.TryAddWithoutValidation("X-Scope-OrgID", tenantId);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var response = await client.SendAsync(request, linked.Token);
            // Even if delete API is disabled in Loki or returns 404/405/204, log and proceed
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Loki delete API responded with {Status} for tenant {TenantId}; continuing cleanup", response.StatusCode, tenantId);
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Loki delete call failed for tenant {TenantId}; continuing cleanup", tenantId);
        }
    }
}
