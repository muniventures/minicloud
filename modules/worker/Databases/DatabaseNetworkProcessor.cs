using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Models;
using Minicloud.Common.Services;

namespace Minicloud.Worker.Databases;

public sealed partial class DatabaseOperationProcessor
{
    private async Task<bool> ApplyBindingNetworkAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        using var request = JsonDocument.Parse(operation.RequestJson);
        var bindingId = request.RootElement.GetProperty("bindingId").GetString();
        var binding = await db.ManagedDatabaseBindings.SingleAsync(x => x.Id == bindingId && x.DatabaseId == database.Id && x.OrganizationId == operation.OrganizationId, ct);
        var app = await db.Apps.SingleAsync(x => x.Id == binding.AppId && x.OrganizationId == operation.OrganizationId && x.DeletionStatus == null, ct);
        await RequireApplicationMutationAsync(operation, app.Id, ct);
        var host = await db.ServerInstances.SingleAsync(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId, ct);
        if (string.IsNullOrWhiteSpace(host.IpAddress)) { throw new InvalidOperationException("Application runtime is unavailable."); }
        var port = 20000 + (int)(BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(binding.Id))) % 40001);
        if (await db.ManagedDatabaseBindings.AnyAsync(x => x.AppId == app.Id && x.Id != binding.Id && x.Port == port && x.Status == "applied", ct))
        { throw new InvalidOperationException("Private connection port conflicts with another binding."); }
        var client = await ExecuteRemoteAsync(operation, host.IpAddress, "private-network.py", new { action = "prepare-client", bindingId = binding.Id, dockerNetwork = "minicloud-" + app.Slug + "_default", operationId = operation.Id }, ct);
        await RequireApplicationMutationAsync(operation, app.Id, ct);
        var server = await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "private-network.py", new { action = "authorize-client", bindingId = binding.Id, container = "minicloud-" + sourceApp.Slug + "-db-1", publicKey = client.GetProperty("publicKey").GetString(), operationId = operation.Id }, ct);
        await RequireApplicationMutationAsync(operation, app.Id, ct);
        var credentials = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(database.EncryptedCredentialsJson!), Json)!;
        var connection = await ExecuteRemoteAsync(operation, host.IpAddress, "private-network.py", new { action = "connect-client", bindingId = binding.Id, gateway = client.GetProperty("gateway").GetString(), serverIp = sourceHost.IpAddress, target = server.GetProperty("target").GetString(), hostKey = server.GetProperty("hostKey").GetString(), sshUser = server.GetProperty("sshUser").GetString(), credentials, port, operationId = operation.Id }, ct);
        await RequireApplicationMutationAsync(operation, app.Id, ct);
        binding.Host = connection.GetProperty("host").GetString();
        binding.Port = port;
        binding.SslMode = "disable";
        binding.Status = "applied";
        binding.Revision++;
        binding.UpdatedAt = clock.GetUtcNow();
        return true;
    }
    private async Task<bool> ApplyDatabaseAccessAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<Minicloud.Common.Contracts.DatabaseAccessRequest>(operation.RequestJson, Json)
            ?? throw new InvalidOperationException("Access request is unavailable.");
        if (database.Status != "ready" || await HasRetainedMigrationFenceAsync(database.Id, database.OrganizationId, ct))
        { throw new InvalidOperationException("Database access requires explicit migration reconciliation."); }
        var previousNetwork = database.NetworkId;
        var previousIp = database.PrivateIp;
        try
        {
        if (request.Mode == "private")
        {
            if (request.NetworkId is null) { throw new InvalidOperationException("A native network is required."); }
            var attached = await privateNetworks.AttachAsync(request.NetworkId, database.OrganizationId, sourceHost.Id, ct);
            await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "native-network.py", new { privateIp = attached.IpAddress, subnetRange = attached.SubnetRange, operationId = operation.Id }, ct);
            if (!await db.ManagedDatabaseNetworks.AnyAsync(x => x.Id == attached.NetworkId && x.OrganizationId == database.OrganizationId && x.Status == "ready", ct))
            { throw new InvalidOperationException("Native network ownership changed."); }
            database.NetworkId = attached.NetworkId;
            database.PrivateIp = attached.IpAddress;
        }
        else { database.NetworkId = null; database.PrivateIp = null; }
        return await ReconcileDatabaseAccessAsync(operation, database, sourceApp, sourceHost, request.Mode, request.AllowedCidrs ?? [], ct);
        }
        catch (DatabaseRemoteOperationException error) when (error.Code == "database_access_reconciliation_required")
        {
            database.Status = "access-reconciliation-required";
            database.AccessMode = "unknown";
            throw;
        }
        catch
        {
            database.NetworkId = previousNetwork;
            database.PrivateIp = previousIp;
            throw;
        }
    }

    private async Task<bool> HasRetainedMigrationFenceAsync(string databaseId, string organizationId, CancellationToken ct)
    {
        var migrations = await db.DatabaseOperations.AsNoTracking().Where(x => x.DatabaseId == databaseId && x.OrganizationId == organizationId && x.Kind == "migrate")
            .Select(x => new { x.ErrorCode, x.ResultJson }).ToListAsync(ct);
        foreach (var migration in migrations)
        {
            if (migration.ErrorCode == "database_migration_reconciliation_required") { return true; }
            if (migration.ResultJson is null) { continue; }
            using var result = JsonDocument.Parse(migration.ResultJson);
            if (result.RootElement.TryGetProperty("sourceFenced", out var fence) && fence.ValueKind == JsonValueKind.True) { return true; }
        }
        return false;
    }

    private async Task<bool> ReconcileDatabaseAccessAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, string mode, IReadOnlyList<string> cidrs, CancellationToken ct)
    {
        var neighbors = await db.ManagedDatabases.Where(x => x.OrganizationId == database.OrganizationId && x.Status != "deleted" &&
            x.EncryptedCredentialsJson != null &&
            (database.ServerId != null ? x.ServerId == database.ServerId : x.AppId == sourceApp.Id || x.RecoverySourceAppId == sourceApp.Id)).ToListAsync(ct);
        var effective = new List<object>();
        foreach (var neighbor in neighbors)
        {
            var material = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(neighbor.EncryptedCredentialsJson ?? throw new InvalidOperationException("Database credentials unavailable.")), Json)!;
            var fenced = neighbor.Status is "migrated" or "migration-reconciliation-required" or "migration-fenced" or "fenced" or "access-reconciliation-required" ||
                await HasRetainedMigrationFenceAsync(neighbor.Id, neighbor.OrganizationId, ct);
            var subnet = fenced || neighbor.NetworkId is null ? null : await db.ManagedDatabaseNetworks.Where(x => x.Id == neighbor.NetworkId && x.OrganizationId == neighbor.OrganizationId && x.Status == "ready").Select(x => x.SubnetRange).SingleAsync(ct);
            effective.Add(new { databaseName = material.DatabaseName, username = material.Username, fenced,
                mode = fenced ? "internal" : neighbor.Id == database.Id ? mode : neighbor.AccessMode, privateIp = neighbor.PrivateIp, privateSubnet = subnet,
                allowedCidrs = neighbor.Id == database.Id ? cidrs : JsonSerializer.Deserialize<string[]>(neighbor.AllowedCidrsJson, Json) });
        }
        var applied = await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "dispatch.py", new { action = "access", appSlug = sourceApp.Slug, serverIp = sourceHost.IpAddress, databases = effective, operationId = operation.Id }, ct);
        database.AccessMode = mode;
        database.AllowedCidrsJson = JsonSerializer.Serialize(cidrs, Json);
        var credentials = JsonSerializer.Deserialize<DatabaseConnectionCredentials>(encryption.Decrypt(database.EncryptedCredentialsJson!), Json)!;
        credentials = credentials with { Host = mode == "public" ? sourceHost.IpAddress : mode == "private" ? database.PrivateIp : null, SslMode = mode is "public" or "private" ? "verify-full" : null, CaCertificatePem = mode is "public" or "private" ? applied.GetProperty("caCertificatePem").GetString() : null };
        database.EncryptedCredentialsJson = encryption.Encrypt(JsonSerializer.Serialize(credentials, Json));
        database.UpdatedAt = clock.GetUtcNow();
        return true;
    }

    private async Task<bool> RevokeBindingNetworkAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        using var request = JsonDocument.Parse(operation.RequestJson);
        var binding = await db.ManagedDatabaseBindings.SingleAsync(x => x.Id == request.RootElement.GetProperty("bindingId").GetString() && x.DatabaseId == database.Id && x.OrganizationId == operation.OrganizationId, ct);
        await RequireApplicationMutationAsync(operation, binding.AppId, ct);
        var host = await db.ServerInstances.SingleOrDefaultAsync(x => x.AppId == binding.AppId && x.OrganizationId == operation.OrganizationId, ct);
        await ExecuteRemoteAsync(operation, sourceHost.IpAddress!, "private-network.py", new { action = "revoke-client", bindingId = binding.Id, operationId = operation.Id }, ct);
        var deleting = await db.Apps.AnyAsync(x => x.Id == binding.AppId && x.OrganizationId == operation.OrganizationId && x.DeletionStatus != null, ct);
        if (host is not null && !deleting)
            await ExecuteRemoteAsync(operation, host.IpAddress!, "private-network.py", new { action = "stop-client", bindingId = binding.Id, operationId = operation.Id }, ct);
        binding.Status = "revoked";
        binding.Host = null;
        binding.UpdatedAt = clock.GetUtcNow();
        binding.Revision++;
        return true;
    }

    private async Task<bool> ProcessBindingAsync(DatabaseOperation operation, ManagedDatabase database, MinicloudApp sourceApp, ServerInstance sourceHost, CancellationToken ct)
    {
        if (operation.ResultJson is not null)
        {
            using var result = JsonDocument.Parse(operation.ResultJson);
            var rolloutId = result.RootElement.GetProperty("rolloutDeploymentId").GetString();
            var rollout = await db.Deployments.SingleAsync(x => x.Id == rolloutId && x.OrganizationId == operation.OrganizationId, ct);
            if (rollout.Status is DeploymentStatus.Failed or DeploymentStatus.Canceled) { throw new InvalidOperationException("Connection rollout failed; inspect the selected application."); }
            return rollout.Status == DeploymentStatus.Succeeded;
        }
        using var request = JsonDocument.Parse(operation.RequestJson);
        var binding = await db.ManagedDatabaseBindings.SingleAsync(x => x.Id == request.RootElement.GetProperty("bindingId").GetString() && x.OrganizationId == operation.OrganizationId, ct);
        var app = await db.Apps.SingleAsync(x => x.Id == binding.AppId && x.OrganizationId == operation.OrganizationId, ct);
        await RequireApplicationMutationAsync(operation, app.Id, ct);
        if (operation.Kind == "bind" && app.DeletionStatus is not null)
            throw new InvalidOperationException("Cannot bind an application pending deletion.");
        if (operation.Kind == "bind") { await ApplyBindingNetworkAsync(operation, database, sourceApp, sourceHost, ct); }
        else { await RevokeBindingNetworkAsync(operation, database, sourceApp, sourceHost, ct); }
        if (operation.Kind == "unbind" && (app.DeletionStatus is not null || !await db.ServerInstances.AnyAsync(x => x.AppId == app.Id && x.OrganizationId == app.OrganizationId, ct)))
        {
            operation.ResultJson = JsonSerializer.Serialize(new { status = "revoked", rolloutSkipped = "application-runtime-unavailable-or-deleting" }, Json);
            return true;
        }
        var deployment = await QueueDatabaseRolloutAsync(app, new HashSet<string>(StringComparer.Ordinal) { binding.ServiceName }, ct);
        operation.ResultJson = JsonSerializer.Serialize(new { rolloutDeploymentId = deployment.Id }, Json);
        return false;
    }

}
