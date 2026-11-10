using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Data;

namespace Minicloud.Common.Services;

public static class DatabaseLifecycleGuards
{
    public static async Task<string?> AppDeletionBlockerAsync(MinicloudDbContext db, string appId, CancellationToken ct, bool allowManagedServerDeletion = false)
    {
        if (await db.ManagedDatabaseServers.AnyAsync(x => x.RuntimeAppId == appId && x.Status != "deleted" && !(allowManagedServerDeletion && x.Status == "deleting" && !db.ManagedDatabases.Any(d => d.ServerId == x.Id && d.Status != "deleted") && db.DatabaseOperations.Any(o => o.ServerId == x.Id && o.Kind == "delete-server" && (o.Status == "queued" || o.Status == "running"))), ct))
            return "Delete the database server through Database servers before deleting its backing application.";
        if (await db.DatabaseBackupPolicies.AnyAsync(x => x.RepositoryAppId == appId && x.Enabled, ct) || await db.DatabaseBackupArtifacts.AnyAsync(x => x.RepositoryAppId == appId && x.Status == "complete", ct))
            return "This repository still holds backup references. Reassign policies and explicitly retire retained backups before deleting it.";
        if (await db.ManagedDatabaseBindings.AnyAsync(x => x.AppId == appId && x.Status != "revoked", ct))
            return "Remove application database bindings before deleting this application.";
        var repositoryCreations = await db.DatabaseOperations.Where(x => x.Kind == "create-repository" &&
            (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling") && x.RequestJson.Contains(appId)).Select(x => x.RequestJson).ToListAsync(ct);
        foreach (var request in repositoryCreations)
        {
            try
            {
                using var json = JsonDocument.Parse(request);
                if (json.RootElement.TryGetProperty("appId", out var target) && target.GetString() == appId)
                    return "Wait for repository creation before deleting this repository.";
            }
            catch (JsonException) { return "Repository creation requires reconciliation before deletion."; }
        }
        if (await db.DatabaseOperations.AnyAsync(x => (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling") && !(allowManagedServerDeletion && x.Kind == "delete-server" && (x.Status == "queued" || x.Status == "running")) && (db.ManagedDatabases.Any(d => d.Id == x.DatabaseId && d.AppId == appId) || db.ManagedDatabaseServers.Any(s => s.Id == x.ServerId && s.RuntimeAppId == appId) || db.DatabaseBackupPolicies.Any(p => p.DatabaseId == x.DatabaseId && p.RepositoryAppId == appId)), ct))
            return "Wait for active database operations before deleting this application.";
        return null;
    }
}
