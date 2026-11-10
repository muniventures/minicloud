using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class BrowserSessionServiceTests
{
    [Fact]
    public async Task PersistsOnlyHashesAndBindsCsrfToTheSessionCookie()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test" });
        await db.SaveChangesAsync();
        var sessions = new BrowserSessionService(db, new IdGenerator(), TimeProvider.System);

        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);
        var stored = await db.BrowserSessions.SingleAsync();

        Assert.NotEqual(created.Cookie, stored.TokenHash);
        Assert.Equal(BrowserSessionService.HashSecret(created.Cookie), stored.TokenHash);
        Assert.NotEqual(created.CsrfToken, stored.CsrfTokenHash);
        Assert.True(sessions.ValidateCsrf(stored, created.CsrfToken));
        Assert.False(sessions.ValidateCsrf(stored, created.CsrfToken + "x"));
        Assert.Equal(created.CsrfToken, sessions.GetCsrfToken(stored, created.Cookie));
        Assert.Null(sessions.GetCsrfToken(stored, "different-cookie"));
    }

    [Fact]
    public async Task IdleExpirySlidesButNeverExceedsAbsoluteExpiry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test" });
        await db.SaveChangesAsync();
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var sessions = new BrowserSessionService(db, new IdGenerator(), clock);
        var created = await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);
        var absoluteExpiry = created.Session.ExpiresAt;

        clock.Advance(TimeSpan.FromMinutes(119));
        var refreshed = await sessions.ValidateAsync(created.Cookie, CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Equal(clock.GetUtcNow().Add(BrowserSessionService.IdleLifetime), refreshed.IdleExpiresAt);

        clock.Advance(TimeSpan.FromHours(11));
        Assert.Null(await sessions.ValidateAsync(created.Cookie, CancellationToken.None));
        var expired = await db.BrowserSessions.SingleAsync();
        Assert.Equal(absoluteExpiry, expired.ExpiresAt);
        Assert.NotNull(expired.RevokedAt);
    }

    [Fact]
    public async Task RevokingUserSessionsPreservesTheUserAndMembership()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        db.Users.Add(new User { Id = "usr_1", KratosIdentityId = "identity_1", Email = "user@example.test", CreatedAt = now, UpdatedAt = now });
        db.Organizations.Add(new Organization { Id = "org_1", Name = "Org", Slug = "org", CreatedAt = now, UpdatedAt = now });
        db.OrganizationMembers.Add(new OrganizationMember { Id = "mem_1", UserId = "usr_1", OrganizationId = "org_1", Role = OrganizationRole.Owner, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
        var sessions = new BrowserSessionService(db, new IdGenerator(), TimeProvider.System);
        await sessions.CreateAsync("usr_1", "identity_1", "kratos_session_1", CancellationToken.None);

        await sessions.RevokeUserSessionsAsync("usr_1", CancellationToken.None);

        Assert.NotNull((await db.BrowserSessions.AsNoTracking().SingleAsync()).RevokedAt);
        Assert.Equal("usr_1", (await db.Users.SingleAsync()).Id);
        Assert.Equal("mem_1", (await db.OrganizationMembers.SingleAsync()).Id);
    }

    private static MinicloudDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
