using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed record CreatedBrowserSession(string Cookie, string CsrfToken, BrowserSession Session);

public sealed class BrowserSessionService(
    MinicloudDbContext db,
    IIdGenerator ids,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(120);

    public async Task<CreatedBrowserSession> CreateAsync(
        string userId,
        string kratosIdentityId,
        string kratosSessionId,
        CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null || user.KratosIdentityId != kratosIdentityId)
        {
            throw new InvalidOperationException("The identity is not bound to this Minicloud user.");
        }

        var now = timeProvider.GetUtcNow();
        var cookie = CreateSecret();
        var sessionId = ids.NewId("bsess");
        var csrfToken = CreateCsrfToken(cookie, sessionId);
        var session = new BrowserSession
        {
            Id = sessionId,
            UserId = userId,
            KratosIdentityId = kratosIdentityId,
            KratosSessionId = kratosSessionId,
            TokenHash = HashSecret(cookie),
            CsrfTokenHash = HashSecret(csrfToken),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now.Add(AbsoluteLifetime),
            IdleExpiresAt = now.Add(IdleLifetime)
        };
        db.BrowserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedBrowserSession(cookie, csrfToken, session);
    }

    public async Task<BrowserSession?> ValidateAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return null;
        }

        var session = await db.BrowserSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == HashSecret(cookie), cancellationToken);
        if (session is null || session.RevokedAt is not null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        if (session.ExpiresAt <= now || session.IdleExpiresAt <= now ||
            session.User.KratosIdentityId != session.KratosIdentityId)
        {
            session.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        session.LastSeenAt = now;
        session.IdleExpiresAt = Min(session.ExpiresAt, now.Add(IdleLifetime));
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<CreatedBrowserSession> CreateOrRotateAsync(
        string userId,
        string kratosIdentityId,
        string kratosSessionId,
        CancellationToken cancellationToken)
    {
        var existing = await db.BrowserSessions.SingleOrDefaultAsync(
            x => x.KratosSessionId == kratosSessionId, cancellationToken);
        if (existing is null)
            return await CreateAsync(userId, kratosIdentityId, kratosSessionId, cancellationToken);

        if (existing.UserId != userId || existing.KratosIdentityId != kratosIdentityId ||
            !await db.Users.AnyAsync(x => x.Id == userId && x.KratosIdentityId == kratosIdentityId, cancellationToken))
            throw new InvalidOperationException("The identity is not bound to this Minicloud user.");

        // Kratos settings can retain its session ID; rotate the local secret, not the unique upstream binding.
        var now = timeProvider.GetUtcNow();
        var cookie = CreateSecret();
        var csrfToken = CreateCsrfToken(cookie, existing.Id);
        existing.TokenHash = HashSecret(cookie);
        existing.CsrfTokenHash = HashSecret(csrfToken);
        existing.CreatedAt = now;
        existing.LastSeenAt = now;
        existing.ExpiresAt = now.Add(AbsoluteLifetime);
        existing.IdleExpiresAt = now.Add(IdleLifetime);
        existing.RevokedAt = null;
        db.Entry(existing).Property(x => x.RevokedAt).IsModified = true;
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedBrowserSession(cookie, csrfToken, existing);
    }

    public async Task RevokeAsync(string cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }

        var session = await db.BrowserSessions
            .SingleOrDefaultAsync(x => x.TokenHash == HashSecret(cookie), cancellationToken);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task RevokeByIdAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await db.BrowserSessions
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is not null && session.RevokedAt is null)
        {
            session.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public string? GetCsrfToken(BrowserSession session, string cookie)
    {
        var csrfToken = CreateCsrfToken(cookie, session.Id);
        return VerifySecret(session.CsrfTokenHash, csrfToken) ? csrfToken : null;
    }

    public async Task<BrowserSession?> FindByIdAsync(string sessionId, CancellationToken cancellationToken) =>
        await db.BrowserSessions
            .Include(x => x.User)
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);

    public async Task RevokeUserSessionsAsync(string userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await db.BrowserSessions
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.RevokedAt, now), cancellationToken);
    }

    public bool ValidateCsrf(BrowserSession session, string? csrfToken)
    {
        return !string.IsNullOrWhiteSpace(csrfToken) && VerifySecret(session.CsrfTokenHash, csrfToken);
    }

    public static string HashSecret(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string CreateSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string CreateCsrfToken(string cookie, string sessionId)
    {
        var key = Encoding.UTF8.GetBytes(cookie);
        var value = Encoding.UTF8.GetBytes(sessionId);
        return Convert.ToBase64String(HMACSHA256.HashData(key, value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool VerifySecret(string storedHash, string secret)
    {
        var expected = Convert.FromHexString(storedHash);
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return expected.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left < right ? left : right;
}
