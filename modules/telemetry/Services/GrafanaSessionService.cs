using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Data;

namespace Minicloud.Telemetry.Services;

public sealed record GrafanaSessionSnapshot(
    string SessionId,
    string OrganizationId,
    string AppId,
    string MinicloudUserId,
    string GrafanaLogin,
    string DataSourceUid,
    DateTimeOffset ExpiresAt);

public sealed record GrafanaExchangeOutcome(
    bool Ok,
    string? SessionToken,
    string? Destination,
    DateTimeOffset? ExpiresAt,
    string? Error);

public sealed class GrafanaSessionService(
    TelemetryDbContext db,
    TimeProvider timeProvider,
    IOptions<TelemetryOptions> options,
    ILogger<GrafanaSessionService> logger)
{
    private static readonly Regex DestinationPattern = new(@"^/(explore|d/[A-Za-z0-9_\-]{1,64})(\?.*)?$", RegexOptions.Compiled);

    public async Task<TelemetryLaunchCodeIssueResponse> IssueLaunchCodeAsync(
        TelemetryLaunchCodeIssueRequest request,
        CancellationToken cancellationToken)
    {
        var safeDestination = string.IsNullOrWhiteSpace(request.Destination) ? "/explore" : request.Destination.Trim();
        if (!DestinationPattern.IsMatch(safeDestination))
        {
            safeDestination = "/explore";
        }

        var code = $"mcl_glaunch_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var now = timeProvider.GetUtcNow();
        var expiresAt = now.Add(TelemetryLimits.GrafanaLaunchCodeTtl);
        var dsUid = $"mcl-{request.AppId}";
        var login = GrafanaProxyGuard.IdentityFor(request.MinicloudUserId, request.OrganizationId);

        var launch = new TelemetryLaunchCode
        {
            Id = Guid.NewGuid().ToString("N"),
            CodeHash = HashLaunchCode(code, options.Value.AuthSigningKey),
            OrganizationId = request.OrganizationId,
            AppId = request.AppId,
            MinicloudUserId = request.MinicloudUserId,
            Destination = safeDestination,
            GrafanaLogin = login,
            DataSourceUid = dsUid,
            ExpiresAt = expiresAt,
            CreatedAt = now
        };

        db.LaunchCodes.Add(launch);
        await db.SaveChangesAsync(cancellationToken);

        var accessOrigin = options.Value.AccessOrigin.TrimEnd('/');
        return new TelemetryLaunchCodeIssueResponse
        {
            LaunchCode = code,
            Destination = safeDestination,
            ExpiresAt = expiresAt,
            ExchangeUrl = $"{accessOrigin}/v1/grafana/exchange",
            GrafanaBaseUrl = $"{accessOrigin}/grafana/"
        };
    }

    public async Task<GrafanaExchangeOutcome> ExchangeLaunchCodeAsync(
        string code,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new GrafanaExchangeOutcome(false, null, null, null, "invalid_code");
        }

        var now = timeProvider.GetUtcNow();
        var hash = HashLaunchCode(code.Trim(), options.Value.AuthSigningKey);
        var row = await db.LaunchCodes
            .SingleOrDefaultAsync(x => x.CodeHash == hash, cancellationToken);
        if (row is null || row.ConsumedAt is not null || row.ExpiresAt <= now)
        {
            return new GrafanaExchangeOutcome(false, null, null, null, "invalid_code");
        }

        row.ConsumedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new GrafanaExchangeOutcome(false, null, null, null, "invalid_code");
        }

        // Verify tenant is still valid
        var tenant = await db.TenantBindings.FirstOrDefaultAsync(x => x.AppId == row.AppId, cancellationToken);
        if (tenant is null || !tenant.Enabled || tenant.Deleting)
        {
            logger.LogWarning("Grafana exchange denied: tenant binding disabled/deleting for app {AppId}", row.AppId);
            return new GrafanaExchangeOutcome(false, null, null, null, "telemetry_disabled");
        }

        var sessionToken = $"mcl_gsess_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        var expiresAt = now.Add(TelemetryLimits.GrafanaSessionAbsolute);

        db.GrafanaSessions.Add(new TelemetryGrafanaSession
        {
            Id = Guid.NewGuid().ToString("N"),
            OrganizationId = row.OrganizationId,
            AppId = row.AppId,
            MinicloudUserId = row.MinicloudUserId,
            GrafanaLogin = row.GrafanaLogin,
            DataSourceUid = row.DataSourceUid,
            SessionHash = HashSessionToken(sessionToken),
            ExpiresAt = expiresAt,
            LastSeenAt = now,
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);
        return new GrafanaExchangeOutcome(true, sessionToken, row.Destination, expiresAt, null);
    }

    public async Task<GrafanaSessionSnapshot?> ValidateProxySessionAsync(
        string sessionToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var hash = HashSessionToken(sessionToken.Trim());
        var session = await db.GrafanaSessions.SingleOrDefaultAsync(x => x.SessionHash == hash, cancellationToken);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            return null;
        }

        if (now - session.LastSeenAt >= TelemetryLimits.GrafanaSessionIdle)
        {
            session.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var tenant = await db.TenantBindings.FirstOrDefaultAsync(x => x.AppId == session.AppId, cancellationToken);
        if (tenant is null || !tenant.Enabled || tenant.Deleting)
        {
            session.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        session.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return new GrafanaSessionSnapshot(
            session.Id,
            session.OrganizationId,
            session.AppId,
            session.MinicloudUserId,
            session.GrafanaLogin,
            session.DataSourceUid,
            session.ExpiresAt);
    }

    public async Task LogoutAsync(string sessionToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return;
        }

        var session = await db.GrafanaSessions
            .SingleOrDefaultAsync(x => x.SessionHash == HashSessionToken(sessionToken.Trim()), cancellationToken);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        var allowed = options.Value.AccessOrigin;
        if (string.IsNullOrWhiteSpace(allowed))
        {
            return true;
        }

        return string.Equals(allowed.Trim().TrimEnd('/'), origin.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    public static string HashLaunchCode(string code, string signingKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{signingKey}\u0001{code}"));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string HashSessionToken(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
