using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Api.Services;

public sealed record TelemetryAuthSnapshot(
    string CredentialId,
    int CredentialVersion,
    string OrganizationId,
    string AppId,
    string ServiceName,
    string TenantId);

public sealed class TelemetryAuthUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// T11 ingestion credential authentication: prefix lookup, expiry/revocation/enabled checks,
/// PBKDF2 verification, bounded 30s positive cache keyed by SHA256(token) carrying the
/// credential version, pre-hash rate limiting of failed attempts (failure table is
/// bounded so distinct unknown prefixes cannot grow memory without limit), coalesced last-used
/// writes, and fail-closed behavior on store outage after cache expiry.
/// Runtime tokens (mrt_*) are never accepted here.
/// </summary>
public sealed class TelemetryCredentialAuthService(
    MinicloudDbContext db,
    ITelemetryTokenService tokens,
    TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailureEntry> _failures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsedWrites = new(StringComparer.Ordinal);

    private const int MaxFailuresPerWindow = 5;
    private const int MaxTrackedPrefixes = 4096;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LastUsedCoalesceWindow = TimeSpan.FromMinutes(1);

    public async Task<TelemetryAuthSnapshot?> AuthenticateAsync(string? authorizationHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        const string bearer = "Bearer ";
        if (!authorizationHeader.StartsWith(bearer, StringComparison.Ordinal))
        {
            return null;
        }

        var rawToken = authorizationHeader[bearer.Length..].Trim();
        if (rawToken.StartsWith("mrt_", StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = tokens.ExtractPrefix(authorizationHeader);
        if (prefix is null)
        {
            return null;
        }

        var cacheKey = CacheKeyFor(rawToken);
        var now = timeProvider.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
        {
            return cached.Snapshot;
        }

        if (IsRateLimited(prefix, now))
        {
            return null;
        }

        List<TelemetryIngestionCredential> candidates;
        try
        {
            candidates = await db.TelemetryIngestionCredentials
                .Where(x => x.KeyPrefix == prefix && x.RevokedAt == null)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            if (_cache.TryGetValue(cacheKey, out var stale) && stale.ExpiresAt > now)
            {
                return stale.Snapshot;
            }

            throw new TelemetryAuthUnavailableException("Telemetry authority is temporarily unavailable.", ex);
        }

        TelemetryIngestionCredential? match = null;
        foreach (var candidate in candidates)
        {
            if (candidate.ExpiresAt is not null && candidate.ExpiresAt <= now)
            {
                continue;
            }

            bool verified;
            try
            {
                verified = tokens.Verify(rawToken, candidate.KeyHash);
            }
            catch (Exception ex)
            {
                throw new TelemetryAuthUnavailableException("Telemetry authority is temporarily unavailable.", ex);
            }

            if (verified)
            {
                match = candidate;
                break;
            }
        }

        if (match is null)
        {
            RecordFailure(prefix, now);
            return null;
        }

        AppTelemetrySettings? settings;
        try
        {
            settings = await db.AppTelemetrySettings
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.AppId == match.AppId, cancellationToken);
        }
        catch (Exception ex)
        {
            if (_cache.TryGetValue(cacheKey, out var stale) && stale.ExpiresAt > now)
            {
                return stale.Snapshot;
            }

            throw new TelemetryAuthUnavailableException("Telemetry authority is temporarily unavailable.", ex);
        }

        if (settings is null || !settings.Enabled || settings.Deleting ||
            settings.OrganizationId != match.OrganizationId)
        {
            RecordFailure(prefix, now);
            return null;
        }

        ClearFailures(prefix);
        var snapshot = new TelemetryAuthSnapshot(
            match.Id, match.CredentialVersion, match.OrganizationId, match.AppId, match.ServiceName, settings.TenantId);
        _cache[cacheKey] = new CacheEntry(snapshot, now.Add(TelemetryLimits.CredentialCacheTtl));

        if (!_lastUsedWrites.TryGetValue(match.Id, out var lastWrite) || now - lastWrite >= LastUsedCoalesceWindow)
        {
            _lastUsedWrites[match.Id] = now;
            try
            {
                var tracked = await db.TelemetryIngestionCredentials
                    .SingleOrDefaultAsync(x => x.Id == match.Id, cancellationToken);
                if (tracked is not null)
                {
                    tracked.LastUsedAt = now;
                    tracked.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            catch
            {
                // Coalesced usage writes are best effort and must not fail authentication.
            }
        }

        return snapshot;
    }

    private bool IsRateLimited(string prefix, DateTimeOffset now)
    {
        if (_failures.TryGetValue(prefix, out var entry))
        {
            if (now - entry.WindowStart > FailureWindow)
            {
                _failures.TryRemove(prefix, out _);
                return false;
            }

            return entry.Count >= MaxFailuresPerWindow;
        }

        return false;
    }

    private void RecordFailure(string prefix, DateTimeOffset now)
    {
        if (!_failures.ContainsKey(prefix) && _failures.Count >= MaxTrackedPrefixes)
        {
            foreach (var stale in _failures.Where(kvp => now - kvp.Value.WindowStart > FailureWindow).Select(kvp => kvp.Key).Take(64).ToList())
            {
                _failures.TryRemove(stale, out _);
            }

            if (_failures.Count >= MaxTrackedPrefixes)
            {
                return;
            }
        }

        _failures.AddOrUpdate(
            prefix,
            _ => new FailureEntry(1, now),
            (_, existing) => now - existing.WindowStart > FailureWindow
                ? new FailureEntry(1, now)
                : new FailureEntry(existing.Count + 1, existing.WindowStart));
    }

    private void ClearFailures(string prefix) => _failures.TryRemove(prefix, out _);

    internal static string CacheKeyFor(string rawToken)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private sealed record CacheEntry(TelemetryAuthSnapshot Snapshot, DateTimeOffset ExpiresAt);
    private sealed record FailureEntry(int Count, DateTimeOffset WindowStart);
}
