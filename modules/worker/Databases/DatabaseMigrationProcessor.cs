using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Databases;

public sealed partial class DatabaseOperationProcessor
{
    private async Task<bool> MigrateDatabaseAsync(DatabaseOperation operation, ManagedDatabase source, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        if (source.Engine != "postgres" || source.Placement != "application" || source.AppId != sourceApp.Id)
            throw new InvalidOperationException("Migration requires an application-hosted PostgreSQL source.");
        var request = JsonSerializer.Deserialize<DatabaseOperationRequest>(operation.RequestJson, Json) ?? throw new ArgumentException();
        if (request.TargetServerId is null) throw new ArgumentException("Select an explicit standalone destination.");
        await RequireApplicationMutationAsync(operation, sourceApp.Id, ct);
        var credentials = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(source.EncryptedCredentialsJson ?? throw new InvalidOperationException()), Json)!;
        var container = "minicloud-" + sourceApp.Slug + "-db-1";
        if (operation.ResultJson is not null)
        {
            using var state = JsonDocument.Parse(operation.ResultJson);
            if (state.RootElement.TryGetProperty("rolloutDeploymentId", out var deploymentId))
            {
                var rollout = await db.Deployments.SingleAsync(x => x.Id == deploymentId.GetString() && x.AppId == sourceApp.Id && x.OrganizationId == source.OrganizationId, ct);
                if (rollout.Status is DeploymentStatus.Failed or DeploymentStatus.Canceled)
                    throw new InvalidOperationException("Migration cutover failed. The original is retained and fenced; recover explicitly before reopening writers.");
                if (rollout.Status != DeploymentStatus.Succeeded) return false;
                source.Status = "migrated";
                source.UpdatedAt = clock.GetUtcNow();
                var oldPolicy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == source.Id, ct);
                var migratedTargetId = state.RootElement.GetProperty("targetDatabaseId").GetString();
                var targetPolicy = await db.DatabaseBackupPolicies.SingleAsync(x => x.DatabaseId == migratedTargetId, ct);
                targetPolicy.Enabled = oldPolicy.Enabled;
                targetPolicy.RepositoryAppId = oldPolicy.RepositoryAppId;
                targetPolicy.FrequencyHours = oldPolicy.FrequencyHours;
                targetPolicy.RetentionDays = oldPolicy.RetentionDays;
                targetPolicy.MinimumCopies = oldPolicy.MinimumCopies;
                targetPolicy.KeepLastCopies = oldPolicy.KeepLastCopies;
                targetPolicy.Status = targetPolicy.Enabled ? "pending" : "disabled";
                foreach (var policyDatabaseId in new[] { source.Id, migratedTargetId! })
                    db.DatabaseOperations.Add(new DatabaseOperation { Id = ids.NewId("dbop"), OrganizationId = source.OrganizationId, DatabaseId = policyDatabaseId, Kind = "configure-backup", ActorJson = operation.ActorJson, RequestJson = "{}", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
                oldPolicy.Enabled = false;
                oldPolicy.Status = "disabled";
                oldPolicy.NextDueAt = null;
                operation.ResultJson = JsonSerializer.Serialize(new { targetDatabaseId = state.RootElement.GetProperty("targetDatabaseId").GetString(), rolloutDeploymentId = rollout.Id, status = "migrated", sourceRetained = true, sourceFenced = true }, Json);
                return true;
            }
        }
        var cutoverStarted = false;
        await RequireApplicationMutationAsync(operation, sourceApp.Id, ct);
        await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "migration-fence.py", new { action = "fence", databaseName = credentials.DatabaseName, container, operationId = operation.Id }, ct);
        try
        {
            ManagedDatabase target;
            var targetExists = false;
            if (operation.ResultJson is not null)
            {
                using var retained = JsonDocument.Parse(operation.ResultJson);
                targetExists = retained.RootElement.TryGetProperty("targetDatabaseId", out _);
            }
            if (!targetExists)
            {
                await CaptureBackupAsync(operation, source, sourceApp, sourceHost, ct);
                await SaveOwnedDatabaseChangesAsync(operation, ct);
                var artifact = await db.DatabaseBackupArtifacts.Where(x => x.DatabaseId == source.Id && x.Status == "complete").OrderByDescending(x => x.CreatedAt).FirstAsync(ct);
                var originalRequest = operation.RequestJson;
                try
                {
                    operation.RequestJson = JsonSerializer.Serialize(request with { Kind = "restore", BackupArtifactId = artifact.Id }, Json);
                    await RestoreBackupAsync(operation, source, ct);
                }
                finally { operation.RequestJson = originalRequest; }
            }
            using (var restored = JsonDocument.Parse(operation.ResultJson!))
                target = await db.ManagedDatabases.SingleAsync(x => x.Id == restored.RootElement.GetProperty("targetDatabaseId").GetString() && x.OrganizationId == source.OrganizationId && x.Status == "ready", ct);
            var targetServer = await db.ManagedDatabaseServers.SingleAsync(x => x.Id == target.ServerId && x.OrganizationId == source.OrganizationId, ct);
            var targetApp = await db.Apps.SingleAsync(x => x.Id == targetServer.RuntimeAppId && x.OrganizationId == source.OrganizationId, ct);
            var targetHost = await db.ServerInstances.SingleAsync(x => x.AppId == targetApp.Id && x.OrganizationId == source.OrganizationId, ct);
            var activeServices = await new DeploymentServiceSetService().BuildActiveServicesAsync(db, sourceApp.Id, ct);
            if (activeServices.Count == 0) throw new InvalidOperationException("No application services are available for explicit cutover.");
            foreach (var service in activeServices)
            {
                var binding = await db.ManagedDatabaseBindings.SingleOrDefaultAsync(x => x.AppId == sourceApp.Id && x.ServiceName == service.Name, ct);
                if (binding is not null && binding.DatabaseId != target.Id) throw new InvalidOperationException("A service already has another database binding.");
                if (binding is null)
                {
                    binding = new ManagedDatabaseBinding { Id = ids.NewId("dbbinding"), OrganizationId = source.OrganizationId, DatabaseId = target.Id, AppId = sourceApp.Id, ServiceName = service.Name, Status = "pending", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
                    db.ManagedDatabaseBindings.Add(binding);
                }
                await RequireApplicationMutationAsync(operation, sourceApp.Id, ct);
                cutoverStarted = true;
                await SaveOwnedDatabaseChangesAsync(operation, ct);
                var originalRequest = operation.RequestJson;
                try
                {
                    operation.RequestJson = JsonSerializer.Serialize(new { bindingId = binding.Id }, Json);
                    await ApplyBindingNetworkAsync(operation, target, targetApp, targetHost, ct);
                }
                finally { operation.RequestJson = originalRequest; }
            }
            await RequireApplicationMutationAsync(operation, sourceApp.Id, ct);
            var deployment = await QueueDatabaseRolloutAsync(sourceApp, activeServices.Select(x => x.Name).ToHashSet(StringComparer.Ordinal), ct);
            operation.ResultJson = JsonSerializer.Serialize(new { targetDatabaseId = target.Id, rolloutDeploymentId = deployment.Id, status = "cutover", sourceRetained = true, sourceFenced = true }, Json);
            await SaveOwnedDatabaseChangesAsync(operation, ct);
            return false;
        }
        catch
        {
            if (!cutoverStarted)
                await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "migration-fence.py", new { action = "unfence", databaseName = credentials.DatabaseName, container, operationId = operation.Id }, CancellationToken.None);
            throw;
        }
    }

    private async Task<Deployment> QueueDatabaseRolloutAsync(MinicloudApp app, IReadOnlySet<string> serviceNames, CancellationToken ct)
    {
        var services = await new DeploymentServiceSetService().BuildActiveServicesAsync(db, app.Id, ct);
        var selected = services.Where(x => serviceNames.Contains(x.Name)).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("No selected service is active.");
        var deployment = lifecycle.CreateQueued(app, new CreateDeploymentRequest(app.Id, app.Database.ToString().ToLowerInvariant(), null, selected));
        lifecycle.AddRetainedServices(deployment, services);
        await catalog.ReserveAsync(app, deployment, ct);
        db.Deployments.Add(deployment);
        return deployment;
    }
}
