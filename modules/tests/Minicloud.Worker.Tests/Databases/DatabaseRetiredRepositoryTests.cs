using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Databases;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Tests.Databases;

public sealed class DatabaseRetiredRepositoryTests
{
    [Theory]
    [InlineData("purged")]
    [InlineData("expired")]
    [InlineData("complete")]
    public async Task ReconciliationSkipsRetiredRepositoriesAndProtectsCompleteBackups(string artifactStatus)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MinicloudDbContext(new DbContextOptionsBuilder<MinicloudDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var encryption = new SecretEncryptionService(Options.Create(new SecretEncryptionOptions { EncryptionKey = "synthetic-reconciliation-key" }));
        db.Organizations.Add(new Organization { Id = "org", Name = "Org", Slug = "org" });
        var database = new ManagedDatabase { Id = "database_source", OrganizationId = "org", Name = "source", Status = "ready" };
        db.ManagedDatabases.Add(database);
        var policy = new DatabaseBackupPolicy { Id = "policy", DatabaseId = database.Id, OrganizationId = "org", Enabled = false,
            EncryptedKeyMaterialJson = encryption.Encrypt(JsonSerializer.Serialize(new { encryptionKey = "test-key", repositoryToken = "test-token", keyVersion = "new-version" })) };
        db.DatabaseBackupPolicies.Add(policy);
        db.DatabaseBackupArtifacts.Add(new DatabaseBackupArtifact { Id = "backup", OrganizationId = "org", DatabaseId = database.Id,
            RepositoryAppId = "deleted-old-repository", BackupId = "backup-fixture", Status = artifactStatus });
        await db.SaveChangesAsync();
        var processor = new DatabaseOperationProcessor(db, null!, null!, null!, null!, encryption, new IdGenerator(), TimeProvider.System,
            null!, Options.Create(new SshDeploymentOptions()), Options.Create(new DatabasePlatformOptions()), null!);
        if (artifactStatus == "complete")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ReconcileBackupPolicyAsync(new DatabaseOperation { Id = "op", OrganizationId = "org", DatabaseId = database.Id }, database, default));
            Assert.NotNull(policy.EncryptedKeyMaterialJson);
            return;
        }
        Assert.True(await processor.ReconcileBackupPolicyAsync(new DatabaseOperation { Id = "op", OrganizationId = "org", DatabaseId = database.Id }, database, default));
        Assert.Equal("disabled", policy.Status);
        Assert.NotNull(policy.EncryptedKeyMaterialJson);
    }
}
