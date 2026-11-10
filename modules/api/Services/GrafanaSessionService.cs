using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed record GrafanaLaunchOutcome(bool Ok, string? Code, string? Destination, DateTimeOffset? ExpiresAt, string? Error);
public sealed record GrafanaExchangeOutcome(bool Ok, string? SessionToken, string? Destination, DateTimeOffset? ExpiresAt, string? Error);
public sealed record GrafanaSessionSnapshot(
    string SessionId,
    string OrganizationId,
    string AppId,
    string MinicloudUserId,
    long GrafanaOrgId,
    long GrafanaUserId,
    string GrafanaLogin,
    string DataSourceUid,
    DateTimeOffset ExpiresAt);

/// <summary>
/// T20 launch-code and proxy-session authority. Launch codes are random,
/// single-use, 60-second, and bound to (user, organization, app, audience,
/// allowlisted relative destination); only SHA256 digests are stored, and the
/// audience is folded into the digest so a code from another environment never
/// verifies. Proxy sessions are opaque tokens (digest-only storage) with 15-minute
/// idle and 1-hour absolute lifetimes; every validation rechecks current Minicloud
/// membership with at most a 30-second cache.
/// </summary>
public sealed class GrafanaSessionService(
    MinicloudDbContext db,
    GrafanaProvisioningService provisioning,
    ControlPlaneAuthorization authorization,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IOptions<GrafanaOptions> options,
    IHostEnvironment hosting,
    ILogger<GrafanaSessionService> logger)
{
    private readonly GrafanaOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, MembershipCacheEntry> _membershipCache = new(StringComparer.Ordinal);

    private static readonly Regex DestinationPattern = new(@"^/(explore|d/[A-Za-z0-9_\-]{1,64})$", RegexOptions.Compiled);

    public async Task<GrafanaLaunchOutcome> IssueLaunchCodeAsync(
        ControlPlanePrincipal principal,
        string appId,
        string? destination,
        string queryGatewayBaseUrl,
        CancellationToken cancellationToken)
    {
        if (!principal.IsDashboardUser || principal.UserId is null)
        {
            return new GrafanaLaunchOutcome(false, null, null, null, "browser_session_required");
        }

        var safeDestination = string.IsNullOrWhiteSpace(destination) ? "/explore" : destination.Trim();
        if (!DestinationPattern.IsMatch(safeDestination))
        {
            return new GrafanaLaunchOutcome(false, null, null, null, "invalid_destination");
        }

        var provisioned = await provisioning.EnsureProvisionedAsync(principal, appId, queryGatewayBaseUrl, cancellationToken);
        if (!provisioned.Ready)
        {
            return new GrafanaLaunchOutcome(false, null, null, null, provisioned.UnavailableReason ?? "grafana_unavailable");
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return new GrafanaLaunchOutcome(false, null, null, null, "unknown_app");
        }

        var code = $"mcl_glaunch_{Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant()}";
        var now = timeProvider.GetUtcNow();
        db.GrafanaLaunchCodes.Add(new GrafanaLaunchCode
        {
            Id = ids.NewId("glaunch"),
            OrganizationId = app.OrganizationId,
            AppId = app.Id,
            MinicloudUserId = principal.UserId,
            CodeHash = HashLaunchCode(code, _options.Audience),
            Destination = safeDestination,
            ExpiresAt = now.Add(TelemetryLimits.GrafanaLaunchCodeTtl),
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new GrafanaLaunchOutcome(true, code, safeDestination, now.Add(TelemetryLimits.GrafanaLaunchCodeTtl), null);
    }

    public async Task<GrafanaExchangeOutcome> ExchangeLaunchCodeAsync(
        string code,
        string queryGatewayBaseUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new GrafanaExchangeOutcome(false, null, null, null, "invalid_code");
        }

        var now = timeProvider.GetUtcNow();
        var row = await db.GrafanaLaunchCodes
            .SingleOrDefaultAsync(x => x.CodeHash == HashLaunchCode(code.Trim(), _options.Audience), cancellationToken);
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

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.AppId, cancellationToken);
        if (app is null || app.OrganizationId != row.OrganizationId)
        {
            return new GrafanaExchangeOutcome(false, null, null, null, "invalid_code");
        }

        if (!await IsCurrentMemberAsync(row.MinicloudUserId, row.OrganizationId, cancellationToken))
        {
            logger.LogWarning("Grafana exchange denied: membership lapsed for launch {LaunchId}.", row.Id);
            return new GrafanaExchangeOutcome(false, null, null, null, "membership_revoked");
        }

        var orgBinding = await db.GrafanaOrganizationBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == row.OrganizationId, cancellationToken);
        var userBinding = await db.GrafanaUserBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == row.OrganizationId && x.MinicloudUserId == row.MinicloudUserId, cancellationToken);
        var dsBinding = await db.GrafanaDataSourceBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == row.AppId && x.DeletedAt == null, cancellationToken);
        if (orgBinding?.ReconciliationStatus != "ready" || userBinding?.ReconciliationStatus != "ready"
            || dsBinding?.ReconciliationStatus != "ready" || orgBinding.GrafanaOrgId <= 0 || userBinding.GrafanaUserId <= 0)
        {
            return new GrafanaExchangeOutcome(false, null, null, null, "grafana_unavailable");
        }

        var sessionToken = $"mcl_gsess_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
        db.GrafanaProxySessions.Add(new GrafanaProxySession
        {
            Id = ids.NewId("gsess"),
            OrganizationId = row.OrganizationId,
            AppId = row.AppId,
            MinicloudUserId = row.MinicloudUserId,
            SessionHash = HashSessionToken(sessionToken),
            ExpiresAt = now.Add(TelemetryLimits.GrafanaSessionAbsolute),
            LastSeenAt = now,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return new GrafanaExchangeOutcome(true, sessionToken, row.Destination, now.Add(TelemetryLimits.GrafanaSessionAbsolute), null);
    }

    public async Task<GrafanaSessionSnapshot?> ValidateProxySessionAsync(string sessionToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var hash = HashSessionToken(sessionToken.Trim());
        var session = await db.GrafanaProxySessions.SingleOrDefaultAsync(x => x.SessionHash == hash, cancellationToken);
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

        if (!await IsCurrentMemberAsync(session.MinicloudUserId, session.OrganizationId, cancellationToken))
        {
            session.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        var orgBinding = await db.GrafanaOrganizationBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == session.OrganizationId, cancellationToken);
        var userBinding = await db.GrafanaUserBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == session.OrganizationId && x.MinicloudUserId == session.MinicloudUserId, cancellationToken);
        var dsBinding = await db.GrafanaDataSourceBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == session.AppId && x.DeletedAt == null, cancellationToken);
        if (orgBinding?.ReconciliationStatus != "ready" || userBinding?.ReconciliationStatus != "ready"
            || dsBinding?.ReconciliationStatus != "ready")
        {
            return null;
        }

        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == session.AppId, cancellationToken);
        if (app is null || app.OrganizationId != session.OrganizationId)
        {
            return null;
        }

        session.LastSeenAt = now;
        await db.SaveChangesAsync(cancellationToken);

        return new GrafanaSessionSnapshot(
            session.Id, session.OrganizationId, session.AppId, session.MinicloudUserId,
            orgBinding.GrafanaOrgId, userBinding.GrafanaUserId,
            GrafanaProvisioningService.ExpectedLogin(session.MinicloudUserId, session.OrganizationId),
            dsBinding.GrafanaUid, session.ExpiresAt);
    }

    public async Task LogoutAsync(string sessionToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return;
        }

        var session = await db.GrafanaProxySessions
            .SingleOrDefaultAsync(x => x.SessionHash == HashSessionToken(sessionToken.Trim()), cancellationToken);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RevokeUserSessionsAsync(string userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.GrafanaProxySessions
            .Where(x => x.MinicloudUserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), cancellationToken);
        await db.GrafanaLaunchCodes
            .Where(x => x.MinicloudUserId == userId && x.ConsumedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConsumedAt, now), cancellationToken);
    }

    /// <summary>
    /// Server-generated Explore launch URL only: relative, fixed path, no caller
    /// input beyond the app identity resolved from storage. Never an open redirect.
    /// </summary>
    public async Task<string?> BuildExploreUrlAsync(string appId, CancellationToken cancellationToken)
    {
        var app = await db.Apps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == appId, cancellationToken);
        if (app is null)
        {
            return null;
        }

        var orgBinding = await db.GrafanaOrganizationBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == app.OrganizationId, cancellationToken);
        var dsBinding = await db.GrafanaDataSourceBindings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AppId == appId && x.DeletedAt == null, cancellationToken);
        if (orgBinding?.ReconciliationStatus != "ready" || dsBinding?.ReconciliationStatus != "ready")
        {
            return null;
        }

        var datasource = Uri.EscapeDataString(dsBinding.GrafanaUid);
        return $"/explore?orgId={orgBinding.GrafanaOrgId}&left={Uri.EscapeDataString($"{{\"datasource\":\"{datasource}\"}}")}";
    }

    public bool IsOriginAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        var allowed = _options.AllowedOrigins.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (allowed.Length == 0)
        {
            return hosting.IsDevelopment();
        }

        return allowed.Any(x => string.Equals(x.Trim().TrimEnd('/'), origin.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }

    internal async Task<bool> IsCurrentMemberAsync(string userId, string organizationId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var key = $"{userId}\u0001{organizationId}";
        if (_membershipCache.TryGetValue(key, out var cached) && cached.ExpiresAt > now)
        {
            return cached.IsMember;
        }

        bool isMember;
        try
        {
            var principal = new ControlPlanePrincipal("browser_session", userId, null, null, new HashSet<string>(StringComparer.Ordinal));
            isMember = await authorization.CanAccessOrganizationAsync(principal.ToActorIdentity(), organizationId, OrganizationAction.ReadApplicationLogs, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Membership authority unavailable; failing closed.");
            if (_membershipCache.TryGetValue(key, out var stale) && stale.ExpiresAt > now)
            {
                return stale.IsMember;
            }

            return false;
        }

        _membershipCache[key] = new MembershipCacheEntry(isMember, now.Add(TelemetryLimits.AuthorizationCacheTtl));
        return isMember;
    }

    internal static string HashLaunchCode(string code, string audience)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{audience}\u0001{code}"));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public static string HashSessionToken(string token)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private sealed record MembershipCacheEntry(bool IsMember, DateTimeOffset ExpiresAt);
}
