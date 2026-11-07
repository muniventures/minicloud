using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;
using Minicloud.Telemetry.Data;

namespace Minicloud.Telemetry.Services;

public sealed class TelemetryAuthUnavailableException(string message) : Exception(message);

public sealed record TelemetryAuthSnapshot(
    string AppId,
    string OrganizationId,
    string ServiceName,
    string TenantId,
    string CredentialId,
    int CredentialVersion);

public sealed class TelemetryCredentialAuthService(
    TelemetryDbContext db,
    TelemetryAuthorityService authority,
    TelemetryStatusReporter reporter,
    TimeProvider timeProvider,
    ILogger<TelemetryCredentialAuthService> logger)
{
    private const string ExpectedScheme = "Bearer";
    private const string ExpectedPrefix = "mcl_ingest_";

    public async Task<TelemetryAuthSnapshot?> AuthenticateAsync(
        string? authorizationHeader,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return null;
        }

        string rawToken;
        var parts = authorizationHeader.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && string.Equals(parts[0], ExpectedScheme, StringComparison.OrdinalIgnoreCase))
        {
            rawToken = parts[1].Trim();
        }
        else if (parts.Length == 1 && parts[0].StartsWith(ExpectedPrefix, StringComparison.Ordinal))
        {
            rawToken = parts[0].Trim();
        }
        else
        {
            return null;
        }

        if (!rawToken.StartsWith(ExpectedPrefix, StringComparison.Ordinal) || rawToken.Length < 32)
        {
            return null;
        }

        // Check authority freshness and outage allowance.
        // Synchronized valid credentials work until expiry or 60 minutes from last confirmed checkpoint.
        var checkpoint = await authority.GetCheckpointAsync(cancellationToken);
        if (checkpoint is null)
        {
            logger.LogWarning("No confirmed authority checkpoint exists; telemetry authority unavailable.");
            throw new TelemetryAuthUnavailableException("Authority checkpoint not established.");
        }

        var now = timeProvider.GetUtcNow();
        if (now - checkpoint.ConfirmedAt > TelemetryLimits.MaxIngestionOutageAllowance)
        {
            logger.LogWarning(
                "Authority checkpoint is stale ({Age} > {Allowance}); denying ingestion as retryable 503.",
                now - checkpoint.ConfirmedAt,
                TelemetryLimits.MaxIngestionOutageAllowance);
            throw new TelemetryAuthUnavailableException("Authority checkpoint expired (>60m).");
        }

        var prefix = ExtractPrefix(rawToken);
        var candidates = await db.CredentialReplicas
            .Where(x => x.KeyPrefix == prefix && x.RevokedAt == null && x.Status == "active")
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return null;
        }

        TelemetryCredentialReplica? matched = null;
        foreach (var candidate in candidates)
        {
            if (candidate.ExpiresAt.HasValue && candidate.ExpiresAt.Value <= now)
            {
                continue;
            }

            if (VerifyHash(rawToken, candidate.KeyHash))
            {
                matched = candidate;
                break;
            }
        }

        if (matched is null)
        {
            return null;
        }

        // Check tenant binding
        var tenant = await db.TenantBindings
            .FirstOrDefaultAsync(x => x.AppId == matched.AppId, cancellationToken);

        if (tenant is null || !tenant.Enabled || tenant.Deleting)
        {
            return null;
        }

        // Record last used asynchronously
        reporter.RecordLastUsed(matched.Id, now);

        return new TelemetryAuthSnapshot(
            matched.AppId,
            matched.OrganizationId,
            matched.ServiceName,
            tenant.TenantId,
            matched.Id,
            matched.CredentialVersion);
    }

    private static string ExtractPrefix(string token)
    {
        var underscoreIndex = token.IndexOf('_', ExpectedPrefix.Length);
        return underscoreIndex > 0 ? token[..underscoreIndex] : token[..Math.Min(token.Length, 19)];
    }

    private static bool VerifyHash(string rawToken, string storedHash)
    {
        if (storedHash.StartsWith("pbkdf2_sha256$", StringComparison.Ordinal))
        {
            var p = storedHash.Split('$');
            if (p.Length == 4 && int.TryParse(p[1], out var iter))
            {
                try
                {
                    var s = Convert.FromBase64String(p[2]);
                    var exp = Convert.FromBase64String(p[3]);
                    var act = Rfc2898DeriveBytes.Pbkdf2(
                        Encoding.UTF8.GetBytes(rawToken),
                        s,
                        iter,
                        HashAlgorithmName.SHA256,
                        exp.Length);
                    return CryptographicOperations.FixedTimeEquals(exp, act);
                }
                catch
                {
                    return false;
                }
            }
        }

        var parts = storedHash.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromHexString(parts[1]);
            expectedHash = Convert.FromHexString(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(rawToken),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
    }
}
