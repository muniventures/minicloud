using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Auth;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Tests;

public sealed class DatabaseConnectionTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private MinicloudDbContext db = default!;
    private DatabaseConnectionService service = default!;
    private readonly SecretEncryptionService encryption = new(Options.Create(new SecretEncryptionOptions { EncryptionKey = "synthetic-test-key-only" }));
    private readonly ActorIdentity actor = new("api_key", null, "test-key", "org_test", new HashSet<string> { ApiKeyScopes.DatabasesManage, ApiKeyScopes.SecretsRead });
    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Organizations.Add(new Organization { Id = "org_test", Name = "Test", Slug = "test" });
        db.ManagedDatabases.Add(new ManagedDatabase { Id = "database_test", OrganizationId = "org_test", Name = "orders", Engine = "postgres", Status = "ready", EncryptedCredentialsJson = encryption.Encrypt(JsonSerializer.Serialize(new DatabaseConnectionCredentials("orders", "orders_user", "synthetic-password"), new JsonSerializerOptions(JsonSerializerDefaults.Web))) });
        await db.SaveChangesAsync();
        service = new(db, new(db), encryption, new(db, new IdGenerator(), TimeProvider.System));
    }
    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task RevealRequiresBothDatabaseManagementAndSecretAuthority()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevealAsync("database_test", actor with { Scopes = new HashSet<string> { ApiKeyScopes.DatabasesManage } }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevealAsync("database_test", actor with { Scopes = new HashSet<string> { ApiKeyScopes.SecretsRead } }, default));
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task CrossOrganizationCredentialsRemainUnavailable()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevealAsync("database_test", actor with { OrganizationId = "other" }, default));
    }

    [Fact]
    public async Task RevealAuditsIdentityOnlyAndDoesNotInventAnEndpoint()
    {
        var result = await service.RevealAsync("database_test", actor, default);
        Assert.Equal("synthetic-password", result.Password);
        Assert.False(result.EndpointAvailable);
        Assert.Null(result.Host);
        var audit = await db.AuditEvents.SingleAsync();
        Assert.DoesNotContain("synthetic-password", audit.Details);
        Assert.DoesNotContain("synthetic-password", result.ToString());
        Assert.DoesNotContain("synthetic-password", new DatabaseConnectionCredentials("orders", "user", "synthetic-password").ToString());
    }

    [Fact]
    public async Task UnreadyOrCorruptedCustodyDoesNotLeakCredentials()
    {
        var database = await db.ManagedDatabases.SingleAsync();
        database.Status = "creating";
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevealAsync(database.Id, actor, default));
        database.Status = "ready"; database.EncryptedCredentialsJson = "malformed-secret";
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevealAsync(database.Id, actor, default));
        Assert.DoesNotContain("malformed-secret", error.Message);
        Assert.Null(error.InnerException);
    }
    [Fact]
    public async Task RecoveryKeyExportWorksForDeletedSourceAndNeverExportsRepositoryToken()
    {
        var database = await db.ManagedDatabases.SingleAsync();
        database.Status = "deleted";
        db.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy { Id = "policy_test", OrganizationId = "org_test", DatabaseId = database.Id, KeyVersion = "key_test", EncryptedKeyMaterialJson = encryption.Encrypt(JsonSerializer.Serialize(new { encryptionKey = "synthetic-recovery-key", repositoryToken = "synthetic-upload-token", keyVersion = "key_test" })) });
        await db.SaveChangesAsync();
        var result = await service.ExportRecoveryMaterialAsync(database.Id, actor, default);
        Assert.Equal("synthetic-recovery-key", result.EncryptionKey);
        Assert.Equal("key_test", result.KeyVersion);
        Assert.DoesNotContain("synthetic-upload-token", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("synthetic-recovery-key", result.ToString());
        var audit = await db.AuditEvents.SingleAsync();
        Assert.Null(audit.AppId);
        Assert.DoesNotContain("synthetic-recovery-key", audit.Details);
        Assert.DoesNotContain("synthetic-upload-token", audit.Details);
    }

    [Fact]
    public async Task RecoveryKeyExportRequiresBothAuthoritiesAndRejectsCorruptCustody()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExportRecoveryMaterialAsync("database_test", actor with { Scopes = new HashSet<string> { ApiKeyScopes.DatabasesManage } }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExportRecoveryMaterialAsync("database_test", actor with { Scopes = new HashSet<string> { ApiKeyScopes.SecretsRead } }, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ExportRecoveryMaterialAsync("database_test", actor with { OrganizationId = "other" }, default));
        db.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy { Id = "policy_test", OrganizationId = "org_test", DatabaseId = "database_test", KeyVersion = "expected", EncryptedKeyMaterialJson = encryption.Encrypt("{bad-secret") });
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportRecoveryMaterialAsync("database_test", actor, default));
        Assert.DoesNotContain("bad-secret", error.Message);
        Assert.Null(error.InnerException);
        Assert.Empty(await db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task PublicConnectionReturnsCertificateAndVerificationMode()
    {
        var database = await db.ManagedDatabases.SingleAsync();
        database.AccessMode = "public";
        database.EncryptedCredentialsJson = encryption.Encrypt(JsonSerializer.Serialize(new DatabaseConnectionCredentials("orders", "user", "synthetic-password", "192.0.2.10", 5432, "verify-full", "synthetic-public-ca"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await db.SaveChangesAsync();
        var result = await service.RevealAsync(database.Id, actor, default);
        Assert.True(result.EndpointAvailable);
        Assert.Equal("verify-full", result.SslMode);
        Assert.Equal("synthetic-public-ca", result.CaCertificatePem);
    }
}
