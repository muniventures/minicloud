using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Minicloud.Common.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Models;

namespace Minicloud.Common.Services;

public sealed class DatabaseBindingService(MinicloudDbContext db, ControlPlaneAuthorization authorization, IIdGenerator ids, TimeProvider clock)
{
    public async Task<IReadOnlyList<ManagedDatabaseBinding>> BindAsync(string databaseId, DatabaseBindingRequest request, ActorIdentity actor, CancellationToken ct)
    {
        var database = await db.ManagedDatabases.SingleOrDefaultAsync(x => x.Id == databaseId, ct) ?? throw new KeyNotFoundException();
        if (!await authorization.CanAccessOrganizationAsync(actor, database.OrganizationId, OrganizationAction.ManageDatabases, ct) ||
            !await authorization.CanAccessAppAsync(actor, request.AppId, OrganizationAction.WriteApps, ct) ||
            !await authorization.CanAccessAppAsync(actor, request.AppId, OrganizationAction.WriteSecrets, ct) ||
            !await authorization.CanAccessAppAsync(actor, request.AppId, OrganizationAction.CreateDeployments, ct)) throw new UnauthorizedAccessException();
        await RequireIdleAsync(database.Id, database.OrganizationId, ct);
        if (database.Engine != "postgres" || database.Placement == "application" || database.Status != "ready") throw new InvalidOperationException("Select a ready standalone PostgreSQL database.");
        if (!await db.Apps.AnyAsync(x => x.Id == request.AppId && x.OrganizationId == database.OrganizationId && x.DeletionStatus == null, ct)) throw new UnauthorizedAccessException();
        if (request.ServiceNames.Count == 0 || request.ServiceNames.Count > 64 || request.ServiceNames.Distinct(StringComparer.Ordinal).Count() != request.ServiceNames.Count) throw new ArgumentException("Select distinct application services.");
        var active = await new DeploymentServiceSetService().BuildActiveServicesAsync(db, request.AppId, ct);
        if (active.Count == 0) { throw new InvalidOperationException("Application has no active deployment."); }
        var services = active.Select(x => x.Name).ToArray();
        if (request.ServiceNames.Any(x => !services.Contains(x, StringComparer.Ordinal))) throw new ArgumentException("A selected service is not in the active application deployment.");
        var existing = await db.ManagedDatabaseBindings.Where(x => x.AppId == request.AppId && request.ServiceNames.Contains(x.ServiceName)).ToListAsync(ct);
        if (existing.Any(x => x.Status != "revoked" && (x.DatabaseId != databaseId || x.Status != "applied"))) throw new InvalidOperationException("A selected service already has a different or pending database binding.");
        var result = new List<ManagedDatabaseBinding>(existing);
        if (existing.Count == request.ServiceNames.Count && existing.All(x => x.Status == "applied")) return result;
        database.Revision++;
        foreach (var prior in existing.Where(x => x.Status == "revoked"))
        {
            prior.DatabaseId = databaseId; prior.Status = "pending"; prior.Host = null; prior.Revision++; prior.UpdatedAt = clock.GetUtcNow();
            QueueBindingOperation(prior, "bind", actor);
        }
        foreach (var name in request.ServiceNames.Except(existing.Select(x => x.ServiceName), StringComparer.Ordinal))
        {
            var binding = new ManagedDatabaseBinding { Id = ids.NewId("dbbinding"), OrganizationId = database.OrganizationId, DatabaseId = database.Id, AppId = request.AppId, ServiceName = name, Status = "pending", CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
            db.ManagedDatabaseBindings.Add(binding);
            QueueBindingOperation(binding, "bind", actor);
            result.Add(binding);
        }
        await db.SaveChangesAsync(ct);
        return result;
    }
    public async Task UnbindAsync(string databaseId, string bindingId, ActorIdentity actor, CancellationToken ct)
    {
        var binding = await db.ManagedDatabaseBindings.SingleOrDefaultAsync(x => x.Id == bindingId && x.DatabaseId == databaseId, ct) ?? throw new KeyNotFoundException();
        if (!await authorization.CanAccessOrganizationAsync(actor, binding.OrganizationId, OrganizationAction.ManageDatabases, ct) ||
            !await authorization.CanAccessAppAsync(actor, binding.AppId, OrganizationAction.WriteApps, ct) ||
            !await authorization.CanAccessAppAsync(actor, binding.AppId, OrganizationAction.WriteSecrets, ct) ||
            !await authorization.CanAccessAppAsync(actor, binding.AppId, OrganizationAction.CreateDeployments, ct)) throw new UnauthorizedAccessException();
        if (binding.Status == "revoked") return;
        var database = await db.ManagedDatabases.SingleAsync(x => x.Id == databaseId && x.OrganizationId == binding.OrganizationId, ct);
        await RequireIdleAsync(database.Id, database.OrganizationId, ct);
        if (binding.Status is not ("applied" or "pending" or "revoking" or "failed"))
            throw new InvalidOperationException("The binding state does not support disconnection.");
        database.Revision++;
        binding.Status = "revoking"; binding.Revision++; binding.UpdatedAt = clock.GetUtcNow();
        QueueBindingOperation(binding, "unbind", actor);
        await db.SaveChangesAsync(ct);
    }

    private async Task RequireIdleAsync(string databaseId, string organizationId, CancellationToken ct)
    {
        if (await db.DatabaseOperations.AnyAsync(x => x.DatabaseId == databaseId && x.OrganizationId == organizationId &&
            (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct))
            throw new InvalidOperationException("Wait for active database operations before changing application connections.");
    }

    private void QueueBindingOperation(ManagedDatabaseBinding binding, string kind, ActorIdentity actor)
    {
        db.DatabaseOperations.Add(new DatabaseOperation { Id = ids.NewId("dbop"), OrganizationId = binding.OrganizationId, DatabaseId = binding.DatabaseId, Kind = kind, RequestJson = JsonSerializer.Serialize(new { bindingId = binding.Id }), ActorJson = JsonSerializer.Serialize(actor), CreatedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() });
    }

}
