using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;
using Minicloud.Common.Services;
using Minicloud.Worker.Ssh;

namespace Minicloud.Worker.Databases;

public sealed partial class DatabaseOperationProcessor(
    MinicloudDbContext db, ControlPlaneAuthorization authorization,
    ProviderCatalogService catalog, DeploymentLifecycleService lifecycle,
    IAppSecretService appSecrets, SecretEncryptionService encryption,
    IIdGenerator ids, TimeProvider clock, ISshDeploymentClient ssh,
    IOptions<SshDeploymentOptions> sshOptions, IOptions<DatabasePlatformOptions> databaseOptions,
    DatabaseNetworkService privateNetworks)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private Task<bool> DispatchOperationAsync(DatabaseOperation operation, CancellationToken ct) => operation.Kind switch
    {
        "create-server" => CreateServerAsync(operation, ct),
        "create-repository" => CreateRepositoryAsync(operation, ct),
        "delete-server" => DeleteServerAsync(operation, ct),
        "create-network" => ReconcileNetworkAsync(operation, ct),
        "delete-network" => DeleteNetworkAsync(operation, ct),
        _ => ProcessDatabaseAsync(operation, ct)
    };

    private async Task<bool> ReconcileNetworkAsync(DatabaseOperation operation, CancellationToken ct)
    {
        using var request = JsonDocument.Parse(operation.RequestJson);
        return await privateNetworks.ReconcileAsync(request.RootElement.GetProperty("networkId").GetString()!, operation.OrganizationId, ct);
    }

    private async Task<bool> DeleteNetworkAsync(DatabaseOperation operation, CancellationToken ct)
    {
        using var request = JsonDocument.Parse(operation.RequestJson);
        return await privateNetworks.DeleteAsync(request.RootElement.GetProperty("networkId").GetString()!, operation.OrganizationId, ct);
    }

    private async Task<bool> CreateRepositoryAsync(DatabaseOperation operation, CancellationToken ct)
    {
        using var request = JsonDocument.Parse(operation.RequestJson);
        var appId = request.RootElement.GetProperty("appId").GetString();
        var size = request.RootElement.GetProperty("sizeGb").GetInt32();
        var app = await db.Apps.SingleAsync(x => x.Id == appId && x.OrganizationId == operation.OrganizationId && x.DeletionStatus == null, ct);
        var deployment = await db.Deployments.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(x => x.AppId == app.Id, ct);
        if (deployment is null)
        {
            var image = databaseOptions.Value.RepositoryImage;
            if (!image.StartsWith("ghcr.io/muniventures/minicloud-backup-repository:", StringComparison.Ordinal) || image.Contains("latest", StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("A versioned repository image is required."); }
            var service = new DeploymentServiceRequest("repository", image, 8080, true, "/", "/health/ready",
                new Dictionary<string, string> { ["BACKUP_ROOT"] = "/data/backups", ["BACKUP_REPOSITORY_ID"] = app.Id,
                    ["BACKUP_BINDINGS_JSON"] = "[]", ["BACKUP_TRUSTED_PROXIES"] = "172.16.0.0/12" },
                Storage: new Dictionary<string, StorageMountRequest> { ["backup-data"] = new(size, "/data/backups", 1000, 1000) });
            deployment = lifecycle.CreateQueued(app, new CreateDeploymentRequest(app.Id, "none", null, [service]));
            await catalog.ReserveAsync(app, deployment, ct);
            db.Deployments.Add(deployment);
            await SaveOwnedDatabaseChangesAsync(operation, ct);
            return false;
        }
        if (deployment.Status is DeploymentStatus.Failed or DeploymentStatus.Canceled) { throw new InvalidOperationException("Repository provisioning failed."); }
        return deployment.Status == DeploymentStatus.Succeeded;
    }

    private async Task<bool> DeleteServerAsync(DatabaseOperation operation, CancellationToken ct)
    {
        var server = await db.ManagedDatabaseServers.SingleAsync(x => x.Id == operation.ServerId && x.OrganizationId == operation.OrganizationId, ct);
        if (await db.ManagedDatabases.AnyAsync(x => x.ServerId == server.Id && x.Status != "deleted", ct)) { throw new InvalidOperationException("Server still contains databases."); }
        if (server.RuntimeAppId is not null)
        {
            var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == server.RuntimeAppId && x.OrganizationId == operation.OrganizationId, ct);
            if (app is not null)
            {
                server.Revision++;
                server.UpdatedAt = clock.GetUtcNow();
                server.Status = "deleting";
                app.DeletionStatus = "queued";
                app.UpdatedAt = clock.GetUtcNow();
                await SaveOwnedDatabaseChangesAsync(operation, ct);
                return false;
            }
        }
        server.Revision++;
        server.Status = "deleted";
        server.UpdatedAt = clock.GetUtcNow();
        return true;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        DatabaseOperation? operation = null;
        var interrupted = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var now = clock.GetUtcNow();
            var abandoned = db.Database.IsNpgsql()
                ? await db.DatabaseOperations.FromSqlInterpolated($"SELECT * FROM database_operations WHERE status IN ('running','cancelling') AND (lease_expires_at IS NULL OR lease_expires_at <= {now}) ORDER BY updated_at FOR UPDATE SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(ct)
                : (await db.DatabaseOperations.Where(x => x.Status == "running" || x.Status == "cancelling").ToListAsync(ct)).Where(x => x.LeaseExpiresAt == null || x.LeaseExpiresAt <= now).OrderBy(x => x.UpdatedAt).FirstOrDefault();
            if (abandoned is not null)
            {
                // Never replay a remotely mutating operation after losing its owner.
                abandoned.Status = "failed";
                abandoned.ErrorCode = abandoned.Kind == "migrate" ? "database_migration_reconciliation_required" : "database_operation_interrupted_reconciliation_required";
                abandoned.ErrorMessage = "The worker lease expired. Inspect source, target and retained resources before an explicit retry; no automatic mutation replay was performed.";
                abandoned.CompletedAt = now;
                abandoned.UpdatedAt = now;
                abandoned.OwnershipToken = null;
                abandoned.LeaseExpiresAt = null;
                await db.SaveChangesAsync(ct);
                interrupted = true;
            }
            var query = db.DatabaseOperations.Where(x => x.Status == "queued");
            operation = db.Database.IsNpgsql()
                ? await db.DatabaseOperations.FromSqlRaw("SELECT * FROM database_operations WHERE status = 'queued' ORDER BY updated_at, created_at FOR UPDATE SKIP LOCKED LIMIT 1").FirstOrDefaultAsync(ct)
                : (await query.ToListAsync(ct)).OrderBy(x => x.UpdatedAt).ThenBy(x => x.CreatedAt).FirstOrDefault();
            if (operation is not null)
            {
                operation.Status = "running";
                operation.OwnershipToken = Guid.NewGuid().ToString("N");
                operation.LeaseExpiresAt = clock.GetUtcNow().AddMinutes(35);
                operation.AttemptCount++;
                operation.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
        });
        if (operation is null) { return interrupted; }
        DatabaseOperationLease? hostLease = null;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            if (db.Database.IsNpgsql())
            {
                var resources = new List<string> { "operation:" + operation.Id };
                if (operation.ServerId is not null) { resources.Add("server:" + operation.ServerId); }
                if (operation.DatabaseId is not null)
                {
                    resources.Add("database:" + operation.DatabaseId);
                    var source = await db.ManagedDatabases.AsNoTracking().SingleAsync(x => x.Id == operation.DatabaseId, ct);
                    if (source.NetworkId is not null) { resources.Add("network:" + source.NetworkId); }
                    if ((source.AppId ?? source.RecoverySourceAppId) is { } sourceApp) { resources.Add("app:" + sourceApp); }
                    var repository = await db.DatabaseBackupPolicies.Where(x => x.DatabaseId == source.Id).Select(x => x.RepositoryAppId).SingleOrDefaultAsync(ct);
                    if (repository is not null) { resources.Add("app:" + repository); }
                }
                using (var request = JsonDocument.Parse(operation.RequestJson))
                {
                    foreach (var property in request.RootElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.String) { continue; }
                        if (property.Name.Equals("TargetServerId", StringComparison.OrdinalIgnoreCase) && property.Value.GetString() is { } targetServer)
                            resources.Add("server:" + targetServer);
                        if (property.Name.Equals("AppId", StringComparison.OrdinalIgnoreCase) && property.Value.GetString() is { } targetApp)
                            resources.Add("app:" + targetApp);
                        if (property.Name.Equals("NetworkId", StringComparison.OrdinalIgnoreCase) && property.Value.GetString() is { } networkId)
                            resources.Add("network:" + networkId);
                    }
                }
                hostLease = await DatabaseOperationLease.AcquireAsync(db.Database.GetConnectionString()!, resources, operation.Id, operation.OwnershipToken!, ct);
                if (hostLease is null)
                {
                    var releasedOwner = operation.OwnershipToken;
                    operation.Status = "queued";
                    operation.OwnershipToken = null;
                    operation.LeaseExpiresAt = null;
                    await SaveOwnedDatabaseChangesAsync(operation, ct, releasedOwner);
                    return true;
                }
            }
            using var leaseRegistration = hostLease?.Lost.Register(operationCancellation.Cancel);
            await ValidateDatabaseOperationAuthorityAsync(operation, operationCancellation.Token);
            var finished = await DispatchOperationAsync(operation, operationCancellation.Token);
            operationCancellation.Token.ThrowIfCancellationRequested();
            operation.Status = finished ? "completed" : "queued";
            operation.CompletedAt = finished ? clock.GetUtcNow() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { if (hostLease is not null) { await hostLease.DisposeAsync(); } throw; }
        catch (Exception error)
        {
            var failedOwner = operation.OwnershipToken;
            db.ChangeTracker.Clear();
            var retained = await db.DatabaseOperations.SingleOrDefaultAsync(x => x.Id == operation.Id, ct);
            if (retained is null || retained.OwnershipToken != failedOwner || retained.LeaseExpiresAt is null || retained.LeaseExpiresAt <= clock.GetUtcNow() || retained.Status is not ("running" or "cancelling"))
            {
                if (hostLease is not null) await hostLease.DisposeAsync();
                return true;
            }
            operation = retained;
            var cancelled = error is not RemoteQuiescenceException && await db.DatabaseOperations.AsNoTracking().AnyAsync(x => x.Id == operation.Id && x.Status == "cancelling" && x.OwnershipToken == operation.OwnershipToken, ct);
            operation.Status = cancelled ? "cancelled" : "failed";
            operation.ErrorCode = operation.Kind == "migrate" ? "database_migration_reconciliation_required" : error is DatabaseRemoteOperationException remote ? remote.Code : error is UnauthorizedAccessException ? "database_authority_changed" : "database_operation_failed";
            operation.ErrorMessage = operation.Kind == "migrate" ? "Migration requires explicit reconciliation. The source may remain fenced; inspect source and target before reopening writers." : "Database operation failed; source data was not replaced. Inspect its current state before retrying.";
            operation.CompletedAt = clock.GetUtcNow();
            if (cancelled)
            {
                operation.ErrorCode = "database_cancelled";
                operation.ErrorMessage = "Backup capture was cancelled. Existing completed backups are retained.";
            }
        }
        if (hostLease is not null) { await hostLease.DisposeAsync(); }
        var completedOwner = operation.OwnershipToken;
        operation.OwnershipToken = null;
        operation.LeaseExpiresAt = null;
        operation.UpdatedAt = clock.GetUtcNow();
        await SaveOwnedDatabaseChangesAsync(operation, ct, completedOwner);
        if (operation.Status == "queued") { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        return true;
    }

    private async Task<bool> CreateServerAsync(DatabaseOperation operation, CancellationToken ct)
    {
        var server = await db.ManagedDatabaseServers.SingleAsync(x => x.Id == operation.ServerId && x.OrganizationId == operation.OrganizationId, ct);
        if (server.RuntimeAppId is null)
        {
            var plan = await db.ProviderPlans.SingleAsync(x => x.Id == server.PlanId && x.OrganizationId == operation.OrganizationId && x.Enabled, ct);
            var slug = "db-" + server.Id["dbserver_".Length..];
            var app = new MinicloudApp { Id = ids.NewId("app"), OrganizationId = operation.OrganizationId, Name = server.Name, Slug = slug, PlanId = plan.Id, Database = DatabaseMode.Postgres, EnvironmentName = "production", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            app.EnvironmentKey = app.Id + ":production";
            db.Apps.Add(app);
            var request = new CreateDeploymentRequest(app.Id, "postgres", null, []);
            var deployment = lifecycle.CreateQueued(app, request);
            await catalog.ReserveAsync(app, deployment, ct);
            // New backing apps, their password custody and deployment admission commit together.
            // EnsurePostgresPasswordAsync uses immediate SQL and cannot precede the new app FK.
            db.AppSecrets.Add(new AppSecret
            {
                Id = ids.NewId("sec"), OrganizationId = app.OrganizationId, AppId = app.Id,
                Name = AppSecretService.PostgresPasswordSecretName,
                EncryptedValue = encryption.Encrypt(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()),
                CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow()
            });
            db.Deployments.Add(deployment);
            server.Revision++;
            server.UpdatedAt = clock.GetUtcNow();
            server.RuntimeAppId = app.Id;
            server.RuntimeDeploymentId = deployment.Id;
            await SaveOwnedDatabaseChangesAsync(operation, ct);
            return false;
        }
        var release = await db.Deployments.SingleAsync(x => x.Id == server.RuntimeDeploymentId, ct);
        if (release.Status == DeploymentStatus.Failed || release.Status == DeploymentStatus.Canceled)
        { server.Revision++; server.Status = "failed"; throw new InvalidOperationException(); }
        if (release.Status != DeploymentStatus.Succeeded) { return false; }
        var runtime = await db.RuntimeServers.SingleAsync(x => x.AppId == server.RuntimeAppId && x.OrganizationId == server.OrganizationId, ct);
        server.Revision++;
        server.RuntimeServerId = runtime.Id;
        server.Status = "ready";
        server.UpdatedAt = clock.GetUtcNow();
        return true;
    }

    private async Task<bool> ProcessDatabaseAsync(DatabaseOperation operation, CancellationToken ct)
    {
        var database = await db.ManagedDatabases.SingleAsync(x => x.Id == operation.DatabaseId && x.OrganizationId == operation.OrganizationId, ct);
        if (operation.Kind == "purge-backups") { return await PurgeBackupsAsync(operation, database, ct); }
        if (operation.Kind == "restore") { return await RestoreBackupAsync(operation, database, ct); }
        if (operation.Kind == "reconcile-policy") { return await ReconcileBackupPolicyAsync(operation, database, ct); }
        if (operation.Kind == "configure-backup" && !await db.DatabaseBackupPolicies.AnyAsync(x => x.DatabaseId == database.Id && x.Enabled, ct))
        { return await ReconcileBackupPolicyAsync(operation, database, ct); }
        var appId = database.AppId ?? database.RecoverySourceAppId;
        if (appId is null)
        {
            var server = await db.ManagedDatabaseServers.SingleAsync(x => x.Id == database.ServerId && x.OrganizationId == database.OrganizationId && x.Status == "ready", ct);
            appId = server.RuntimeAppId;
        }
        var app = await db.Apps.SingleAsync(x => x.Id == appId && x.OrganizationId == database.OrganizationId && x.DeletionStatus == null, ct);
        var host = await db.ServerInstances.SingleAsync(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId, ct);
        if (string.IsNullOrWhiteSpace(host.IpAddress) || string.IsNullOrWhiteSpace(host.ProviderServerId)) { throw new InvalidOperationException(); }
        if (operation.Kind == "configure-backup") { await ConfigureBackupAsync(operation, database, app, host, ct); return true; }
        if (operation.Kind == "backup") { await CaptureBackupAsync(operation, database, app, host, ct); return true; }
        if (operation.Kind is "bind" or "unbind") { return await ProcessBindingAsync(operation, database, app, host, ct); }
        if (operation.Kind == "access") { return await ApplyDatabaseAccessAsync(operation, database, app, host, ct); }
        if (operation.Kind == "migrate") { return await MigrateDatabaseAsync(operation, database, app, host, ct); }
        if (operation.Kind == "enroll")
        {
            await ExecuteRemoteAsync(operation, host.IpAddress, "dispatch.py", new { action = "inspect", engine = database.Engine, sqlitePath = "/opt/minicloud/apps/" + app.Slug + "/data/sqlite/app.db", container = "minicloud-" + app.Slug + "-db-1", databaseName = app.Slug.Replace('-', '_'), operationId = operation.Id }, ct);
            if (app.Database == DatabaseMode.Postgres)
            {
                var secret = await db.AppSecrets.SingleOrDefaultAsync(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId && x.Name == AppSecretService.PostgresPasswordSecretName, ct)
                    ?? throw new InvalidOperationException("Existing database credential custody is unavailable.");
                var credentials = new DatabaseConnectionCredentials(app.Slug.Replace('-', '_'), "minicloud", encryption.Decrypt(secret.EncryptedValue));
                database.EncryptedCredentialsJson = encryption.Encrypt(JsonSerializer.Serialize(credentials, Json));
            }
            database.Status = "ready";
            if (database.Engine == "postgres")
            {
                await SaveOwnedDatabaseChangesAsync(operation, ct);
                await ReconcileDatabaseAccessAsync(operation, database, app, host, database.AccessMode,
                    JsonSerializer.Deserialize<string[]>(database.AllowedCidrsJson, Json) ?? [], ct);
            }
            if (await db.DatabaseBackupPolicies.AnyAsync(x => x.DatabaseId == database.Id && x.Enabled, ct))
            { await ConfigureBackupAsync(operation, database, app, host, ct); }
            return true;
        }
        if (operation.Kind == "rotate-credentials") { return await RotateCredentialsAsync(operation, database, app, host, ct); }
        if (operation.Kind is "create-database" or "delete")
        {
            if (database.AppId is not null) { throw new InvalidOperationException("Application lifecycle is managed separately."); }
            if (operation.Kind == "delete" && await db.ManagedDatabaseBindings.AnyAsync(x => x.DatabaseId == database.Id && x.OrganizationId == database.OrganizationId && x.Status != "revoked", ct))
                throw new InvalidOperationException("Unbind application services before deleting this database.");
            var credentials = database.EncryptedCredentialsJson is null
                ? new DatabaseConnectionCredentials("db_" + database.Id["database_".Length..], "user_" + database.Id["database_".Length..], Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)))
                : JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(database.EncryptedCredentialsJson), Json)!;
            database.EncryptedCredentialsJson = encryption.Encrypt(JsonSerializer.Serialize(credentials, Json));
            await SaveOwnedDatabaseChangesAsync(operation, ct);
            await ExecuteRemoteAsync(operation, host.IpAddress, "operation.py", new { action = operation.Kind == "create-database" ? "create" : "delete", databaseName = credentials.DatabaseName, username = credentials.Username, password = credentials.Password, container = "minicloud-" + app.Slug + "-db-1", adminUser = "minicloud", operationId = operation.Id }, ct);
            database.Status = operation.Kind == "delete" ? "deleted" : "ready";
            await ReconcileDatabaseAccessAsync(operation, database, app, host, database.AccessMode, JsonSerializer.Deserialize<string[]>(database.AllowedCidrsJson, Json) ?? [], ct);
            if (operation.Kind == "delete")
            {
                var retainedPolicy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == database.Id, ct);
                retainedPolicy.Enabled = false;
                retainedPolicy.NextDueAt = null;
                retainedPolicy.Status = "source-deleted";
                retainedPolicy.UpdatedAt = clock.GetUtcNow();
            }
            database.UpdatedAt = clock.GetUtcNow();
            if (operation.Kind == "create-database" && await db.DatabaseBackupPolicies.AnyAsync(x => x.DatabaseId == database.Id && x.Enabled, ct))
            { await ConfigureBackupAsync(operation, database, app, host, ct); }
            return true;
        }
        throw new InvalidOperationException("Operation handler unavailable.");
    }

    private async Task<JsonElement> ExecuteRemoteAsync(DatabaseOperation operation, string host, string scriptName, object payload, CancellationToken ct)
    {
        await ValidateDatabaseOperationAuthorityAsync(operation, ct);
        var options = sshOptions.Value;
        var key = !string.IsNullOrWhiteSpace(options.PrivateKeyContent) ? options.PrivateKeyContent : await File.ReadAllTextAsync(options.PrivateKeyPath, ct);
        var directory = Path.Combine(AppContext.BaseDirectory, "scripts", "database-platform");
        var wrapper = new StringBuilder("#!/bin/bash\nset -euo pipefail\numask 077\nsudo -n install -d -m 755 -o minicloud -g minicloud /opt/minicloud/locks\nsudo -n install -d -m 700 -o minicloud -g minicloud /opt/minicloud/locks/db-backups\nexec 7>/opt/minicloud/locks/docker.deploy.lock\nflock -x 7\ncd -- \"$(dirname -- \"$1\")\"\n");
        foreach (var file in Directory.GetFiles(directory, "*.py").Where(x => !Path.GetFileName(x).StartsWith("test", StringComparison.Ordinal)))
        {
            var content = Convert.ToBase64String(await File.ReadAllBytesAsync(file, ct));
            wrapper.Append("printf '%s' '").Append(content).Append("' | base64 -d > '").Append(Path.GetFileName(file)).Append("'\n");
        }
        wrapper.Append("python3 lease-watchdog.py \"$1\" \"$$\" --once\npython3 lease-watchdog.py \"$1\" \"$$\" & watchdog=$!\ntrap 'kill \"$watchdog\" 2>/dev/null || true' EXIT\n");
        wrapper.Append("python3 '").Append(scriptName).Append("' \"$1\"\n");
        var script = wrapper.ToString();
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(payload, Json))!.AsObject();
        envelope["ownership_token"] = operation.OwnershipToken;
        var response = await ssh.ExecuteDeploymentAsync(new SshDeploymentRequest(host, options.Username, key, "/opt/minicloud/database-operations/" + operation.Id, envelope.ToJsonString(), script, [], TimeSpan.FromMinutes(30)), (_, _) => Task.CompletedTask, ct);
        const string marker = "__MINICLOUD_DATABASE_RESULT__=";
        var result = response.OutputLines.LastOrDefault(x => x.StartsWith(marker, StringComparison.Ordinal))
            ?? throw new DatabaseRemoteOperationException("database_runtime_interrupted");
        if (result.Length > 65536) { throw new InvalidOperationException("Database result exceeds its limit."); }
        using var document = JsonDocument.Parse(result[marker.Length..]);
        if (response.ExitCode != 0)
        {
            var category = document.RootElement.TryGetProperty("errorType", out var type) ? type.GetString() : null;
            var code = category switch
            {
                "BackupCapacityError" => "database_capacity_unavailable",
                "AccessRollbackUnverifiedError" => "database_access_reconciliation_required",
                "AccessChangeRolledBackError" => "database_access_change_rolled_back",
                "TimeoutExpired" or "TimeoutError" => "database_runtime_timeout",
                "CalledProcessError" => "database_runtime_command_failed",
                "UnauthorizedAccessException" => "database_authority_changed",
                _ => "database_runtime_failed"
            };
            throw new DatabaseRemoteOperationException(code);
        }
        if (!response.CompletionMarkerFound || response.CompletionSha != operation.Id)
        { throw new DatabaseRemoteOperationException("database_runtime_confirmation_missing"); }
        return document.RootElement.Clone();
    }

}
