using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Api.Auth;

public sealed record ControlPlanePrincipal(
    string Kind,
    string? UserId,
    string? ApiKeyId,
    string? OrganizationId,
    IReadOnlySet<string> Scopes,
    string? BrowserSessionId = null)
{
    public bool IsDashboardUser => Kind == "browser_session";
    public bool IsApiKey => Kind == "api_key";
}

public static class ControlPlanePrincipalExtensions
{
    public static Minicloud.Common.Auth.ActorIdentity ToActorIdentity(this ControlPlanePrincipal principal) =>
        new(principal.Kind, principal.UserId, principal.ApiKeyId, principal.OrganizationId, principal.Scopes);

    public static ControlPlanePrincipal? GetControlPlanePrincipal(this ClaimsPrincipal user)
    {
        var kind = user.FindFirstValue("auth_kind");
        if (kind is null)
        {
            return null;
        }

        var scopes = user.FindAll("scope").Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
        return new ControlPlanePrincipal(
            kind,
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            user.FindFirstValue("api_key_id"),
            user.FindFirstValue("organization_id"),
            scopes);
    }
}

public sealed class ControlPlanePrincipalResolver(
    MinicloudDbContext db,
    IApiKeyTokenService apiKeyTokenService,
    TimeProvider timeProvider,
    BrowserSessionService browserSessions,
    IKratosSessionValidator kratosSessions)
{
    public async Task<ControlPlanePrincipal?> ResolveAsync(string bearerToken, CancellationToken cancellationToken)
    {
        if (bearerToken.StartsWith("mc_live_", StringComparison.Ordinal))
        {
            return await ResolveApiKeyAsync(bearerToken, cancellationToken);
        }

        return null;
    }

    public async Task<ControlPlanePrincipal?> ResolveBrowserSessionAsync(
        string cookie,
        CancellationToken cancellationToken)
    {
        var session = await browserSessions.ValidateAsync(cookie, cancellationToken);
        if (session is null)
        {
            return null;
        }

        if (!await kratosSessions.IsSessionActiveAsync(
                session.KratosSessionId,
                session.KratosIdentityId,
                cancellationToken))
        {
            await browserSessions.RevokeAsync(cookie, cancellationToken);
            return null;
        }

        return new ControlPlanePrincipal(
            "browser_session",
            session.UserId,
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            session.Id);
    }

    private async Task<ControlPlanePrincipal?> ResolveApiKeyAsync(string token, CancellationToken cancellationToken)
    {
        var prefix = ExtractApiKeyPrefix(token);
        if (prefix is null)
        {
            return null;
        }

        var candidates = await db.ApiKeys
            .Where(x => x.KeyPrefix == prefix && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        foreach (var candidate in candidates)
        {
            if (candidate.ExpiresAt is not null && candidate.ExpiresAt <= now)
            {
                continue;
            }

            if (!apiKeyTokenService.Verify(token, candidate.KeyHash))
            {
                continue;
            }

            candidate.LastUsedAt = now;
            candidate.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            var scopes = JsonSerializer.Deserialize<string[]>(candidate.ScopesJson) ?? [];
            return new ControlPlanePrincipal("api_key", null, candidate.Id, candidate.OrganizationId, scopes.ToHashSet(StringComparer.Ordinal));
        }

        return null;
    }

    private static string? ExtractApiKeyPrefix(string token)
    {
        var firstSecretSeparator = token.IndexOf('_', "mc_live_".Length);
        return firstSecretSeparator < 0 ? null : token[..firstSecretSeparator];
    }
}
