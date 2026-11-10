using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;
using Minicloud.Common.Contracts;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Databases;

public sealed partial class DatabaseOperationProcessor
{
    private sealed record BackupMaterial(string EncryptionKey, string RepositoryToken, string KeyVersion);

    private static string BackupNamespace(string organizationId) => organizationId.Replace('_', '-').ToLowerInvariant();

    private BackupMaterial ReadBackupMaterial(DatabaseBackupPolicy policy) =>
        JsonSerializer.Deserialize<BackupMaterial>(encryption.Decrypt(policy.EncryptedKeyMaterialJson
            ?? throw new InvalidOperationException("Backup custody is unavailable.")), Json)!;

    private async Task<(MinicloudApp App, ServerInstance Host)> BackupSourceAsync(ManagedDatabase database, CancellationToken ct)
    {
        var appId = database.AppId ?? database.RecoverySourceAppId;
        if (appId is null)
        {
            appId = await db.ManagedDatabaseServers.Where(x => x.Id == database.ServerId && x.OrganizationId == database.OrganizationId && x.Status == "ready")
                .Select(x => x.RuntimeAppId).SingleAsync(ct);
        }
        var app = await db.Apps.SingleAsync(x => x.Id == appId && x.OrganizationId == database.OrganizationId && x.DeletionStatus == null, ct);
        var host = await db.ServerInstances.SingleAsync(x => x.AppId == app.Id && x.OrganizationId == database.OrganizationId, ct);
        if (string.IsNullOrWhiteSpace(host.IpAddress) || string.IsNullOrWhiteSpace(host.ProviderServerId)) { throw new InvalidOperationException("Source host is unavailable."); }
        return (app, host);
    }

    private async Task<(MinicloudApp App, ServerInstance Host, string Url)> BackupRepositoryAsync(DatabaseBackupPolicy policy, CancellationToken ct)
    {
        var app = await db.Apps.SingleAsync(x => x.Id == policy.RepositoryAppId && x.OrganizationId == policy.OrganizationId && x.Database == DatabaseMode.None && x.Slug.StartsWith("dbrepo-") && x.DeletionStatus == null, ct);
        var host = await db.ServerInstances.SingleAsync(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId, ct);
        var domain = await db.Domains.Where(x => x.AppId == app.Id && x.ServiceName == "repository" && x.SslStatus == DomainBindingService.SslReady && x.Status == DomainBindingService.StatusActive)
            .OrderByDescending(x => x.IsPrimary).FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("Repository HTTPS is unavailable.");
        if (string.IsNullOrWhiteSpace(host.IpAddress) || string.IsNullOrWhiteSpace(host.ProviderServerId)) { throw new InvalidOperationException("Repository host is unavailable."); }
        return (app, host, "https://" + domain.Hostname);
    }

    private async Task ConfigureBackupAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        var policy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct);
        if (policy.RepositoryAppId is null)
        {
            if (policy.Enabled) { throw new InvalidOperationException("Repository is required."); }
            policy.Status = "disabled";
            policy.NextDueAt = null;
            return;
        }
        var repository = await BackupRepositoryAsync(policy, ct);
        if (repository.App.Id == sourceApp.Id || repository.Host.IpAddress == sourceHost.IpAddress ||
            repository.Host.ProviderAccountId == sourceHost.ProviderAccountId && repository.Host.ProviderServerId == sourceHost.ProviderServerId)
        { throw new InvalidOperationException("Repository must use an independent host."); }
        if (policy.EncryptedKeyMaterialJson is null)
        {
            var material = new BackupMaterial(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), ids.NewId("dbkey"));
            policy.KeyVersion = material.KeyVersion;
            policy.EncryptedKeyMaterialJson = encryption.Encrypt(JsonSerializer.Serialize(material, Json));
            await SaveOwnedDatabaseChangesAsync(operation, ct);
        }
        await ReconcileBackupPolicyAsync(operation, database, ct);
        policy.Status = policy.Enabled ? "configured" : "disabled";
        policy.NextDueAt = policy.Enabled ? clock.GetUtcNow() : null;
        policy.UpdatedAt = clock.GetUtcNow();
    }

    private async Task CaptureBackupAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        var policy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct);
        ValidateScheduledPolicy(operation, policy);
        if (!policy.Enabled) { throw new InvalidOperationException("Backup policy is disabled."); }
        // Reconcile destination admission even on the first backup after creating
        // a database with a default-enabled policy.
        await ConfigureBackupAsync(operation, database, sourceApp, sourceHost, ct);
        var repository = await BackupRepositoryAsync(policy, ct);
        var material = ReadBackupMaterial(policy);
        policy.LastAttemptAt = clock.GetUtcNow();
        policy.Status = "running";
        await SaveOwnedDatabaseChangesAsync(operation, ct);
        try
        {
            var environment = new Dictionary<string, string>
            {
                ["BACKUP_DATABASE_ID"] = database.Id, ["BACKUP_ENVIRONMENT"] = BackupNamespace(database.OrganizationId),
                ["BACKUP_URL"] = repository.Url, ["BACKUP_TOKEN"] = material.RepositoryToken,
                ["BACKUP_ENCRYPTION_KEY"] = material.EncryptionKey, ["BACKUP_KEY_ID"] = material.KeyVersion,
                ["BACKUP_ENGINE"] = database.Engine
            };
            if (database.Engine == "postgres")
            {
                var credentials = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(database.EncryptedCredentialsJson ?? throw new InvalidOperationException("Source custody is missing.")), Json)!;
                // Docker exec uses the source database's own identity; no global
                // roles or neighboring database contents enter the dump.
                environment["DATABASE_URL"] = "postgresql://" + Uri.EscapeDataString(credentials.Username) + ":" + Uri.EscapeDataString(credentials.Password) + "@127.0.0.1:5432/" + Uri.EscapeDataString(credentials.DatabaseName);
                environment["POSTGRES_CONTAINER"] = "minicloud-" + sourceApp.Slug + "-db-1";
            }
            else if (database.Engine == "sqlite")
            {
                environment["SQLITE_PATH"] = "/opt/minicloud/apps/" + sourceApp.Slug + "/data/sqlite/app.db";
            }
            else { throw new InvalidOperationException("Unsupported backup engine."); }
            var result = await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "dispatch.py", new { action = "backup", environment, operationId = operation.Id }, ct);
            var now = clock.GetUtcNow();
            var backupId = result.GetProperty("backupId").GetString() ?? throw new InvalidOperationException();
            if (!await db.DatabaseBackupArtifacts.AnyAsync(x => x.DatabaseId == database.Id && x.BackupId == backupId, ct))
            {
                db.DatabaseBackupArtifacts.Add(new DatabaseBackupArtifact
                {
                    Id = ids.NewId("dbbackup"), OrganizationId = database.OrganizationId, DatabaseId = database.Id,
                    RepositoryAppId = repository.App.Id, BackupId = backupId, KeyVersion = material.KeyVersion,
                    Status = "complete", SizeBytes = result.GetProperty("ciphertextBytes").GetInt64(), Sha256 = result.GetProperty("ciphertextSha256").GetString(),
                    CompletedAt = now, ExpiresAt = now.AddDays(policy.RetentionDays), CreatedAt = now, UpdatedAt = now
                });
            }
            policy.LastSuccessAt = now;
            policy.LastErrorCode = null;
            policy.Status = "healthy";
            policy.NextDueAt = now.AddHours(policy.FrequencyHours);
            operation.ResultJson = JsonSerializer.Serialize(new { backupId, status = "complete" }, Json);
        }
        catch
        {
            policy.Status = "failed";
            policy.LastErrorCode = "backup_capture_failed";
            var failedSinceSuccess = await db.DatabaseOperations.CountAsync(x => x.DatabaseId == database.Id && x.Kind == "backup" && x.Status == "failed" &&
                (policy.LastSuccessAt == null || x.CreatedAt > policy.LastSuccessAt), ct);
            policy.NextDueAt = clock.GetUtcNow().AddMinutes(Math.Min(15, Math.Pow(2, Math.Min(failedSinceSuccess, 4))));
            await SaveOwnedDatabaseChangesAsync(operation, ct);
            throw;
        }
    }

    internal async Task<bool> ReconcileBackupPolicyAsync(DatabaseOperation operation, ManagedDatabase database, CancellationToken ct)
    {
        var policy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct);
        await db.Entry(policy).ReloadAsync(ct);
        ValidateScheduledPolicy(operation, policy);
        if (policy.EncryptedKeyMaterialJson is null) { return true; }
        var material = ReadBackupMaterial(policy);
        var repositoryIds = await db.DatabaseBackupArtifacts.Where(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId && x.Status != "purged")
            .Select(x => x.RepositoryAppId).Distinct().ToListAsync(ct);
        if (policy.RepositoryAppId is not null && !repositoryIds.Contains(policy.RepositoryAppId)) { repositoryIds.Add(policy.RepositoryAppId); }
        foreach (var repositoryId in repositoryIds)
        {
            await db.Entry(policy).ReloadAsync(ct);
            ValidateScheduledPolicy(operation, policy);
            if (!await db.Apps.AnyAsync(x => x.Id == repositoryId && x.OrganizationId == database.OrganizationId && x.DeletionStatus == null, ct))
            {
                if (await db.DatabaseBackupArtifacts.AnyAsync(x => x.DatabaseId == database.Id && x.RepositoryAppId == repositoryId && x.Status == "complete", ct))
                    throw new InvalidOperationException("A retained backup repository is unavailable.");
                continue;
            }
            var repository = await BackupRepositoryAsync(new DatabaseBackupPolicy { OrganizationId = database.OrganizationId, RepositoryAppId = repositoryId }, ct);
            var active = policy.Enabled && database.Status != "deleted" && repositoryId == policy.RepositoryAppId;
            var artifacts = await db.DatabaseBackupArtifacts.Where(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId && x.RepositoryAppId == repositoryId && x.Status == "complete")
                .OrderBy(x => x.UpdatedAt).Take(100).ToListAsync(ct);
            var result = await ExecuteRemoteAsync(operation, repository.Host.IpAddress!, "repository-control.py", new
            {
                container = "minicloud-" + repository.App.Slug + "-repository-1",
                binding = new { environment = BackupNamespace(database.OrganizationId), databaseId = database.Id, token = material.RepositoryToken,
                    retentionDays = policy.RetentionDays, minimumComplete = policy.MinimumCopies, enabled = active, keepLastCopies = policy.KeepLastCopies, policyRevision = policy.Revision },
                backupIds = artifacts.Select(x => x.BackupId).ToArray(), operationId = operation.Id
            }, ct);
            var present = result.GetProperty("presentBackupIds").EnumerateArray().Select(x => x.GetString()).ToHashSet(StringComparer.Ordinal);
            foreach (var artifact in artifacts)
            {
                if (!present.Contains(artifact.BackupId))
                {
                    artifact.Status = artifact.ExpiresAt <= clock.GetUtcNow() ? "expired" : "missing";
                }
                artifact.UpdatedAt = clock.GetUtcNow();
            }
        }
        if (!policy.Enabled || database.Status == "deleted") { policy.NextDueAt = null; policy.Status = "disabled"; }
        policy.UpdatedAt = clock.GetUtcNow();
        return true;
    }

    private static void ValidateScheduledPolicy(DatabaseOperation operation, DatabaseBackupPolicy policy)
    {
        if (operation.BackupPolicyId is not null && (operation.BackupPolicyId != policy.Id ||
            operation.OrganizationId != policy.OrganizationId || operation.DatabaseId != policy.DatabaseId ||
            operation.ScheduledPolicyRevision != policy.Revision)) { throw new UnauthorizedAccessException("Scheduled backup policy changed."); }
    }

    public async Task ScheduleBackupsAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var candidates = await db.DatabaseBackupPolicies.Where(x => (x.Enabled && x.NextDueAt != null && x.NextDueAt <= now) ||
                (x.EncryptedKeyMaterialJson != null && x.UpdatedAt <= now.AddMinutes(-15)))
            .OrderBy(x => x.UpdatedAt).Take(50).AsNoTracking().ToListAsync(ct);
        foreach (var policy in candidates)
        {
            var database = await db.ManagedDatabases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == policy.DatabaseId && x.OrganizationId == policy.OrganizationId, ct);
            if (database is null || await db.DatabaseOperations.AnyAsync(x => x.DatabaseId == database.Id && (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct)) { continue; }
            var backup = policy.Enabled && database.Status == "ready" && policy.NextDueAt <= now;
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var claim = db.DatabaseBackupPolicies.Where(x => x.Id == policy.Id && x.Revision == policy.Revision && x.UpdatedAt == policy.UpdatedAt);
                var count = backup
                    ? await claim.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.NextDueAt, now.AddMinutes(15)), ct)
                    : await claim.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UpdatedAt, now), ct);
                if (count != 0)
                {
                    db.DatabaseOperations.Add(new DatabaseOperation { Id = ids.NewId("dbop"), OrganizationId = database.OrganizationId,
                        DatabaseId = database.Id, ServerId = database.ServerId, Kind = backup ? "backup" : "reconcile-policy",
                        BackupPolicyId = policy.Id, ScheduledPolicyRevision = policy.Revision, ActorJson = "{}", RequestJson = "{}", CreatedAt = now, UpdatedAt = now });
                    await db.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
            });
        }
    }
    private async Task<bool> RestoreBackupAsync(DatabaseOperation operation, ManagedDatabase database, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<DatabaseOperationRequest>(operation.RequestJson, Json) ?? throw new InvalidOperationException();
        var artifact = await db.DatabaseBackupArtifacts.SingleAsync(x => x.Id == request.BackupArtifactId && x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId && x.Status == "complete", ct);
        var policy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId, ct);
        var material = ReadBackupMaterial(policy);
        if (material.KeyVersion != artifact.KeyVersion) { throw new InvalidOperationException("Recovery key version unavailable."); }
        var repositoryPolicy = new DatabaseBackupPolicy { OrganizationId = policy.OrganizationId, RepositoryAppId = artifact.RepositoryAppId };
        var repository = await BackupRepositoryAsync(repositoryPolicy, ct);
        if (database.Engine == "sqlite")
        {
            var source = await BackupSourceAsync(database, ct);
            var fileName = operation.Id + ".db";
            var target = "/opt/minicloud/apps/" + source.App.Slug + "/data/sqlite/recovery-" + fileName;
            var result = await ExecuteRemoteAsync(operation, source.Host.IpAddress!, "dispatch.py", new
            {
                action = "restore", repositoryUrl = repository.Url, @namespace = BackupNamespace(database.OrganizationId),
                sourceDatabaseId = database.Id, backupId = artifact.BackupId, repositoryToken = material.RepositoryToken,
                encryptionKey = material.EncryptionKey, target, operationId = operation.Id
            }, ct);
            operation.ResultJson = JsonSerializer.Serialize(new { status = "restored", backupId = artifact.BackupId, restoredFileName = "recovery-" + fileName }, Json);
            return true;
        }
        if (database.Engine != "postgres" || string.IsNullOrWhiteSpace(request.TargetName)) { throw new InvalidOperationException("Unsupported restore destination."); }
        ManagedDatabaseServer? targetServer = null;
        MinicloudApp targetApp;
        ServerInstance targetHost;
        if (request.TargetServerId is not null)
        {
            targetServer = await db.ManagedDatabaseServers.SingleAsync(x => x.Id == request.TargetServerId && x.OrganizationId == database.OrganizationId && x.Status == "ready" && x.Engine == "postgres", ct);
            targetApp = await db.Apps.SingleAsync(x => x.Id == targetServer.RuntimeAppId && x.OrganizationId == database.OrganizationId && x.DeletionStatus == null, ct);
            targetHost = await db.ServerInstances.SingleAsync(x => x.AppId == targetApp.Id && x.OrganizationId == database.OrganizationId, ct);
        }
        else
        {
            if (database.Placement == "dedicated") { throw new InvalidOperationException("Dedicated recovery requires a separate empty dedicated server."); }
            var source = await BackupSourceAsync(database, ct);
            targetApp = source.App;
            targetHost = source.Host;
            if (database.ServerId is not null)
            {
                targetServer = await db.ManagedDatabaseServers.SingleAsync(x => x.Id == database.ServerId && x.OrganizationId == database.OrganizationId && x.Status == "ready", ct);
            }
        }
        ManagedDatabase? targetDatabase = null;
        if (operation.ResultJson is not null)
        {
            using var retained = JsonDocument.Parse(operation.ResultJson);
            if (retained.RootElement.TryGetProperty("targetDatabaseId", out var targetId))
            {
                targetDatabase = await db.ManagedDatabases.SingleAsync(x => x.Id == targetId.GetString() && x.OrganizationId == database.OrganizationId, ct);
            }
        }
        if (targetDatabase is null)
        {
            if (targetServer?.Mode == "dedicated" && await db.ManagedDatabases.AnyAsync(x => x.ServerId == targetServer.Id && x.Status != "deleted", ct))
            { throw new InvalidOperationException("Dedicated target must be empty."); }
            var targetId = ids.NewId("database");
            var suffix = targetId["database_".Length..];
            var credentials = new DatabaseConnectionCredentials("db_" + suffix, "user_" + suffix, Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)));
            targetDatabase = new ManagedDatabase { Id = targetId, OrganizationId = database.OrganizationId, Name = request.TargetName,
                Engine = "postgres", EngineVersion = "16", ServerId = targetServer?.Id,
                RecoverySourceAppId = targetServer is null ? targetApp.Id : null,
                Placement = targetServer?.Mode ?? "application", AccessMode = "internal", Status = "restoring",
                EncryptedCredentialsJson = encryption.Encrypt(JsonSerializer.Serialize(credentials, Json)), CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            db.ManagedDatabases.Add(targetDatabase);
            db.DatabaseBackupPolicies.Add(new DatabaseBackupPolicy { Id = ids.NewId("dbpolicy"), OrganizationId = database.OrganizationId,
                DatabaseId = targetId, Enabled = false, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
            operation.ResultJson = JsonSerializer.Serialize(new { targetDatabaseId = targetId, status = "restoring" }, Json);
            if (targetServer is not null) { targetServer.Revision++; }
            await SaveOwnedDatabaseChangesAsync(operation, ct);
        }
        var targetCredentials = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(targetDatabase.EncryptedCredentialsJson!), Json)!;
        await ExecuteRemoteAsync(operation, targetHost.IpAddress!, "operation.py", new { action = "create", container = "minicloud-" + targetApp.Slug + "-db-1", databaseName = targetCredentials.DatabaseName,
            username = targetCredentials.Username, password = targetCredentials.Password, adminUser = "minicloud", operationId = operation.Id }, ct);
        await ExecuteRemoteAsync(operation, targetHost.IpAddress!, "dispatch.py", new
        {
            action = "restore", repositoryUrl = repository.Url, @namespace = BackupNamespace(database.OrganizationId), sourceDatabaseId = database.Id,
            backupId = artifact.BackupId, repositoryToken = material.RepositoryToken, encryptionKey = material.EncryptionKey,
            target = targetCredentials.DatabaseName, username = targetCredentials.Username, adminUser = "minicloud",
            container = "minicloud-" + targetApp.Slug + "-db-1", operationId = operation.Id
        }, ct);
        targetDatabase.Status = "ready";
        await ReconcileDatabaseAccessAsync(operation, targetDatabase, targetApp, targetHost, "internal", [], ct);
        targetDatabase.UpdatedAt = clock.GetUtcNow();
        operation.ResultJson = JsonSerializer.Serialize(new { targetDatabaseId = targetDatabase.Id, backupId = artifact.BackupId, status = "restored" }, Json);
        return true;
    }

}
