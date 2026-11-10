using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Databases;

public sealed partial class DatabaseOperationProcessor
{
    internal async Task SaveOwnedDatabaseChangesAsync(DatabaseOperation operation, CancellationToken ct, string? expectedOwner = null)
    {
        var token = expectedOwner ?? operation.OwnershipToken;
        if (string.IsNullOrWhiteSpace(token)) throw new UnauthorizedAccessException("Database operation ownership is unavailable.");
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var existingTransaction = db.Database.CurrentTransaction;
            await using var transaction = existingTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            var current = db.Database.IsNpgsql()
                ? await db.DatabaseOperations.FromSqlInterpolated($"SELECT * FROM database_operations WHERE id = {operation.Id} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct)
                : await db.DatabaseOperations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operation.Id, ct);
            if (current is null || current.OrganizationId != operation.OrganizationId || current.OwnershipToken != token ||
                current.Status is not ("running" or "cancelling") || current.LeaseExpiresAt is null || current.LeaseExpiresAt <= clock.GetUtcNow())
                throw new UnauthorizedAccessException("Database operation ownership changed before persistence.");
            if (current.Status == "cancelling" && operation.Status != "cancelled" && operation.Status != "failed")
                throw new UnauthorizedAccessException("Database operation cancellation prevents further writes.");
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        });
    }

    private async Task ValidateDatabaseOperationAuthorityAsync(DatabaseOperation operation, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(operation.OwnershipToken) || !await db.DatabaseOperations.AsNoTracking().AnyAsync(current =>
            current.Id == operation.Id && current.OrganizationId == operation.OrganizationId && current.Status == "running" &&
            current.OwnershipToken == operation.OwnershipToken && current.LeaseExpiresAt > clock.GetUtcNow(), ct))
            throw new UnauthorizedAccessException("Database operation lease changed.");
        if (operation.BackupPolicyId is not null)
        {
            if (operation.Kind is not ("backup" or "reconcile-policy") || operation.ScheduledPolicyRevision is null ||
                !await db.DatabaseBackupPolicies.AsNoTracking().AnyAsync(policy => policy.Id == operation.BackupPolicyId &&
                    policy.OrganizationId == operation.OrganizationId && policy.DatabaseId == operation.DatabaseId &&
                    policy.Revision == operation.ScheduledPolicyRevision && (operation.Kind == "reconcile-policy" || policy.Enabled) &&
                    db.ManagedDatabases.Any(database => database.Id == policy.DatabaseId && database.OrganizationId == policy.OrganizationId &&
                        database.ServerId == operation.ServerId && (operation.Kind == "reconcile-policy" || database.Status != "deleted")), ct))
                throw new UnauthorizedAccessException("Scheduled database policy authority changed.");
            return;
        }
        if (operation.ScheduledPolicyRevision is not null) throw new UnauthorizedAccessException();
        await CurrentOperationActorAsync(operation, ct);
        if (operation.Kind == "migrate")
        {
            var sourceApp = await db.ManagedDatabases.Where(x => x.Id == operation.DatabaseId && x.OrganizationId == operation.OrganizationId).Select(x => x.AppId).SingleAsync(ct);
            if (sourceApp is null) throw new UnauthorizedAccessException();
            await RequireApplicationMutationAsync(operation, sourceApp, ct);
        }
        if (operation.Kind is "bind" or "unbind")
        {
            using var request = JsonDocument.Parse(operation.RequestJson);
            var bindingId = request.RootElement.GetProperty("bindingId").GetString();
            var targetApp = await db.ManagedDatabaseBindings.Where(x => x.Id == bindingId && x.DatabaseId == operation.DatabaseId && x.OrganizationId == operation.OrganizationId).Select(x => x.AppId).SingleAsync(ct);
            await RequireApplicationMutationAsync(operation, targetApp, ct);
        }
    }

    private async Task<ActorIdentity> CurrentOperationActorAsync(DatabaseOperation operation, CancellationToken ct)
    {
        var actor = JsonSerializer.Deserialize<ActorIdentity>(operation.ActorJson, Json) ?? throw new UnauthorizedAccessException();
        if (actor.IsApiKey)
        {
            var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor.ApiKeyId && x.OrganizationId == operation.OrganizationId && x.RevokedAt == null && (x.ExpiresAt == null || x.ExpiresAt > clock.GetUtcNow()), ct)
                ?? throw new UnauthorizedAccessException();
            actor = actor with { OrganizationId = key.OrganizationId, Scopes = (JsonSerializer.Deserialize<string[]>(key.ScopesJson) ?? []).ToHashSet(StringComparer.Ordinal) };
        }
        else if (!actor.IsDashboardUser || actor.UserId is null) { throw new UnauthorizedAccessException(); }
        if (!await authorization.CanAccessOrganizationAsync(actor, operation.OrganizationId, OrganizationAction.ManageDatabases, ct))
            throw new UnauthorizedAccessException();
        return actor;
    }

    private async Task RequireApplicationMutationAsync(DatabaseOperation operation, string appId, CancellationToken ct)
    {
        var actor = await CurrentOperationActorAsync(operation, ct);
        foreach (var action in new[] { OrganizationAction.WriteApps, OrganizationAction.WriteSecrets, OrganizationAction.CreateDeployments })
            if (!await authorization.CanAccessAppAsync(actor, appId, action, ct)) throw new UnauthorizedAccessException();
    }
}
