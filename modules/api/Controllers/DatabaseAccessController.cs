using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Minicloud.Api.Auth;
using Minicloud.Common.Contracts;
using Minicloud.Common.Data;
using Minicloud.Common.Services;
using static Minicloud.Api.Controllers.EndpointResults;

namespace Minicloud.Api.Controllers;

[ApiController, Authorize, Route("v1/databases/{id}/access")]
public sealed class DatabaseAccessController(MinicloudDbContext db, DatabaseManagementService service, TimeProvider clock) : ControllerBase
{
    [HttpPut]
    public Task<IResult> Update(string id, DatabaseAccessRequest request, CancellationToken ct) => DatabasesController.Handle(async () =>
    {
        var actor = RequirePrincipal(HttpContext).ToActorIdentity();
        var database = await service.GetAsync(id, actor, true, ct);
        if (database.Status != "ready") { throw new InvalidOperationException("Database must be ready and reconciled before changing access."); }
        var migrations = await db.DatabaseOperations.AsNoTracking().Where(x => x.DatabaseId == id && x.OrganizationId == database.OrganizationId && x.Kind == "migrate")
            .Select(x => new { x.ErrorCode, x.ResultJson }).ToListAsync(ct);
        foreach (var migration in migrations)
        {
            if (migration.ErrorCode == "database_migration_reconciliation_required")
            { throw new InvalidOperationException("Retained migration source requires explicit reconciliation."); }
            if (migration.ResultJson is null) { continue; }
            using var result = JsonDocument.Parse(migration.ResultJson);
            if (result.RootElement.TryGetProperty("sourceFenced", out var fence) && fence.ValueKind == JsonValueKind.True)
            { throw new InvalidOperationException("Retained migration source remains fenced."); }
        }
        if (database.Engine != "postgres" || request.Mode is not ("internal" or "public" or "private")) { throw new ArgumentException("This database does not support the selected access mode."); }
        if (request.Mode == "private" && !await db.ManagedDatabaseNetworks.AnyAsync(x => x.Id == request.NetworkId && x.OrganizationId == database.OrganizationId && x.Status == "ready", ct))
        { throw new ArgumentException("Select a ready organization-owned provider network."); }
        var cidrs = request.AllowedCidrs ?? [];
        foreach (var cidr in cidrs)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2 || !System.Net.IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var length) || length < 0 || length > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            { throw new ArgumentException("Allowed clients must be valid IP/CIDR ranges."); }
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            { throw new ArgumentException("Public database endpoints currently support IPv4 client ranges."); }
        }
        if (cidrs.Count > 100) { throw new ArgumentException("At most 100 client ranges are supported."); }
        if (await db.DatabaseOperations.AnyAsync(x => x.DatabaseId == id && (x.Status == "queued" || x.Status == "running" || x.Status == "cancelling"), ct)) { throw new InvalidOperationException("A database operation is active."); }
        var operation = service.Queue(database.OrganizationId, database.Id, database.ServerId, "access", request, actor);
        database.Revision++;
        database.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return Results.Accepted($"/v1/databases/{id}/operations", new { operation.Id, operation.Kind, operation.Status });
    });
}
