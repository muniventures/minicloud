using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;

namespace Minicloud.Worker.Databases;

public sealed partial class DatabaseOperationProcessor
{
    private async Task<bool> PurgeBackupsAsync(DatabaseOperation operation, ManagedDatabase database, CancellationToken ct)
    {
        using var request = JsonDocument.Parse(operation.RequestJson);
        if (!request.RootElement.TryGetProperty("confirmation", out var confirmation) || confirmation.GetString() != database.Name)
            throw new InvalidOperationException("Exact database confirmation is required.");
        var policy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct);
        if (policy.Enabled || policy.KeepLastCopies)
            throw new InvalidOperationException("Disable backups and indefinite retention before purging.");
        var repositories = await db.DatabaseBackupArtifacts.Where(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId && x.Status != "purged")
            .Select(x => x.RepositoryAppId).Distinct().ToListAsync(ct);
        if (policy.RepositoryAppId is not null && !repositories.Contains(policy.RepositoryAppId)) repositories.Add(policy.RepositoryAppId);
        var total = 0;
        if (repositories.Count != 0)
        {
            var material = ReadBackupMaterial(policy);
            foreach (var repositoryId in repositories)
            {
                var artifacts = await db.DatabaseBackupArtifacts.Where(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId && x.RepositoryAppId == repositoryId).ToListAsync(ct);
                var exists = await db.Apps.AnyAsync(x => x.Id == repositoryId && x.OrganizationId == database.OrganizationId && x.DeletionStatus == null, ct);
                if (!exists)
                {
                    if (artifacts.Any(x => x.Status == "complete")) throw new InvalidOperationException("A retained repository is unavailable.");
                }
                else
                {
                    var repository = await BackupRepositoryAsync(new DatabaseBackupPolicy { OrganizationId = database.OrganizationId, RepositoryAppId = repositoryId }, ct);
                    var binding = new { environment = BackupNamespace(database.OrganizationId), databaseId = database.Id, token = material.RepositoryToken,
                        retentionDays = policy.RetentionDays, minimumComplete = policy.MinimumCopies, enabled = false, keepLastCopies = false, policyRevision = policy.Revision };
                    await ExecuteRemoteAsync(operation, repository.Host.IpAddress!, "repository-control.py", new
                    {
                        container = "minicloud-" + repository.App.Slug + "-repository-1", binding,
                        backupIds = Array.Empty<string>(), operationId = operation.Id
                    }, ct);
                    int remaining;
                    do
                    {
                        var result = await ExecuteRemoteAsync(operation, repository.Host.IpAddress!, "repository-control.py", new
                        {
                            action = "purge-source", container = "minicloud-" + repository.App.Slug + "-repository-1", binding,
                            expectedRepositoryId = repository.App.Id, operationId = operation.Id
                        }, ct);
                        if (result.GetProperty("status").GetString() != "purged") throw new InvalidOperationException("Repository purge was not confirmed.");
                        var count = result.GetProperty("purgedCount").GetInt32();
                        remaining = result.GetProperty("remainingCount").GetInt32();
                        if (count is < 0 or > 100 || remaining < 0 || remaining > 0 && count == 0)
                            throw new InvalidOperationException("Repository purge did not make bounded progress.");
                        total += count;
                    } while (remaining != 0);
                }
                foreach (var artifact in artifacts) { artifact.Status = "purged"; artifact.UpdatedAt = clock.GetUtcNow(); }
                operation.ResultJson = JsonSerializer.Serialize(new { status = "purging", purgedCount = total, repositoryId }, Json);
                await SaveOwnedDatabaseChangesAsync(operation, ct);
            }
        }
        policy.RepositoryAppId = null;
        policy.EncryptedKeyMaterialJson = null;
        policy.KeyVersion = null;
        policy.NextDueAt = null;
        policy.Status = "purged";
        policy.UpdatedAt = clock.GetUtcNow();
        operation.ResultJson = JsonSerializer.Serialize(new { status = "purged", purgedCount = total, recoveryMaterialRemoved = true }, Json);
        db.AuditEvents.Add(new AuditEvent { Id = ids.NewId("audit"), OrganizationId = database.OrganizationId, Actor = "worker",
            Action = "database.backups.purged", Details = JsonSerializer.Serialize(new { databaseId = database.Id, operationId = operation.Id, purgedCount = total }, Json), CreatedAt = clock.GetUtcNow() });
        return true;
    }
}
