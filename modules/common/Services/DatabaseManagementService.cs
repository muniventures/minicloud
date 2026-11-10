using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class DatabaseManagementService(MinicloudDbContext db, ControlPlaneAuthorization authorization, ProviderCatalogService catalog, IIdGenerator ids, TimeProvider clock)
{
    public async Task RequireAsync(ActorIdentity actor, string organizationId, bool write, CancellationToken ct)
    {
        if (!await authorization.CanAccessOrganizationAsync(actor, organizationId, write ? OrganizationAction.ManageDatabases : OrganizationAction.ReadDatabases, ct))
            throw new UnauthorizedAccessException();
    }

    public async Task<ManagedDatabaseServer> CreateServerAsync(CreateDatabaseServerRequest request, ActorIdentity actor, CancellationToken ct)
    {
        await RequireAsync(actor, request.OrganizationId, true, ct);
        Name(request.Name);
        if (request.Mode is not ("shared" or "dedicated") || request.Engine != "postgres" || request.EngineVersion != "16")
            throw new ArgumentException("Unsupported database server mode, engine or version.");
        var plan = await catalog.ResolveAsync(request.OrganizationId, request.Plan, ct);
        var server = new ManagedDatabaseServer { Id = ids.NewId("dbserver"), OrganizationId = request.OrganizationId, Name = request.Name, Mode = request.Mode, PlanId = plan.Id, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        db.ManagedDatabaseServers.Add(server);
        Queue(server.OrganizationId, null, server.Id, "create-server", request, actor);
        await db.SaveChangesAsync(ct);
        return server;
    }

    public async Task<ManagedDatabase> CreateDatabaseAsync(CreateManagedDatabaseRequest request, ActorIdentity actor, CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var database = await CreateDatabaseCoreAsync(request, actor, ct);
            await transaction.CommitAsync(ct);
            return database;
        });
    }

    private async Task<ManagedDatabase> CreateDatabaseCoreAsync(CreateManagedDatabaseRequest request, ActorIdentity actor, CancellationToken ct)
    {
        await RequireAsync(actor, request.OrganizationId, true, ct);
        Name(request.Name);
        if ((request.AppId is null) == (request.ServerId is null)) throw new ArgumentException("Select exactly one application or database server.");
        var database = new ManagedDatabase { Id = ids.NewId("database"), OrganizationId = request.OrganizationId, Name = request.Name, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        if (request.AppId is not null)
        {
            var app = await db.Apps.SingleOrDefaultAsync(x => x.Id == request.AppId && x.OrganizationId == request.OrganizationId && x.DeletionStatus == null, ct)
                ?? throw new ArgumentException("Application is unavailable.");
            if (app.Database == DatabaseMode.None) throw new ArgumentException("Application has no managed database.");
            database.AppId = app.Id;
            database.Engine = app.Database == DatabaseMode.Sqlite ? "sqlite" : "postgres";
            database.EngineVersion = database.Engine == "sqlite" ? "3" : "16";
            database.Status = "enrolling";
        }
        else
        {
            var server = await db.ManagedDatabaseServers.SingleOrDefaultAsync(x => x.Id == request.ServerId && x.OrganizationId == request.OrganizationId, ct)
                ?? throw new ArgumentException("Database server is unavailable.");
            if (server.Status != "ready") throw new InvalidOperationException("Database server is not ready.");
            // A concurrency-token update serializes competing dedicated-slot reservations.
            if (server.Mode == "dedicated" && await db.ManagedDatabases.AnyAsync(x => x.ServerId == server.Id && x.Status != "deleted", ct))
                throw new InvalidOperationException("Dedicated server already contains a database.");
            server.Revision++;
            database.ServerId = server.Id;
            database.Placement = server.Mode;
        }
        db.ManagedDatabases.Add(database);
        var policy = new DatabaseBackupPolicy { Id = ids.NewId("dbpolicy"), OrganizationId = database.OrganizationId, DatabaseId = database.Id, CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        if (request.Backup is not null) await ApplyPolicyAsync(policy, request.Backup, ct);
        db.DatabaseBackupPolicies.Add(policy);
        Queue(database.OrganizationId, database.Id, database.ServerId, database.AppId is null ? "create-database" : "enroll", request, actor);
        await db.SaveChangesAsync(ct);
        return database;
    }

    public async Task<DatabaseBackupPolicy> SetPolicyAsync(string id, BackupPolicyRequest request, ActorIdentity actor, CancellationToken ct)
    {
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var policy = await SetPolicyCoreAsync(id, request, actor, ct);
            await transaction.CommitAsync(ct);
            return policy;
        });
    }

    private async Task<DatabaseBackupPolicy> SetPolicyCoreAsync(string id, BackupPolicyRequest request, ActorIdentity actor, CancellationToken ct)
    {
        var database = await GetAsync(id, actor, true, ct);
        if (await db.DatabaseOperations.AnyAsync(x => x.DatabaseId == id && x.BackupPolicyId == null && (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct))
            throw new InvalidOperationException("Wait for active database operations before changing backup policy.");
        var policy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == id && x.OrganizationId == database.OrganizationId, ct);
        if (database.Status == "deleted" && (request.Enabled || request.RepositoryAppId is not null && request.RepositoryAppId != policy.RepositoryAppId))
            throw new InvalidOperationException("Deleted sources only support disabled retention updates without changing the retained repository.");
        database.Revision++;
        await ApplyPolicyAsync(policy, request, ct);
        policy.Revision++;
        policy.UpdatedAt = clock.GetUtcNow();
        Queue(database.OrganizationId, id, database.ServerId, "configure-backup", request, actor);
        await db.SaveChangesAsync(ct);
        return policy;
    }

    private async Task ApplyPolicyAsync(DatabaseBackupPolicy policy, BackupPolicyRequest request, CancellationToken ct)
    {
        if (request.FrequencyHours is < 1 or > 168 || request.RetentionDays is < 1 or > 3650 || request.MinimumCopies is < 1 or > 100)
            throw new ArgumentException("Backup frequency, retention or minimum copy count is outside supported limits.");
        if (request.Enabled && request.RepositoryAppId is null)
            throw new ArgumentException("An organization-owned backup repository is required.");
        var repositoryId = request.RepositoryAppId ?? policy.RepositoryAppId;
        if (repositoryId is not null)
        {
            var repositories = db.Apps.Where(x => x.Id == repositoryId && x.OrganizationId == policy.OrganizationId);
            if (db.Database.IsNpgsql())
            {
                repositories = db.Apps.FromSqlInterpolated($"SELECT * FROM apps WHERE id = {repositoryId} AND organization_id = {policy.OrganizationId} FOR UPDATE");
            }
            var repository = await repositories.SingleOrDefaultAsync(ct);
            if (repository is not null) { await db.Entry(repository).ReloadAsync(ct); }
            if (repository?.DeletionStatus is not null ||
                (request.Enabled || request.RepositoryAppId is not null) && (repository is null || repository.Database != DatabaseMode.None || !repository.Slug.StartsWith("dbrepo-", StringComparison.Ordinal)))
                throw new ArgumentException("An available organization-owned backup repository is required.");
        }
        var source = db.ManagedDatabases.Local.SingleOrDefault(x => x.Id == policy.DatabaseId) ?? await db.ManagedDatabases.SingleOrDefaultAsync(x => x.Id == policy.DatabaseId, ct);
        if (request.RepositoryAppId is not null && source?.AppId == request.RepositoryAppId) throw new ArgumentException("Repository must be off-server.");
        policy.Enabled = request.Enabled;
        policy.KeepLastCopies = request.KeepLastCopies;
        if (request.Enabled || request.RepositoryAppId is not null) { policy.RepositoryAppId = request.RepositoryAppId; }
        policy.FrequencyHours = request.FrequencyHours; policy.RetentionDays = request.RetentionDays; policy.MinimumCopies = request.MinimumCopies;
        policy.Status = request.Enabled ? "pending" : "disabled";
    }

    public async Task<ManagedDatabase> GetAsync(string id, ActorIdentity actor, bool write, CancellationToken ct)
    {
        var database = await db.ManagedDatabases.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Database not found.");
        await RequireAsync(actor, database.OrganizationId, write, ct);
        return database;
    }

    public async Task<DatabaseOperation> OperateAsync(string id, DatabaseOperationRequest request, ActorIdentity actor, CancellationToken ct)
    {
        var database = await GetAsync(id, actor, true, ct);
        if (request.Kind is not ("backup" or "restore" or "migrate" or "delete" or "rotate-credentials")) throw new ArgumentException("Unsupported database operation.");
        if (await db.DatabaseOperations.AnyAsync(x => x.DatabaseId == id && (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct)) throw new InvalidOperationException("A database operation is already active.");
        if (request.Kind == "delete" && database.AppId is not null)
            throw new InvalidOperationException("Application-hosted database lifecycle is managed through its application.");
        if (request.Kind == "delete" && await db.ManagedDatabaseBindings.AnyAsync(x => x.DatabaseId == id && x.OrganizationId == database.OrganizationId && x.Status != "revoked", ct))
            throw new InvalidOperationException("Unbind application services before deleting this database.");
        if (request.Kind is "restore" or "migrate")
        {
            if (request.TargetName is null) throw new ArgumentException("A new target database name is required.");
            Name(request.TargetName);
            if (request.TargetServerId is not null && !await db.ManagedDatabaseServers.AnyAsync(x => x.Id == request.TargetServerId && x.OrganizationId == database.OrganizationId && x.Status == "ready", ct)) throw new ArgumentException("Target server is unavailable.");
            if (request.Kind == "restore" && !await db.DatabaseBackupArtifacts.AnyAsync(x => x.Id == request.BackupArtifactId && x.DatabaseId == id && x.OrganizationId == database.OrganizationId && x.Status == "complete", ct)) throw new ArgumentException("Completed source backup is required.");
        }
        database.Revision++;
        var operation = Queue(database.OrganizationId, id, database.ServerId, request.Kind, request, actor);
        await db.SaveChangesAsync(ct);
        return operation;
    }

    public DatabaseOperation Queue(string organizationId, string? databaseId, string? serverId, string kind, object request, ActorIdentity actor)
    {
        var operation = new DatabaseOperation { Id = ids.NewId("dbop"), OrganizationId = organizationId, DatabaseId = databaseId, ServerId = serverId, Kind = kind, RequestJson = JsonSerializer.Serialize(request), ActorJson = JsonSerializer.Serialize(actor), CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        db.DatabaseOperations.Add(operation);
        return operation;
    }

    private static void Name(string value)
    {
        if (!Regex.IsMatch(value, "^[a-z][a-z0-9_-]{0,62}$")) throw new ArgumentException("Use 1–63 lowercase letters, digits, underscores or dashes, beginning with a letter.");
    }
}
