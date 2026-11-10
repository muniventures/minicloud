using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed record TelemetryServiceError(string Code, string Message, bool IsConflict = false);

public sealed record TelemetryIssueSuccess(TelemetryCredentialIssueResponse Response);

public sealed record TelemetryIssueResult(TelemetryIssueSuccess? Success, TelemetryServiceError? Error)
{
    public static TelemetryIssueResult Ok(TelemetryIssueSuccess success) => new(success, null);
    public static TelemetryIssueResult Fail(TelemetryServiceError error) => new(null, error);
}

public sealed record TelemetrySettingsResult(TelemetrySettingsResponse? Settings, TelemetryServiceError? Error)
{
    public static TelemetrySettingsResult Ok(TelemetrySettingsResponse settings) => new(settings, null);
    public static TelemetrySettingsResult Fail(TelemetryServiceError error) => new(null, error);
}

/// <summary>
/// T11 control-plane authority for telemetry: settings lifecycle, issue-once credentials,
/// tombstoning with durable cleanup. Authorization is enforced here; the app's organization
/// is always resolved from storage. Callers map a null (unknown/inaccessible) outcome to a
/// non-disclosing 404.
/// </summary>
public sealed class TelemetryService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ITelemetryTokenService tokens,
    DeploymentServiceSetService serviceSets,
    ControlPlaneAuthorization authorization,
    AuditEventService audit,
    IHostEnvironment? hosting = null,
    IOptions<TelemetryClientOptions>? options = null)
{
    public async Task<TelemetrySettingsResponse?> GetSettingsAsync(
        ControlPlanePrincipal principal,
        string appId,
        CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return null;
        }

        var canRead = await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ReadApplicationLogs, cancellationToken);
        var canManage = await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ManageTelemetry, cancellationToken);
        if (!canRead && !canManage)
        {
            return null;
        }

        var settings = await db.AppTelemetrySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        return settings is null ? null : ToSettingsResponse(settings);
    }

    public async Task<TelemetrySettingsResponse?> GetSettingsForManageAsync(
        ControlPlanePrincipal principal,
        string appId,
        CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ManageTelemetry, cancellationToken))
        {
            return null;
        }

        var settings = await db.AppTelemetrySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        return settings is null ? null : ToSettingsResponse(settings);
    }

    public async Task<TelemetrySettingsResult?> SetEnabledAsync(
        ControlPlanePrincipal principal,
        string appId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ManageTelemetry, cancellationToken))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var settings = await db.AppTelemetrySettings.SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        if (settings is null)
        {
            if (!enabled)
            {
                settings = new AppTelemetrySettings
                {
                    Id = ids.NewId("tset"),
                    OrganizationId = app.OrganizationId,
                    AppId = app.Id,
                    TenantId = await GenerateUniqueTenantIdAsync(cancellationToken),
                    Enabled = false,
                    Deleting = false,
                    RetentionPolicy = "7d",
                    ProvisioningStatus = "pending",
                    ProvisioningVersion = 0,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.AppTelemetrySettings.Add(settings);
                await db.SaveChangesAsync(cancellationToken);
                return TelemetrySettingsResult.Ok(ToSettingsResponse(settings));
            }

            settings = new AppTelemetrySettings
            {
                Id = ids.NewId("tset"),
                OrganizationId = app.OrganizationId,
                AppId = app.Id,
                TenantId = await GenerateUniqueTenantIdAsync(cancellationToken),
                Enabled = true,
                Deleting = false,
                RetentionPolicy = "7d",
                ProvisioningStatus = "pending",
                ProvisioningVersion = 0,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AppTelemetrySettings.Add(settings);
        }
        else
        {
            if (settings.OrganizationId != app.OrganizationId)
            {
                return null;
            }

            settings.Enabled = enabled;
            settings.Deleting = false;
            settings.DeletedAt = null;
            settings.UpdatedAt = now;
        }

        db.TelemetryAuthorityOutboxes.Add(new TelemetryAuthorityOutbox
        {
            EventType = "tenant",
            EntityId = settings.AppId,
            PayloadJson = settings.TenantId,
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        await audit.AppendAsync(
            app.OrganizationId,
            app.Id,
            AuditEventService.ActorFor(principal.ToActorIdentity()),
            enabled ? "telemetry.enabled" : "telemetry.disabled",
            $"environment={hosting?.EnvironmentName ?? "production"}",
            cancellationToken);
        return TelemetrySettingsResult.Ok(ToSettingsResponse(settings));
    }

    public async Task<TelemetryIssueResult?> IssueCredentialAsync(
        ControlPlanePrincipal principal,
        string appId,
        string serviceName,
        string name,
        DateTimeOffset? expiresAt,
        CancellationToken cancellationToken)
    {
        var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ManageTelemetry, cancellationToken))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(name))
        {
            return TelemetryIssueResult.Fail(new TelemetryServiceError("validation_failed", "Service name and credential name are required."));
        }

        var settings = await db.AppTelemetrySettings.SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        if (settings is null || settings.OrganizationId != app.OrganizationId)
        {
            return TelemetryIssueResult.Fail(new TelemetryServiceError("telemetry_not_enabled", "Telemetry is not enabled for this app."));
        }

        if (!settings.Enabled || settings.Deleting)
        {
            return TelemetryIssueResult.Fail(new TelemetryServiceError("telemetry_disabled", "Telemetry is disabled for this app."));
        }

        var activeServices = await serviceSets.BuildActiveServicesAsync(db, appId, cancellationToken);
        if (!activeServices.Any(x => string.Equals(x.Name, serviceName, StringComparison.Ordinal)))
        {
            return TelemetryIssueResult.Fail(new TelemetryServiceError("service_not_deployed", "Service must be deployed before a credential can be issued."));
        }

        var now = timeProvider.GetUtcNow();
        var existingExpiries = await db.TelemetryIngestionCredentials
            .Where(x => x.AppId == appId && x.ServiceName == serviceName && x.RevokedAt == null)
            .Select(x => x.ExpiresAt)
            .ToListAsync(cancellationToken);
        var activeCount = existingExpiries.Count(e => e == null || e > now);
        if (activeCount >= TelemetryLimits.MaxActiveCredentialsPerService)
        {
            return TelemetryIssueResult.Fail(new TelemetryServiceError(
                "credential_limit_exceeded",
                $"At most {TelemetryLimits.MaxActiveCredentialsPerService} active credentials per service.",
                IsConflict: true));
        }

        var expiry = expiresAt ?? now.Add(TelemetryLimits.DefaultCredentialLifetime);
        if (expiry <= now)
        {
            return TelemetryIssueResult.Fail(new TelemetryServiceError("validation_failed", "Expiry must be in the future."));
        }

        if (expiry - now > TelemetryLimits.MaxCredentialLifetime)
        {
            return TelemetryIssueResult.Fail(new TelemetryServiceError("validation_failed", "Expiry must not exceed 365 days."));
        }

        var token = tokens.Generate();
        var versions = await db.TelemetryIngestionCredentials
            .Where(x => x.AppId == appId && x.ServiceName == serviceName)
            .Select(x => x.CredentialVersion)
            .ToListAsync(cancellationToken);
        var credential = new TelemetryIngestionCredential
        {
            Id = ids.NewId("tcred"),
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            ServiceName = serviceName,
            Name = name,
            KeyPrefix = token.Prefix,
            KeyHash = tokens.Hash(token.Token),
            CredentialVersion = versions.Count == 0 ? 1 : versions.Max() + 1,
            CreatedByActor = AuditEventService.ActorFor(principal.ToActorIdentity()),
            ExpiresAt = expiry,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TelemetryIngestionCredentials.Add(credential);

        db.TelemetryAuthorityOutboxes.Add(new TelemetryAuthorityOutbox
        {
            EventType = "credential",
            EntityId = credential.Id,
            PayloadJson = credential.KeyPrefix,
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        await audit.AppendAsync(
            app.OrganizationId,
            app.Id,
            AuditEventService.ActorFor(principal.ToActorIdentity()),
            "telemetry.credential_issued",
            $"service={serviceName} credential={credential.Id}",
            cancellationToken);

        return TelemetryIssueResult.Ok(new TelemetryIssueSuccess(new TelemetryCredentialIssueResponse(
            credential.Id,
            credential.ServiceName,
            credential.Name,
            credential.KeyPrefix,
            credential.CredentialVersion,
            credential.ExpiresAt,
            credential.CreatedAt,
            token.Token)));
    }

    public async Task<IReadOnlyList<TelemetryCredentialResponse>?> ListCredentialsAsync(
        ControlPlanePrincipal principal,
        string appId,
        CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ManageTelemetry, cancellationToken))
        {
            return null;
        }

        var rows = await db.TelemetryIngestionCredentials.AsNoTracking()
            .Where(x => x.AppId == appId)
            .ToListAsync(cancellationToken);
        return rows
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new TelemetryCredentialResponse(
                x.Id, x.ServiceName, x.Name, x.KeyPrefix, x.CredentialVersion,
                x.CreatedByActor, x.ExpiresAt, x.RevokedAt, x.LastUsedAt, x.CreatedAt))
            .ToList();
    }

    /// <summary>
    /// Idempotent revocation. Returns false when the credential/app pairing is unknown
    /// (mapped to a non-disclosing 404); revoking an already-revoked credential succeeds.
    /// </summary>
    public async Task<bool?> RevokeCredentialAsync(
        ControlPlanePrincipal principal,
        string appId,
        string credentialId,
        CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ManageTelemetry, cancellationToken))
        {
            return null;
        }

        var credential = await db.TelemetryIngestionCredentials
            .SingleOrDefaultAsync(x => x.Id == credentialId && x.AppId == appId, cancellationToken);
        if (credential is null || credential.OrganizationId != app.OrganizationId)
        {
            return null;
        }

        if (credential.RevokedAt is null)
        {
            var now = timeProvider.GetUtcNow();
            credential.RevokedAt = now;
            credential.UpdatedAt = now;

            db.TelemetryAuthorityOutboxes.Add(new TelemetryAuthorityOutbox
            {
                EventType = "credential",
                EntityId = credential.Id,
                PayloadJson = "revoked",
                CreatedAt = now
            });

            await db.SaveChangesAsync(cancellationToken);
            await audit.AppendAsync(
                app.OrganizationId,
                app.Id,
                AuditEventService.ActorFor(principal.ToActorIdentity()),
                "telemetry.credential_revoked",
                $"service={credential.ServiceName} credential={credential.Id}",
                cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Tombstones telemetry access and enqueues a durable external cleanup job.
    /// Called on app/branch deletion; never removes ownership records synchronously.
    /// A recreated app with the same slug gets a new AppId and therefore a new tenant.
    /// </summary>
    public async Task TombstoneAppAsync(string appId, CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var settings = await db.AppTelemetrySettings.SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        if (settings is not null)
        {
            settings.Enabled = false;
            settings.Deleting = true;
            settings.DeletedAt = now;
            settings.UpdatedAt = now;
        }

        var credentials = await db.TelemetryIngestionCredentials
            .Where(x => x.AppId == appId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var credential in credentials)
        {
            credential.RevokedAt = now;
            credential.UpdatedAt = now;
        }

        if (settings is not null && !await db.TelemetryCleanupJobs.AnyAsync(
                x => x.AppId == appId && x.TenantId == settings.TenantId && x.Status == "pending", cancellationToken))
        {
            db.TelemetryCleanupJobs.Add(new TelemetryCleanupJob
            {
                Id = ids.NewId("tclean"),
                OrganizationId = app.OrganizationId,
                AppId = app.Id,
                TenantId = settings.TenantId,
                Status = "pending",
                CutoffAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        db.TelemetryAuthorityOutboxes.Add(new TelemetryAuthorityOutbox
        {
            EventType = "tombstone",
            EntityId = appId,
            PayloadJson = settings?.TenantId ?? string.Empty,
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Minicloud.Common.Contracts.TelemetryReadTokenIssueResponse?> IssueReadTokenAsync(
        ControlPlanePrincipal principal,
        string appId,
        CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null || !await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), app.OrganizationId, OrganizationAction.ReadApplicationLogs, cancellationToken))
        {
            return null;
        }

        var settings = await db.AppTelemetrySettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == appId, cancellationToken);
        if (settings is null || !settings.Enabled || settings.Deleting)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var lifetime = TimeSpan.FromMinutes(15);
        var expiresAt = now.Add(lifetime);
        var token = Minicloud.Common.Services.TelemetryReadToken.Create(
            principal.UserId ?? principal.ApiKeyId ?? principal.Kind,
            app.OrganizationId,
            app.Id,
            settings.TenantId,
            lifetime,
            options?.Value?.AuthSigningKey ?? "minicloud-telemetry-auth-signing-dev-key-must-be-long",
            timeProvider);

        var accessOrigin = options?.Value?.AccessOrigin ?? "http://localhost:8088";
        var queryUrl = $"{accessOrigin.TrimEnd('/')}/api/logs";
        return new Minicloud.Common.Contracts.TelemetryReadTokenIssueResponse(token, expiresAt, queryUrl);
    }

    private async Task<string> GenerateUniqueTenantIdAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var tenant = $"mcl_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
            if (!await db.AppTelemetrySettings.AnyAsync(x => x.TenantId == tenant, cancellationToken))
            {
                return tenant;
            }
        }

        throw new InvalidOperationException("Could not generate a unique telemetry tenant ID.");
    }

    private TelemetrySettingsResponse ToSettingsResponse(AppTelemetrySettings settings) =>
        new(settings.AppId, settings.OrganizationId, settings.TenantId, settings.Enabled,
            settings.RetentionPolicy, settings.ProvisioningStatus, settings.LastAcceptedAt,
            settings.CreatedAt, settings.UpdatedAt, options?.Value.IngestionUrl);
}
