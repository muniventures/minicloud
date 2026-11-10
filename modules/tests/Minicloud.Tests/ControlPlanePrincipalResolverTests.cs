using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ControlPlanePrincipalResolverTests
{
    [Fact]
    public async Task UnknownBearerDoesNotAdmitOrCreateHumanUsers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        var resolver = CreateResolver(db, new BrowserSessionService(db, new IdGenerator(), TimeProvider.System), new KratosValidator());

        var principal = await resolver.ResolveAsync("unrecognized-token", CancellationToken.None);

        Assert.Null(principal);
        Assert.Empty(await db.Users.ToListAsync());
    }

    [Fact]
    public async Task BrowserSessionRequiresExactBoundIdentityAndActiveUpstreamSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User
        {
            Id = "usr_bound",
            KratosIdentityId = "identity_bound",
            Email = "user@example.test"
        });
        await db.SaveChangesAsync();
        var sessions = new BrowserSessionService(db, new IdGenerator(), TimeProvider.System);
        var created = await sessions.CreateAsync("usr_bound", "identity_bound", "session_bound", CancellationToken.None);
        var resolver = CreateResolver(db, sessions, new KratosValidator());

        var principal = await resolver.ResolveBrowserSessionAsync(created.Cookie, CancellationToken.None);

        Assert.NotNull(principal);
        Assert.Equal("browser_session", principal.Kind);
        Assert.Equal("usr_bound", principal.UserId);
        Assert.Equal(created.Session.Id, principal.BrowserSessionId);
    }

    [Fact]
    public async Task InactiveKratosSessionRevokesTheLocalSession()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new User { Id = "usr_bound", KratosIdentityId = "identity_bound", Email = "user@example.test" });
        await db.SaveChangesAsync();
        var sessions = new BrowserSessionService(db, new IdGenerator(), TimeProvider.System);
        var created = await sessions.CreateAsync("usr_bound", "identity_bound", "session_bound", CancellationToken.None);
        var resolver = CreateResolver(db, sessions, new KratosValidator(active: false));

        Assert.Null(await resolver.ResolveBrowserSessionAsync(created.Cookie, CancellationToken.None));
        var stored = await db.BrowserSessions.SingleAsync(x => x.Id == created.Session.Id);
        Assert.NotNull(stored.RevokedAt);
    }

    private static MinicloudDbContext CreateDb(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);

    private static ControlPlanePrincipalResolver CreateResolver(
        MinicloudDbContext db,
        BrowserSessionService sessions,
        IKratosSessionValidator kratosSessions) =>
        new(db, new ApiKeyTokenService(), TimeProvider.System, sessions, kratosSessions);

    private sealed class KratosValidator(bool active = true) : IKratosSessionValidator
    {
        public Task<bool> IsSessionActiveAsync(string sessionId, string identityId, CancellationToken cancellationToken) =>
            Task.FromResult(active && sessionId == "session_bound" && identityId == "identity_bound");

        public Task RevokeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
