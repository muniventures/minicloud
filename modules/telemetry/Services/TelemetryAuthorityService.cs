using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Data;

namespace Minicloud.Telemetry.Services;

public sealed class TelemetryAuthorityService(
    TelemetryDbContext db,
    TimeProvider timeProvider,
    ILogger<TelemetryAuthorityService> logger)
{
    public async Task<TelemetrySyncBatchResponse> ApplySyncBatchAsync(
        TelemetrySyncBatchRequest request,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var checkpoint = await db.AuthorityCheckpoints.FirstOrDefaultAsync(cancellationToken);
        if (checkpoint is null)
        {
            checkpoint = new TelemetryAuthorityCheckpoint
            {
                Id = 1,
                Sequence = request.CheckpointSequence,
                CheckpointTimestamp = request.CheckpointTimestamp,
                ConfirmedAt = now
            };
            db.AuthorityCheckpoints.Add(checkpoint);
        }
        else
        {
            // Gaps or backwards sequence rejected if out of order, but allow same/higher sequence
            if (request.CheckpointSequence < checkpoint.Sequence)
            {
                logger.LogWarning(
                    "Rejecting stale sync sequence {Incoming} (current is {Current})",
                    request.CheckpointSequence,
                    checkpoint.Sequence);
                return new TelemetrySyncBatchResponse
                {
                    Success = false,
                    AppliedSequence = checkpoint.Sequence,
                    ConfirmedAt = checkpoint.ConfirmedAt,
                    Error = "stale_sequence"
                };
            }

            checkpoint.Sequence = request.CheckpointSequence;
            checkpoint.CheckpointTimestamp = request.CheckpointTimestamp;
            checkpoint.ConfirmedAt = now;
        }

        // Apply tenant bindings
        foreach (var item in request.Tenants)
        {
            var existing = await db.TenantBindings.FirstOrDefaultAsync(x => x.AppId == item.AppId, cancellationToken);
            if (existing is null)
            {
                db.TenantBindings.Add(new TelemetryTenantBinding
                {
                    AppId = item.AppId,
                    OrganizationId = item.OrganizationId,
                    TenantId = item.TenantId,
                    Enabled = item.Enabled,
                    Deleting = item.Deleting,
                    RetentionPolicy = item.RetentionPolicy,
                    UpdatedAt = item.UpdatedAt
                });
            }
            else
            {
                existing.OrganizationId = item.OrganizationId;
                existing.TenantId = item.TenantId;
                existing.Enabled = item.Enabled;
                existing.Deleting = item.Deleting;
                existing.RetentionPolicy = item.RetentionPolicy;
                existing.UpdatedAt = item.UpdatedAt;
            }
        }

        // Apply credentials
        foreach (var item in request.Credentials)
        {
            var existing = await db.CredentialReplicas.FirstOrDefaultAsync(x => x.Id == item.Id, cancellationToken);
            if (existing is null)
            {
                db.CredentialReplicas.Add(new TelemetryCredentialReplica
                {
                    Id = item.Id,
                    OrganizationId = item.OrganizationId,
                    AppId = item.AppId,
                    ServiceName = item.ServiceName,
                    TenantId = item.TenantId,
                    KeyPrefix = item.KeyPrefix,
                    KeyHash = item.KeyHash,
                    CredentialVersion = item.CredentialVersion,
                    Status = item.Status,
                    ExpiresAt = item.ExpiresAt,
                    RevokedAt = item.RevokedAt,
                    UpdatedAt = item.UpdatedAt
                });
            }
            else
            {
                existing.OrganizationId = item.OrganizationId;
                existing.AppId = item.AppId;
                existing.ServiceName = item.ServiceName;
                existing.TenantId = item.TenantId;
                existing.KeyPrefix = item.KeyPrefix;
                existing.KeyHash = item.KeyHash;
                existing.CredentialVersion = item.CredentialVersion;
                existing.Status = item.Status;
                existing.ExpiresAt = item.ExpiresAt;
                existing.RevokedAt = item.RevokedAt;
                existing.UpdatedAt = item.UpdatedAt;
            }
        }

        // Apply deployments
        foreach (var dep in request.Deployments)
        {
            var existing = await db.DeploymentReplicas.FirstOrDefaultAsync(x => x.Id == dep.Id, cancellationToken);
            if (existing is null)
            {
                db.DeploymentReplicas.Add(new TelemetryDeploymentReplica
                {
                    Id = dep.Id,
                    AppId = dep.AppId,
                    CreatedAt = dep.CreatedAt
                });
            }
        }

        // Apply data sources
        foreach (var ds in request.DataSources)
        {
            var existing = await db.DataSourceReplicas.FirstOrDefaultAsync(x => x.Id == ds.Id, cancellationToken);
            if (existing is null)
            {
                db.DataSourceReplicas.Add(new TelemetryDataSourceReplica
                {
                    Id = ds.Id,
                    AppId = ds.AppId,
                    OrganizationId = ds.OrganizationId,
                    TenantId = ds.TenantId,
                    GrafanaUid = ds.GrafanaUid,
                    KeyPrefix = ds.KeyPrefix,
                    KeyHash = ds.KeyHash,
                    UpdatedAt = ds.UpdatedAt
                });
            }
            else
            {
                existing.OrganizationId = ds.OrganizationId;
                existing.AppId = ds.AppId;
                existing.TenantId = ds.TenantId;
                existing.GrafanaUid = ds.GrafanaUid;
                existing.KeyPrefix = ds.KeyPrefix;
                existing.KeyHash = ds.KeyHash;
                existing.UpdatedAt = ds.UpdatedAt;
            }
        }

        // Apply tombstones
        foreach (var tombstone in request.Tombstones)
        {
            var existing = await db.CleanupRecords.FirstOrDefaultAsync(x => x.Id == tombstone.Id, cancellationToken);
            if (existing is null)
            {
                db.CleanupRecords.Add(new TelemetryCleanupRecord
                {
                    Id = tombstone.Id,
                    OrganizationId = tombstone.OrganizationId,
                    AppId = tombstone.AppId,
                    TenantId = tombstone.TenantId,
                    Status = "pending",
                    CutoffAt = tombstone.CutoffAt,
                    CreatedAt = tombstone.CreatedAt,
                    UpdatedAt = now
                });
            }

            // Also ensure tenant binding is tombstoned/disabled
            var tenant = await db.TenantBindings.FirstOrDefaultAsync(x => x.AppId == tombstone.AppId, cancellationToken);
            if (tenant is not null)
            {
                tenant.Deleting = true;
                tenant.Enabled = false;
                tenant.UpdatedAt = now;
            }

            // And revoke all credentials for the tombstoned app
            var creds = await db.CredentialReplicas.Where(x => x.AppId == tombstone.AppId && x.RevokedAt == null).ToListAsync(cancellationToken);
            foreach (var cred in creds)
            {
                cred.Status = "revoked";
                cred.RevokedAt = now;
                cred.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return new TelemetrySyncBatchResponse
        {
            Success = true,
            AppliedSequence = checkpoint.Sequence,
            ConfirmedAt = checkpoint.ConfirmedAt
        };
    }

    public async Task<TelemetryAuthorityCheckpoint?> GetCheckpointAsync(CancellationToken cancellationToken)
    {
        return await db.AuthorityCheckpoints.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> IsIngestionAllowedDuringOutageAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await GetCheckpointAsync(cancellationToken);
        if (checkpoint is null)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        return (now - checkpoint.ConfirmedAt) <= TelemetryLimits.MaxIngestionOutageAllowance;
    }
}
