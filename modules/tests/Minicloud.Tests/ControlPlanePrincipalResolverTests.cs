using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Minicloud.Api.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class ControlPlanePrincipalResolverTests
{
    [Theory]
    [InlineData("original@example.test", "Original")]
    [InlineData("changed@example.test", "Changed")]
    public async Task ExistingFirebaseUser_AuthenticatesWithoutSavingOrChangingProfile(string email, string name)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var saves = new SaveCounter();
        await using var db = CreateDb(connection, saves);
        await db.Database.EnsureCreatedAsync();
        var originalTime = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        db.Users.Add(new User
        {
            Id = "usr_existing", FirebaseUid = "firebase-existing",
            Email = "original@example.test", DisplayName = "Original",
            CreatedAt = originalTime, UpdatedAt = originalTime
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        saves.Count = 0;
        var resolver = CreateResolver(db, new VerifiedFirebaseToken("firebase-existing", email, name));

        var principal = await resolver.ResolveAsync("firebase-token", CancellationToken.None);

        Assert.NotNull(principal);
        Assert.True(principal.IsDashboardUser);
        Assert.Equal("usr_existing", principal.UserId);
        Assert.Equal(0, saves.Count);
        Assert.Empty(db.ChangeTracker.Entries<User>());
        var user = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal("original@example.test", user.Email);
        Assert.Equal("Original", user.DisplayName);
        Assert.Equal(originalTime, user.UpdatedAt);
    }

    [Fact]
    public async Task NewFirebaseUser_IsCreatedOnceAndReused()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var saves = new SaveCounter();
        await using var db = CreateDb(connection, saves);
        await db.Database.EnsureCreatedAsync();
        var resolver = CreateResolver(db, new VerifiedFirebaseToken("firebase-new", "new@example.test", "New"));

        var first = await resolver.ResolveAsync("firebase-token", CancellationToken.None);
        db.ChangeTracker.Clear();
        var second = await resolver.ResolveAsync("firebase-token", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.UserId, second.UserId);
        Assert.Equal(1, saves.Count);
        var user = await db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(first.UserId, user.Id);
        Assert.Equal("firebase-new", user.FirebaseUid);
        Assert.Equal("new@example.test", user.Email);
        Assert.Equal("New", user.DisplayName);
        Assert.NotEqual(default, user.CreatedAt);
        Assert.Equal(user.CreatedAt, user.UpdatedAt);
    }

    private static MinicloudDbContext CreateDb(SqliteConnection connection, SaveCounter saves) =>
        new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).AddInterceptors(saves).Options);

    private static ControlPlanePrincipalResolver CreateResolver(MinicloudDbContext db, VerifiedFirebaseToken token) =>
        new(db, new ApiKeyTokenService(), new TokenVerifier(token), new IdGenerator(), TimeProvider.System);

    private sealed class TokenVerifier(VerifiedFirebaseToken token) : IFirebaseTokenVerifier
    {
        public Task<VerifiedFirebaseToken?> VerifyAsync(string value, CancellationToken cancellationToken) =>
            Task.FromResult<VerifiedFirebaseToken?>(token);
    }

    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
