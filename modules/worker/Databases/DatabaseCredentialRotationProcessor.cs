using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Databases;

public sealed partial class DatabaseOperationProcessor
{
    private sealed class CredentialRotationState
    {
        public CredentialRotationState() { }
        public string EncryptedCredentials { get; set; } = "";
        public bool SqlApplied { get; set; }
        public bool RolloutsQueued { get; set; }
        public List<CredentialRotationRollout> Rollouts { get; set; } = [];
    }
    private sealed record CredentialRotationRollout(string AppId, string DeploymentId);

    private async Task<bool> RotateCredentialsAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        if (database.AppId is not null || database.Engine != "postgres" || database.Status != "ready")
            throw new InvalidOperationException("Credential rotation requires a standalone PostgreSQL database.");
        var bindings = await db.ManagedDatabaseBindings.Where(x => x.DatabaseId == database.Id && x.OrganizationId == operation.OrganizationId && x.Status != "revoked" && x.Status != "deleted").ToListAsync(ct);
        if (bindings.Any(x => x.Status != "applied")) throw new InvalidOperationException("Resolve pending application bindings before rotating credentials.");
        foreach (var appId in bindings.Select(x => x.AppId).Distinct())
            await RequireApplicationMutationAsync(operation, appId, ct);
        var state = operation.ResultJson is null ? null : JsonSerializer.Deserialize<CredentialRotationState>(operation.ResultJson, Json);
        if (state is null)
        {
            var current = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(database.EncryptedCredentialsJson ?? throw new InvalidOperationException("Credential custody is unavailable.")), Json)!;
            var changed = current with { Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(36)) };
            state = new CredentialRotationState { EncryptedCredentials = encryption.Encrypt(JsonSerializer.Serialize(changed, Json)) };
            operation.ResultJson = JsonSerializer.Serialize(state, Json);
            await SaveOwnedDatabaseChangesAsync(operation, ct);
        }
        if (string.IsNullOrWhiteSpace(state.EncryptedCredentials)) throw new InvalidOperationException("Rotation checkpoint is unavailable.");
        if (!state.SqlApplied)
        {
            var changed = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(state.EncryptedCredentials), Json)!;
            await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "operation.py", new { action = "rotate", databaseName = changed.DatabaseName, username = changed.Username, password = changed.Password, container = "minicloud-" + sourceApp.Slug + "-db-1", adminUser = "minicloud", operationId = operation.Id }, ct);
            database.EncryptedCredentialsJson = state.EncryptedCredentials;
            database.Revision++;
            database.UpdatedAt = clock.GetUtcNow();
            state.SqlApplied = true;
            operation.ResultJson = JsonSerializer.Serialize(state, Json);
            await SaveOwnedDatabaseChangesAsync(operation, ct);
        }
        if (!state.RolloutsQueued)
        {
            foreach (var application in bindings.GroupBy(x => x.AppId))
            {
                if (state.Rollouts.Any(x => x.AppId == application.Key)) continue;
                await RequireApplicationMutationAsync(operation, application.Key, ct);
                var app = await db.Apps.SingleAsync(x => x.Id == application.Key && x.OrganizationId == operation.OrganizationId && x.DeletionStatus == null, ct);
                var deployment = await QueueDatabaseRolloutAsync(app, application.Select(x => x.ServiceName).ToHashSet(StringComparer.Ordinal), ct);
                state.Rollouts.Add(new CredentialRotationRollout(app.Id, deployment.Id));
                operation.ResultJson = JsonSerializer.Serialize(state, Json);
                await SaveOwnedDatabaseChangesAsync(operation, ct);
            }
            state.RolloutsQueued = true;
            operation.ResultJson = JsonSerializer.Serialize(state, Json);
            await SaveOwnedDatabaseChangesAsync(operation, ct);
        }
        var deploymentIds = state.Rollouts.Select(x => x.DeploymentId).ToArray();
        var deployments = await db.Deployments.AsNoTracking().Where(x => x.OrganizationId == operation.OrganizationId && deploymentIds.Contains(x.Id)).ToListAsync(ct);
        if (deployments.Count != state.Rollouts.Count || deployments.Any(x => !state.Rollouts.Any(r => r.AppId == x.AppId && r.DeploymentId == x.Id)))
            throw new InvalidOperationException("Credential rotation rollout ownership changed.");
        return RotationDeploymentsComplete(deployments);
    }

    internal static bool RotationDeploymentsComplete(IReadOnlyCollection<Deployment> deployments)
    {
        if (deployments.Any(x => x.Status is DeploymentStatus.Failed or DeploymentStatus.Canceled))
            throw new InvalidOperationException("Database credentials changed, but an application rollout failed. The new credentials are retained; repair the affected application before retrying.");
        return deployments.All(x => x.Status == DeploymentStatus.Succeeded);
    }
}
