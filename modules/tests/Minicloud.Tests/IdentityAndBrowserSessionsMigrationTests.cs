using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Minicloud.Common.Data;

namespace Minicloud.Tests;

public sealed class IdentityAndBrowserSessionsMigrationTests
{
    private const string PreviousMigration = "20261110042000_ApplicationEnvironmentModel";
    private const string CurrentMigration = "20261110204807_IdentityAndBrowserSessions";

    [Fact]
    public async Task MigrationPreservesInternalIdentitiesMembershipsAndApiCredentials()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                CREATE TABLE users (
                    id TEXT NOT NULL PRIMARY KEY,
                    firebase_uid TEXT NOT NULL,
                    email TEXT NOT NULL,
                    display_name TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IX_users_firebase_uid ON users(firebase_uid);
                CREATE UNIQUE INDEX IX_users_email ON users(email);
                INSERT INTO users VALUES ('usr_existing', 'old-firebase-id', 'operator@example.test', 'Operator', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                CREATE TABLE organizations (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    slug TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO organizations VALUES ('org_existing', 'Workspace', 'workspace', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                CREATE TABLE organization_members (
                    id TEXT NOT NULL PRIMARY KEY,
                    organization_id TEXT NOT NULL,
                    user_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO organization_members VALUES ('member_existing', 'org_existing', 'usr_existing', 'Owner', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                CREATE TABLE api_keys (
                    id TEXT NOT NULL PRIMARY KEY,
                    organization_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    key_prefix TEXT NOT NULL,
                    key_hash TEXT NOT NULL,
                    scopes_json TEXT NOT NULL,
                    last_used_at TEXT NULL,
                    expires_at TEXT NULL,
                    revoked_at TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                INSERT INTO api_keys VALUES ('key_existing', 'org_existing', 'deploy', 'prefix', 'hash-preserved', '[]', NULL, NULL, NULL, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var db = new MinicloudDbContext(
            new DbContextOptionsBuilder<MinicloudDbContext>()
                .UseSqlite(connection)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);
        foreach (var migration in db.Database.GetMigrations().Where(migration =>
                     string.CompareOrdinal(migration, PreviousMigration) <= 0))
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({migration}, '10.0.5')");
        }
        Assert.Contains(PreviousMigration, await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync(CurrentMigration);

        var user = await db.Users.AsNoTracking().SingleAsync();
        var member = await db.OrganizationMembers.AsNoTracking().SingleAsync();
        var apiKey = await db.ApiKeys.AsNoTracking().SingleAsync();
        Assert.Equal("usr_existing", user.Id);
        Assert.Null(user.KratosIdentityId);
        Assert.Equal("member_existing", member.Id);
        Assert.Equal("org_existing", member.OrganizationId);
        Assert.Equal("usr_existing", member.UserId);
        Assert.Equal("hash-preserved", apiKey.KeyHash);
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(CurrentMigration, applied);
    }
}
