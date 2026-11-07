using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Api.Services;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class AppSecretServiceTests : IAsyncLifetime
{
    private readonly SqliteTestHarness _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    [Fact]
    public async Task EnsurePostgresPasswordAsync_CreatesGeneratedSecretImmediately()
    {
        await using var db = _db.CreateDbContext();
        var app = await SeedAppAsync(db);
        var service = CreateService(db);

        var result = await service.EnsurePostgresPasswordAsync(app, requestedPassword: null, CancellationToken.None);

        Assert.True(result.WasGenerated);
        Assert.True(result.ShouldUpdateDatabasePassword);
        Assert.False(string.IsNullOrWhiteSpace(result.Value));
        Assert.Equal(1, await db.AppSecrets.CountAsync());
    }

    [Fact]
    public async Task EnsurePostgresPasswordAsync_UpdatesExistingRequestedSecret()
    {
        await using var db = _db.CreateDbContext();
        var app = await SeedAppAsync(db);
        var service = CreateService(db);
        await service.EnsurePostgresPasswordAsync(app, "first-password", CancellationToken.None);

        var result = await service.EnsurePostgresPasswordAsync(app, "second-password", CancellationToken.None);

        Assert.False(result.WasGenerated);
        Assert.True(result.ShouldUpdateDatabasePassword);
        Assert.Equal("second-password", result.Value);
        Assert.Equal(1, await db.AppSecrets.CountAsync());

        var stored = await db.AppSecrets.SingleAsync();
        var encryption = CreateEncryptionService();
        Assert.Equal("second-password", encryption.Decrypt(stored.EncryptedValue));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private static async Task<MinicloudApp> SeedAppAsync(MinicloudDbContext db)
    {
        var now = new DateTimeOffset(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);
        var organization = new Organization
        {
            Id = $"org_{Guid.NewGuid():N}",
            Name = "Test Org",
            Slug = $"test-org-{Guid.NewGuid():N}"[..24],
            CreatedAt = now,
            UpdatedAt = now
        };
        var app = new MinicloudApp
        {
            Id = $"app_{Guid.NewGuid():N}",
            OrganizationId = organization.Id,
            Organization = organization,
            Name = "Test App",
            Slug = $"test-app-{Guid.NewGuid():N}"[..24],
            Plan = AppPlan.P0,
            Database = DatabaseMode.Postgres,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Organizations.Add(organization);
        db.Apps.Add(app);
        await db.SaveChangesAsync();
        return app;
    }

    private static AppSecretService CreateService(MinicloudDbContext db) =>
        new(db, CreateEncryptionService(), new TestIdGenerator(), new TestTimeProvider());

    private static SecretEncryptionService CreateEncryptionService() =>
        new(Options.Create(new SecretEncryptionOptions
        {
            EncryptionKey = "app-secret-service-test-key"
        }));

    private sealed class TestIdGenerator : IIdGenerator
    {
        public string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);
    }
}
